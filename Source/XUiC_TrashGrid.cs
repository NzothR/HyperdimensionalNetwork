using System;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class XUiC_TrashGrid : XUiC_ItemStackGrid
{
    private ItemStack[] _slots = ItemStack.CreateArray(TrashContainerService.SlotCount);
    private bool _pending;
    public override XUiC_ItemStack.StackLocationTypes StackLocation => XUiC_ItemStack.StackLocationTypes.ToolBelt;
    internal bool Pending => _pending;

    public override void Init()
    {
        base.Init();
        for (int i = 0; i < itemControllers.Length; i++)
        {
            if (itemControllers[i] is not XUiC_TrashSlot slot) continue;
            slot.Bind(this, i);
            slot.SlotNumber = i;
            slot.StackLocation = StackLocation;
            slot.InfoWindow = xui?.GetChildByType<XUiC_ItemInfoWindow>();
        }
        RefreshSlots();
    }

    public override ItemStack[] GetSlots() => _slots;
    public override void HandleSlotChangedEvent(int slotNumber, ItemStack stack) { }

    public override void OnOpen()
    {
        base.OnOpen();
        _slots = ItemStack.CreateArray(TrashContainerService.SlotCount);
        RefreshSlots();
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !NetworkRemoteClient.IsRemoteClient) return;
        _pending = true;
        if (!NetworkRemoteClient.RequestTrash(player, NetworkRequestOperation.TrashOpen, 0, null, 0,
            StackLocation, result =>
            {
                _pending = false;
                if (result.Success) Apply(result);
                else NetworkLog.Debug("Manual trash failed to open remotely. player=" + player.entityId + "; error=" + result.Error);
            })) _pending = false;
    }

    public override void OnClose()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        int destroyed = _slots.Sum(stack => stack == null || stack.IsEmpty() ? 0 : stack.count);
        if ((bool)player && NetworkRemoteClient.IsRemoteClient)
            NetworkRemoteClient.RequestTrash(player, NetworkRequestOperation.TrashClose, 0, null, 0, StackLocation,
                result => NetworkLog.Debug("Remote manual trash closed. player=" + player.entityId + "; success=" + result.Success
                    + "; destroyed=" + result.Amount + "; error=" + result.Error));
        _slots = ItemStack.CreateArray(TrashContainerService.SlotCount);
        RefreshSlots();
        NetworkLog.Debug("Manual trash window closed; local contents cleared. player=" + (player?.entityId ?? -1)
            + "; projectedDestroyed=" + destroyed);
        base.OnClose();
    }

    internal bool Put(int slot, bool single)
    {
        XUiC_DragAndDropWindow drag = xui?.DragAndDropWindow;
        ItemStack cursor = drag?.CurrentStack;
        if (_pending || cursor == null || cursor.IsEmpty() || slot < 0 || slot >= _slots.Length) return false;
        ItemStack target = _slots[slot];
        bool empty = target == null || target.IsEmpty();
        if (!empty && !NetworkStorage.NormalizeKey(target.itemValue).Equals(NetworkStorage.NormalizeKey(cursor.itemValue))) return false;
        int maxStack = Math.Max(1, cursor.itemValue.ItemClass?.MaxCount ?? 1);
        int count = Math.Min(single ? 1 : cursor.count, maxStack - (empty ? 0 : target.count));
        if (count <= 0) return false;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (NetworkRemoteClient.IsRemoteClient)
        {
            _pending = true;
            bool sent = NetworkRemoteClient.RequestTrash(player, NetworkRequestOperation.TrashPut, slot, cursor.itemValue, count,
                drag.PickUpType, result =>
                {
                    _pending = false;
                    if (!result.Success) { NetworkLog.Debug("Manual trash put rejected. error=" + result.Error); return; }
                    drag.SetCurrentStack(result.UpdatedSourceCount > 0
                        ? new ItemStack(result.Value.Clone(), result.UpdatedSourceCount) : ItemStack.Empty.Clone());
                    drag.PickUpType = StackLocation;
                    Apply(result);
                });
            if (!sent) _pending = false;
            return sent;
        }
        _slots[slot] = new ItemStack(cursor.itemValue.Clone(), (empty ? 0 : target.count) + count);
        ItemStack remaining = cursor.Clone(); remaining.count -= count;
        drag.SetCurrentStack(remaining.count > 0 ? remaining : ItemStack.Empty.Clone());
        drag.PickUpType = StackLocation;
        RefreshSlots();
        NetworkLog.Debug("Local manual trash put. player=" + (player?.entityId ?? -1) + "; slot=" + slot + "; count=" + count);
        return true;
    }

    internal bool Take(int slot, bool half)
    {
        XUiC_DragAndDropWindow drag = xui?.DragAndDropWindow;
        if (_pending || drag == null || drag.CurrentStack != null && !drag.CurrentStack.IsEmpty()
            || slot < 0 || slot >= _slots.Length) return false;
        ItemStack source = _slots[slot];
        if (source == null || source.IsEmpty()) return false;
        int count = half ? Math.Max(1, source.count / 2) : source.count;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (NetworkRemoteClient.IsRemoteClient)
        {
            _pending = true;
            bool sent = NetworkRemoteClient.RequestTrash(player, NetworkRequestOperation.TrashTake, slot, source.itemValue,
                count, StackLocation, result =>
                {
                    _pending = false;
                    if (!result.Success) { NetworkLog.Debug("Manual trash take rejected. error=" + result.Error); return; }
                    if (drag.CurrentStack != null && !drag.CurrentStack.IsEmpty())
                    {
                        NetworkLog.Error("Trash withdrawal cursor occupied after server commit; player=" + player.entityId);
                        return;
                    }
                    drag.SetCurrentStack(new ItemStack(result.Value.Clone(), result.Amount));
                    drag.PickUpType = StackLocation;
                    Apply(result);
                });
            if (!sent) _pending = false;
            return sent;
        }
        ItemValue value = source.itemValue.Clone();
        ItemStack remaining = source.Clone(); remaining.count -= count;
        _slots[slot] = remaining.count > 0 ? remaining : ItemStack.Empty.Clone();
        drag.SetCurrentStack(new ItemStack(value, count)); drag.PickUpType = StackLocation;
        RefreshSlots();
        NetworkLog.Debug("Local manual trash take. player=" + (player?.entityId ?? -1) + "; slot=" + slot + "; count=" + count);
        return true;
    }

    private void Apply(NetworkRemoteResult result)
    {
        _slots = ItemStack.CreateArray(TrashContainerService.SlotCount);
        foreach (TrashSlotEntry entry in result.TrashSlots)
            if (entry.Slot >= 0 && entry.Slot < _slots.Length)
                _slots[entry.Slot] = new ItemStack(entry.Value.Clone(), entry.Count);
        RefreshSlots();
    }

    internal void RefreshSlots()
    {
        if (itemControllers == null) return;
        for (int i = 0; i < itemControllers.Length && i < _slots.Length; i++)
        {
            XUiC_ItemStack slot = itemControllers[i];
            slot.ForceSetItemStack(_slots[i]?.Clone() ?? ItemStack.Empty.Clone());
            slot.SlotNumber = i;
        }
        IsDirty = true;
    }
}

