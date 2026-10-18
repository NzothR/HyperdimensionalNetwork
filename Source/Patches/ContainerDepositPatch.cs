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
        if (lootButton != null) lootButton.OnPress += (sender, mouse) => DepositLoot(FindParent<XUiC_LootWindow>(__instance));
        XUiController backpackButton = __instance.GetChildById("btnHyperdimensionalBackpackAll");
        if (backpackButton != null) backpackButton.OnPress += (sender, mouse) => DepositBackpack(FindParent<XUiC_BackpackWindow>(__instance));
    }

    private static void DepositLoot(XUiC_LootWindow window)
    {
        XUiC_LootContainer grid = window?.GetChildByType<XUiC_LootContainer>();
        bool moved = DepositGrid(window?.xui, grid, null);
        XUiC_ContainerStandardControls controls = window?.GetChildByType<XUiC_ContainerStandardControls>();
        if (controls?.MoveAllDone != null) controls.MoveAllDone(true, moved);
        NetworkLog.Info("Quick deposit from container completed. moved=" + moved);
    }

    private static void DepositBackpack(XUiC_BackpackWindow window)
    {
        XUiC_Backpack grid = window?.GetChildByType<XUiC_Backpack>();
        PackedBoolArray locked = window?.xui?.playerUI?.entityPlayer?.bag?.LockedSlots;
        bool moved = DepositGrid(window?.xui, grid, locked);
        NetworkLog.Info("Quick deposit from backpack completed. moved=" + moved);
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
