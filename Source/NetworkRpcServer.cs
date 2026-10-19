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
    private static readonly Dictionary<string, List<StorageAddition>> ReturnedAuthorizations = new Dictionary<string, List<StorageAddition>>(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> ReturnedAmmoBankUnits = new Dictionary<string, int>(StringComparer.Ordinal);
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
            else if (!NetworkMembershipService.IsMembershipOperation(request.Operation) && !string.IsNullOrEmpty(request.ExpectedNetworkId)
                && !string.Equals(request.ExpectedNetworkId, networkId, StringComparison.OrdinalIgnoreCase))
            {
                result = Failure(request, "StaleNetwork");
                NetworkLog.Debug("Rejected stale network request. player=" + player.entityId + "; expected=" + request.ExpectedNetworkId + "; actual=" + networkId + "; operation=" + request.Operation);
            }
            else if (NetworkMembershipService.ShouldRejectRequest(player, networkId, request.Operation))
            {
                result = Failure(request, "MembershipTransitionInProgress");
            }
            else if (IsTraderOperation(request.Operation) && !NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TraderEnabled))
            {
                NetworkLog.Debug("Trader request rejected because the network feature is disabled. player=" + player.entityId + "; network=" + networkId + "; operation=" + request.Operation);
                result = Failure(request, "TraderDisabled");
            }
            else if (request.Operation == NetworkRequestOperation.MembershipQuery)
            {
                result = NetworkMembershipService.ProcessRequest(world, player, request);
            }
            else if (NetworkMembershipService.IsMembershipOperation(request.Operation))
            {
                result = ProcessMutation(world, player, sender, request);
            }
            else if ((request.Operation == NetworkRequestOperation.ReturnAuthorization
                || request.Operation == NetworkRequestOperation.ReturnWithdrawal || request.Operation == NetworkRequestOperation.AmmoBankRefund)
                && NetworkMembershipService.IsPlayerBlocked(player))
            {
                using (NetworkMembershipService.EnterInternalNetworkScope(networkId))
                    result = ProcessMutation(world, player, sender, request);
            }
            else if (request.Operation == NetworkRequestOperation.QueryPage)
            {
                result = QueryPage(player, request);
            }
            else if (request.Operation == NetworkRequestOperation.QueryMatching)
            {
                result = QueryMatching(player, request);
            }
            else if (request.Operation == NetworkRequestOperation.QueryRestockSettings)
            {
                result = RestockSettingsService.TryGet(player, out RestockSettingsSnapshot settings, out string error)
                    ? RestockSettingsResult(request, settings)
                    : Failure(request, error);
            }
            else if (request.Operation == NetworkRequestOperation.TraderQueryMarket)
            {
                result = TraderMarketplaceService.Query(world, player, request);
            }
            else if (request.Operation == NetworkRequestOperation.TraderQueryRules)
            {
                result = TraderAutomationService.QueryRules(player, request);
            }
            else
            {
                result = ProcessMutation(world, player, sender, request);
            }
            result.NetworkId = PlayerNetworkDirectory.GetNetworkId(player) ?? string.Empty;
            result.Revision = NetworkStorage.GetRevision(player);
            result.MembershipRevision = PlayerNetworkDirectory.GetMembershipRevision();
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Network request failed safely. player=" + player?.entityId + "; requestId=" + request?.RequestId + "; operation=" + request?.Operation + "; exception=" + ex);
            result = Failure(request, "InternalFailure");
        }

        SendResponse(request.EntityId, result);
    }

    private static bool IsTraderOperation(NetworkRequestOperation operation)
    {
        return operation == NetworkRequestOperation.TraderVoidSell || operation == NetworkRequestOperation.TraderOpenMarket
            || operation == NetworkRequestOperation.TraderQueryMarket || operation == NetworkRequestOperation.TraderBuyItem;
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

    private static NetworkRemoteResult QueryMatching(EntityPlayer player, NetworkRemoteRequest request)
    {
        if (request.Requirements == null || request.Requirements.Count == 0 || request.Requirements.Count > 64)
            return Failure(request, "InvalidMatchingQuery");
        List<StoredEntry> entries = NetworkStorage.GetEntriesForRequirements(player, request.Requirements);
        if (entries.Count > 4096)
        {
            NetworkLog.Error("Matching query exceeded the response variant limit. player=" + player.entityId + "; requestId=" + request.RequestId + "; variants=" + entries.Count);
            return Failure(request, "MatchingQueryTooBroad");
        }
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Entries = entries };
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
                ReturnedAuthorizations.Remove(oldest);
                ReturnedAmmoBankUnits.Remove(oldest);
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
            case NetworkRequestOperation.DepositEntityBagAll:
                return DepositEntityBagAll(world, player, request);
            case NetworkRequestOperation.WithdrawCursor:
            case NetworkRequestOperation.WithdrawBackpack:
                return Withdraw(player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.ReturnWithdrawal:
                return ReturnWithdrawal(player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.ConsumeRequirements:
                return ConsumeRequirements(world, player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.VehicleRefuel:
                return RefuelVehicle(world, player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.VehicleRepair:
                return RepairVehicle(world, player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.ReturnAuthorization:
                return ReturnAuthorization(player, request);
            case NetworkRequestOperation.SetRestockSetting:
                return SetRestockSetting(player, request);
            case NetworkRequestOperation.ManualRestock:
            case NetworkRequestOperation.AutomaticRestock:
                return RestockTransactionService.Execute(world, player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.AmmoBankReload:
                return AmmoBankService.ExecuteRemoteReload(player, request);
            case NetworkRequestOperation.AmmoBankRefund:
                return ReturnAmmoBankSupply(player, request);
            case NetworkRequestOperation.TraderVoidSell:
                return TraderTransactionService.ExecuteVoidSell(player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.TraderOpenMarket:
                return TraderMarketplaceService.Open(world, player, request);
            case NetworkRequestOperation.TraderCloseMarket:
                return TraderMarketplaceService.Close(player, request);
            case NetworkRequestOperation.TraderBuyItem:
                return TraderMarketplaceService.Buy(world, player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.TraderSaveRule:
            case NetworkRequestOperation.TraderDeleteRule:
            case NetworkRequestOperation.TraderMoveRule:
            case NetworkRequestOperation.TraderRunRules:
                return TraderAutomationService.ExecuteRuleRequest(world, player, request);
            case NetworkRequestOperation.AutomationProcessSlot:
                return InventoryAutomationService.ExecuteSlot(player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.TrashOpen:
            case NetworkRequestOperation.TrashPut:
            case NetworkRequestOperation.TrashTake:
            case NetworkRequestOperation.TrashClose:
                return TrashContainerService.Execute(player, sender?.latestPlayerData, request);
            case NetworkRequestOperation.BookConsumeNetwork:
            case NetworkRequestOperation.BookScrapNetwork:
                return BookNetworkService.Execute(player, request);
            case NetworkRequestOperation.MembershipInvite:
            case NetworkRequestOperation.MembershipAcceptInvite:
            case NetworkRequestOperation.MembershipDeclineInvite:
            case NetworkRequestOperation.MembershipLeave:
            case NetworkRequestOperation.MembershipKick:
            case NetworkRequestOperation.MembershipTransferAdministrator:
            case NetworkRequestOperation.MembershipAcknowledgePrepare:
                return NetworkMembershipService.ProcessRequest(world, player, request);
            default:
                return Failure(request, "UnsupportedOperation");
        }
    }

    private static NetworkRemoteResult SetRestockSetting(EntityPlayer player, NetworkRemoteRequest request)
    {
        if (!RestockSettingsService.TrySet(player, request.RestockSetting, request.RestockSettingValue, out string error, out RestockSettingsSnapshot settings))
            return Failure(request, error);
        return RestockSettingsResult(request, settings, success: true);
    }

    private static NetworkRemoteResult ReturnAmmoBankSupply(EntityPlayer player, NetworkRemoteRequest request)
    {
        if (request.OriginalRequestId <= 0 || request.Value == null || request.Count <= 0)
            return Failure(request, "InvalidAmmoRefund");
        string originalKey = request.EntityId + ":" + request.OriginalRequestId;
        if (!CompletedRequests.TryGetValue(originalKey, out CachedRequest original)
            || original.Request.Operation != NetworkRequestOperation.AmmoBankReload || !original.Result.Success
            || original.Request.Value == null || !NetworkStorage.NormalizeKey(original.Request.Value).Equals(NetworkStorage.NormalizeKey(request.Value)))
            return Failure(request, "AmmoRefundNotAuthorized");
        int returned = ReturnedAmmoBankUnits.TryGetValue(originalKey, out int totalReturned) ? totalReturned : 0;
        if (request.Count > original.Result.Amount - returned)
            return Failure(request, "AmmoRefundExceedsSupply");
        if (!NetworkStorage.AddStack(player, new ItemStack(original.Request.Value.Clone(), request.Count)))
            return Failure(request, "AmmoRefundFailed");
        ReturnedAmmoBankUnits[originalKey] = returned + request.Count;
        NetworkLog.Debug("Unused ammo bank supply returned to network. player=" + player.entityId + "; originalRequestId=" + request.OriginalRequestId + "; amount=" + request.Count);
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = request.Count };
    }

    private static NetworkRemoteResult RestockSettingsResult(NetworkRemoteRequest request, RestockSettingsSnapshot settings, bool success = true)
    {
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = success,
            RestockSettingsLoaded = true,
            AutoRestockEnabled = settings.AutoRestockEnabled,
            RestockTargetMode = settings.TargetMode,
            IsNetworkAdministrator = settings.IsNetworkAdministrator,
            AutoRestockCooldownMs = settings.AutoRestockCooldownMs,
            ReplenishmentEnabled = settings.ReplenishmentEnabled,
            AmmoBankEnabled = settings.AmmoBankEnabled,
            TraderEnabled = settings.TraderEnabled,
            CraftingChainEnabled = settings.CraftingChainEnabled,
            AutoDepositEnabled = settings.AutoDepositEnabled,
            TrashEnabled = settings.TrashEnabled,
            BookReadingEnabled = settings.BookReadingEnabled,
            RetainSearchOnClose = settings.RetainSearchOnClose,
            ManualSaleEnabled = settings.ManualSaleEnabled,
            PersonalAutoDeposit = settings.PersonalAutoDeposit,
            PersonalAutoTrash = settings.PersonalAutoTrash,
            PersonalTrashScope = settings.PersonalTrashScope,
            PersonalAutoRead = settings.PersonalAutoRead,
            ReadBookPolicy = settings.ReadBookPolicy
        };
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

        NetworkStackCompatibility.RestoreExtendedCount(cursor);
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

    internal static NetworkRemoteResult DepositPlayerSlot(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request, bool toolbelt)
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
        float interactDistance = Constants.cPlayerInteractDistance;
        float distanceSq = (player.position - center).sqrMagnitude;
        if (distanceSq > interactDistance * interactDistance)
        {
            NetworkLog.Debug("Remote loot container deposit rejected by the vanilla interaction-distance check. player=" + player.entityId
                + "; position=" + request.Position + "; distance=" + Math.Sqrt(distanceSq) + "; maximum=" + interactDistance);
            return Failure(request, "OutOfRange");
        }

        List<StorageAddition> additions = new List<StorageAddition>();
        List<KeyValuePair<int, ItemStack>> originalStacks = new List<KeyValuePair<int, ItemStack>>();
        NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(storage.ItemGrid));
        PackedBoolArray lockedSlots = storage.ItemGrid.SlotLocks;
        for (int i = 0; i < storage.ItemGrid.Length; i++)
        {
            if (lockedSlots != null && i < lockedSlots.Length && lockedSlots[i]) continue;
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
                if ((lockedSlots == null || i >= lockedSlots.Length || !lockedSlots[i])
                    && (storage.ItemGrid[i] == null || !storage.ItemGrid[i].IsEmpty())) storage.UpdateSlot(i, ItemStack.Empty.Clone());
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

    private static NetworkRemoteResult DepositEntityBagAll(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        Entity bagEntity = world.GetEntity(request.TargetEntityId);
        if (bagEntity?.bag == null || !(bagEntity is ILockTarget lockTarget) || !PlayerHoldsLootLock(player.entityId, lockTarget))
            return Failure(request, "EntityBagUnavailable");

        float interactDistance = bagEntity is EntityDrone ? Constants.cDigAndBuildDistance + 1f : Constants.cPlayerInteractDistance;
        float distanceSq = bagEntity.GetDistanceSq(player);
        if (distanceSq > interactDistance * interactDistance)
        {
            NetworkLog.Debug("Remote entity bag deposit rejected by the vanilla interaction-distance check. player=" + player.entityId
                + "; entity=" + bagEntity.entityId + "; type=" + bagEntity.GetType().Name + "; distance=" + Math.Sqrt(distanceSq) + "; maximum=" + interactDistance);
            return Failure(request, "OutOfRange");
        }

        Bag bag = bagEntity.bag;
        NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(bag));
        List<StorageAddition> additions = new List<StorageAddition>();
        List<KeyValuePair<int, ItemStack>> originalStacks = new List<KeyValuePair<int, ItemStack>>();
        PackedBoolArray lockedSlots = bag.LockedSlots;
        for (int i = 0; i < bag.SlotCount; i++)
        {
            if (lockedSlots != null && i < lockedSlots.Length && lockedSlots[i]) continue;
            ItemStack stack = bag.ItemGrid[i];
            if (stack == null || stack.IsEmpty()) continue;
            ItemStack clone = stack.Clone();
            originalStacks.Add(new KeyValuePair<int, ItemStack>(i, clone));
            additions.Add(new StorageAddition { Value = clone.itemValue.Clone(), Count = clone.count });
        }
        if (additions.Count == 0) return Failure(request, "NoItems");
        if (!NetworkStorage.AddBatch(player, additions)) return Failure(request, "StorageCommitFailed");

        byte[] updatedBag;
        bool committed = false;
        try
        {
            for (int i = 0; i < bag.SlotCount; i++)
                if ((lockedSlots == null || i >= lockedSlots.Length || !lockedSlots[i])
                    && (bag.ItemGrid[i] == null || !bag.ItemGrid[i].IsEmpty())) bag.SetSlot(i, ItemStack.Empty.Clone());
            bag.ItemGrid.Touch();
            updatedBag = WriteEntityBag(bag);
            SynchronizeEntityBag(bagEntity);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                if (!NetworkStorage.RemoveBatch(player, additions))
                    NetworkLog.Error("Could not compensate network items after an entity bag update failure. player=" + player.entityId + "; entity=" + bagEntity.entityId + "; requestId=" + request.RequestId);
                foreach (KeyValuePair<int, ItemStack> original in originalStacks)
                {
                    try
                    {
                        if (original.Key < bag.SlotCount) bag.SetSlot(original.Key, original.Value);
                    }
                    catch (Exception ex)
                    {
                        NetworkLog.Error("Entity bag rollback could not restore a slot. entity=" + bagEntity.entityId + "; slot=" + original.Key + "; exception=" + ex);
                    }
                }
                try { bag.ItemGrid.Touch(); }
                catch (Exception ex) { NetworkLog.Error("Entity bag rollback could not refresh its inventory state. entity=" + bagEntity.entityId + "; exception=" + ex); }
                try { SynchronizeEntityBag(bagEntity); }
                catch (Exception ex) { NetworkLog.Error("Entity bag rollback could not synchronize restored contents. entity=" + bagEntity.entityId + "; exception=" + ex); }
            }
        }

        NetworkLog.Debug("Entity bag deposited into network. player=" + player.entityId + "; entity=" + bagEntity.entityId + "; type=" + bagEntity.GetType().Name
            + "; variants=" + additions.Count + "; total=" + additions.Sum(item => item.Count) + "; requestId=" + request.RequestId);
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            Amount = additions.Sum(entry => (int)Math.Min(entry.Count, int.MaxValue)),
            EntityBagData = updatedBag
        };
    }

    private static void SynchronizeEntityBag(Entity entity)
    {
        if (entity is EntityVehicle vehicle) vehicle.SetBagModified();
        else if (entity is EntityDrone drone) drone.SendSyncData(8);
    }

    private static byte[] WriteEntityBag(Bag bag)
    {
        List<ItemValue> writeMetadata = NetworkStackCompatibility.PrepareForWrite(NetworkStackCompatibility.GetSlots(bag));
        try { return StreamUtils.ToBlob(writer => bag.Write(writer, StreamModeWrite.ToClient)).ToArray(); }
        finally { NetworkStackCompatibility.ClearWriteMetadata(writeMetadata); }
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

    private static NetworkRemoteResult ConsumeRequirements(World world, EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        if (request.ConsumptionSource == NetworkConsumptionSource.CraftingChainReservation
            && !NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.CraftingChainEnabled))
        {
            NetworkLog.Debug("Crafting-chain reservation rejected because the network feature is disabled. player=" + player.entityId + "; requestId=" + request.RequestId);
            return Failure(request, "CraftingChainDisabled");
        }
        if (request.Requirements == null || request.Requirements.Count == 0 || request.Requirements.Count > 64)
            return Failure(request, "InvalidMaterialRequirements");
        if (request.ConsumptionSource == NetworkConsumptionSource.CraftingChainReservation && request.Count > 0)
        {
            if (request.Value == null || request.Count > request.Requirements.Where(item => item != null
                && NetworkItemMatcher.Matches(item.itemValue, request.Value)).Sum(item => (long)item.count)
                || NetworkStorage.GetMatchingCount(player, request.Value) < request.Count)
            {
                NetworkLog.Debug("Crafting-chain target network stock changed before server reservation. player="
                    + player.entityId + "; requested=" + request.Count + "; item=" + request.Value?.type);
                return Failure(request, "InsufficientMaterials");
            }
        }
        List<ItemStack> physical;
        if (request.ConsumptionSource == NetworkConsumptionSource.WorkstationInput)
        {
            TileEntityWorkstation workstation = world?.GetTileEntity(request.Position) as TileEntityWorkstation;
            if (workstation == null || workstation.Input == null) return Failure(request, "WorkstationUnavailable");
            Vector3 center = workstation.ToWorldPos().ToVector3() + Vector3.one * 0.5f;
            if ((player.position - center).sqrMagnitude > 64f) return Failure(request, "WorkstationOutOfRange");
            physical = new List<ItemStack>();
            foreach (ItemStack stack in workstation.Input)
                if (stack != null && !stack.IsEmpty()) physical.Add(stack.Clone());
        }
        else if (request.ConsumptionSource == NetworkConsumptionSource.PlayerInventory
            || request.ConsumptionSource == NetworkConsumptionSource.NetworkPreferredPlayerInventory
            || request.ConsumptionSource == NetworkConsumptionSource.CraftingChainReservation)
        {
            if (playerData == null || playerData.bagData == null || playerData.inventoryData == null)
                return Failure(request, "PlayerInventoryUnavailable");
            try { physical = ReadPlayerInventory(playerData); }
            catch (Exception ex)
            {
                NetworkLog.Error("Could not read remote player's inventory for a material transaction. player=" + player.entityId + "; requestId=" + request.RequestId + "; exception=" + ex);
                return Failure(request, "PlayerInventoryUnavailable");
            }
        }
        else return Failure(request, "InvalidMaterialSource");

        List<StoredEntry> network = NetworkStorage.GetEntriesForRequirements(player, request.Requirements);
        List<StorageAddition> removals = new List<StorageAddition>();
        foreach (ItemStack requirement in request.Requirements)
        {
            if (requirement?.itemValue == null || requirement.count <= 0) return Failure(request, "InvalidMaterialRequirements");
            long remaining = requirement.count;
            if (request.ConsumptionSource == NetworkConsumptionSource.NetworkPreferredPlayerInventory
                || request.ConsumptionSource == NetworkConsumptionSource.CraftingChainReservation)
                ConsumeNetwork(requirement, network, removals, ref remaining);
            foreach (ItemStack carried in physical)
            {
                if (remaining == 0) break;
                if (carried == null || carried.IsEmpty() || !NetworkItemMatcher.Matches(carried.itemValue, requirement.itemValue)) continue;
                int take = (int)Math.Min(remaining, carried.count);
                carried.count -= take;
                remaining -= take;
            }
            ConsumeNetwork(requirement, network, removals, ref remaining);
            if (remaining > 0)
            {
                NetworkLog.Debug("Remote material transaction rejected for insufficient exact materials. player=" + player.entityId + "; requestId=" + request.RequestId + "; item=" + requirement.itemValue.type + "; quality=" + requirement.itemValue.Quality + "; missing=" + remaining);
                return Failure(request, "InsufficientMaterials");
            }
        }
        if (removals.Count > 4096)
        {
            NetworkLog.Error("Material transaction exceeded the response variant limit. player=" + player.entityId + "; requestId=" + request.RequestId + "; variants=" + removals.Count);
            return Failure(request, "MaterialTransactionTooBroad");
        }
        if (removals.Count > 0 && !NetworkStorage.RemoveBatch(player, removals))
            return Failure(request, "NetworkMaterialCommitFailed");
        NetworkLog.Debug("Remote material transaction committed. player=" + player.entityId + "; requestId=" + request.RequestId + "; source=" + request.ConsumptionSource + "; requirements=" + request.Requirements.Count + "; networkVariants=" + removals.Count);
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            Entries = removals.Select(item => new StoredEntry { Value = item.Value.Clone(), Count = item.Count }).ToList(),
            Amount = (int)Math.Min(removals.Sum(item => item.Count), int.MaxValue)
        };
    }

    private static void ConsumeNetwork(ItemStack requirement, List<StoredEntry> network,
        List<StorageAddition> removals, ref long remaining)
    {
        foreach (StoredEntry entry in network)
        {
            if (remaining == 0) break;
            if (entry.Count <= 0 || !NetworkItemMatcher.Matches(entry.Value, requirement.itemValue)) continue;
            long take = Math.Min(remaining, entry.Count);
            entry.Count -= take;
            remaining -= take;
            AddPlannedRemoval(removals, entry.Value, take);
        }
    }

    private static NetworkRemoteResult RefuelVehicle(World world, EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        EntityVehicle entity = world?.GetEntity(request.Slot) as EntityVehicle;
        if (entity == null || entity.GetVehicle() == null || !IsNearEntity(player, entity, 8f)) return Failure(request, "VehicleUnavailable");
        string fuelName = entity.GetVehicle().GetFuelItem();
        ItemValue fuel = string.IsNullOrEmpty(fuelName) ? null : ItemClass.GetItem(fuelName);
        if (fuel?.ItemClass == null || entity.GetVehicle().GetFuelPercent() >= 1f) return Failure(request, "VehicleFuelUnavailable");
        if (!SyncPlayerInventoryFromLatest(player, playerData)) return Failure(request, "PlayerInventoryUnavailable");
        int requested = Mathf.CeilToInt(Mathf.Min(2500f,
            (entity.GetVehicle().GetMaxFuelLevel() - entity.GetVehicle().GetFuelLevel()) * 25f));
        int amount = Math.Min(requested, ActionResourceService.Count(player, fuel));
        if (amount <= 0 || !ActionResourceService.Consume(player, fuel, amount)) return Failure(request, "FuelUnavailable");
        try
        {
            entity.GetVehicle().AddFuel(amount / 25f);
            CapturePlayerInventory(player, playerData, out byte[] bagData, out byte[] inventoryData);
            NetworkLog.Debug("Remote vehicle refueled. player=" + player.entityId + "; vehicle=" + entity.entityId + "; item=" + fuel.type + "; amount=" + amount);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = amount, BagData = bagData, InventoryData = inventoryData };
        }
        catch (Exception ex)
        {
            NetworkStorage.AddStack(player, new ItemStack(fuel.Clone(), amount));
            NetworkLog.Error("Remote vehicle refueling failed after material reservation. player=" + player.entityId + "; vehicle=" + entity.entityId + "; exception=" + ex);
            return Failure(request, "VehicleRefuelFailed");
        }
    }

    private static NetworkRemoteResult RepairVehicle(World world, EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        EntityVehicle entity = world?.GetEntity(request.Slot) as EntityVehicle;
        if (entity == null || entity.GetVehicle() == null || !IsNearEntity(player, entity, 8f)
            || entity.GetVehicle().GetRepairAmountNeeded() <= 0) return Failure(request, "VehicleUnavailable");
        ItemValue kit = ItemClass.GetItem("resourceRepairKit");
        if (kit?.ItemClass == null || !SyncPlayerInventoryFromLatest(player, playerData)
            || !ActionResourceService.Consume(player, kit, 1)) return Failure(request, "RepairKitUnavailable");
        try
        {
            float bonus = 0f;
            ProgressionValue perk = player.Progression?.GetProgressionValue("perkGreaseMonkey");
            if (perk != null) bonus = perk.Level * 0.1f;
            entity.GetVehicle().RepairParts(1000, bonus);
            CapturePlayerInventory(player, playerData, out byte[] bagData, out byte[] inventoryData);
            NetworkLog.Debug("Remote vehicle repaired. player=" + player.entityId + "; vehicle=" + entity.entityId + "; item=" + kit.type);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = 1, BagData = bagData, InventoryData = inventoryData };
        }
        catch (Exception ex)
        {
            NetworkStorage.AddStack(player, new ItemStack(kit.Clone(), 1));
            NetworkLog.Error("Remote vehicle repair failed after material reservation. player=" + player.entityId + "; vehicle=" + entity.entityId + "; exception=" + ex);
            return Failure(request, "VehicleRepairFailed");
        }
    }

    private static List<ItemStack> ReadPlayerInventory(PlayerDataFile playerData)
    {
        Bag bag = ReadBag(playerData.bagData);
        if (bag == null) throw new InvalidDataException("Backpack snapshot was empty.");
        List<ItemStack> result = new List<ItemStack>();
        for (int i = 0; i < bag.SlotCount; i++)
        {
            ItemStack stack = bag.ItemGrid[i];
            if (stack != null && !stack.IsEmpty()) result.Add(stack.Clone());
        }
        using (MemoryStream input = new MemoryStream(playerData.inventoryData.ToArray(), writable: false))
        using (PooledBinaryReader reader = MemoryPools.poolBinaryReader.AllocSync(false))
        {
            reader.SetBaseStream(input);
            if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported toolbelt snapshot version.");
            ItemStackGrid grid = ItemStackGrid.Read(reader, StreamModeRead.Persistency);
            NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(grid));
            reader.ReadByte();
            if (input.Position != input.Length) throw new InvalidDataException("Unexpected trailing toolbelt snapshot data.");
            for (int i = 0; i < grid.Length; i++)
            {
                ItemStack stack = grid[i];
                if (stack != null && !stack.IsEmpty()) result.Add(stack.Clone());
            }
        }
        return result;
    }

    private static void AddPlannedRemoval(List<StorageAddition> removals, ItemValue value, long count)
    {
        ItemValue key = NetworkStorage.NormalizeKey(value);
        StorageAddition existing = removals.FirstOrDefault(item => NetworkStorage.NormalizeKey(item.Value).Equals(key));
        if (existing == null) removals.Add(new StorageAddition { Value = key, Count = count });
        else existing.Count += count;
    }

    private static bool SyncPlayerInventoryFromLatest(EntityPlayer player, PlayerDataFile playerData)
    {
        if (player == null || playerData?.bagData == null || playerData.inventoryData == null) return false;
        try
        {
            ApplyPlayerInventorySnapshot(player, playerData.bagData, toolbelt: false);
            ApplyPlayerInventorySnapshot(player, playerData.inventoryData, toolbelt: true);
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not apply latest remote player inventory before a vehicle transaction. player=" + player.entityId + "; exception=" + ex);
            return false;
        }
    }

    private static void CapturePlayerInventory(EntityPlayer player, PlayerDataFile playerData, out byte[] bagData, out byte[] inventoryData)
    {
        List<ItemValue> bagMetadata = NetworkStackCompatibility.PrepareForWrite(NetworkStackCompatibility.GetSlots(player.bag));
        List<ItemValue> inventoryMetadata = NetworkStackCompatibility.PrepareForWrite(NetworkStackCompatibility.GetSlots(player.inventory.ItemGrid));
        MemoryStream bag;
        MemoryStream inventory;
        try
        {
            bag = StreamUtils.ToBlob(writer => player.bag.Write(writer, StreamModeWrite.Persistency));
            inventory = StreamUtils.ToBlob(writer => player.inventory.Write(writer, StreamModeWrite.Persistency));
        }
        finally
        {
            NetworkStackCompatibility.ClearWriteMetadata(bagMetadata);
            NetworkStackCompatibility.ClearWriteMetadata(inventoryMetadata);
        }
        playerData.bagData = bag;
        playerData.inventoryData = inventory;
        playerData.bModifiedSinceLastSave = true;
        bagData = bag.ToArray();
        inventoryData = inventory.ToArray();
    }

    private static bool IsNearEntity(EntityPlayer player, Entity entity, float distance)
    {
        return (bool)player && (bool)entity && (player.position - entity.position).sqrMagnitude <= distance * distance;
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
        List<StorageAddition> batch = additions?.ToList() ?? new List<StorageAddition>();
        if (!NetworkStorage.AddBatch(player, batch))
        {
            NetworkLog.Debug("Network inventory deposit did not commit. player=" + player.entityId + "; toolbelt=" + toolbelt
                + "; variants=" + batch.Count + "; total=" + batch.Sum(item => item?.Count ?? 0));
            return false;
        }
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
            if (!NetworkStorage.RemoveBatch(player, batch))
                NetworkLog.Error("Could not compensate network inventory after a live player inventory update failed. player=" + player.entityId + "; toolbelt=" + toolbelt + "; exception=" + ex);
            try { ApplyPlayerInventorySnapshot(player, toolbelt ? playerData.inventoryData : playerData.bagData, toolbelt); }
            catch (Exception restoreException) { NetworkLog.Error("Could not restore the server-side player inventory snapshot after a failed deposit. player=" + player.entityId + "; toolbelt=" + toolbelt + "; exception=" + restoreException); }
            NetworkLog.Error("Server-side player inventory update failed during deposit. player=" + player.entityId + "; toolbelt=" + toolbelt + "; exception=" + ex);
            return false;
        }
    }

    internal static void ApplyPlayerInventorySnapshot(EntityPlayer player, MemoryStream snapshot, bool toolbelt)
    {
        if (player == null || snapshot == null) throw new InvalidDataException("Player inventory snapshot was unavailable.");
        using MemoryStream input = new MemoryStream(snapshot.ToArray(), writable: false);
        if (toolbelt)
        {
            int selectedSlot = player.inventory.holdingItemIdx;
            StreamUtils.FromBlob(input, reader => player.inventory.ReadInto(reader, StreamModeRead.Persistency));
            NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(player.inventory.ItemGrid));
            if (selectedSlot >= 0 && selectedSlot < player.inventory.Length) player.inventory.SetSelectedSlot(selectedSlot);
        }
        else
        {
            StreamUtils.FromBlob(input, reader => player.bag.ReadInto(reader, StreamModeRead.Persistency));
            NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(player.bag));
        }
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

    private static NetworkRemoteResult ReturnAuthorization(EntityPlayer player, NetworkRemoteRequest request)
    {
        string originalKey = request.EntityId + ":" + request.OriginalRequestId;
        if (!CompletedRequests.TryGetValue(originalKey, out CachedRequest original)
            || original.Request.Operation != NetworkRequestOperation.ConsumeRequirements || !original.Result.Success
            || !string.Equals(original.Request.ExpectedNetworkId, request.ExpectedNetworkId, StringComparison.OrdinalIgnoreCase)
            || request.Requirements == null || request.Requirements.Count == 0)
            return Failure(request, "AuthorizationReturnNotAllowed");
        ReturnedAuthorizations.TryGetValue(originalKey, out List<StorageAddition> previouslyReturned);
        List<StorageAddition> refunds = new List<StorageAddition>();
        foreach (ItemStack requested in request.Requirements)
        {
            if (requested?.itemValue == null || requested.count <= 0) return Failure(request, "InvalidAuthorizationReturn");
            ItemValue key = NetworkStorage.NormalizeKey(requested.itemValue);
            StoredEntry granted = original.Result.Entries.FirstOrDefault(entry => NetworkStorage.NormalizeKey(entry.Value).Equals(key));
            if (granted == null) return Failure(request, "AuthorizationReturnNotGranted");
            long alreadyReturned = previouslyReturned?.Where(item => NetworkStorage.NormalizeKey(item.Value).Equals(key)).Sum(item => item.Count) ?? 0;
            long thisRefund = refunds.Where(item => NetworkStorage.NormalizeKey(item.Value).Equals(key)).Sum(item => item.Count);
            if (requested.count > granted.Count - alreadyReturned - thisRefund) return Failure(request, "AuthorizationReturnExceedsGrant");
            AddPlannedRemoval(refunds, key, requested.count);
        }
        if (!NetworkStorage.AddBatch(player, refunds)) return Failure(request, "AuthorizationReturnCommitFailed");
        if (previouslyReturned == null)
        {
            previouslyReturned = new List<StorageAddition>();
            ReturnedAuthorizations[originalKey] = previouslyReturned;
        }
        foreach (StorageAddition refund in refunds) AddPlannedRemoval(previouslyReturned, refund.Value, refund.Count);
        NetworkLog.Debug("Unused network material authorization returned. player=" + player.entityId + "; originalRequestId=" + request.OriginalRequestId + "; variants=" + refunds.Count);
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = (int)Math.Min(refunds.Sum(item => item.Count), int.MaxValue) };
    }

    internal static bool TryUpdateBag(MemoryStream blob, int slot, ItemValue expected, int count, out ItemStack source, out MemoryStream updated, bool allowLocked = false)
    {
        source = null;
        updated = null;
        if (blob == null || blob.Length == 0) return false;
        Bag bag;
        try { bag = ReadBag(blob); }
        catch (Exception ex) { NetworkLog.Error("Could not read the requesting player's backpack snapshot. " + ex); return false; }
        if (bag == null || slot < 0 || slot >= bag.SlotCount || !allowLocked && bag.LockedSlots != null && slot < bag.LockedSlots.Length && bag.LockedSlots[slot]) return false;
        source = bag.ItemGrid[slot];
        bool matches = source != null && !source.IsEmpty()
            && NetworkStorage.NormalizeKey(source.itemValue).Equals(NetworkStorage.NormalizeKey(expected));
        if (!matches || source.count < count)
        {
            NetworkLog.Debug("Rejected backpack deposit snapshot. slot=" + slot + "; requestedItem=" + expected.type + "; requestedCount=" + count
                + "; snapshotItem=" + (source?.itemValue?.type ?? 0) + "; snapshotCount=" + (source?.count ?? 0) + "; itemMatches=" + matches);
            return false;
        }
        ItemStack remainder = source.Clone();
        remainder.count -= count;
        bag.ItemGrid.SetItem(slot, remainder.count > 0 ? remainder : ItemStack.Empty.Clone());
        updated = WriteBag(bag);
        return true;
    }

    internal static bool TryUpdateToolbelt(MemoryStream blob, int slot, ItemValue expected, int count, out ItemStack source, out MemoryStream updated)
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
                NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(grid));
                selectedSlot = reader.ReadByte();
                if (input.Position != input.Length) return false;
            }
            if (slot >= grid.Length) return false;
            source = grid[slot];
            bool matches = source != null && !source.IsEmpty()
                && NetworkStorage.NormalizeKey(source.itemValue).Equals(NetworkStorage.NormalizeKey(expected));
            if (!matches || source.count < count)
            {
                NetworkLog.Debug("Rejected toolbelt deposit snapshot. slot=" + slot + "; requestedItem=" + expected.type + "; requestedCount=" + count
                    + "; snapshotItem=" + (source?.itemValue?.type ?? 0) + "; snapshotCount=" + (source?.count ?? 0) + "; itemMatches=" + matches);
                return false;
            }
            ItemStack remainder = source.Clone();
            remainder.count -= count;
            grid.SetItem(slot, remainder.count > 0 ? remainder : ItemStack.Empty.Clone());
            using (MemoryStream output = new MemoryStream())
            using (PooledBinaryWriter writer = MemoryPools.poolBinaryWriter.AllocSync(false))
            {
                writer.SetBaseStream(output);
                BinaryWriter binary = writer;
                binary.Write((byte)1);
                List<ItemValue> writeMetadata = NetworkStackCompatibility.PrepareForWrite(NetworkStackCompatibility.GetSlots(grid));
                try { grid.Write(writer, StreamModeWrite.Persistency); }
                finally { NetworkStackCompatibility.ClearWriteMetadata(writeMetadata); }
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

    internal static Bag ReadBag(MemoryStream blob)
    {
        Bag bag = null;
        StreamUtils.FromBlob(new MemoryStream(blob.ToArray(), writable: false), reader =>
        {
            bag = Bag.Read(reader, XUiC_ItemStack.StackLocationTypes.Backpack, StreamModeRead.Persistency, null);
        });
        if (bag != null) NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(bag));
        return bag;
    }

    internal static MemoryStream WriteBag(Bag bag)
    {
        List<ItemValue> writeMetadata = NetworkStackCompatibility.PrepareForWrite(NetworkStackCompatibility.GetSlots(bag));
        try { return StreamUtils.ToBlob(writer => bag.Write(writer, StreamModeWrite.Persistency)); }
        finally { NetworkStackCompatibility.ClearWriteMetadata(writeMetadata); }
    }

    private static bool PlayerHoldsLootLock(int playerEntityId, ILockTarget target)
    {
        LockManager manager = LockManager.Instance;
        return manager != null && (HasLock(manager.singleLocks, playerEntityId, target) || HasLock(manager.sharedLocks, playerEntityId, target));
    }

    private static bool HasLock(object locks, int playerEntityId, ILockTarget target)
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
            object lockTarget = type.GetField("Target")?.GetValue(entry) ?? type.GetProperty("Target")?.GetValue(entry, null);
            object channel = type.GetField("Channel")?.GetValue(entry) ?? type.GetProperty("Channel")?.GetValue(entry, null);
            if (ReferenceEquals(lockTarget, target) && channel is ushort value && value == 0) return true;
        }
        return false;
    }

    private static bool SameRequest(NetworkRemoteRequest left, NetworkRemoteRequest right)
    {
        if (left.Operation != right.Operation || left.SourceLocation != right.SourceLocation || left.EntityId != right.EntityId || left.Slot != right.Slot
            || left.Count != right.Count || left.OriginalRequestId != right.OriginalRequestId || !left.Position.Equals(right.Position)
            || left.ConsumptionSource != right.ConsumptionSource
            || left.RestockSetting != right.RestockSetting || left.RestockSettingValue != right.RestockSettingValue
            || left.TraderNetworkSource != right.TraderNetworkSource
            || left.TraderPersonalPayout != right.TraderPersonalPayout || left.TraderBuyToBackpack != right.TraderBuyToBackpack || left.TraderAutomation != right.TraderAutomation
            || left.TraderRuleMove != right.TraderRuleMove || !string.Equals(left.TraderRuleId, right.TraderRuleId, StringComparison.Ordinal)
            || left.TraderEntityId != right.TraderEntityId || left.TraderInventoryIndex != right.TraderInventoryIndex || left.TargetEntityId != right.TargetEntityId
            || !string.Equals(left.TraderSessionId, right.TraderSessionId, StringComparison.Ordinal)
            || !string.Equals(left.ExpectedNetworkId, right.ExpectedNetworkId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(left.MembershipTargetPlayerId, right.MembershipTargetPlayerId, StringComparison.Ordinal)
            || !string.Equals(left.MembershipOperationId, right.MembershipOperationId, StringComparison.Ordinal)
            || left.MembershipCandidatePage != right.MembershipCandidatePage
            || !string.Equals(left.Category, right.Category, StringComparison.Ordinal)
            || !string.Equals(left.Search, right.Search, StringComparison.Ordinal)
            || (left.Requirements?.Count ?? 0) != (right.Requirements?.Count ?? 0)
            || !SameSlots(left.RestockToolbeltSlots, right.RestockToolbeltSlots)
            || !SameSlots(left.RestockBackpackSlots, right.RestockBackpackSlots)) return false;
        if (left.Requirements != null)
            for (int i = 0; i < left.Requirements.Count; i++)
                if (left.Requirements[i].count != right.Requirements[i].count
                    || !left.Requirements[i].itemValue.Equals(right.Requirements[i].itemValue)) return false;
        if (left.Value == null || right.Value == null) return left.Value == right.Value && SameRule(left.TraderRule, right.TraderRule);
        return left.Value.Equals(right.Value) && SameRule(left.TraderRule, right.TraderRule);
    }

    private static bool SameSlots(List<int> left, List<int> right)
    {
        if ((left?.Count ?? 0) != (right?.Count ?? 0)) return false;
        for (int i = 0; i < (left?.Count ?? 0); i++) if (left[i] != right[i]) return false;
        return true;
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
            Position = request.Position,
            TargetEntityId = request.TargetEntityId,
            ConsumptionSource = request.ConsumptionSource,
            Requirements = request.Requirements?.Select(item => new ItemStack(item.itemValue.Clone(), item.count)).ToList() ?? new List<ItemStack>(),
            RestockSetting = request.RestockSetting,
            RestockSettingValue = request.RestockSettingValue,
            TraderNetworkSource = request.TraderNetworkSource,
            TraderPersonalPayout = request.TraderPersonalPayout,
            TraderBuyToBackpack = request.TraderBuyToBackpack,
            TraderAutomation = request.TraderAutomation,
            TraderSessionId = request.TraderSessionId,
            TraderEntityId = request.TraderEntityId,
            TraderInventoryIndex = request.TraderInventoryIndex,
            RestockToolbeltSlots = request.RestockToolbeltSlots?.ToList() ?? new List<int>(),
            RestockBackpackSlots = request.RestockBackpackSlots?.ToList() ?? new List<int>(),
            TraderRule = request.TraderRule?.Clone(), TraderRuleId = request.TraderRuleId, TraderRuleMove = request.TraderRuleMove,
            MembershipTargetPlayerId = request.MembershipTargetPlayerId, MembershipOperationId = request.MembershipOperationId,
            MembershipCandidatePage = request.MembershipCandidatePage
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
            TrashSlots = result.TrashSlots?.Select(entry => new TrashSlotEntry { Slot = entry.Slot, Value = entry.Value.Clone(), Count = entry.Count }).ToList()
                ?? new List<TrashSlotEntry>(),
            BagData = result.BagData == null ? null : (byte[])result.BagData.Clone(),
            InventoryData = result.InventoryData == null ? null : (byte[])result.InventoryData.Clone(),
            EntityBagData = result.EntityBagData == null ? null : (byte[])result.EntityBagData.Clone(),
            RestockSettingsLoaded = result.RestockSettingsLoaded,
            AutoRestockEnabled = result.AutoRestockEnabled,
            RestockTargetMode = result.RestockTargetMode,
            IsNetworkAdministrator = result.IsNetworkAdministrator,
            AutoRestockCooldownMs = result.AutoRestockCooldownMs,
            ReplenishmentEnabled = result.ReplenishmentEnabled,
            AmmoBankEnabled = result.AmmoBankEnabled,
            TraderEnabled = result.TraderEnabled,
            CraftingChainEnabled = result.CraftingChainEnabled,
            AutoDepositEnabled = result.AutoDepositEnabled,
            TrashEnabled = result.TrashEnabled,
            BookReadingEnabled = result.BookReadingEnabled,
            RetainSearchOnClose = result.RetainSearchOnClose,
            ManualSaleEnabled = result.ManualSaleEnabled,
            PersonalAutoDeposit = result.PersonalAutoDeposit,
            PersonalAutoTrash = result.PersonalAutoTrash,
            PersonalTrashScope = result.PersonalTrashScope,
            PersonalAutoRead = result.PersonalAutoRead,
            ReadBookPolicy = result.ReadBookPolicy,
            TraderSessionId = result.TraderSessionId,
            TraderOffers = result.TraderOffers?.Select(offer => new TraderOffer
            {
                Value = offer.Value?.Clone(), Count = offer.Count, BundleSize = offer.BundleSize,
                BundlePrice = offer.BundlePrice, TraderEntityId = offer.TraderEntityId, InventoryIndex = offer.InventoryIndex
            }).ToList() ?? new List<TraderOffer>(),
            TraderRules = result.TraderRules?.Select(rule => rule.Clone()).ToList() ?? new List<TraderAutomationRule>(),
            TraderRunProgressAvailable = result.TraderRunProgressAvailable,
            TraderRunActive = result.TraderRunActive,
            TraderRunCurrentRule = result.TraderRunCurrentRule,
            TraderRunTotalRules = result.TraderRunTotalRules,
            TraderRunMessage = result.TraderRunMessage,
            TraderRunRuleId = result.TraderRunRuleId,
            MembershipNotice = result.MembershipNotice,
            MembershipRevision = result.MembershipRevision,
            MembershipOperationId = result.MembershipOperationId,
            MembershipTargetPlayerId = result.MembershipTargetPlayerId,
            MembershipSourceNetworkId = result.MembershipSourceNetworkId,
            MembershipTargetNetworkId = result.MembershipTargetNetworkId,
            MembershipInviterName = result.MembershipInviterName,
            MembershipWillMerge = result.MembershipWillMerge,
            MembershipOnlineCount = result.MembershipOnlineCount,
            NetworkMembers = result.NetworkMembers?.Select(member => new NetworkMemberInfo
            {
                PlayerId = member.PlayerId, DisplayName = member.DisplayName, IsOnline = member.IsOnline,
                IsAdministrator = member.IsAdministrator, Eligible = member.Eligible, EligibilityError = member.EligibilityError
            }).ToList() ?? new List<NetworkMemberInfo>(),
            MembershipCandidates = result.MembershipCandidates?.Select(member => new NetworkMemberInfo
            {
                PlayerId = member.PlayerId, DisplayName = member.DisplayName, IsOnline = member.IsOnline,
                IsAdministrator = member.IsAdministrator, Eligible = member.Eligible, EligibilityError = member.EligibilityError
            }).ToList() ?? new List<NetworkMemberInfo>()
        };
    }

    private static bool SameRule(TraderAutomationRule left, TraderAutomationRule right)
    {
        if (left == null || right == null) return left == right;
        return left.Id == right.Id && left.Kind == right.Kind && left.Enabled == right.Enabled && left.ItemType == right.ItemType
            && left.QualityMode == right.QualityMode && left.Quality == right.Quality && left.MaximumCount == right.MaximumCount
            && left.MinimumNetworkCount == right.MinimumNetworkCount && left.NetworkCountComparison == right.NetworkCountComparison
            && left.MinimumCurrencyCount == right.MinimumCurrencyCount && left.CurrencyCountComparison == right.CurrencyCountComparison
            && left.MaximumSpend == right.MaximumSpend && left.Scheduled == right.Scheduled && left.ScheduledHour == right.ScheduledHour
            && left.IntervalDays == right.IntervalDays && left.LastScheduledDay == right.LastScheduledDay
            && left.Scope == right.Scope && left.Category == right.Category
            && left.ExcludedCategories == right.ExcludedCategories && left.ExcludedItemTypes == right.ExcludedItemTypes;
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

    internal static void SendUnsolicited(int entityId, NetworkRemoteResult result)
    {
        if (result == null) return;
        result.RequestId = 0;
        if (result.MembershipRevision <= 0) result.MembershipRevision = PlayerNetworkDirectory.GetMembershipRevision();
        EntityPlayerLocal localPlayer = GameManager.Instance?.World?.GetEntity(entityId) as EntityPlayerLocal;
        if ((bool)localPlayer)
        {
            NetworkRemoteClient.HandleResponse(entityId, result);
            return;
        }
        SendResponse(entityId, result);
    }

    private static void SendResponse(int entityId, NetworkRemoteResult result)
    {
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (!(bool)connection || !connection.IsServer) return;
        connection.SendPackage(NetPackageManager.GetPackage<NetPackageHyperdimensionalResponse>().Setup(entityId, result), _onlyClientsAttachedToAnEntity: false, entityId);
    }
}