public sealed class XUiC_TrashSlot : XUiC_ItemStack
{
    private XUiC_TrashGrid _grid;
    private int _slot;
    internal void Bind(XUiC_TrashGrid grid, int slot) { _grid = grid; _slot = slot; }
    public override void SwapItem() { if (_grid?.Put(_slot, false) != true) _grid?.Take(_slot, false); }
    public override void HandleDropOne() { if (_grid?.Put(_slot, true) != true) _grid?.Take(_slot, true); }
    internal void TakeHalf() => _grid?.Take(_slot, true);
    internal void HandleCustomSwap() { SwapItem(); HandleClickComplete(); }

    public override void Update(float dt)
    {
        if (!TryMousePut()) base.Update(dt);
    }

    private bool TryMousePut()
    {
        if (!xui || !isOver || _grid?.Pending == true) return false;
        CursorControllerAbs cursor = xui.playerUI.CursorController;
        if (xui.DragAndDropWindow?.CurrentStack == null || xui.DragAndDropWindow.CurrentStack.IsEmpty()) return false;
        if (cursor.GetMouseButtonUp(UICamera.MouseButton.LeftButton))
        { return _grid.Put(_slot, false); }
        if (cursor.GetMouseButtonUp(UICamera.MouseButton.RightButton))
        { return _grid.Put(_slot, true); }
        return false;
    }
}
