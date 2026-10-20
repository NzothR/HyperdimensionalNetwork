using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class InventoryAutomationClient
{
    private const int MaximumLocalTargetsPerUpdate = 8;

    private readonly struct Target
    {
        public readonly bool Toolbelt;
        public readonly int Slot;
        public Target(bool toolbelt, int slot) { Toolbelt = toolbelt; Slot = slot; }
        public override string ToString() => (Toolbelt ? "T" : "B") + Slot;
    }

    private static readonly Queue<Target> Pending = new Queue<Target>();
    private static readonly HashSet<string> Queued = new HashSet<string>(StringComparer.Ordinal);
    private static int _playerId = -1;
    private static bool _rulesLoaded;
    private static bool _queryPending;
    private static bool _mutationPending;
    private static bool _hasInventoryRules;
    private static bool _scanRequested;
    private static float _nextQueryAt;

    private sealed class PendingDeposit
    {
        public Target Target;
        public ItemValue Value;
        public int Count;
    }

    private sealed class PreparedDeposit
    {
        public Target Target;
        public ItemStack Original;
        public ItemValue Value;
        public int Count;
    }

    private sealed class LocalBatch
    {
        public RestockSettingsSnapshot Settings;
        public List<TraderAutomationRule> TraderRules;
        public List<TraderAutomationRule> PersonalRules;
        public List<PendingDeposit> Deposits = new List<PendingDeposit>();
    }

    internal static void OnNetworkChanged(EntityPlayerLocal player)
    {
        Pending.Clear(); Queued.Clear(); _playerId = player?.entityId ?? -1;
        _rulesLoaded = false; _queryPending = false; _mutationPending = false;
        _hasInventoryRules = false; _scanRequested = false; _nextQueryAt = 0f;
    }

    internal static void SetRules(EntityPlayerLocal player, IEnumerable<TraderAutomationRule> rules,
        bool hasNetworkInventoryAutoSellRule = false)
    {
        if (!(bool)player) return;
        if (_playerId != player.entityId) OnNetworkChanged(player);
        _hasInventoryRules = hasNetworkInventoryAutoSellRule || rules?.Any(rule => rule.Enabled &&
            (rule.Personal && rule.Kind is (TraderAutomationRuleKind.AutoDeposit or TraderAutomationRuleKind.AutoTrash)
                || rule.Kind is (TraderAutomationRuleKind.AutoSell or TraderAutomationRuleKind.AutoScrap)
                    && TraderAutomationService.IncludesPlayerInventory(rule.AutoSellSource))) == true;
        _rulesLoaded = true;
        _nextQueryAt = Time.realtimeSinceStartup + 15f;
        if (!_hasInventoryRules) { Pending.Clear(); Queued.Clear(); }
        else if (_scanRequested) { QueueInventory(player); _scanRequested = false; }
        NetworkLog.Debug("Inventory automation rule watch updated. player=" + player.entityId + "; active=" + _hasInventoryRules);
    }

    internal static void OnSlotChanged(EntityPlayerLocal player, bool toolbelt, int slot, ItemStack previous, ItemStack current)
    {
        if (!(bool)player) return;
        if (!_rulesLoaded) { _scanRequested = true; return; }
        if (!_hasInventoryRules) return;
        // AutoRestockClient observes every changed slot, including changes detected by its
        // per-update grid comparison. Queueing only this slot avoids rescanning 195 slots for
        // every item produced by harvesting or vehicle dismantling.
        if (current == null || current.IsEmpty()) return;
        QueueSlot(toolbelt, slot);
        NetworkLog.Debug("Inventory automation queued changed slot. player=" + player.entityId + "; source="
            + (toolbelt ? "T" : "B") + slot + "; pending=" + Pending.Count);
    }

    internal static void RunNow(EntityPlayerLocal player)
    {
        if (!(bool)player) return;
        if (_playerId != player.entityId) OnNetworkChanged(player);
        _hasInventoryRules = true;
        _rulesLoaded = true;
        QueueInventory(player);
        NetworkLog.Debug("Manual inventory automation scan queued. player=" + player.entityId + "; slots=" + Pending.Count);
    }

    internal static void Update(EntityPlayerLocal player)
    {
        using var profile = NetworkProfiler.Measure("Client.inventoryRules");
        if (!(bool)player) return;
        if (_playerId != player.entityId) OnNetworkChanged(player);
        if (!_queryPending && Time.realtimeSinceStartup >= _nextQueryAt)
        {
            _nextQueryAt = Time.realtimeSinceStartup + 15f;
            _queryPending = true;
            if (!TraderRuleClient.Query(player, result =>
            {
                _queryPending = false;
                if (result?.Success == true) SetRules(player, result.TraderRules, result.HasNetworkInventoryAutoSellRule);
                else NetworkLog.Debug("Inventory automation rule refresh failed. player=" + player.entityId + "; error=" + result?.Error);
            })) _queryPending = false;
        }
        if (!_rulesLoaded || !_hasInventoryRules || _mutationPending || NetworkRemoteClient.IsMutationPending || Pending.Count == 0) return;
        if (!NetworkRemoteClient.IsRemoteClient
            && !AutoScrapQueueService.HasOnlineMember(GameManager.Instance?.World, PlayerNetworkDirectory.GetNetworkId(player))) return;
        Target target = Pending.Dequeue(); Queued.Remove(target.ToString());
        if (NetworkRemoteClient.IsRemoteClient)
        {
            ItemStack stack = GetStack(player, target);
            if (stack == null || stack.IsEmpty() || BookAutomationClient.ShouldOwnItem(player, stack.itemValue)) return;
            _mutationPending = true;
            bool sent = NetworkRemoteClient.ProcessAutomationSlot(player, target.Toolbelt, target.Slot, stack.itemValue, stack.count, result =>
            {
                _mutationPending = false;
                if (result?.Success != true)
                {
                    NetworkLog.Debug("Inventory automation request failed. player=" + player.entityId + "; source=" + target
                        + "; error=" + result?.Error);
                    return;
                }
                if (result.BagData != null || result.InventoryData != null)
                {
                    NetworkInventorySync.Apply(player, result, suppressRestockEvents: true);
                    AutoRestockClient.RefreshInventoryViews(player, refreshToolbelt: target.Toolbelt);
                }
            });
            if (!sent) { _mutationPending = false; QueueSlot(target.Toolbelt, target.Slot); }
        }
        else
        {
            LocalBatch batch = null;
            int processed = 0;
            ProcessLocalTarget(player, target, ref batch);
            processed++;

            while (processed < MaximumLocalTargetsPerUpdate && Pending.Count > 0)
            {
                Target next = Pending.Dequeue(); Queued.Remove(next.ToString());
                ProcessLocalTarget(player, next, ref batch);
                processed++;
            }

            FlushLocalDeposits(player, batch?.Deposits);
        }
    }

    private static void ProcessLocalTarget(EntityPlayerLocal player, Target target, ref LocalBatch batch)
    {
        ItemStack stack = GetStack(player, target);
        if (stack == null || stack.IsEmpty() || BookAutomationClient.ShouldOwnItem(player, stack.itemValue)) return;
        if (batch == null)
        {
            batch = new LocalBatch
            {
                Settings = RestockSettingsService.Get(player),
                TraderRules = TraderAutomationService.GetRules(player),
                PersonalRules = PersonalAutomationRuleService.GetRules(player)
            };
        }
        RestockSettingsSnapshot settings = batch.Settings;
        bool locked = !target.Toolbelt && player.bag?.LockedSlots != null && target.Slot < player.bag.LockedSlots.Length
            && player.bag.LockedSlots[target.Slot];
        TraderAutomationRule scrap = !locked ? batch.TraderRules.FirstOrDefault(rule => rule.Enabled
            && rule.Kind == TraderAutomationRuleKind.AutoScrap
            && TraderAutomationService.IncludesPlayerInventory(rule.AutoSellSource)
            && TraderAutomationService.Matches(rule, stack.itemValue)
            && TraderAutomationService.MatchesNetworkCountRequirement(player, rule)) : null;
        if (scrap != null)
        {
            int maximum = scrap.MaximumCount == 0 ? stack.count : Math.Min(stack.count, scrap.MaximumCount);
            int consumed = AutoScrapQueueService.GetPlayerConsumption(player, scrap, stack.itemValue, maximum);
            if (consumed > 0)
            {
                ItemStackGrid grid = target.Toolbelt ? player.inventory.ItemGrid : player.bag.ItemGrid;
                ItemStack remaining = stack.Clone(); remaining.count -= consumed;
                bool queued = false;
                try
                {
                    using (AutoRestockClient.SuppressInventoryEvents(player))
                        grid.SetItem(target.Slot, remaining.count > 0 ? remaining : ItemStack.Empty.Clone());
                    queued = AutoScrapQueueService.CommitPlayerConsumption(player, scrap, stack.itemValue, maximum, consumed);
                }
                catch (Exception ex) { NetworkLog.Error("Local automatic scrap inventory update failed. player=" + player.entityId + "; exception=" + ex); }
                if (queued)
                {
                    AutoRestockClient.RefreshBaseline(player); AutoRestockClient.RefreshInventoryViews(player, refreshToolbelt: target.Toolbelt);
                    return;
                }
                using (AutoRestockClient.SuppressInventoryEvents(player)) grid.SetItem(target.Slot, stack);
                NetworkLog.Error("Local automatic scrap queue save failed; source restored. player=" + player.entityId + "; rule=" + scrap.Id);
            }
        }
        TraderAutomationRule sale = settings.TraderEnabled && !locked
            ? batch.TraderRules.FirstOrDefault(rule => rule.Enabled
                && rule.Kind == TraderAutomationRuleKind.AutoSell
                && TraderAutomationService.IncludesPlayerInventory(rule.AutoSellSource)
                && TraderAutomationService.Matches(rule, stack.itemValue)
                && TraderAutomationService.MatchesNetworkCountRequirement(player, rule)) : null;
        if (sale != null)
        {
            int count = sale.MaximumCount == 0 ? stack.count : Math.Min(stack.count, sale.MaximumCount);
            NetworkRemoteResult sold = TraderTransactionService.ExecuteVoidSell(player, null, new NetworkRemoteRequest
            {
                EntityId = player.entityId, RequestId = DateTime.UtcNow.Ticks, Operation = NetworkRequestOperation.TraderVoidSell,
                Value = stack.itemValue.Clone(), Count = count, SourceLocation = target.Toolbelt
                    ? XUiC_ItemStack.StackLocationTypes.ToolBelt : XUiC_ItemStack.StackLocationTypes.Backpack,
                Slot = target.Slot, TraderAutomation = true
            });
            if (sold.Success)
            {
                AutoRestockClient.RefreshBaseline(player);
                AutoRestockClient.RefreshInventoryViews(player, refreshToolbelt: target.Toolbelt);
                NetworkLog.Debug("Automatic trader sale completed from player inventory. player=" + player.entityId
                    + "; rule=" + sale.Id + "; source=" + target + "; item=" + stack.itemValue.type
                    + "; count=" + count + "; proceeds=" + sold.Amount);
                return;
            }
            NetworkLog.Debug("Automatic trader sale from player inventory was skipped. player=" + player.entityId
                + "; rule=" + sale.Id + "; source=" + target + "; item=" + stack.itemValue.type + "; error=" + sold.Error);
        }
        if (settings.TrashEnabled && settings.PersonalAutoTrash
            && InventoryAutomationService.ScopeIncludes(settings.PersonalTrashScope, target.Toolbelt, locked))
        {
            TraderAutomationRule trash = batch.PersonalRules.FirstOrDefault(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoTrash
                && TraderAutomationService.Matches(rule, stack.itemValue));
            if (trash != null)
            {
                int count = trash.MaximumCount == 0 ? stack.count : Math.Min(stack.count, trash.MaximumCount);
                using (AutoRestockClient.SuppressInventoryEvents(player))
                {
                    ItemStack remaining = stack.Clone(); remaining.count -= count;
                    (target.Toolbelt ? player.inventory.ItemGrid : player.bag.ItemGrid).SetItem(target.Slot,
                        remaining.count > 0 ? remaining : ItemStack.Empty.Clone());
                }
                AutoRestockClient.RefreshBaseline(player); AutoRestockClient.RefreshInventoryViews(player);
                NetworkLog.Debug("Local personal trash destroyed items. player=" + player.entityId + "; rule=" + trash.Id
                    + "; source=" + target + "; count=" + count);
                return;
            }
        }
        if (!target.Toolbelt && !locked && settings.AutoDepositEnabled && settings.PersonalAutoDeposit)
        {
            TraderAutomationRule deposit = batch.PersonalRules.FirstOrDefault(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoDeposit
                && TraderAutomationService.Matches(rule, stack.itemValue));
            if (deposit != null)
            {
                int count = deposit.MaximumCount == 0 ? stack.count : Math.Min(stack.count, deposit.MaximumCount);
                if (count > 0) batch.Deposits.Add(new PendingDeposit
                    { Target = target, Value = stack.itemValue.Clone(), Count = count });
            }
        }
    }

    private static void FlushLocalDeposits(EntityPlayerLocal player, List<PendingDeposit> deposits)
    {
        if (deposits == null || deposits.Count == 0) return;
        using var profile = NetworkProfiler.Measure("Client.inventoryDepositBatch");
        List<PreparedDeposit> prepared = new List<PreparedDeposit>(deposits.Count);
        foreach (PendingDeposit deposit in deposits)
        {
            ItemStack stack = GetStack(player, deposit.Target);
            if (stack == null || stack.IsEmpty() || !stack.itemValue.Equals(deposit.Value)) continue;
            int count = Math.Min(stack.count, deposit.Count);
            if (count <= 0) continue;
            prepared.Add(new PreparedDeposit
            {
                Target = deposit.Target,
                Original = stack.Clone(),
                Value = stack.itemValue.Clone(),
                Count = count
            });
        }
        if (prepared.Count == 0) return;

        List<StorageAddition> additions = prepared.Select(entry => new StorageAddition
            { Value = entry.Value.Clone(), Count = entry.Count }).ToList();
        if (!NetworkStorage.AddBatch(player, additions))
        {
            NetworkLog.Debug("Local personal deposit batch was refused. player=" + player.entityId + "; slots=" + prepared.Count);
            return;
        }

        try
        {
            using (AutoRestockClient.SuppressInventoryEvents(player))
            {
                foreach (PreparedDeposit deposit in prepared)
                {
                    ItemStackGrid grid = deposit.Target.Toolbelt ? player.inventory?.ItemGrid : player.bag?.ItemGrid;
                    if (grid == null || deposit.Target.Slot < 0 || deposit.Target.Slot >= grid.Length)
                        throw new InvalidOperationException("Inventory grid changed during local deposit batch.");
                    ItemStack current = grid[deposit.Target.Slot];
                    if (current == null || current.IsEmpty() || !current.itemValue.Equals(deposit.Value)
                        || current.count < deposit.Count)
                        throw new InvalidOperationException("Inventory stack changed during local deposit batch.");
                    ItemStack remaining = current.Clone(); remaining.count -= deposit.Count;
                    grid.SetItem(deposit.Target.Slot, remaining.count > 0 ? remaining : ItemStack.Empty.Clone());
                }
            }
        }
        catch (Exception ex)
        {
            using (AutoRestockClient.SuppressInventoryEvents(player))
            {
                foreach (PreparedDeposit deposit in prepared)
                {
                    ItemStackGrid grid = deposit.Target.Toolbelt ? player.inventory?.ItemGrid : player.bag?.ItemGrid;
                    if (grid == null || deposit.Target.Slot < 0 || deposit.Target.Slot >= grid.Length) continue;
                    try { grid.SetItem(deposit.Target.Slot, deposit.Original); }
                    catch (Exception restoreException) { NetworkLog.Error("Failed to restore inventory after deposit batch error. player=" + player.entityId + "; source=" + deposit.Target + "; exception=" + restoreException); }
                }
            }
            if (!NetworkStorage.RemoveBatch(player, additions))
                NetworkLog.Error("Local personal deposit batch rollback failed; inventory restored where possible. player=" + player.entityId + "; slots=" + prepared.Count);
            NetworkLog.Error("Local personal deposit batch inventory update failed. player=" + player.entityId + "; slots=" + prepared.Count + "; exception=" + ex);
            AutoRestockClient.RefreshBaseline(player);
            return;
        }

        AutoRestockClient.RefreshBaseline(player);
        AutoRestockClient.RefreshInventoryViews(player, refreshToolbelt: prepared.Any(entry => entry.Target.Toolbelt));
        int totalCount = prepared.Sum(entry => entry.Count);
        NetworkLog.Debug("Local personal deposit batch moved items. player=" + player.entityId + "; slots=" + prepared.Count
            + "; count=" + totalCount);
    }

    private static ItemStack GetStack(EntityPlayerLocal player, Target target)
    {
        ItemStackGrid grid = target.Toolbelt ? player.inventory?.ItemGrid : player.bag?.ItemGrid;
        return grid == null || target.Slot < 0 || target.Slot >= grid.Length ? null : grid[target.Slot];
    }

    private static void QueueInventory(EntityPlayerLocal player)
    {
        for (int i = 0; i < (player.bag?.ItemGrid?.Length ?? 0); i++) QueueSlot(false, i);
        for (int i = 0; i < (player.inventory?.ItemGrid?.Length ?? 0); i++) QueueSlot(true, i);
        NetworkLog.Debug("Inventory automation scanned all player slots after an inventory change. player=" + player.entityId
            + "; queued=" + Pending.Count);
    }

    private static void QueueSlot(bool toolbelt, int slot)
    {
        if (slot < 0) return;
        Target target = new Target(toolbelt, slot);
        if (Queued.Add(target.ToString())) Pending.Enqueue(target);
    }
}
