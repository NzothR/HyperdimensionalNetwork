using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class NetworkRpcServer
{
    private sealed class CachedRequest
    {
        public NetworkRemoteRequest Request;
        public NetworkRemoteResult Result;
    }

    private static readonly object RequestSync = new object();
    private static readonly Dictionary<string, CachedRequest> CompletedRequests = new Dictionary<string, CachedRequest>(StringComparer.Ordinal);
    private static readonly Queue<string> CompletedRequestOrder = new Queue<string>();
    private static readonly HashSet<string> ReturnedWithdrawals = new HashSet<string>(StringComparer.Ordinal);
    private const int MaximumCachedRequests = 512;

    public static void ProcessRequest(World world, EntityPlayer player, ClientInfo sender, NetworkRemoteRequest request)
    {
        NetworkRemoteResult result;
        try
        {
            string networkId = PlayerNetworkDirectory.GetNetworkId(player);
            if (string.IsNullOrEmpty(networkId))
            {
                result = Failure(request, "NetworkUnavailable");
            }
            else if (!string.IsNullOrEmpty(request.ExpectedNetworkId)
                && !string.Equals(request.ExpectedNetworkId, networkId, StringComparison.OrdinalIgnoreCase))
            {
                result = Failure(request, "StaleNetwork");
                NetworkLog.Debug("Rejected stale network request. player=" + player.entityId + "; expected=" + request.ExpectedNetworkId + "; actual=" + networkId + "; operation=" + request.Operation);
            }
            else if (request.Operation == NetworkRequestOperation.QueryPage)
            {
                result = QueryPage(player, request);
            }
            else
            {
                result = ProcessMutation(world, player, sender, request);
            }
            result.NetworkId = networkId;
            result.Revision = NetworkStorage.GetRevision(player);
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Network request failed safely. player=" + player?.entityId + "; requestId=" + request?.RequestId + "; operation=" + request?.Operation + "; exception=" + ex);
            result = Failure(request, "InternalFailure");
        }

        SendResponse(request.EntityId, result);
    }

    private static NetworkRemoteResult QueryPage(EntityPlayer player, NetworkRemoteRequest request)
    {
        int pageSize = Math.Max(1, Math.Min(128, request.PageSize));
        NetworkStoragePage page = NetworkStorage.GetPage(player, request.Category, request.Search, request.Page, pageSize);
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            NetworkId = page.NetworkId ?? string.Empty,
            Revision = page.Revision,
            TotalEntries = page.TotalEntries,
            Page = page.Page,
            PageSize = page.PageSize,
            Entries = page.Entries
        };
    }

    private static NetworkRemoteResult ProcessMutation(World world, EntityPlayer player, ClientInfo sender, NetworkRemoteRequest request)
    {
        string key = request.EntityId + ":" + request.RequestId;
        lock (RequestSync)
        {
            if (CompletedRequests.TryGetValue(key, out CachedRequest cached))
            {
                if (!SameRequest(cached.Request, request))
                {
                    NetworkLog.Error("Rejected request ID reuse with different contents. player=" + request.EntityId + "; requestId=" + request.RequestId);
                    return Failure(request, "RequestIdReuse");
                }
                NetworkLog.Debug("Replayed cached network transaction result. player=" + request.EntityId + "; requestId=" + request.RequestId + "; operation=" + request.Operation);
                return Clone(cached.Result);
            }
            NetworkRemoteResult result;
            try { result = ExecuteMutation(world, player, sender, request); }
            catch (Exception ex)
            {
                NetworkLog.Error("Network transaction failed. player=" + player.entityId + "; requestId=" + request.RequestId + "; operation=" + request.Operation + "; exception=" + ex);
                result = Failure(request, "InternalFailure");
            }
            result.NetworkId = PlayerNetworkDirectory.GetNetworkId(player) ?? string.Empty;
            result.Revision = NetworkStorage.GetRevision(player);
            CompletedRequests[key] = new CachedRequest { Request = Clone(request), Result = Clone(result) };
            CompletedRequestOrder.Enqueue(key);
            while (CompletedRequestOrder.Count > MaximumCachedRequests)
            {
                string oldest = CompletedRequestOrder.Dequeue();
                CompletedRequests.Remove(oldest);
                ReturnedWithdrawals.Remove(oldest);
            }
            NetworkLog.Debug("Network transaction processed. player=" + player.entityId + "; network=" + result.NetworkId + "; requestId=" + request.RequestId + "; operation=" + request.Operation + "; success=" + result.Success + "; amount=" + result.Amount + "; revision=" + result.Revision + "; error=" + result.Error);
            return result;
        }
    }

    private static NetworkRemoteResult ExecuteMutation(World world, EntityPlayer player, ClientInfo sender, NetworkRemoteRequest request)
    {
        switch (request.Operation)
        {
            case NetworkRequestOperation.DepositCursor:
                return DepositCursor(player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.DepositBackpackSlot:
                return DepositPlayerSlot(player, sender?.latestPlayerData, request, toolbelt: false);
            case NetworkRequestOperation.DepositToolbeltSlot:
                return DepositPlayerSlot(player, sender?.latestPlayerData, request, toolbelt: true);
            case NetworkRequestOperation.DepositBackpackAll:
                return DepositBackpackAll(player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.DepositLootAll:
                return DepositLootAll(world, player, request);
            case NetworkRequestOperation.WithdrawCursor:
            case NetworkRequestOperation.WithdrawBackpack:
                return Withdraw(player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.ReturnWithdrawal:
                return ReturnWithdrawal(player, sender?.latestPlayerData, request);
            default:
                return Failure(request, "UnsupportedOperation");
        }
    }

    private static NetworkRemoteResult DepositCursor(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        if (playerData == null || playerData.dragAndDropItem == null || playerData.dragAndDropItem.IsEmpty() || request.Value == null || request.Count <= 0)
            return Failure(request, "CursorUnavailable");
        ItemStack cursor = playerData.dragAndDropItem;
        if (!NetworkStorage.NormalizeKey(cursor.itemValue).Equals(NetworkStorage.NormalizeKey(request.Value)) || cursor.count < request.Count)
            return Failure(request, "CursorChanged");

        if (request.SourceLocation == XUiC_ItemStack.StackLocationTypes.Backpack)
            ApplyPlayerInventorySnapshot(player, playerData.bagData, toolbelt: false);
        else if (request.SourceLocation == XUiC_ItemStack.StackLocationTypes.ToolBelt)
            ApplyPlayerInventorySnapshot(player, playerData.inventoryData, toolbelt: true);

        int updatedCount = cursor.count - request.Count;
        ItemValue value = cursor.itemValue.Clone();
        if (!NetworkStorage.AddStack(player, new ItemStack(value.Clone(), request.Count)))
            return Failure(request, "StorageCommitFailed");
        playerData.dragAndDropItem = updatedCount > 0 ? new ItemStack(value.Clone(), updatedCount) : ItemStack.Empty.Clone();
        playerData.bModifiedSinceLastSave = true;
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            Value = value,
            Amount = request.Count,
            UpdatedSourceCount = updatedCount
        };
    }

    private static NetworkRemoteResult DepositPlayerSlot(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request, bool toolbelt)
    {
        if (playerData == null || request.Value == null || request.Count <= 0 || request.Slot < 0)
            return Failure(request, "InvalidPlayerInventoryRequest");
        ItemStack source;
        MemoryStream updated;
        bool parsed = toolbelt
            ? TryUpdateToolbelt(playerData.inventoryData, request.Slot, request.Value, request.Count, out source, out updated)
            : TryUpdateBag(playerData.bagData, request.Slot, request.Value, request.Count, out source, out updated);
        if (!parsed) return Failure(request, "PlayerInventoryChanged");

        StorageAddition addition = new StorageAddition { Value = source.itemValue.Clone(), Count = request.Count };
        if (!CommitInventoryDeposit(player, playerData, addition, updated, toolbelt))
            return Failure(request, "StorageCommitFailed");
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            Value = source.itemValue.Clone(),
            Amount = request.Count,
            UpdatedSourceCount = source.count - request.Count,
            InventoryData = toolbelt ? updated.ToArray() : null,
            BagData = toolbelt ? null : updated.ToArray()
        };
    }

    private static NetworkRemoteResult DepositBackpackAll(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        if (playerData == null || playerData.bagData == null || playerData.bagData.Length == 0)
            return Failure(request, "BackpackUnavailable");
        Bag bag = ReadBag(playerData.bagData);
        if (bag == null) return Failure(request, "BackpackUnavailable");
        List<StorageAddition> additions = new List<StorageAddition>();
        for (int i = 0; i < bag.SlotCount; i++)
        {
            if (bag.LockedSlots != null && i < bag.LockedSlots.Length && bag.LockedSlots[i]) continue;
            ItemStack stack = bag.ItemGrid[i];
            if (stack == null || stack.IsEmpty()) continue;
            additions.Add(new StorageAddition { Value = stack.itemValue.Clone(), Count = stack.count });
            bag.ItemGrid.SetItem(i, ItemStack.Empty.Clone());
        }
        if (additions.Count == 0) return Failure(request, "NoItems");
        MemoryStream updated = WriteBag(bag);
        if (!CommitInventoryDeposit(player, playerData, additions, updated, toolbelt: false))
            return Failure(request, "StorageCommitFailed");
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            Amount = additions.Sum(entry => (int)Math.Min(entry.Count, int.MaxValue)),
            BagData = updated.ToArray()
        };
    }

    private static NetworkRemoteResult DepositLootAll(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        TileEntity tileEntity = world.GetTileEntity(request.Position);
        TEFeatureStorage storage = tileEntity?.GetSelfOrFeature<TEFeatureStorage>();
        if (storage == null || !PlayerHoldsLootLock(player.entityId, storage)) return Failure(request, "LootContainerUnavailable");
        Vector3 center = storage.ToWorldPos().ToVector3() + Vector3.one * 0.5f;
        if ((player.position - center).sqrMagnitude > 64f) return Failure(request, "OutOfRange");

        List<StorageAddition> additions = new List<StorageAddition>();
        List<KeyValuePair<int, ItemStack>> originalStacks = new List<KeyValuePair<int, ItemStack>>();
        for (int i = 0; i < storage.ItemGrid.Length; i++)
        {
            ItemStack stack = storage.ItemGrid[i];
            if (stack == null || stack.IsEmpty()) continue;
            ItemStack clone = stack.Clone();
            originalStacks.Add(new KeyValuePair<int, ItemStack>(i, clone));
            additions.Add(new StorageAddition { Value = clone.itemValue.Clone(), Count = clone.count });
        }
        if (additions.Count == 0) return Failure(request, "NoItems");
        if (!NetworkStorage.AddBatch(player, additions)) return Failure(request, "StorageCommitFailed");

        bool committed = false;
        try
        {
            for (int i = 0; i < storage.ItemGrid.Length; i++)
                if (storage.ItemGrid[i] == null || !storage.ItemGrid[i].IsEmpty()) storage.UpdateSlot(i, ItemStack.Empty.Clone());
            storage.SetModified();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                if (!NetworkStorage.RemoveBatch(player, additions))
                    NetworkLog.Error("Could not compensate network items after a loot container update failure. player=" + player.entityId + "; requestId=" + request.RequestId);
                foreach (KeyValuePair<int, ItemStack> original in originalStacks)
                    if (original.Key < storage.ItemGrid.Length && storage.ItemGrid[original.Key].IsEmpty()) storage.UpdateSlot(original.Key, original.Value);
                try { storage.SetModified(); } catch (Exception ex) { NetworkLog.Error("Loot container rollback could not be synchronized. " + ex); }
            }
        }
        if (!committed) return Failure(request, "LootContainerCommitFailed");
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = additions.Sum(entry => (int)Math.Min(entry.Count, int.MaxValue)) };
    }

    private static NetworkRemoteResult Withdraw(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        if (request.Value == null || request.Count <= 0) return Failure(request, "InvalidWithdrawal");
        if (request.Operation == NetworkRequestOperation.WithdrawCursor)
        {
            if (playerData == null || playerData.dragAndDropItem == null || !playerData.dragAndDropItem.IsEmpty())
                return Failure(request, "CursorUnavailable");
            int removedFromNetwork = NetworkStorage.Remove(player, request.Value, request.Count);
            if (removedFromNetwork <= 0) return Failure(request, "NotEnoughItems");
            playerData.dragAndDropItem = new ItemStack(request.Value.Clone(), removedFromNetwork);
            playerData.bModifiedSinceLastSave = true;
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Value = request.Value.Clone(), Amount = removedFromNetwork };
        }

        if (playerData == null || playerData.bagData == null || playerData.bagData.Length == 0
            || !new ItemStack(request.Value, 1).CanMoveTo(XUiC_ItemStack.StackLocationTypes.Backpack))
            return Failure(request, "BackpackUnavailable");
        Bag bag;
        try { bag = ReadBag(playerData.bagData); }
        catch (Exception ex) { NetworkLog.Error("Could not read backpack during network withdrawal. player=" + player.entityId + "; exception=" + ex); return Failure(request, "BackpackUnavailable"); }
        if (bag == null) return Failure(request, "BackpackUnavailable");
        try { ApplyPlayerInventorySnapshot(player, playerData.bagData, toolbelt: false); }
        catch (Exception ex) { NetworkLog.Error("Could not synchronize live backpack before network withdrawal. player=" + player.entityId + "; exception=" + ex); return Failure(request, "BackpackUnavailable"); }
        int available = (int)Math.Min(Math.Min(NetworkStorage.GetCount(player, request.Value), request.Count), int.MaxValue);
        List<KeyValuePair<int, int>> placements = PlanBagPlacement(bag, request.Value, available);
        int amount = placements.Sum(placement => placement.Value);
        if (amount <= 0) return Failure(request, "BackpackFull");
        foreach (KeyValuePair<int, int> placement in placements)
        {
            ItemStack current = bag.ItemGrid[placement.Key];
            if (current.IsEmpty()) bag.ItemGrid.SetItem(placement.Key, new ItemStack(request.Value.Clone(), placement.Value));
            else bag.ItemGrid.SetItem(placement.Key, new ItemStack(current.itemValue.Clone(), current.count + placement.Value));
        }
        MemoryStream updatedBag = WriteBag(bag);
        int removed = NetworkStorage.Remove(player, request.Value, amount);
        if (removed != amount)
        {
            if (removed > 0 && !NetworkStorage.AddStack(player, new ItemStack(request.Value.Clone(), removed)))
                NetworkLog.Error("Could not compensate a partial quick withdrawal. player=" + player.entityId + "; item=" + request.Value.type + "; removed=" + removed + "; intended=" + amount);
            return Failure(request, "NotEnoughItems");
        }
        try
        {
            ApplyPlayerInventorySnapshot(player, updatedBag, toolbelt: false);
            playerData.bagData = updatedBag;
            playerData.bModifiedSinceLastSave = true;
        }
        catch (Exception ex)
        {
            if (!NetworkStorage.AddStack(player, new ItemStack(request.Value.Clone(), removed)))
                NetworkLog.Error("Could not compensate a quick withdrawal after backpack apply failed. player=" + player.entityId + "; item=" + request.Value.type + "; count=" + removed + "; exception=" + ex);
            try { ApplyPlayerInventorySnapshot(player, playerData.bagData, toolbelt: false); }
            catch (Exception restoreException) { NetworkLog.Error("Could not restore server backpack after a quick withdrawal failure. player=" + player.entityId + "; exception=" + restoreException); }
            return Failure(request, "BackpackApplyFailed");
        }
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            Value = request.Value.Clone(),
            Amount = removed,
            BagData = updatedBag.ToArray()
        };
    }

    private static List<KeyValuePair<int, int>> PlanBagPlacement(Bag bag, ItemValue value, int requested)
    {
        List<KeyValuePair<int, int>> placements = new List<KeyValuePair<int, int>>();
        int remaining = requested;
        ItemValue key = NetworkStorage.NormalizeKey(value);
        ItemStack incoming = new ItemStack(value, requested);
        for (int i = 0; i < bag.SlotCount && remaining > 0; i++)
        {
            if (bag.LockedSlots != null && i < bag.LockedSlots.Length && bag.LockedSlots[i]) continue;
            ItemStack current = bag.ItemGrid[i];
            if (current == null || current.IsEmpty() || !NetworkStorage.NormalizeKey(current.itemValue).Equals(key)
                || !current.CanStackPartlyWith(incoming, out int canAdd)) continue;
            int add = Math.Min(remaining, canAdd);
            placements.Add(new KeyValuePair<int, int>(i, add));
            remaining -= add;
        }
        int stackLimit = Math.Max(1, value.ItemClass?.MaxCount ?? 1);
        for (int i = 0; i < bag.SlotCount && remaining > 0; i++)
        {
            if (bag.LockedSlots != null && i < bag.LockedSlots.Length && bag.LockedSlots[i]) continue;
            if (bag.ItemGrid[i] != null && !bag.ItemGrid[i].IsEmpty()) continue;
            int add = Math.Min(remaining, stackLimit);
            placements.Add(new KeyValuePair<int, int>(i, add));
            remaining -= add;
        }
        return placements;
    }

    private static bool CommitInventoryDeposit(EntityPlayer player, PlayerDataFile playerData, StorageAddition addition, MemoryStream updated, bool toolbelt)
    {
        return CommitInventoryDeposit(player, playerData, new[] { addition }, updated, toolbelt);
    }

    private static bool CommitInventoryDeposit(EntityPlayer player, PlayerDataFile playerData, IEnumerable<StorageAddition> additions, MemoryStream updated, bool toolbelt)
    {
        if (!NetworkStorage.AddBatch(player, additions)) return false;
        try
        {
            ApplyPlayerInventorySnapshot(player, updated, toolbelt);
            if (toolbelt) playerData.inventoryData = updated;
            else playerData.bagData = updated;
            playerData.bModifiedSinceLastSave = true;
            return true;
        }
        catch (Exception ex)
        {
            if (!NetworkStorage.RemoveBatch(player, additions))
                NetworkLog.Error("Could not compensate network inventory after a live player inventory update failed. player=" + player.entityId + "; toolbelt=" + toolbelt + "; exception=" + ex);
            try { ApplyPlayerInventorySnapshot(player, toolbelt ? playerData.inventoryData : playerData.bagData, toolbelt); }
            catch (Exception restoreException) { NetworkLog.Error("Could not restore the server-side player inventory snapshot after a failed deposit. player=" + player.entityId + "; toolbelt=" + toolbelt + "; exception=" + restoreException); }
            NetworkLog.Error("Server-side player inventory update failed during deposit. player=" + player.entityId + "; toolbelt=" + toolbelt + "; exception=" + ex);
            return false;
        }
    }

    private static void ApplyPlayerInventorySnapshot(EntityPlayer player, MemoryStream snapshot, bool toolbelt)
    {
        if (player == null || snapshot == null) throw new InvalidDataException("Player inventory snapshot was unavailable.");
        using MemoryStream input = new MemoryStream(snapshot.ToArray(), writable: false);
        if (toolbelt)
        {
            int selectedSlot = player.inventory.holdingItemIdx;
            StreamUtils.FromBlob(input, reader => player.inventory.ReadInto(reader, StreamModeRead.Persistency));
            if (selectedSlot >= 0 && selectedSlot < player.inventory.Length) player.inventory.SetSelectedSlot(selectedSlot);
        }
        else
            StreamUtils.FromBlob(input, reader => player.bag.ReadInto(reader, StreamModeRead.Persistency));
    }

    private static NetworkRemoteResult ReturnWithdrawal(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        string originalKey = request.EntityId + ":" + request.OriginalRequestId;
        if (ReturnedWithdrawals.Contains(originalKey)) return Failure(request, "WithdrawalAlreadyReturned");
        if (!CompletedRequests.TryGetValue(originalKey, out CachedRequest original)
            || original.Request.Operation != NetworkRequestOperation.WithdrawCursor
            || !original.Result.Success || original.Result.Value == null || request.Value == null
            || !NetworkStorage.NormalizeKey(original.Result.Value).Equals(NetworkStorage.NormalizeKey(request.Value))
            || request.Count > original.Result.Amount)
            return Failure(request, "WithdrawalReturnNotAuthorized");
        ItemStack cursor = playerData?.dragAndDropItem;
        if (cursor == null || cursor.IsEmpty()
            || !NetworkStorage.NormalizeKey(cursor.itemValue).Equals(NetworkStorage.NormalizeKey(original.Result.Value))
            || cursor.count < request.Count)
            return Failure(request, "WithdrawalNoLongerEscrowed");
        if (!NetworkStorage.AddStack(player, new ItemStack(original.Result.Value.Clone(), request.Count)))
            return Failure(request, "WithdrawalReturnCommitFailed");
        int remaining = cursor.count - request.Count;
        playerData.dragAndDropItem = remaining > 0 ? new ItemStack(cursor.itemValue.Clone(), remaining) : ItemStack.Empty.Clone();
        playerData.bModifiedSinceLastSave = true;
        ReturnedWithdrawals.Add(originalKey);
        NetworkLog.Debug("Unaccepted withdrawal returned to network. player=" + player.entityId + "; originalRequestId=" + request.OriginalRequestId + "; returned=" + request.Count);
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = request.Count };
    }

    private static bool TryUpdateBag(MemoryStream blob, int slot, ItemValue expected, int count, out ItemStack source, out MemoryStream updated)
    {
        source = null;
        updated = null;
        if (blob == null || blob.Length == 0) return false;
        Bag bag;
        try { bag = ReadBag(blob); }
        catch (Exception ex) { NetworkLog.Error("Could not read the requesting player's backpack snapshot. " + ex); return false; }
        if (bag == null || slot >= bag.SlotCount || bag.LockedSlots != null && slot < bag.LockedSlots.Length && bag.LockedSlots[slot]) return false;
        source = bag.ItemGrid[slot];
        if (source == null || source.IsEmpty() || source.count < count
            || !NetworkStorage.NormalizeKey(source.itemValue).Equals(NetworkStorage.NormalizeKey(expected))) return false;
        ItemStack remainder = source.Clone();
        remainder.count -= count;
        bag.ItemGrid.SetItem(slot, remainder.count > 0 ? remainder : ItemStack.Empty.Clone());
        updated = WriteBag(bag);
        return true;
    }

    private static bool TryUpdateToolbelt(MemoryStream blob, int slot, ItemValue expected, int count, out ItemStack source, out MemoryStream updated)
    {
        source = null;
        updated = null;
        if (blob == null || blob.Length == 0) return false;
        try
        {
            ItemStackGrid grid;
            byte selectedSlot;
            using (MemoryStream input = new MemoryStream(blob.ToArray(), writable: false))
            using (PooledBinaryReader reader = MemoryPools.poolBinaryReader.AllocSync(false))
            {
                reader.SetBaseStream(input);
                byte version = reader.ReadByte();
                if (version != 1) return false;
                grid = ItemStackGrid.Read(reader, StreamModeRead.Persistency);
                selectedSlot = reader.ReadByte();
                if (input.Position != input.Length) return false;
            }
            if (slot >= grid.Length) return false;
            source = grid[slot];
            if (source == null || source.IsEmpty() || source.count < count
                || !NetworkStorage.NormalizeKey(source.itemValue).Equals(NetworkStorage.NormalizeKey(expected))) return false;
            ItemStack remainder = source.Clone();
            remainder.count -= count;
            grid.SetItem(slot, remainder.count > 0 ? remainder : ItemStack.Empty.Clone());
            using (MemoryStream output = new MemoryStream())
            using (PooledBinaryWriter writer = MemoryPools.poolBinaryWriter.AllocSync(false))
            {
                writer.SetBaseStream(output);
                BinaryWriter binary = writer;
                binary.Write((byte)1);
                grid.Write(writer, StreamModeWrite.Persistency);
                binary.Write(selectedSlot);
                writer.Flush();
                updated = new MemoryStream(output.ToArray(), writable: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not update the requesting player's toolbelt snapshot. " + ex);
            source = null;
            updated = null;
            return false;
        }
    }

    private static Bag ReadBag(MemoryStream blob)
    {
        Bag bag = null;
        StreamUtils.FromBlob(new MemoryStream(blob.ToArray(), writable: false), reader =>
        {
            bag = Bag.Read(reader, XUiC_ItemStack.StackLocationTypes.Backpack, StreamModeRead.Persistency, null);
        });
        return bag;
    }

    private static MemoryStream WriteBag(Bag bag)
    {
        return StreamUtils.ToBlob(writer => bag.Write(writer, StreamModeWrite.Persistency));
    }

    private static bool PlayerHoldsLootLock(int playerEntityId, TEFeatureStorage storage)
    {
        LockManager manager = LockManager.Instance;
        return manager != null && (HasLock(manager.singleLocks, playerEntityId, storage) || HasLock(manager.sharedLocks, playerEntityId, storage));
    }

    private static bool HasLock(object locks, int playerEntityId, TEFeatureStorage storage)
    {
        if (locks == null) return false;
        System.Reflection.MethodInfo lookup = locks.GetType().GetMethods()
            .FirstOrDefault(method => method.Name == "TryGetByKey" && method.GetParameters().Length == 2
                && method.GetParameters()[0].ParameterType == typeof(int) && method.GetParameters()[1].IsOut);
        if (lookup == null) return false;
        object[] arguments = { playerEntityId, null };
        if (!(bool)lookup.Invoke(locks, arguments) || !(arguments[1] is System.Collections.IEnumerable entries)) return false;
        foreach (object entry in entries)
        {
            Type type = entry.GetType();
            object target = type.GetField("Target")?.GetValue(entry) ?? type.GetProperty("Target")?.GetValue(entry, null);
            object channel = type.GetField("Channel")?.GetValue(entry) ?? type.GetProperty("Channel")?.GetValue(entry, null);
            if (ReferenceEquals(target, storage) && channel is ushort value && value == 0) return true;
        }
        return false;
    }

    private static bool SameRequest(NetworkRemoteRequest left, NetworkRemoteRequest right)
    {
        if (left.Operation != right.Operation || left.SourceLocation != right.SourceLocation || left.EntityId != right.EntityId || left.Slot != right.Slot
            || left.Count != right.Count || left.OriginalRequestId != right.OriginalRequestId || !left.Position.Equals(right.Position)
            || !string.Equals(left.ExpectedNetworkId, right.ExpectedNetworkId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(left.Category, right.Category, StringComparison.Ordinal)
            || !string.Equals(left.Search, right.Search, StringComparison.Ordinal)) return false;
        if (left.Value == null || right.Value == null) return left.Value == right.Value;
        return left.Value.Equals(right.Value);
    }

    private static NetworkRemoteRequest Clone(NetworkRemoteRequest request)
    {
        return new NetworkRemoteRequest
        {
            EntityId = request.EntityId,
            RequestId = request.RequestId,
            OriginalRequestId = request.OriginalRequestId,
            ExpectedNetworkId = request.ExpectedNetworkId,
            Operation = request.Operation,
            SourceLocation = request.SourceLocation,
            Category = request.Category,
            Search = request.Search,
            Page = request.Page,
            PageSize = request.PageSize,
            Value = request.Value?.Clone(),
            Count = request.Count,
            Slot = request.Slot,
            Position = request.Position
        };
    }

    private static NetworkRemoteResult Clone(NetworkRemoteResult result)
    {
        return new NetworkRemoteResult
        {
            Success = result.Success,
            Error = result.Error,
            RequestId = result.RequestId,
            Revision = result.Revision,
            NetworkId = result.NetworkId,
            TotalEntries = result.TotalEntries,
            Page = result.Page,
            PageSize = result.PageSize,
            Amount = result.Amount,
            UpdatedSourceCount = result.UpdatedSourceCount,
            Value = result.Value?.Clone(),
            Entries = result.Entries?.Select(entry => new StoredEntry { Value = entry.Value.Clone(), Count = entry.Count }).ToList() ?? new List<StoredEntry>(),
            BagData = result.BagData == null ? null : (byte[])result.BagData.Clone(),
            InventoryData = result.InventoryData == null ? null : (byte[])result.InventoryData.Clone()
        };
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
    {
        return new NetworkRemoteResult
        {
            RequestId = request?.RequestId ?? 0,
            Success = false,
            Error = error ?? "Failure"
        };
    }

    private static void SendResponse(int entityId, NetworkRemoteResult result)
    {
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (!(bool)connection || !connection.IsServer) return;
        connection.SendPackage(NetPackageManager.GetPackage<NetPackageHyperdimensionalResponse>().Setup(entityId, result), _onlyClientsAttachedToAnEntity: false, entityId);
    }
}
