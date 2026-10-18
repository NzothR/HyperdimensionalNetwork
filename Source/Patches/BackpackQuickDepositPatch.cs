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
        return !TryDeposit(__instance);
    }

    [HarmonyPrefix, HarmonyPatch("Update")]
    public static bool UpdatePrefix(XUiC_ItemStack __instance)
    {
        if (__instance?.xui?.playerUI?.windowManager == null
            || __instance.xui.playerUI.windowManager.IsInputActive()
            || !__instance.isOver
            || !IsShiftHeld()) return true;
        CursorControllerAbs cursor = __instance.xui.playerUI.CursorController;
        if (!cursor.GetMouseButtonUp(UICamera.MouseButton.LeftButton)) return true;
        return !TryDeposit(__instance);
    }

    private static bool TryDeposit(XUiC_ItemStack instance)
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
        int amount = source.count;
        ItemStack deposit = source.Clone();
        if (!NetworkStorage.AddStack(player, deposit))
        {
            NetworkLog.Debug("Shift deposit failed. item=" + source.itemValue.type + "; count=" + amount);
            return true;
        }

        ItemStack remainder = source.Clone();
        remainder.count -= amount;
        instance.ItemStack = remainder.count > 0 ? remainder : ItemStack.Empty.Clone();
        if (sourceGrid is XUiC_Backpack backpack) backpack.RefreshBackpackSlots();
        else if (sourceGrid is XUiC_Toolbelt toolbelt) toolbelt.PlayerInventory_OnToolbeltItemsChanged();

        NetworkLog.Debug("Shift deposit completed. source=" + sourceGrid.GetType().Name + "; item=" + deposit.itemValue.type + "; count=" + amount + "; revision=" + NetworkStorage.GetRevision(player));
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
