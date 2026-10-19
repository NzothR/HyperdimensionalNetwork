using System;
using System.IO;
using SandboxOptions;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class TraderTransactionService
{
    private static bool _voidSaleMode;

    public static bool VoidSaleMode => _voidSaleMode;

    public static bool CanSell(ItemValue value)
    {
        return value != null && !value.IsEmpty() && value.type > 0 && value.ItemClass != null && IsSellable(value, value.ItemClass);
    }

    public static bool ToggleVoidSaleMode()
    {
        _voidSaleMode = !_voidSaleMode;
        NetworkLog.Debug("Virtual trader sale mode changed. enabled=" + _voidSaleMode);
        return _voidSaleMode;
    }

    public static void DisableVoidSaleMode()
    {
        _voidSaleMode = false;
    }

    public static bool SellFromSlot(EntityPlayerLocal player, XUiC_ItemStack slot, bool networkSource, Action<NetworkRemoteResult> completed)
    {
        if (!(bool)player || slot == null || slot.ItemStack == null || slot.ItemStack.IsEmpty()) return false;
        if (NetworkRemoteClient.IsMutationPending) return true;
        ItemStack stack = slot.ItemStack.Clone();
        XUiC_ItemStack.StackLocationTypes location = networkSource
            ? XUiC_ItemStack.StackLocationTypes.ToolBelt
            : slot.StackLocation;
        int slotNumber = slot.SlotNumber;
        if (!networkSource && location != XUiC_ItemStack.StackLocationTypes.Backpack && location != XUiC_ItemStack.StackLocationTypes.ToolBelt) return false;
        if (NetworkRemoteClient.IsRemoteClient)
            return NetworkRemoteClient.RequestTraderVoidSell(player, stack.itemValue, stack.count, networkSource, location, slotNumber, completed);

        NetworkRemoteRequest request = new NetworkRemoteRequest
        {
            EntityId = player.entityId,
            RequestId = DateTime.UtcNow.Ticks,
            Operation = NetworkRequestOperation.TraderVoidSell,
            Value = stack.itemValue.Clone(),
            Count = stack.count,
            SourceLocation = location,
            Slot = slotNumber,
            TraderNetworkSource = networkSource
        };
        NetworkRemoteResult result = ExecuteVoidSell(player, null, request);
        completed?.Invoke(result);
        return true;
    }

    public static NetworkRemoteResult ExecuteVoidSell(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        if (!(bool)player || request?.Value == null || request.Value.type <= 0 || request.Count <= 0)
            return Failure(request, "InvalidTraderSale");

        ItemValue value = request.Value.Clone();
        ItemClass itemClass = value.ItemClass;
        if (itemClass == null || !IsSellable(value, itemClass)) return Failure(request, "ItemNotSellable");
        int price;
        try { price = GetVirtualSellPrice(player, value, request.Count, itemClass); }
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

        ItemValue currency = ItemClass.GetItem(TraderInfo.CurrencyItem, false);
        if (currency == null || currency.IsEmpty() || !NetworkStorage.AddStack(player, new ItemStack(currency.Clone(), price)))
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
            BagData = request.TraderNetworkSource ? null : updatedBag?.ToArray(),
            InventoryData = request.TraderNetworkSource ? null : updatedToolbelt?.ToArray()
        };
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

    private static int GetVirtualSellPrice(EntityPlayer player, ItemValue value, int count, ItemClass itemClass)
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
        float configured = price * (float)(count / bundleSize) * SandboxOptionManager.GetFloat(SandboxOptions.SandboxOptions.TraderSellPrices);
        return Mathf.CeilToInt((int)configured);
    }

    private static bool IsSellable(ItemValue value, ItemClass itemClass)
    {
        return itemClass.IsBlock() ? Block.list[value.type].SellableToTrader : itemClass.SellableToTrader;
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
}
