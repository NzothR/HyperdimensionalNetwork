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
        if (lootButton != null) lootButton.OnPress += (sender, mouse) =>
        {
            XUiC_LootWindow lootWindow = FindParent<XUiC_LootWindow>(__instance);
            if (lootWindow != null)
            {
                DepositLootFromWindow(lootWindow);
                return;
            }
            XUiC_BagContainer bagContainer = FindParent<XUiC_BagContainer>(__instance);
            if (bagContainer != null) DepositLootBagFromWindow(bagContainer);
        };
        XUiController backpackButton = __instance.GetChildById("btnHyperdimensionalBackpackAll");
        if (backpackButton != null) backpackButton.OnPress += (sender, mouse) => DepositBackpack(FindParent<XUiC_BackpackWindow>(__instance));
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
                else NetworkLog.Debug("Remote loot deposit confirmed. amount=" + result.Amount + "; requestId=" + result.RequestId);
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

    internal static bool DepositLootBagFromWindow(XUiC_BagContainer bagContainer)
    {
        EntityPlayer player = bagContainer?.xui?.playerUI?.entityPlayer;
        Bag bag = bagContainer?.Bag;
        XUiController groupController = bagContainer?.xui?.FindWindowGroupByName(XUiC_BagStorageWindowGroup.ID);
        XUiC_BagStorageWindowGroup group = groupController?.GetChildByType<XUiC_BagStorageWindowGroup>();
        EntityLootContainer lootBag = group?.Entity as EntityLootContainer;
        if (!(bool)player || bag == null || lootBag?.bag == null || !ReferenceEquals(bag, lootBag.bag)) return false;

        if (NetworkRemoteClient.IsRemoteClient)
        {
            bool queued = NetworkRemoteClient.DepositLootBagAll(player, lootBag.entityId, result =>
            {
                bool moved = result.Success && result.Amount > 0;
                if (result.Success)
                {
                    if (NetworkInventorySync.ApplyLootBag(lootBag, result.LootBagData)) bagContainer.OnBagItemChangedInternal();
                    else NetworkLog.Error("Loot bag deposit succeeded but its server snapshot could not be applied. entity=" + lootBag.entityId + "; requestId=" + result.RequestId);
                    NetworkLog.Debug("Remote zombie loot bag deposit confirmed. entity=" + lootBag.entityId + "; amount=" + result.Amount + "; requestId=" + result.RequestId);
                }
                else NetworkLog.Debug("Remote zombie loot bag deposit rejected. entity=" + lootBag.entityId + "; error=" + result.Error + "; requestId=" + result.RequestId);
                XUiC_ContainerStandardControls controls = bagContainer.GetChildByType<XUiC_ContainerStandardControls>();
                if (controls?.MoveAllDone != null) controls.MoveAllDone(true, moved);
            });
            if (!queued)
            {
                XUiC_ContainerStandardControls controls = bagContainer.GetChildByType<XUiC_ContainerStandardControls>();
                if (controls?.MoveAllDone != null) controls.MoveAllDone(true, false);
                NetworkLog.Error("Remote zombie loot bag deposit could not be queued; the bag was left unchanged. entity=" + lootBag.entityId);
            }
            return queued;
        }

        bool localMoved = DepositGrid(bagContainer.xui, bagContainer, bag.LockedSlots);
        XUiC_ContainerStandardControls localControls = bagContainer.GetChildByType<XUiC_ContainerStandardControls>();
        if (localControls?.MoveAllDone != null) localControls.MoveAllDone(true, localMoved);
        NetworkLog.Debug("Local zombie loot bag deposit completed. entity=" + lootBag.entityId + "; moved=" + localMoved);
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
