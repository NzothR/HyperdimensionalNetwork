using System;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_ItemStack))]
internal static class BackpackQuickDepositPatch
{
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;

    [HarmonyPrefix, HarmonyPatch("HandleMoveToPreferredLocation")]
    public static bool PreferredLocationPrefix(XUiC_ItemStack __instance)
    {
        if (!IsShiftHeld()) return true;
        return !TryDeposit(__instance, half: false);
    }

    [HarmonyPrefix, HarmonyPatch("Update")]
    public static bool UpdatePrefix(XUiC_ItemStack __instance)
    {
        if (__instance?.xui?.playerUI?.windowManager == null
            || __instance.xui.playerUI.windowManager.IsInputActive()
            || !__instance.isOver) return true;
        CursorControllerAbs cursor = __instance.xui.playerUI.CursorController;
        if (!IsShiftHeld()) return true;

        if (cursor.GetMouseButtonUp(UICamera.MouseButton.RightButton))
        {
            if (__instance is XUiC_HyperdimensionalSlot networkSlot)
            {
                XUiC_DragAndDropWindow drag = __instance.xui.DragAndDropWindow;
                if (drag?.CurrentStack != null && !drag.CurrentStack.IsEmpty()) return true;
                NetworkLog.Debug("Shift-right network withdrawal requested. slot=" + networkSlot.SlotNumber);
                networkSlot.TakeFromNetwork(half: true);
                return false;
            }
            return !TryDeposit(__instance, half: true);
        }

        if (cursor.GetMouseButtonUp(UICamera.MouseButton.LeftButton)) return !TryDeposit(__instance, half: false);
        return true;
    }

    private static bool TryDeposit(XUiC_ItemStack instance, bool half)
    {
        if (instance?.xui?.playerUI?.windowManager == null
            || instance.xui.playerUI.windowManager.IsInputActive()
            || !instance.xui.playerUI.windowManager.IsWindowOpen("hyperdimensionalNetwork")) return false;
        ItemStack source = instance.ItemStack;
        if (source == null || source.IsEmpty()) return false;
        XUiC_DragAndDropWindow drag = instance.xui.DragAndDropWindow;
        if (drag?.CurrentStack != null && !drag.CurrentStack.IsEmpty()) return false;

        XUiC_ItemStackGrid sourceGrid = FindSourceGrid(instance);
        if (sourceGrid == null) return false;
        EntityPlayer player = instance.xui.playerUI.entityPlayer;
        int amount = half ? source.count / 2 : source.count;
        if (amount <= 0)
        {
            NetworkLog.Debug("Shift-right deposit skipped because the stack has no half to move. item=" + source.itemValue.type + "; count=" + source.count);
            return true;
        }
        ItemStack deposit = source.Clone();
        deposit.count = amount;
        if (!NetworkStorage.AddStack(player, deposit))
        {
            NetworkLog.Debug("Shift deposit failed. item=" + source.itemValue.type + "; count=" + amount + "; half=" + half);
            return true;
        }

        ItemStack remainder = source.Clone();
        remainder.count -= amount;
        instance.ItemStack = remainder.count > 0 ? remainder : ItemStack.Empty.Clone();
        if (sourceGrid is XUiC_Backpack backpack) backpack.RefreshBackpackSlots();
        else if (sourceGrid is XUiC_Toolbelt toolbelt) toolbelt.PlayerInventory_OnToolbeltItemsChanged();

        NetworkLog.Debug("Shift deposit completed. source=" + sourceGrid.GetType().Name + "; item=" + deposit.itemValue.type + "; count=" + amount + "; half=" + half + "; revision=" + NetworkStorage.GetRevision(player));
        return true;
    }

    private static bool IsShiftHeld()
    {
        try { return (GetAsyncKeyState(VkLeftShift) & 0x8000) != 0 || (GetAsyncKeyState(VkRightShift) & 0x8000) != 0; }
        catch (Exception ex) { NetworkLog.Debug("Could not detect Shift key for quick deposit. " + ex.Message); return false; }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static XUiC_ItemStackGrid FindSourceGrid(XUiController controller)
    {
        for (XUiController current = controller?.Parent; current != null; current = current.Parent)
        {
            if (current is XUiC_Backpack or XUiC_Toolbelt) return current as XUiC_ItemStackGrid;
        }
        return null;
    }
}
