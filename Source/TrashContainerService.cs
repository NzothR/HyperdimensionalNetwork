using System;
using System.Collections.Generic;
using System.Linq;

namespace HyperdimensionalNetwork;

internal static class TrashContainerService
{
    internal const int SlotCount = 48;
    private sealed class Session
    {
        public string NetworkId;
        public string WorldId;
        public readonly ItemStack[] Slots = ItemStack.CreateArray(SlotCount);
    }
    private static readonly Dictionary<int, Session> Sessions = new Dictionary<int, Session>();

    internal static NetworkRemoteResult Execute(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        if (!(bool)player || request == null) return Failure(request, "PlayerUnavailable");
        if (!NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TrashEnabled)) return Failure(request, "TrashDisabled");
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        string worldId = GameManager.Instance?.World?.Guid ?? string.Empty;
        if (string.IsNullOrEmpty(networkId) || string.IsNullOrEmpty(worldId)) return Failure(request, "NetworkUnavailable");
        if (request.Operation == NetworkRequestOperation.TrashOpen)
        {
            Sessions.Remove(player.entityId);
            Session opened = new Session { NetworkId = networkId, WorldId = worldId };
            Sessions[player.entityId] = opened;
            NetworkLog.Debug("Manual trash container opened. player=" + player.entityId + "; network=" + networkId);
            return Snapshot(request, opened);
        }
        if (!Sessions.TryGetValue(player.entityId, out Session session)
            || session.NetworkId != networkId || session.WorldId != worldId) return Failure(request, "TrashSessionUnavailable");
        if (request.Operation == NetworkRequestOperation.TrashClose)
        {
            int destroyed = session.Slots.Sum(stack => stack == null || stack.IsEmpty() ? 0 : stack.count);
            Sessions.Remove(player.entityId);
            NetworkLog.Debug("Manual trash container closed and destroyed. player=" + player.entityId + "; count=" + destroyed);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = destroyed };
        }
        if (playerData == null || request.Slot < 0 || request.Slot >= SlotCount) return Failure(request, "InvalidTrashSlot");
        if (request.Operation == NetworkRequestOperation.TrashPut) return Put(player, playerData, request, session);
        if (request.Operation == NetworkRequestOperation.TrashTake) return Take(playerData, request, session);
        return Failure(request, "UnsupportedOperation");
    }

    private static NetworkRemoteResult Put(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request, Session session)
    {
        ItemStack cursor = playerData.dragAndDropItem;
        if (cursor == null || cursor.IsEmpty() || request.Value == null || request.Count <= 0 || cursor.count < request.Count
            || !NetworkStorage.NormalizeKey(cursor.itemValue).Equals(NetworkStorage.NormalizeKey(request.Value)))
            return Failure(request, "CursorChanged");
        ItemStack slot = session.Slots[request.Slot];
        bool empty = slot == null || slot.IsEmpty();
        if (!empty && !NetworkStorage.NormalizeKey(slot.itemValue).Equals(NetworkStorage.NormalizeKey(cursor.itemValue)))
            return Failure(request, "TrashSlotOccupied");
        int maxStack = Math.Max(1, cursor.itemValue.ItemClass?.MaxCount ?? 1);
        if ((empty ? 0 : slot.count) + request.Count > maxStack) return Failure(request, "TrashSlotFull");
        if (request.SourceLocation == XUiC_ItemStack.StackLocationTypes.Backpack && playerData.bagData != null)
            NetworkRpcServer.ApplyPlayerInventorySnapshot(player, playerData.bagData, toolbelt: false);
        else if (request.SourceLocation == XUiC_ItemStack.StackLocationTypes.ToolBelt && playerData.inventoryData != null)
            NetworkRpcServer.ApplyPlayerInventorySnapshot(player, playerData.inventoryData, toolbelt: true);
        session.Slots[request.Slot] = new ItemStack(cursor.itemValue.Clone(), (empty ? 0 : slot.count) + request.Count);
        int remaining = cursor.count - request.Count;
        playerData.dragAndDropItem = remaining > 0 ? new ItemStack(cursor.itemValue.Clone(), remaining) : ItemStack.Empty.Clone();
        playerData.bModifiedSinceLastSave = true;
        NetworkLog.Debug("Item placed in manual trash. player=" + player.entityId + "; slot=" + request.Slot + "; item=" + cursor.itemValue.type
            + "; count=" + request.Count + "; cursorRemaining=" + remaining);
        NetworkRemoteResult result = Snapshot(request, session);
        result.Value = cursor.itemValue.Clone(); result.Amount = request.Count; result.UpdatedSourceCount = remaining;
        return result;
    }

    private static NetworkRemoteResult Take(PlayerDataFile playerData, NetworkRemoteRequest request, Session session)
    {
        if (playerData.dragAndDropItem != null && !playerData.dragAndDropItem.IsEmpty()) return Failure(request, "CursorOccupied");
        ItemStack slot = session.Slots[request.Slot];
        if (slot == null || slot.IsEmpty() || request.Count <= 0 || request.Count > slot.count) return Failure(request, "TrashSlotChanged");
        ItemValue value = slot.itemValue.Clone();
        slot.count -= request.Count;
        session.Slots[request.Slot] = slot.count > 0 ? slot : ItemStack.Empty.Clone();
        playerData.dragAndDropItem = new ItemStack(value.Clone(), request.Count);
        playerData.bModifiedSinceLastSave = true;
        NetworkRemoteResult result = Snapshot(request, session);
        result.Value = value; result.Amount = request.Count;
        NetworkLog.Debug("Item withdrawn from manual trash. slot=" + request.Slot + "; item=" + value.type + "; count=" + request.Count);
        return result;
    }

    private static NetworkRemoteResult Snapshot(NetworkRemoteRequest request, Session session)
    {
        NetworkRemoteResult result = new NetworkRemoteResult { RequestId = request.RequestId, Success = true };
        for (int i = 0; i < session.Slots.Length; i++)
        {
            ItemStack stack = session.Slots[i];
            if (stack != null && !stack.IsEmpty()) result.TrashSlots.Add(new TrashSlotEntry
            { Slot = i, Value = stack.itemValue.Clone(), Count = stack.count });
        }
        return result;
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
}
