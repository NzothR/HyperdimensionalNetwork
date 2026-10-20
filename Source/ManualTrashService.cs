using System;
using System.Collections;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class ManualTrashService
{
    private static Bag _activeBag;
    private static bool _windowOpen;
    internal static bool IsOpen => _windowOpen;

    internal static void Toggle(XUi xui)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        GUIWindowManager manager = xui?.playerUI?.windowManager;
        if (!(bool)player || manager == null) return;
        if (_activeBag != null && manager.IsWindowOpen("bagStorage"))
        {
            manager.Close("bagStorage");
            return;
        }
        if (manager.IsWindowOpen("bagStorage")) return;

        int capacity = Math.Max(1, Math.Min(player.bag.SlotCount,
            player.CarryCapacity > 0 ? player.CarryCapacity : player.bag.SlotCount));
        XUiV_Grid backpackGrid = xui.GetChildByType<XUiC_Backpack>()?.GetChildById("inventory")?.ViewComponent as XUiV_Grid;
        int columns = Math.Max(1, Math.Min(10, backpackGrid?.Columns ?? 8));
        int rows = (capacity + columns - 1) / columns;
        Bag temporary = _activeBag ?? new Bag(new Vector2i(capacity, 1), XUiC_ItemStack.StackLocationTypes.LootContainer, null);
        LootContainer definition = new LootContainer { size = new Vector2i(columns, rows) };
        XUiC_BagContainer container = xui.FindWindowGroupByName("bagStorage")?.GetChildByType<XUiC_BagContainer>();
        int visibleSlots = container?.GetItemStackControllers()?.Length ?? 0;
        if (visibleSlots > 0 && capacity > visibleSlots)
        {
            NetworkLog.Error("Manual trash cannot expose every backpack slot in the vanilla container grid. player="
                + player.entityId + "; requested=" + capacity + "; vanillaGridSlots=" + visibleSlots);
            GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkTrashCapacityUnsupported"));
            return;
        }
        _activeBag = temporary;
        NetworkLog.Debug("Manual trash opened through vanilla bag storage. player=" + player.entityId
            + "; backpackSlots=" + player.bag.SlotCount + "; carryCapacity=" + player.CarryCapacity
            + "; trashSlots=" + capacity + "; columns=" + columns + "; rows=" + rows);
        if (manager.IsWindowOpen("hyperdimensionalNetwork")) manager.Close("hyperdimensionalNetwork");
        try
        {
            XUiController networkDeposit = container?.GetChildById("btnHyperdimensionalLootAll");
            if (networkDeposit?.ViewComponent != null) networkDeposit.ViewComponent.IsVisible = false;
            XUiC_BagStorageWindowGroup.Open(xui, player, temporary, definition,
                Localization.Get("HyperdimensionalNetworkTrashWindow"), null,
                () => OnClosed(xui, player, temporary));
            _windowOpen = true;
        }
        catch (Exception ex)
        {
            _activeBag = null;
            _windowOpen = false;
            XUiController networkDeposit = container?.GetChildById("btnHyperdimensionalLootAll");
            if (networkDeposit?.ViewComponent != null) networkDeposit.ViewComponent.IsVisible = true;
            NetworkLog.Error("Vanilla manual trash window failed to open. player=" + player.entityId + "; exception=" + ex);
            ThreadManager.StartCoroutine(RestoreBackpack(xui, player));
        }
    }

    private static void OnClosed(XUi xui, EntityPlayerLocal player, Bag temporary)
    {
        _windowOpen = false;
        int destroyed = 0;
        int protectedCount = 0;
        for (int i = 0; i < temporary.ItemGrid.Length; i++)
        {
            ItemStack stack = temporary.ItemGrid[i];
            if (stack == null || stack.IsEmpty()) continue;
            if (!NetworkItemPolicy.CanEnterOrDestroy(stack.itemValue))
            {
                ItemStack protectedStack = stack.Clone();
                if (player.bag.AddItem(protectedStack) || player.inventory.AddItem(protectedStack))
                    temporary.ItemGrid.SetItem(i, ItemStack.Empty.Clone());
                else protectedCount += stack.count;
                continue;
            }
            destroyed += stack.count;
            temporary.ItemGrid.SetItem(i, ItemStack.Empty.Clone());
        }
        if (ReferenceEquals(_activeBag, temporary) && protectedCount == 0) _activeBag = null;
        XUiController networkDeposit = xui.FindWindowGroupByName("bagStorage")?.GetChildByType<XUiC_BagContainer>()
            ?.GetChildById("btnHyperdimensionalLootAll");
        if (networkDeposit?.ViewComponent != null) networkDeposit.ViewComponent.IsVisible = true;
        NetworkLog.Debug("Vanilla manual trash closed. player=" + player.entityId + "; destroyed=" + destroyed
            + "; protectedRetained=" + protectedCount);
        ThreadManager.StartCoroutine(RestoreBackpack(xui, player));
    }

    private static IEnumerator RestoreBackpack(XUi xui, EntityPlayerLocal player)
    {
        yield return null;
        GUIWindowManager manager = xui?.playerUI?.windowManager;
        if (!(bool)player || !player.Spawned || manager == null || GameManager.Instance?.World == null) yield break;
        XUiC_WindowSelector selector = xui.FindWindowGroupByName("windowpaging")?.GetChildByType<XUiC_WindowSelector>();
        selector?.SetSelected("crafting");
        if (!manager.IsWindowOpen("windowpaging")) manager.Open("windowpaging", _bModal: false);
        if (!manager.IsWindowOpen("crafting")) manager.Open("crafting", _bModal: true);
        NetworkLog.Debug("Backpack page restored after manual trash. player=" + player.entityId
            + "; craftingOpen=" + manager.IsWindowOpen("crafting")
            + "; pagingOpen=" + manager.IsWindowOpen("windowpaging"));
    }
}
