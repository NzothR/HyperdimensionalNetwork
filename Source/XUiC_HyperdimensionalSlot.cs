namespace HyperdimensionalNetwork;

public sealed class XUiC_HyperdimensionalSlot : XUiC_ItemStack
{
    private XUiC_HyperdimensionalGrid _grid;
    private int _visibleIndex;
    private bool _leftDown;
    private bool _rightDown;

    public void Bind(XUiC_HyperdimensionalGrid grid, int index) { _grid = grid; _visibleIndex = index; }
    public void SetVisibleIndex(int index) => _visibleIndex = index;

    public override void Update(float _dt)
    {
        if (!TryHandleMousePut()) base.Update(_dt);
    }

    public override void SwapItem() { if (!Put(false)) Take(false); }
    public override void HandleDropOne() { if (!Put(true)) Take(true); }

    private bool TryHandleMousePut()
    {
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
}
