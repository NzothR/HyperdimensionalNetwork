using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_ContainerStandardControls))]
internal static class ContainerDepositPatch
{
    [HarmonyPostfix, HarmonyPatch("Init")]
    public static void InitPostfix(XUiC_ContainerStandardControls __instance)
    {
        XUiController lootButton = __instance.GetChildById("btnHyperdimensionalLootAll");
        if (lootButton != null)
        {
            if (lootButton.ViewComponent != null)
                lootButton.ViewComponent.ToolTip = string.Format(
                    Localization.Get("HyperdimensionalNetworkQuickDepositHotkey"),
                    NetworkHotkeyService.GetDepositKeyDisplay());
            lootButton.OnPress += (sender, mouse) => DepositFromControls(__instance);
        }
        XUiController backpackButton = __instance.GetChildById("btnHyperdimensionalBackpackAll");
        if (backpackButton != null) backpackButton.OnPress += (sender, mouse) => DepositBackpack(FindParent<XUiC_BackpackWindow>(__instance));
        XUiController trashButton = __instance.GetChildById("btnHyperdimensionalTrash");
        if (trashButton != null) trashButton.OnPress += (sender, mouse) =>
        {
            EntityPlayerLocal player = __instance.xui?.playerUI?.entityPlayer;
            if (!(bool)player || !AutoRestockClient.IsFeatureEnabled(player, NetworkFeatureSetting.TrashEnabled)) return;
            ManualTrashService.Toggle(__instance.xui);
        };
    }

    internal static bool DepositFromControls(XUiC_ContainerStandardControls controls)
    {
        if (controls?.MoveAllowed == null) return false;
        controls.UpdateLockedSlotStates?.Invoke(controls);
        if (!controls.MoveAllowed(out XUiController parentWindow, out XUiC_ItemStackGrid source, out _)
            || source == null)
        {
            NetworkLog.Debug("Container deposit skipped because the standard controls did not provide a source grid. controls=" + controls.GetType().Name);
            return false;
        }

        if (source is XUiC_LootContainer)
        {
            XUiC_LootWindow lootWindow = parentWindow as XUiC_LootWindow ?? FindParent<XUiC_LootWindow>(controls);
            if (lootWindow == null)
            {
                NetworkLog.Debug("Container deposit found a loot grid without its parent loot window. source=" + source.GetType().Name);
                return false;
            }
            return DepositLootFromWindow(lootWindow);
        }

        if (source is XUiC_BagContainer bagContainer)
        {
            XUiC_BagStorageWindowGroup bagWindow = FindParent<XUiC_BagStorageWindowGroup>(controls);
            if (bagWindow?.Entity?.bag == null)
            {
                NetworkLog.Debug("Container deposit found a bag grid without an entity-backed storage window. source=" + source.GetType().Name);
                return false;
            }
            return DepositEntityBagFromWindow(bagContainer, bagWindow.Entity);
        }

        if (NetworkRemoteClient.IsRemoteClient)
        {
            NetworkLog.Debug("Remote container deposit skipped because this modded grid has no supported server-side storage identity. source=" + source.GetType().FullName);
            return false;
        }

        bool moved = DepositGrid(controls.xui, source, controls.LockedSlots);
        controls.MoveAllDone?.Invoke(true, moved);
        NetworkLog.Debug("Local generic container deposit completed. source=" + source.GetType().FullName + "; moved=" + moved);
        return moved;
    }

