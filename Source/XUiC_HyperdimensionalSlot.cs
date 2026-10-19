namespace HyperdimensionalNetwork;

public sealed class XUiC_HyperdimensionalSlot : XUiC_ItemStack
{
    private XUiC_HyperdimensionalGrid _grid;
    private XUiController _sellButton;
    private int _visibleIndex;
    private bool _leftDown;
    private bool _rightDown;

    public void Bind(XUiC_HyperdimensionalGrid grid, int index) { _grid = grid; _visibleIndex = index; }
    public void SetVisibleIndex(int index) => _visibleIndex = index;

    public override bool GetBindingValueInternal(ref string value, string bindingName)
    {
        if (bindingName == "itemcount" && ItemStack != null && !ItemStack.IsEmpty() && ItemStack.count > 1)
        {
            value = itemcountFormatter.Format(ItemStack.count);
            return true;
        }
        return base.GetBindingValueInternal(ref value, bindingName);
    }

    public override void Update(float _dt)
    {
        UpdateSellButton();
        if (!TryHandleMousePut()) base.Update(_dt);
    }

    public override void Init()
    {
        base.Init();
        _sellButton = GetChildById("btnNetworkItemSell");
        if (_sellButton != null) _sellButton.OnPress += OnSellPressed;
        UpdateSellButton();
    }

    private void UpdateSellButton()
    {
        if (_sellButton?.ViewComponent == null) return;
        ItemStack stack = ItemStack;
        bool visible = TraderTransactionService.VoidSaleMode && stack != null && !stack.IsEmpty()
            && TraderTransactionService.CanSell(stack.itemValue);
        if (_sellButton.ViewComponent.IsVisible != visible) _sellButton.ViewComponent.IsVisible = visible;
    }

    private void OnSellPressed(XUiController sender, int mouseButton)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !TraderTransactionService.VoidSaleMode || ItemStack == null || ItemStack.IsEmpty()) return;
        if (NetworkRemoteClient.IsMutationPending) return;
        int itemType = ItemStack.itemValue.type;
        int itemCount = ItemStack.count;
        bool started = TraderTransactionService.SellFromSlot(player, this, networkSource: true, result =>
        {
            if (!result.Success)
            {
                GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkTraderSaleFailed") + ": " + result.Error, false, false, 0f);
                NetworkLog.Debug("Virtual trader sale rejected. player=" + player.entityId + "; requestId=" + result.RequestId + "; error=" + result.Error);
                return;
            }
            if (result.BagData != null || result.InventoryData != null)
                NetworkLog.Error("Network item sale unexpectedly returned player inventory data. player=" + player.entityId + "; requestId=" + result.RequestId);
            _grid?.Reload();
            GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkTraderSaleComplete") + " " + result.Amount, false, false, 0f);
            NetworkLog.Debug("Virtual trader sale feedback displayed. player=" + player.entityId + "; requestId=" + result.RequestId + "; item=" + itemType + "; count=" + itemCount + "; proceeds=" + result.Amount);
        });
        if (!started) return;
        if (NetworkRemoteClient.IsRemoteClient) NetworkLog.Debug("Remote virtual trader sale requested. player=" + player.entityId + "; item=" + itemType + "; count=" + itemCount);
    }

    public override void SwapItem() { if (!Put(false)) Take(false); }
    public override void HandleDropOne() { if (!Put(true)) Take(true); }

    private bool TryHandleMousePut()
    {
        if (_grid?.IsMutationPending == true) { ResetPressState(); return true; }
        if (!xui || !isOver || IsLocked || StackLock) { ResetPressState(); return false; }
        CursorControllerAbs cursor = xui.playerUI.CursorController;
        if (cursor.GetMouseButtonDown(UICamera.MouseButton.LeftButton)) _leftDown = true;
        if (cursor.GetMouseButtonDown(UICamera.MouseButton.RightButton)) _rightDown = true;
        XUiC_DragAndDropWindow drag = xui.DragAndDropWindow;
        if (drag?.CurrentStack == null || drag.CurrentStack.IsEmpty()) { ResetPressState(); return false; }
        if (cursor.GetMouseButtonUp(UICamera.MouseButton.LeftButton))
        {
            bool pressed = _leftDown;
            _leftDown = false;
            if (pressed) return Put(false);
        }
        if (cursor.GetMouseButtonUp(UICamera.MouseButton.RightButton))
        {
            bool pressed = _rightDown;
            _rightDown = false;
            if (pressed) return Put(true);
        }
        return false;
    }

    private void ResetPressState()
    {
        if (!xui || !xui.playerUI) return;
        CursorControllerAbs cursor = xui.playerUI.CursorController;
        if (cursor.GetMouseButtonUp(UICamera.MouseButton.LeftButton)) _leftDown = false;
        if (cursor.GetMouseButtonUp(UICamera.MouseButton.RightButton)) _rightDown = false;
    }

    private bool Put(bool single)
    {
        XUiC_DragAndDropWindow drag = xui?.DragAndDropWindow;
        return drag?.CurrentStack != null && !drag.CurrentStack.IsEmpty() && _grid != null && _grid.AddFromMouse(drag.CurrentStack, single);
    }

    private bool Take(bool half)
    {
        XUiC_DragAndDropWindow drag = xui?.DragAndDropWindow;
        return drag != null && (drag.CurrentStack == null || drag.CurrentStack.IsEmpty()) && _grid != null && _grid.TakeToMouse(_visibleIndex, half);
    }

    internal void TakeFromNetwork(bool half) => Take(half);
    internal void MoveFromNetworkToPlayer() => _grid?.TakeToPlayer(_visibleIndex);
    internal void HandleStackSwapFromNetwork()
    {
        if (!Put(false)) Take(false);
        HandleClickComplete();
    }
}
