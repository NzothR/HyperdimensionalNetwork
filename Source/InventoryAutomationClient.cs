using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class InventoryAutomationClient
{
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
        QueueInventory(player);
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
        ItemStackGrid grid = target.Toolbelt ? player.inventory?.ItemGrid : player.bag?.ItemGrid;
        if (grid == null || target.Slot < 0 || target.Slot >= grid.Length) return;
        ItemStack stack = grid[target.Slot];
        if (stack == null || stack.IsEmpty()) return;
        if (BookAutomationClient.ShouldOwnItem(player, stack.itemValue)) return;
        if (NetworkRemoteClient.IsRemoteClient)
        {
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
        else ProcessLocal(player, target, stack);
    }

    private static void ProcessLocal(EntityPlayerLocal player, Target target, ItemStack stack)
    {
        RestockSettingsSnapshot settings = RestockSettingsService.Get(player);
        bool locked = !target.Toolbelt && player.bag?.LockedSlots != null && target.Slot < player.bag.LockedSlots.Length
            && player.bag.LockedSlots[target.Slot];
        TraderAutomationRule scrap = !locked ? TraderAutomationService.GetRules(player).FirstOrDefault(rule => rule.Enabled
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
            ? TraderAutomationService.GetRules(player).FirstOrDefault(rule => rule.Enabled
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
        List<TraderAutomationRule> rules = PersonalAutomationRuleService.GetRules(player);
        if (settings.TrashEnabled && settings.PersonalAutoTrash
            && InventoryAutomationService.ScopeIncludes(settings.PersonalTrashScope, target.Toolbelt, locked))
        {
            TraderAutomationRule trash = rules.FirstOrDefault(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoTrash
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
            TraderAutomationRule deposit = rules.FirstOrDefault(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoDeposit
                && TraderAutomationService.Matches(rule, stack.itemValue));
            if (deposit != null)
            {
                int count = deposit.MaximumCount == 0 ? stack.count : Math.Min(stack.count, deposit.MaximumCount);
                if (NetworkStorage.AddStack(player, new ItemStack(stack.itemValue.Clone(), count)))
                {
                    using (AutoRestockClient.SuppressInventoryEvents(player))
                    {
                        ItemStack remaining = stack.Clone(); remaining.count -= count;
                        player.bag.ItemGrid.SetItem(target.Slot, remaining.count > 0 ? remaining : ItemStack.Empty.Clone());
                    }
                    AutoRestockClient.RefreshBaseline(player); AutoRestockClient.RefreshInventoryViews(player);
                    NetworkLog.Debug("Local personal deposit moved items. player=" + player.entityId + "; rule=" + deposit.Id
                        + "; source=" + target + "; count=" + count);
                }
            }
        }
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
