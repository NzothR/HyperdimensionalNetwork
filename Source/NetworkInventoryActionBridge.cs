using System;

namespace HyperdimensionalNetwork;

internal static class NetworkInventoryActionBridge
{
    internal static void ActivateExisting(BaseItemActionEntry action, XUiC_HyperdimensionalSlot source,
        XUiC_ItemStack target)
    {
        XUiController previous = action.ItemController;
        try
        {
            action.ItemController = target;
            action.OnActivated();
        }
        finally { action.ItemController = previous ?? source; }
    }

    internal static void MoveOneToBackpack(XUiC_HyperdimensionalSlot networkSlot, string actionName,
        Action<XUiC_ItemStack> activate)
    {
        EntityPlayerLocal player = networkSlot?.xui?.playerUI?.entityPlayer;
        ItemStack source = networkSlot?.ItemStack?.Clone();
        if (!(bool)player || source == null || source.IsEmpty() || activate == null) return;
        XUi xui = networkSlot.xui;

        if (NetworkRemoteClient.IsRemoteClient)
        {
            bool sent = NetworkRemoteClient.Withdraw(player, source.itemValue, 1, toBackpack: true, result =>
            {
                if (!result.Success)
                {
                    NetworkLog.Debug("Network item action extraction rejected. action=" + actionName + "; player="
                        + player.entityId + "; item=" + source.itemValue.type + "; error=" + result.Error);
                    xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
                    return;
                }
                if (result.Amount != 1 || result.BagData == null || !NetworkInventorySync.Apply(player, result, suppressRestockEvents: true))
                {
                    NetworkLog.Error("Network item action extraction could not be applied locally. action=" + actionName
                        + "; player=" + player.entityId + "; item=" + source.itemValue.type + "; amount=" + result.Amount
                        + "; slot=" + result.UpdatedSourceCount + "; requestId=" + result.RequestId);
                    xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
                    return;
                }
                XUiC_ItemStack physicalSlot = FindBackpackController(xui, result.UpdatedSourceCount, source.itemValue);
                if (physicalSlot == null)
                {
                    NetworkLog.Error("Network item action extraction slot was not found after server confirmation. action="
                        + actionName + "; player=" + player.entityId + "; item=" + source.itemValue.type
                        + "; slot=" + result.UpdatedSourceCount + "; requestId=" + result.RequestId);
                    return;
                }
                ActivateExtracted(player, xui, physicalSlot, source.itemValue, actionName, activate);
            });
            if (!sent) NetworkLog.Debug("Network item action extraction was not queued. action=" + actionName
                + "; player=" + player.entityId + "; item=" + source.itemValue.type);
            return;
        }

        int removed = NetworkStorage.Remove(player, source.itemValue, 1);
        if (removed != 1)
        {
            NetworkLog.Debug("Local network item action extraction failed. action=" + actionName + "; player="
                + player.entityId + "; item=" + source.itemValue.type + "; removed=" + removed);
            xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
            return;
        }
        int insertedSlot = -1;
        bool inserted = false;
        Bag backpack = xui.PlayerInventory?.Backpack;
        ItemStack[] before = backpack?.ItemGrid == null ? null : new ItemStack[backpack.ItemGrid.Length];
        if (before != null)
            for (int i = 0; i < before.Length; i++) before[i] = backpack.ItemGrid[i]?.Clone() ?? ItemStack.Empty.Clone();
        try
        {
            inserted = backpack?.AddItem(new ItemStack(source.itemValue.Clone(), 1)) == true;
            if (inserted && before != null)
            {
                for (int i = 0; i < before.Length; i++)
                {
                    ItemStack after = backpack.ItemGrid[i];
                    bool changed = after != null && !after.IsEmpty() &&
                        (before[i].IsEmpty() || !NetworkStorage.NormalizeKey(before[i].itemValue).Equals(NetworkStorage.NormalizeKey(after.itemValue))
                            || after.count > before[i].count);
                    if (changed && NetworkStorage.NormalizeKey(after.itemValue).Equals(NetworkStorage.NormalizeKey(source.itemValue)))
                    {
                        insertedSlot = i;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) { NetworkLog.Error("Could not place extracted network item in backpack. action=" + actionName
            + "; player=" + player.entityId + "; item=" + source.itemValue.type + "; exception=" + ex); }
        if (!inserted)
        {
            if (!NetworkStorage.AddStack(player, new ItemStack(source.itemValue.Clone(), 1)))
                NetworkLog.Error("Network item action extraction rollback failed. action=" + actionName + "; player="
                    + player.entityId + "; item=" + source.itemValue.type);
            NetworkLog.Debug("Network item action extraction rejected because the backpack has no space. action="
                + actionName + "; player=" + player.entityId + "; item=" + source.itemValue.type);
            AutoRestockClient.RefreshBaseline(player);
            xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
            return;
        }
        XUiC_ItemStack target = FindBackpackController(xui, insertedSlot, source.itemValue);
        if (target == null)
        {
            NetworkLog.Error("Local network item action extraction slot was not found. action=" + actionName
                + "; player=" + player.entityId + "; item=" + source.itemValue.type + "; slot=" + insertedSlot);
            AutoRestockClient.RefreshBaseline(player);
            xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
            return;
        }
        ActivateExtracted(player, xui, target, source.itemValue, actionName, activate);
    }

    internal static void DropStack(XUiC_HyperdimensionalSlot slot)
    {
        EntityPlayerLocal player = slot?.xui?.playerUI?.entityPlayer;
        ItemStack source = slot?.ItemStack?.Clone();
        if (!(bool)player || source == null || source.IsEmpty()) return;
        if (!source.itemValue.ItemClass.CanDrop(source.itemValue))
        {
            GameManager.ShowTooltip(player, "This item cannot be dropped.");
            return;
        }

        if (NetworkRemoteClient.IsRemoteClient)
        {
            bool sent = NetworkRemoteClient.DropNetworkItem(player, source.itemValue, source.count, result =>
            {
                if (!result.Success)
                    NetworkLog.Debug("Remote network item drop rejected. player=" + player.entityId + "; item="
                        + source.itemValue.type + "; count=" + source.count + "; error=" + result.Error);
                else
                {
                    player.PlayOneShot("itemdropped");
                    NetworkLog.Debug("Remote network item drop confirmed. player=" + player.entityId + "; item="
                        + source.itemValue.type + "; count=" + result.Amount + "; requestId=" + result.RequestId);
                }
                slot.xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
            });
            if (!sent) NetworkLog.Debug("Remote network item drop was not queued. player=" + player.entityId
                + "; item=" + source.itemValue.type + "; count=" + source.count);
            return;
        }

        NetworkRemoteResult result = DropOnWorld(player.world, player, new NetworkRemoteRequest
        {
            EntityId = player.entityId,
            RequestId = DateTime.UtcNow.Ticks,
            Operation = NetworkRequestOperation.DropNetworkItem,
            Value = source.itemValue.Clone(),
            Count = source.count
        });
        if (result.Success) player.PlayOneShot("itemdropped");
        else NetworkLog.Debug("Local network item drop rejected. player=" + player.entityId + "; item="
            + source.itemValue.type + "; count=" + source.count + "; error=" + result.Error);
        slot.xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
    }

    internal static NetworkRemoteResult DropOnWorld(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        if (!(bool)player || request?.Value == null || request.Value.ItemClass == null || request.Count <= 0
            || !request.Value.ItemClass.CanDrop(request.Value)) return Failure(request, "InvalidNetworkDrop");
        if (NetworkStorage.GetCount(player, request.Value) < request.Count) return Failure(request, "NetworkDropUnavailable");
        int removed = NetworkStorage.Remove(player, request.Value, request.Count);
        if (removed != request.Count)
        {
            if (removed > 0 && !NetworkStorage.AddStack(player, new ItemStack(request.Value.Clone(), removed)))
                NetworkLog.Error("Network drop debit rollback failed. player=" + player.entityId + "; item="
                    + request.Value.type + "; requested=" + request.Count + "; removed=" + removed);
            return Failure(request, "NetworkDropUnavailable");
        }
        try
        {
            GameManager.Instance.ItemDropServer(new ItemStack(request.Value.Clone(), removed), player.GetDropPosition(),
                UnityEngine.Vector3.zero, player.entityId);
        }
        catch (Exception ex)
        {
            if (!NetworkStorage.AddStack(player, new ItemStack(request.Value.Clone(), removed)))
                NetworkLog.Error("Network drop rollback failed after world spawn exception. player=" + player.entityId
                    + "; item=" + request.Value.type + "; count=" + removed + "; exception=" + ex);
            return Failure(request, "NetworkDropFailed");
        }
        NetworkLog.Debug("Network item dropped into the world. player=" + player.entityId + "; requestId="
            + request.RequestId + "; item=" + request.Value.type + "; count=" + removed);
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = removed };
    }

    private static void ActivateExtracted(EntityPlayerLocal player, XUi xui, XUiC_ItemStack target, ItemValue value,
        string actionName, Action<XUiC_ItemStack> activate)
    {
        try
        {
            using (AutoRestockClient.SuppressInventoryEvents(player))
            {
                AutoRestockClient.RefreshBaseline(player);
                activate(target);
                AutoRestockClient.RefreshBaseline(player);
            }
            NetworkLog.Debug("Network item action invoked on extracted backpack item. action=" + actionName
                + "; player=" + player.entityId + "; item=" + value.type + "; slot=" + target.SlotNumber);
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Network item action failed after extracting its source. action=" + actionName
                + "; player=" + player.entityId + "; item=" + value.type + "; slot=" + target.SlotNumber
                + "; exception=" + ex);
        }
        finally { xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload(); }
    }

    private static XUiC_ItemStack FindBackpackController(XUi xui, int index, ItemValue expected)
    {
        XUiC_ItemStack[] slots = xui?.GetChildByType<XUiC_Backpack>()?.GetItemStackControllers();
        if (slots == null || index < 0 || index >= slots.Length) return null;
        ItemStack stack = slots[index]?.ItemStack;
        return stack != null && !stack.IsEmpty()
            && NetworkStorage.NormalizeKey(stack.itemValue).Equals(NetworkStorage.NormalizeKey(expected))
            ? slots[index] : null;
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
}
