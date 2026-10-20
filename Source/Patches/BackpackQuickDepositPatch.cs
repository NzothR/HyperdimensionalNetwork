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
        if (!half && TryQuickTrash(instance)) return true;
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
        if (NetworkRemoteClient.IsRemoteClient)
        {
            bool toolbeltSource = sourceGrid is XUiC_Toolbelt;
            bool queued = NetworkRemoteClient.DepositPlayerSlot(player, toolbeltSource, instance.SlotNumber, deposit.itemValue, amount, result =>
            {
                if (!result.Success)
                {
                    NetworkLog.Debug("Remote shift deposit rejected. item=" + deposit.itemValue.type + "; count=" + amount + "; error=" + result.Error + "; requestId=" + result.RequestId);
                    return;
                }
                if (!NetworkInventorySync.Apply(player as EntityPlayerLocal, result)) return;
                if (sourceGrid is XUiC_Backpack backpack) backpack.RefreshBackpackSlots();
                else if (sourceGrid is XUiC_Toolbelt toolbelt) toolbelt.PlayerInventory_OnToolbeltItemsChanged();
                NetworkLog.Debug("Remote shift deposit confirmed. source=" + sourceGrid.GetType().Name + "; item=" + deposit.itemValue.type + "; count=" + result.Amount + "; requestId=" + result.RequestId);
                (instance.xui.GetChildByType<XUiC_HyperdimensionalGrid>())?.Reload();
            });
            if (!queued) NetworkLog.Error("Remote shift deposit could not be queued; source inventory was left unchanged. item=" + deposit.itemValue.type + "; count=" + amount);
            return true;
        }
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

    private static bool TryQuickTrash(XUiC_ItemStack instance)
    {
        if (instance?.xui?.playerUI?.windowManager?.IsWindowOpen("hyperdimensionalTrash") != true
            || instance.ItemStack == null || instance.ItemStack.IsEmpty()
            || FindSourceGrid(instance) == null) return false;
        XUiC_DragAndDropWindow drag = instance.xui.DragAndDropWindow;
        XUiC_TrashGrid trash = instance.xui.GetChildByType<XUiC_TrashGrid>();
        if (trash == null || trash.Pending || drag == null || drag.CurrentStack != null && !drag.CurrentStack.IsEmpty()) return false;
        ItemStack[] slots = trash.GetSlots();
        int target = -1;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null || slots[i].IsEmpty()) { target = i; break; }
            if (NetworkStorage.NormalizeKey(slots[i].itemValue).Equals(NetworkStorage.NormalizeKey(instance.ItemStack.itemValue))
                && slots[i].count + instance.ItemStack.count <= instance.ItemStack.itemValue.ItemClass.MaxCount)
            { target = i; break; }
        }
        if (target < 0) return false;
        instance.SwapItem();
        instance.HandleClickComplete();
        if (drag.CurrentStack == null || drag.CurrentStack.IsEmpty()) return false;
        bool placed = trash.Put(target, false);
        NetworkLog.Debug("Shift-click manual trash transfer. source=" + instance.SlotNumber + "; target=" + target
            + "; item=" + instance.ItemStack?.itemValue?.type + "; queued=" + placed);
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
