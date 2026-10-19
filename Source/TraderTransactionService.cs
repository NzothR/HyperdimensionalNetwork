using System;
using System.IO;
using SandboxOptions;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class TraderTransactionService
{
    private static bool _voidSaleMode;
    private static bool _personalPayoutMode;

    public static bool VoidSaleMode => _voidSaleMode;
    public static bool PersonalPayoutMode => _personalPayoutMode;

    public static void LoadPersonalPayoutMode(EntityPlayerLocal player)
    {
        string key = PersonalPayoutPreferenceKey(player);
        _personalPayoutMode = !string.IsNullOrEmpty(key) && PlayerPrefs.GetInt(key, 0) != 0;
    }

    public static bool TogglePersonalPayoutMode(EntityPlayerLocal player)
    {
        _personalPayoutMode = !_personalPayoutMode;
        string key = PersonalPayoutPreferenceKey(player);
        if (!string.IsNullOrEmpty(key)) { PlayerPrefs.SetInt(key, _personalPayoutMode ? 1 : 0); PlayerPrefs.Save(); }
        NetworkLog.Debug("Virtual trader payout target changed. personalInventory=" + _personalPayoutMode);
        return _personalPayoutMode;
    }

    private static string PersonalPayoutPreferenceKey(EntityPlayerLocal player)
    {
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        return string.IsNullOrEmpty(playerId) ? null : "HyperdimensionalNetwork.TraderPersonalPayout." + playerId;
    }

    public static bool CanSell(ItemValue value)
    {
        return value != null && !value.IsEmpty() && value.type > 0 && value.ItemClass != null && IsSellable(value, value.ItemClass);
    }

    public static void SetVoidSaleMode(bool enabled)
    {
        if (_voidSaleMode == enabled) return;
        _voidSaleMode = enabled;
        NetworkLog.Debug("Virtual trader sale mode changed. enabled=" + enabled);
    }

    public static void DisableVoidSaleMode()
    {
        _voidSaleMode = false;
    }

    public static bool SellFromSlot(EntityPlayerLocal player, XUiC_ItemStack slot, bool networkSource, int count,
        Action<NetworkRemoteResult> completed)
    {
        if (!(bool)player || slot == null || slot.ItemStack == null || slot.ItemStack.IsEmpty()
            || count <= 0 || count > slot.ItemStack.count) return false;
        if (NetworkRemoteClient.IsMutationPending) return true;
        ItemStack stack = slot.ItemStack.Clone();
        stack.count = count;
        XUiC_ItemStack.StackLocationTypes location = networkSource
            ? XUiC_ItemStack.StackLocationTypes.ToolBelt
            : slot.StackLocation;
        int slotNumber = slot.SlotNumber;
        if (!networkSource && location != XUiC_ItemStack.StackLocationTypes.Backpack && location != XUiC_ItemStack.StackLocationTypes.ToolBelt) return false;
        if (NetworkRemoteClient.IsRemoteClient)
            return NetworkRemoteClient.RequestTraderVoidSell(player, stack.itemValue, stack.count, networkSource, _personalPayoutMode, location, slotNumber, completed);

        NetworkRemoteRequest request = new NetworkRemoteRequest
        {
            EntityId = player.entityId,
            RequestId = DateTime.UtcNow.Ticks,
            Operation = NetworkRequestOperation.TraderVoidSell,
            Value = stack.itemValue.Clone(),
            Count = stack.count,
            SourceLocation = location,
            Slot = slotNumber,
            TraderNetworkSource = networkSource,
            TraderPersonalPayout = _personalPayoutMode
        };
        NetworkRemoteResult result = ExecuteVoidSell(player, null, request);
        completed?.Invoke(result);
        return true;
    }

    public static NetworkRemoteResult ExecuteVoidSell(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        if (!NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TraderEnabled)) return Failure(request, "TraderDisabled");
        if (!(bool)player || request?.Value == null || request.Value.type <= 0 || request.Count <= 0)
            return Failure(request, "InvalidTraderSale");

        ItemValue value = request.Value.Clone();
        ItemClass itemClass = value.ItemClass;
        if (itemClass == null || !IsSellable(value, itemClass)) return Failure(request, "ItemNotSellable");
        EntityPlayer pricePlayer = request.TraderAutomation
            ? TraderAutomationService.ResolveOnlineAdministrator(GameManager.Instance?.World, PlayerNetworkDirectory.GetNetworkId(player))
            : player;
        int price;
        try { price = GetVirtualSellPrice(pricePlayer, value, request.Count, itemClass); }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not calculate virtual trader sale price. player=" + player.entityId + "; requestId=" + request.RequestId + "; item=" + value.type + "; exception=" + ex);
            return Failure(request, "TraderPriceUnavailable");
        }
        if (price <= 0) return Failure(request, "SaleValueTooLow");

        MemoryStream originalBag = null;
        MemoryStream originalToolbelt = null;
        MemoryStream updatedBag = null;
        MemoryStream updatedToolbelt = null;
        if (!request.TraderNetworkSource)
        {
            if (!TryGetPlayerInventory(player, playerData, out Bag bag, out ItemStackGrid toolbelt, out byte selectedSlot,
                    out originalBag, out originalToolbelt))
                return Failure(request, "PlayerInventoryUnavailable");
            if (!TryRemoveStack(bag, toolbelt, request.SourceLocation, request.Slot, value, request.Count))
                return Failure(request, "SaleSourceChanged");
            updatedBag = NetworkRpcServer.WriteBag(bag);
            updatedToolbelt = WriteToolbelt(toolbelt, selectedSlot);
        }
        else if (NetworkStorage.GetCount(player, value) < request.Count)
        {
            return Failure(request, "NetworkSaleSourceChanged");
        }

        bool sourceDebited = false;
        if (request.TraderNetworkSource)
        {
            sourceDebited = NetworkStorage.Remove(player, value, request.Count) == request.Count;
        }
        else
        {
            sourceDebited = ApplySnapshots(player, playerData, updatedBag, updatedToolbelt);
        }
        if (!sourceDebited) return Failure(request, request.TraderNetworkSource ? "NetworkSaleSourceChanged" : "PlayerInventoryUpdateFailed");

        MemoryStream sourceBag = updatedBag;
        MemoryStream sourceToolbelt = updatedToolbelt;

        ItemValue currency = ItemClass.GetItem(TraderInfo.CurrencyItem, false);
        bool creditedToPlayer = request.TraderPersonalPayout
            && TryCreditPersonalPayout(player, playerData, currency, price, out updatedBag, out updatedToolbelt);
        bool creditedToNetwork = !creditedToPlayer && !(request.TraderPersonalPayout && request.TraderAutomation)
            && currency != null && !currency.IsEmpty()
            && NetworkStorage.AddStack(player, new ItemStack(currency.Clone(), price));
        if (!creditedToPlayer && !creditedToNetwork)
        {
            bool restored = request.TraderNetworkSource
                ? NetworkStorage.AddStack(player, new ItemStack(value, request.Count))
                : RestoreSnapshots(player, playerData, originalBag, originalToolbelt);
            NetworkLog.Error("Virtual trader sale could not credit network proceeds; source compensation=" + restored
                + "; player=" + player.entityId + "; requestId=" + request.RequestId + "; item=" + value.type + "; count=" + request.Count + "; price=" + price);
            return Failure(request, "SaleCreditFailed");
        }

        try
        {
            if (!request.TraderAutomation)
                player.Progression?.AddLevelExp(Math.Max(price, 1), "_xpFromSelling", (Progression.XPTypes)4, true, true, -1, null);
        }
        catch (Exception ex)
        {
            NetworkLog.Debug("Virtual trader sale XP award failed after a committed transaction. player=" + player.entityId + "; requestId=" + request.RequestId + "; exception=" + ex);
        }
        NetworkLog.Debug("Virtual trader sale committed. player=" + player.entityId + "; requestId=" + request.RequestId
            + "; networkSource=" + request.TraderNetworkSource + "; item=" + value.type + "; count=" + request.Count + "; proceeds=" + price);
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            Amount = price,
            Value = currency.Clone(),
            BagData = (creditedToPlayer ? updatedBag : sourceBag)?.ToArray(),
            InventoryData = (creditedToPlayer ? updatedToolbelt : sourceToolbelt)?.ToArray()
        };
    }

    private static bool TryCreditPersonalPayout(EntityPlayer player, PlayerDataFile playerData, ItemValue currency, int amount,
        out MemoryStream updatedBag, out MemoryStream updatedToolbelt)
    {
        updatedBag = null;
        updatedToolbelt = null;
        if (currency == null || currency.IsEmpty() || !TryGetPlayerInventory(player, playerData, out Bag bag,
                out ItemStackGrid toolbelt, out byte selectedSlot, out MemoryStream originalBag, out MemoryStream originalToolbelt)) return false;
        if (!bag.AddItem(new ItemStack(currency.Clone(), amount)))
        {
            NetworkLog.Debug("Virtual trader proceeds fell back to the network because the player's backpack could not fit the full payout. player=" + player.entityId + "; amount=" + amount);
            return false;
        }
        MemoryStream candidateBag = NetworkRpcServer.WriteBag(bag);
        MemoryStream candidateToolbelt = WriteToolbelt(toolbelt, selectedSlot);
        if (!ApplySnapshots(player, playerData, candidateBag, candidateToolbelt))
        {
            if (!RestoreSnapshots(player, playerData, originalBag, originalToolbelt))
                NetworkLog.Error("Virtual trader personal payout could not restore the original player inventory before network fallback. player=" + player.entityId);
            NetworkLog.Error("Virtual trader personal payout inventory update failed; attempting shared network credit. player=" + player.entityId + "; amount=" + amount);
            return false;
        }
        updatedBag = candidateBag;
        updatedToolbelt = candidateToolbelt;
        NetworkLog.Debug("Virtual trader proceeds credited to player backpack. player=" + player.entityId + "; amount=" + amount);
        return true;
    }

    private static bool TryGetPlayerInventory(EntityPlayer player, PlayerDataFile playerData, out Bag bag,
        out ItemStackGrid toolbelt, out byte selectedSlot, out MemoryStream bagSnapshot, out MemoryStream toolbeltSnapshot)
    {
        bag = null;
        toolbelt = null;
        selectedSlot = 0;
        bagSnapshot = playerData?.bagData;
        toolbeltSnapshot = playerData?.inventoryData;
        if ((bagSnapshot == null || toolbeltSnapshot == null) && player is EntityPlayerLocal localPlayer)
        {
            bagSnapshot = StreamUtils.ToBlob(writer => localPlayer.bag.Write(writer, StreamModeWrite.Persistency));
            toolbeltSnapshot = StreamUtils.ToBlob(writer => localPlayer.inventory.Write(writer, StreamModeWrite.Persistency));
        }
        if (bagSnapshot == null || toolbeltSnapshot == null) return false;
        try
        {
            bag = NetworkRpcServer.ReadBag(bagSnapshot);
            using (MemoryStream input = new MemoryStream(toolbeltSnapshot.ToArray(), writable: false))
            using (PooledBinaryReader reader = MemoryPools.poolBinaryReader.AllocSync(false))
            {
                reader.SetBaseStream(input);
                if (reader.ReadByte() != 1) return false;
                toolbelt = ItemStackGrid.Read(reader, StreamModeRead.Persistency);
                selectedSlot = reader.ReadByte();
                if (input.Position != input.Length) return false;
            }
            return bag != null && toolbelt != null;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not parse player inventory for virtual trader sale. player=" + player.entityId + "; exception=" + ex);
            return false;
        }
    }

    private static bool TryRemoveStack(Bag bag, ItemStackGrid toolbelt, XUiC_ItemStack.StackLocationTypes location,
        int slot, ItemValue expected, int count)
    {
        ItemStack stack;
        if (location == XUiC_ItemStack.StackLocationTypes.Backpack)
        {
            if (slot < 0 || slot >= bag.SlotCount || bag.LockedSlots != null && slot < bag.LockedSlots.Length && bag.LockedSlots[slot]) return false;
            stack = bag.ItemGrid[slot];
        }
        else if (location == XUiC_ItemStack.StackLocationTypes.ToolBelt)
        {
            if (slot < 0 || slot >= toolbelt.Length) return false;
            stack = toolbelt[slot];
        }
        else return false;

        if (stack == null || stack.IsEmpty() || stack.count < count
            || !NetworkStorage.NormalizeKey(stack.itemValue).Equals(NetworkStorage.NormalizeKey(expected))) return false;
        stack.count -= count;
        if (location == XUiC_ItemStack.StackLocationTypes.Backpack)
            bag.ItemGrid.SetItem(slot, stack.count > 0 ? stack : ItemStack.Empty.Clone());
        else
            toolbelt.SetItem(slot, stack.count > 0 ? stack : ItemStack.Empty.Clone());
        return true;
    }

    private static MemoryStream WriteToolbelt(ItemStackGrid toolbelt, byte selectedSlot)
    {
        using (MemoryStream output = new MemoryStream())
        using (PooledBinaryWriter writer = MemoryPools.poolBinaryWriter.AllocSync(false))
        {
            writer.SetBaseStream(output);
            BinaryWriter binary = writer;
            binary.Write((byte)1);
            toolbelt.Write(writer, StreamModeWrite.Persistency);
            binary.Write(selectedSlot);
            writer.Flush();
            return new MemoryStream(output.ToArray(), writable: false);
        }
    }

    private static bool ApplySnapshots(EntityPlayer player, PlayerDataFile playerData, MemoryStream bag, MemoryStream toolbelt)
    {
        if (bag == null || toolbelt == null) return false;
        try
        {
            NetworkRpcServer.ApplyPlayerInventorySnapshot(player, bag, toolbelt: false);
            NetworkRpcServer.ApplyPlayerInventorySnapshot(player, toolbelt, toolbelt: true);
            if (playerData != null)
            {
                playerData.bagData = bag;
                playerData.inventoryData = toolbelt;
                playerData.bModifiedSinceLastSave = true;
            }
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not apply player inventory after virtual trader sale. player=" + player.entityId + "; exception=" + ex);
            return false;
        }
    }

    private static bool RestoreSnapshots(EntityPlayer player, PlayerDataFile playerData, MemoryStream bag, MemoryStream toolbelt)
    {
        if (bag == null || toolbelt == null) return false;
        bool restored = ApplySnapshots(player, playerData, bag, toolbelt);
        if (restored) NetworkLog.Debug("Player inventory was restored after virtual trader sale credit failed. player=" + player.entityId);
        return restored;
    }

    internal static int GetVirtualSellPrice(EntityPlayer player, ItemValue value, int count, ItemClass itemClass)
    {
        int bundleSize;
        float economicValue;
        if (itemClass.IsBlock())
        {
            Block block = Block.list[value.type];
            economicValue = block.EconomicValue * block.EconomicSellScale;
            bundleSize = block.EconomicBundleSize;
        }
        else
        {
            economicValue = itemClass.EconomicValue * itemClass.EconomicSellScale;
            economicValue = EffectManager.GetValue(PassiveEffects.EconomicValue, value, economicValue, player);
            bundleSize = itemClass.EconomicBundleSize;
        }
        if (economicValue == 0f || bundleSize <= 0) return 0;
        float price;
        if (value.HasQuality)
        {
            price = economicValue * TraderInfo.SellMarkdown;
            float minimum = itemClass.TraderQualityMinMod;
            float maximum = itemClass.TraderQualityMaxMod;
            float quality = ((int)value.Quality - 1f) / 5f;
            price *= minimum <= 0f && maximum <= 0f
                ? Mathf.Lerp(TraderInfo.QualityMinMod, TraderInfo.QualityMaxMod, quality)
                : Mathf.Lerp(minimum, maximum, quality);
            price *= value.PercentUsesLeft;
        }
        else if (itemClass.HasSubItems)
        {
            price = 0f;
            for (int i = 0; i < value.ModificationCount; i++)
            {
                ItemValue modification = value.GetModification(i);
                if (!modification.IsEmpty() && modification.ItemClass != null)
                    price += GetVirtualSellPrice(player, modification, 1, modification.ItemClass);
            }
        }
        else price = economicValue * TraderInfo.SellMarkdown;

        price += price * EffectManager.GetValue(PassiveEffects.BarteringSelling, null, 0f, player, null, itemClass.ItemTags);
        int basePrice = (int)(price * (float)(count / bundleSize));
        return Mathf.CeilToInt(basePrice * SandboxOptionManager.GetFloat(SandboxOptions.SandboxOptions.TraderSellPrices));
    }

    private static bool IsSellable(ItemValue value, ItemClass itemClass)
    {
        return itemClass.IsBlock() ? Block.list[value.type].SellableToTrader : itemClass.SellableToTrader;
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
}