    internal static bool DepositLootFromWindow(XUiC_LootWindow window)
    {
        XUiC_LootContainer grid = window?.GetChildByType<XUiC_LootContainer>();
        EntityPlayer player = window?.xui?.playerUI?.entityPlayer;
        if (grid == null || !(bool)player) return false;
        if (NetworkRemoteClient.IsRemoteClient && grid.localTileEntity == null)
        {
            NetworkLog.Debug("Remote quick deposit skipped because the active loot window has no network-authoritative tile entity.");
            return false;
        }
        if (NetworkRemoteClient.IsRemoteClient && grid.localTileEntity != null)
        {
            bool queued = NetworkRemoteClient.DepositLootAll(player, grid.localTileEntity.ToWorldPos(), result =>
            {
                bool moved = result.Success && result.Amount > 0;
                if (!result.Success) NetworkLog.Debug("Remote loot deposit rejected. error=" + result.Error + "; requestId=" + result.RequestId);
                else
                {
                    NetworkLog.Debug("Remote loot deposit confirmed. amount=" + result.Amount + "; requestId=" + result.RequestId);
                    if (moved) RefreshRemoteLootContainer(grid);
                }
                XUiC_ContainerStandardControls remoteControls = window?.GetChildByType<XUiC_ContainerStandardControls>();
                if (remoteControls?.MoveAllDone != null) remoteControls.MoveAllDone(true, moved);
                NetworkLog.Debug("Quick deposit from container completed. moved=" + moved);
            });
            if (!queued)
            {
                XUiC_ContainerStandardControls failedControls = window.GetChildByType<XUiC_ContainerStandardControls>();
                if (failedControls?.MoveAllDone != null) failedControls.MoveAllDone(true, false);
                NetworkLog.Error("Remote loot deposit could not be queued; container was left unchanged.");
            }
            return queued;
        }
        bool moved = DepositGrid(window?.xui, grid, grid.localTileEntity?.ItemGrid.SlotLocks);
        XUiC_ContainerStandardControls controls = window?.GetChildByType<XUiC_ContainerStandardControls>();
        if (controls?.MoveAllDone != null) controls.MoveAllDone(true, moved);
        NetworkLog.Debug("Quick deposit from container completed. moved=" + moved);
        return moved;
    }

    private static void RefreshRemoteLootContainer(XUiC_LootContainer grid)
    {
        TEFeatureStorage storage = grid?.localTileEntity;
        if (storage?.ItemGrid == null) return;
        PackedBoolArray lockedSlots = storage.ItemGrid.SlotLocks;
        int cleared = 0;
        for (int i = 0; i < storage.ItemGrid.Length; i++)
        {
            if (lockedSlots != null && i < lockedSlots.Length && lockedSlots[i]) continue;
            ItemStack stack = storage.ItemGrid[i];
            if (stack == null || stack.IsEmpty()) continue;
            if (!NetworkItemPolicy.CanEnterOrDestroy(stack.itemValue)) continue;
            // The server has already committed the transfer. Update only the local mirror;
            // SetModified would send a stale client-side tile entity back to the server.
            storage.UpdateSlot(i, ItemStack.Empty.Clone());
            cleared++;
        }
        NetworkLog.Debug("Remote loot container mirror refreshed after confirmed deposit. position="
            + storage.ToWorldPos() + "; slotsCleared=" + cleared);
    }

    internal static bool DepositEntityBagFromWindow(XUiC_BagContainer bagContainer, Entity bagEntity)
    {
        EntityPlayer player = bagContainer?.xui?.playerUI?.entityPlayer;
        Bag bag = bagContainer?.Bag;
        if (!(bool)player || bag == null || bagEntity?.bag == null || !ReferenceEquals(bag, bagEntity.bag)) return false;

        if (NetworkRemoteClient.IsRemoteClient)
        {
            bool queued = NetworkRemoteClient.DepositEntityBagAll(player, bagEntity.entityId, result =>
            {
                bool moved = result.Success && result.Amount > 0;
                if (result.Success)
                {
                    if (NetworkInventorySync.ApplyEntityBag(bagEntity, result.EntityBagData)) bagContainer.OnBagItemChangedInternal();
                    else NetworkLog.Error("Entity bag deposit succeeded but its server snapshot could not be applied. entity=" + bagEntity.entityId + "; type=" + bagEntity.GetType().Name + "; requestId=" + result.RequestId);
                    NetworkLog.Debug("Remote entity bag deposit confirmed. entity=" + bagEntity.entityId + "; type=" + bagEntity.GetType().Name + "; amount=" + result.Amount + "; requestId=" + result.RequestId);
                }
                else NetworkLog.Debug("Remote entity bag deposit rejected. entity=" + bagEntity.entityId + "; type=" + bagEntity.GetType().Name + "; error=" + result.Error + "; requestId=" + result.RequestId);
                XUiC_ContainerStandardControls controls = bagContainer.GetChildByType<XUiC_ContainerStandardControls>();
                if (controls?.MoveAllDone != null) controls.MoveAllDone(true, moved);
            });
            if (!queued)
            {
                XUiC_ContainerStandardControls controls = bagContainer.GetChildByType<XUiC_ContainerStandardControls>();
                if (controls?.MoveAllDone != null) controls.MoveAllDone(true, false);
                NetworkLog.Error("Remote entity bag deposit could not be queued; the bag was left unchanged. entity=" + bagEntity.entityId + "; type=" + bagEntity.GetType().Name);
            }
            return queued;
        }

        bool localMoved = DepositGrid(bagContainer.xui, bagContainer, bag.LockedSlots);
        XUiC_ContainerStandardControls localControls = bagContainer.GetChildByType<XUiC_ContainerStandardControls>();
        if (localControls?.MoveAllDone != null) localControls.MoveAllDone(true, localMoved);
        NetworkLog.Debug("Local entity bag deposit completed. entity=" + bagEntity.entityId + "; type=" + bagEntity.GetType().Name + "; moved=" + localMoved);
        return localMoved;
    }

    private static void DepositBackpack(XUiC_BackpackWindow window)
    {
        XUiC_Backpack grid = window?.GetChildByType<XUiC_Backpack>();
        PackedBoolArray locked = window?.xui?.playerUI?.entityPlayer?.bag?.LockedSlots;
        EntityPlayer player = window?.xui?.playerUI?.entityPlayer;
        if (NetworkRemoteClient.IsRemoteClient && (bool)player)
        {
            bool queued = NetworkRemoteClient.DepositBackpackAll(player, result =>
            {
                bool moved = result.Success && result.Amount > 0;
                if (result.Success)
                {
                    if (NetworkInventorySync.Apply(player as EntityPlayerLocal, result)) grid?.RefreshBackpackSlots();
                    (window?.xui?.GetChildByType<XUiC_HyperdimensionalGrid>())?.Reload();
                    NetworkLog.Debug("Remote backpack deposit confirmed. amount=" + result.Amount + "; requestId=" + result.RequestId);
                }
                else NetworkLog.Debug("Remote backpack deposit rejected. error=" + result.Error + "; requestId=" + result.RequestId);
                NetworkLog.Debug("Quick deposit from backpack completed. moved=" + moved);
            });
            if (!queued) NetworkLog.Error("Remote backpack deposit could not be queued; backpack was left unchanged.");
            return;
        }
        bool moved = DepositGrid(window?.xui, grid, locked);
        NetworkLog.Debug("Quick deposit from backpack completed. moved=" + moved);
    }

    private static bool DepositGrid(XUi xui, XUiC_ItemStackGrid source, PackedBoolArray locked)
    {
        if (source == null || !(bool)xui?.playerUI?.entityPlayer) return false;
        XUiC_ItemStack[] slots = source.GetItemStackControllers();
        List<StorageAddition> additions = new List<StorageAddition>();
        List<XUiC_ItemStack> changed = new List<XUiC_ItemStack>();
        List<ItemStack> replacement = new List<ItemStack>();
        for (int i = 0; i < slots.Length; i++)
        {
            if (locked != null && i < locked.Length && locked[i]) continue;
            if (slots[i] == null) continue;
            ItemStack stack = slots[i]?.ItemStack;
            if (stack == null || stack.IsEmpty()) continue;
            if (!NetworkItemPolicy.CanEnterOrDestroy(stack.itemValue)) continue;
            additions.Add(new StorageAddition { Value = stack.itemValue.Clone(), Count = stack.count });
            changed.Add(slots[i]);
            replacement.Add(ItemStack.Empty.Clone());
        }
        if (additions.Count == 0 || !NetworkStorage.AddBatch(xui.playerUI.entityPlayer, additions)) return false;
        for (int i = 0; i < changed.Count; i++)
        {
            XUiC_ItemStack slot = changed[i];
            slot.ItemStack = replacement[i];
            source.HandleSlotChangedEvent(slot.SlotNumber, replacement[i]);
        }
        if (source is XUiC_Backpack backpack) backpack.RefreshBackpackSlots();
        if (source is XUiC_Toolbelt toolbelt) toolbelt.PlayerInventory_OnToolbeltItemsChanged();
        return true;
    }

    private static T FindParent<T>(XUiController controller) where T : XUiController
    {
        for (XUiController current = controller; current != null; current = current.Parent) if (current is T found) return found;
        return null;
    }
}
