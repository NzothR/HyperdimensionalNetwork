using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal enum TraderAutomationRuleKind : byte { AutoSell = 1, AutoBuy = 2, AutoDeposit = 3, AutoTrash = 4, AutoScrap = 5 }
internal enum TraderQualityMatchMode : byte { Any = 0, Exact = 1, Greater = 2, GreaterOrEqual = 3, Less = 4, LessOrEqual = 5 }
internal enum TraderRuleComparison : byte { Greater = 0, GreaterOrEqual = 1, Equal = 2, LessOrEqual = 3, Less = 4, Ignore = 5 }
internal enum TraderRuleScope : byte { SingleItem = 0, Category = 1 }
internal enum TraderAutoSellSource : byte { NetworkDeposits = 0, BackpackAndToolbelt = 1, Both = 2 }

internal sealed class TraderAutomationRule
{
    public string Id = Guid.NewGuid().ToString("N");
    public bool Personal;
    public TraderAutomationRuleKind Kind;
    public bool Enabled = true;
    public int ItemType;
    public TraderRuleScope Scope;
    public TraderAutoSellSource AutoSellSource;
    public string Category = string.Empty;
    public string ExcludedCategories = string.Empty;
    public string ExcludedItemTypes = string.Empty;
    public TraderQualityMatchMode QualityMode;
    public int Quality;
    public int MaximumCount = 1;
    public int MinimumNetworkCount;
    public TraderRuleComparison NetworkCountComparison = TraderRuleComparison.Ignore;
    public int MinimumCurrencyCount;
    public TraderRuleComparison CurrencyCountComparison = TraderRuleComparison.GreaterOrEqual;
    public int MaximumSpend;
    public bool Scheduled;
    public int ScheduledHour = 5;
    public int IntervalDays = 1;
    public int LastScheduledDay = -1;

    public TraderAutomationRule Clone() => (TraderAutomationRule)MemberwiseClone();
}

internal static class TraderAutomationService
{
    private sealed class PendingBuyRun
    {
        public World World;
        public int PlayerEntityId;
        public string SessionId;
        public string NetworkId;
        public List<TraderAutomationRule> Rules;
        public string Reason;
        public float StartedAt;
        public float NextQueryAt;
        public int LastOfferCount = -1;
        public int StableOfferQueries;
        public int NextRuleIndex;
        public bool OffersReady;
        public List<TraderOffer> CurrentOffers = new List<TraderOffer>();
    }

    private sealed class TraderRunProgress
    {
        public bool Active;
        public int CurrentRule;
        public int TotalRules;
        public string Message = string.Empty;
        public string RuleId = string.Empty;
    }
    private const int FileMagic = 0x31525448; // HTR1
    private const int FileVersion = 5;
    private const int MaximumNetworks = 100000;
    private const int MaximumRulesPerNetwork = 2048;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, List<TraderAutomationRule>> Rules = new Dictionary<string, List<TraderAutomationRule>>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ProcessingDeposits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ManualRunNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TraderRunProgress> ManualRunProgress = new Dictionary<string, TraderRunProgress>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ActiveRuleIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PendingBuyRun> PendingRuns = new List<PendingBuyRun>();
    private static string _path;
    private static bool _loaded;
    private static bool _unsafe;
    private static float _nextScheduleCheck;
    private static float _nextAutomationPoll;

    public static List<TraderAutomationRule> GetRules(EntityPlayer player)
    {
        if (!(bool)player || !EnsureLoaded()) return new List<TraderAutomationRule>();
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) return new List<TraderAutomationRule>();
        lock (Sync)
            return Rules.TryGetValue(networkId, out List<TraderAutomationRule> rules)
                ? rules.Select(rule => rule.Clone()).ToList() : new List<TraderAutomationRule>();
    }

    internal static bool RemoveNetworkData(string networkId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(networkId)) { error = "NetworkUnavailable"; return false; }
        List<PendingBuyRun> cancelled;
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "TraderRulesUnavailable"; return false; }
            bool hadRules = Rules.TryGetValue(networkId, out List<TraderAutomationRule> oldRules);
            Rules.Remove(networkId);
            if (!Save())
            {
                if (hadRules) Rules[networkId] = oldRules;
                error = "TraderRulesSaveFailed";
                return false;
            }
            cancelled = PendingRuns.Where(run => string.Equals(run.NetworkId, networkId, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (PendingBuyRun run in cancelled) PendingRuns.Remove(run);
            ProcessingDeposits.Remove(networkId);
            ManualRunNetworks.Remove(networkId);
            ManualRunProgress.Remove(networkId);
            ActiveRuleIds.Remove(networkId);
        }
        foreach (PendingBuyRun run in cancelled)
        {
            if (!string.IsNullOrEmpty(run.SessionId)) TraderMarketplaceService.ForceClose(run.SessionId);
        }
        NetworkLog.Debug("Trader rules and pending runs removed for retired network. network=" + networkId + "; cancelledRuns=" + cancelled.Count);
        return true;
    }

    internal static void CancelNetworkRuntime(string networkId, string reason)
    {
        if (string.IsNullOrWhiteSpace(networkId)) return;
        List<PendingBuyRun> cancelled;
        lock (Sync)
        {
            cancelled = PendingRuns.Where(run => string.Equals(run.NetworkId, networkId, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (PendingBuyRun run in cancelled) PendingRuns.Remove(run);
            ManualRunNetworks.Remove(networkId);
            ManualRunProgress.Remove(networkId);
            ActiveRuleIds.Remove(networkId);
        }
        foreach (PendingBuyRun run in cancelled)
            if (!string.IsNullOrEmpty(run.SessionId)) TraderMarketplaceService.ForceClose(run.SessionId);
        if (cancelled.Count > 0)
            NetworkLog.Debug("Pending trader runs cancelled for a network merge. network=" + networkId + "; runs=" + cancelled.Count + "; reason=" + reason);
    }

    public static NetworkRemoteResult QueryRules(EntityPlayer player, NetworkRemoteRequest request)
    {
        if (!(bool)player) return new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = "PlayerUnavailable" };
        bool isAdmin = (bool)player && PlayerNetworkDirectory.IsNetworkAdministrator(player);
        NetworkRemoteResult result = new NetworkRemoteResult
        {
            RequestId = request?.RequestId ?? 0,
            Success = true,
            IsNetworkAdministrator = isAdmin,
            // Existing wire flag tells non-admin clients to watch inventory slots
            // for network-level rules; it now covers automatic scrap as well.
            HasNetworkInventoryAutoSellRule = GetRules(player).Any(rule => rule.Enabled
                && (rule.Kind == TraderAutomationRuleKind.AutoSell && NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TraderEnabled)
                    || rule.Kind == TraderAutomationRuleKind.AutoScrap)
                && IncludesPlayerInventory(rule.AutoSellSource)),
            TraderRules = (isAdmin ? GetRules(player) : new List<TraderAutomationRule>())
                .Concat(PersonalAutomationRuleService.GetRules(player)).ToList()
        };
        if (isAdmin) AttachRunProgress(result, PlayerNetworkDirectory.GetNetworkId(player));
        return result;
    }

    private static void AttachRunProgress(NetworkRemoteResult result, string networkId)
    {
        if (result == null || string.IsNullOrEmpty(networkId)) return;
        lock (Sync)
        {
            result.TraderRunProgressAvailable = true;
            if (ManualRunProgress.TryGetValue(networkId, out TraderRunProgress progress))
            {
                result.TraderRunCurrentRule = progress.CurrentRule;
                result.TraderRunTotalRules = progress.TotalRules;
                result.TraderRunMessage = progress.Message ?? string.Empty;
                result.TraderRunRuleId = progress.RuleId ?? string.Empty;
            }
            result.TraderRunActive = IsNetworkExecutionActiveUnsafe(networkId);
            PendingBuyRun pending = PendingRuns.FirstOrDefault(run => string.Equals(run.NetworkId, networkId, StringComparison.OrdinalIgnoreCase));
            if (pending != null)
            {
                result.TraderRunCurrentRule = pending.NextRuleIndex + 1;
                result.TraderRunTotalRules = pending.Rules?.Count ?? 1;
                result.TraderRunMessage = "自动规则正在执行，当前第 " + (pending.NextRuleIndex + 1) + " 条";
                result.TraderRunRuleId = pending.Rules?.ElementAtOrDefault(pending.NextRuleIndex)?.Id ?? string.Empty;
            }
            else if (result.TraderRunActive && string.IsNullOrEmpty(result.TraderRunMessage))
            {
                result.TraderRunCurrentRule = 1;
                result.TraderRunTotalRules = 1;
                result.TraderRunMessage = "自动出售规则正在执行";
            }
            if (result.TraderRunActive && ActiveRuleIds.TryGetValue(networkId, out string activeRuleId))
                result.TraderRunRuleId = activeRuleId;
        }
    }

    private static bool IsNetworkExecutionActiveUnsafe(string networkId)
        => ManualRunNetworks.Contains(networkId) || ProcessingDeposits.Contains(networkId)
            || PendingRuns.Any(run => string.Equals(run.NetworkId, networkId, StringComparison.OrdinalIgnoreCase))
            || ManualRunProgress.TryGetValue(networkId, out TraderRunProgress progress) && progress.Active;

    internal static bool IsNetworkExecutionActive(string networkId)
    {
        if (string.IsNullOrWhiteSpace(networkId)) return false;
        lock (Sync) return IsNetworkExecutionActiveUnsafe(networkId);
    }

    private static void SetRunProgress(string networkId, bool active, int currentRule, int totalRules, string message, string ruleId = "")
    {
        if (string.IsNullOrEmpty(networkId)) return;
        lock (Sync) ManualRunProgress[networkId] = new TraderRunProgress
        {
            Active = active, CurrentRule = currentRule, TotalRules = totalRules, Message = message ?? string.Empty, RuleId = ruleId ?? string.Empty
        };
    }

    public static EntityPlayer ResolveOnlineAdministrator(World world, string networkId)
    {
        if (world?.Players?.list == null || string.IsNullOrEmpty(networkId)) return null;
        foreach (EntityPlayer candidate in world.Players.list)
            if ((bool)candidate && PlayerNetworkDirectory.IsNetworkAdministrator(candidate)
                && string.Equals(PlayerNetworkDirectory.GetNetworkId(candidate), networkId, StringComparison.OrdinalIgnoreCase)) return candidate;
        return null;
    }

    public static NetworkRemoteResult ExecuteRuleRequest(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        if (!(bool)player) return new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = "PlayerUnavailable" };
        string error;
        bool success;
        switch (request.Operation)
        {
            case NetworkRequestOperation.TraderSaveRule:
                success = request.TraderRule?.Personal == true
                    ? PersonalAutomationRuleService.TrySave(player, request.TraderRule, out error)
                    : TrySaveRule(player, request.TraderRule, out error);
                break;
            case NetworkRequestOperation.TraderDeleteRule:
                success = PersonalAutomationRuleService.GetRules(player).Any(rule => rule.Id == request.TraderRuleId)
                    ? PersonalAutomationRuleService.TryDelete(player, request.TraderRuleId, out error)
                    : TryDeleteRule(player, request.TraderRuleId, out error);
                break;
            case NetworkRequestOperation.TraderMoveRule:
                success = PersonalAutomationRuleService.GetRules(player).Any(rule => rule.Id == request.TraderRuleId)
                    ? PersonalAutomationRuleService.TryMove(player, request.TraderRuleId, request.TraderRuleMove, out error)
                    : MoveRule(player, request.TraderRuleId, request.TraderRuleMove, out error);
                break;
            case NetworkRequestOperation.TraderRunRules:
                success = RunNow(world, player, request.TraderRuleId, out error);
                break;
            default:
                return new NetworkRemoteResult { RequestId = request.RequestId, Success = false, Error = "UnsupportedTraderRuleOperation" };
        }
        NetworkRemoteResult result = QueryRules(player, request);
        result.Success = success;
        result.Error = error;
        NetworkLog.Debug("Trader rule operation processed. player=" + player.entityId + "; operation=" + request.Operation + "; rule=" + request.TraderRuleId + "; success=" + success + "; error=" + error);
        return result;
    }

    public static bool TrySaveRule(EntityPlayer player, TraderAutomationRule rule, out string error)
    {
        error = string.Empty;
        if (!(bool)player || !PlayerNetworkDirectory.IsNetworkAdministrator(player)) { error = "PermissionDenied"; return false; }
        if (rule == null || rule.Personal || rule.Kind == TraderAutomationRuleKind.AutoDeposit) { error = "InvalidAutomationRule"; return false; }
        if (rule.Kind == TraderAutomationRuleKind.AutoTrash
            && !NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TrashEnabled))
        { error = "TrashDisabled"; return false; }
        if (rule.Kind is TraderAutomationRuleKind.AutoBuy or TraderAutomationRuleKind.AutoSell
            && !NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TraderEnabled))
        { error = "TraderDisabled"; return false; }
        if (!Validate(rule, out error))
        {
            NetworkLog.Debug("Trader automation rule save rejected. player=" + player.entityId + "; kind=" + rule?.Kind
                + "; scope=" + rule?.Scope + "; item=" + (rule?.ItemType ?? 0) + "; maximumCount=" + (rule?.MaximumCount ?? -1)
                + "; zeroMeansUnlimited=" + AllowsUnlimitedMaximumCount(rule) + "; error=" + error);
            return false;
        }
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) { error = "NetworkUnavailable"; return false; }
        bool scanExisting = rule.Kind is (TraderAutomationRuleKind.AutoSell or TraderAutomationRuleKind.AutoScrap) && rule.Enabled
            && IncludesNetworkDeposits(rule.AutoSellSource);
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "TraderRulesUnavailable"; return false; }
            if (IsNetworkExecutionActiveUnsafe(networkId)) { error = "TraderRuleRunInProgress"; return false; }
            List<TraderAutomationRule> previous = Rules.TryGetValue(networkId, out List<TraderAutomationRule> current)
                ? current.Select(item => item.Clone()).ToList() : new List<TraderAutomationRule>();
            List<TraderAutomationRule> updated = previous.Select(item => item.Clone()).ToList();
            int index = updated.FindIndex(item => string.Equals(item.Id, rule.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                if (rule.Scheduled && !updated[index].Scheduled) rule.LastScheduledDay = CurrentWorldDay();
                updated[index] = rule.Clone();
            }
            else
            {
                if (updated.Count >= MaximumRulesPerNetwork) { error = "TraderRuleLimitReached"; return false; }
                if (rule.Scheduled) rule.LastScheduledDay = CurrentWorldDay();
                rule.Id = Guid.NewGuid().ToString("N");
                updated.Add(rule.Clone());
            }
            Rules[networkId] = updated;
            if (Save())
            {
                NetworkLog.Debug("Trader automation rule saved. player=" + player.entityId + "; network=" + networkId + "; rule=" + rule.Id + "; type=" + rule.Kind + "; scope=" + rule.Scope + "; autoSellSource=" + rule.AutoSellSource + "; item=" + rule.ItemType + "; category=" + rule.Category + "; excludedItems=" + rule.ExcludedItemTypes + "; excludedCategories=" + rule.ExcludedCategories + "; maximumCount=" + rule.MaximumCount + "; zeroMeansUnlimited=" + AllowsUnlimitedMaximumCount(rule));
            }
            else
            {
                if (previous.Count == 0) Rules.Remove(networkId); else Rules[networkId] = previous;
                error = "TraderRuleSaveFailed";
                return false;
            }
        }
        if (scanExisting)
        {
            List<TraderAutomationRule> scrapRules = GetRules(player)
                .Where(candidate => candidate.Enabled && candidate.Kind == TraderAutomationRuleKind.AutoScrap).ToList();
            foreach (TraderAutomationRule scrapRule in scrapRules)
                AutoScrapQueueService.ScanNetwork(player, scrapRule, "rule-save-scan");
            List<TraderAutomationRule> sellRules = GetRules(player)
                .Where(candidate => candidate.Enabled && candidate.Kind == TraderAutomationRuleKind.AutoSell).ToList();
            foreach (TraderAutomationRule sellRule in sellRules) ExecuteSellRule(player, sellRule, "rule-save-scan");
            NetworkLog.Debug("Existing network inventory scanned after an automatic sale rule was added or changed. player="
                + player.entityId + "; rule=" + rule.Id + "; orderedSellRules=" + sellRules.Count);
        }
        return true;
    }

    public static bool TryDeleteRule(EntityPlayer player, string ruleId, out string error)
    {
        error = string.Empty;
        if (!(bool)player || !PlayerNetworkDirectory.IsNetworkAdministrator(player)) { error = "PermissionDenied"; return false; }
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) { error = "NetworkUnavailable"; return false; }
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "TraderRulesUnavailable"; return false; }
            if (IsNetworkExecutionActiveUnsafe(networkId)) { error = "TraderRuleRunInProgress"; return false; }
            if (!Rules.TryGetValue(networkId, out List<TraderAutomationRule> current)) { error = "TraderRuleNotFound"; return false; }
            List<TraderAutomationRule> previous = current.Select(item => item.Clone()).ToList();
            current.RemoveAll(item => string.Equals(item.Id, ruleId, StringComparison.Ordinal));
            if (current.Count == previous.Count) { error = "TraderRuleNotFound"; return false; }
            if (Save())
            {
                NetworkLog.Debug("Trader automation rule deleted. player=" + player.entityId + "; network=" + networkId + "; rule=" + ruleId);
                return true;
            }
            Rules[networkId] = previous;
            error = "TraderRuleSaveFailed";
            return false;
        }
    }

    public static bool MoveRule(EntityPlayer player, string ruleId, int offset, out string error)
    {
        error = string.Empty;
        if (!(bool)player || !PlayerNetworkDirectory.IsNetworkAdministrator(player)) { error = "PermissionDenied"; return false; }
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId) || offset == 0 || Math.Abs(offset) >= MaximumRulesPerNetwork) { error = "InvalidTraderRuleMove"; return false; }
        lock (Sync)
        {
            if (!EnsureLoaded() || !Rules.TryGetValue(networkId, out List<TraderAutomationRule> rules)) { error = "TraderRulesUnavailable"; return false; }
            if (IsNetworkExecutionActiveUnsafe(networkId)) { error = "TraderRuleRunInProgress"; return false; }
            int index = rules.FindIndex(rule => rule.Id == ruleId);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= rules.Count) { error = "TraderRuleMoveUnavailable"; return false; }
            TraderAutomationRule moving = rules[index];
            rules.RemoveAt(index);
            rules.Insert(target, moving);
            if (Save()) return true;
            rules.Remove(moving);
            rules.Insert(index, moving);
            error = "TraderRuleSaveFailed";
            return false;
        }
    }

    public static void OnNetworkDeposit(EntityPlayer player, IEnumerable<StorageAddition> additions)
    {
        if (!(bool)player || additions == null) return;
        if (!AutoScrapQueueService.HasOnlineMember(GameManager.Instance?.World, PlayerNetworkDirectory.GetNetworkId(player))) return;
        List<StorageAddition> pending = additions.ToList();
        AutoScrapQueueService.OnNetworkDeposit(player, pending);
        if (!NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TraderEnabled)) return;
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) return;
        lock (Sync)
        {
            if (!EnsureLoaded() || ManualRunNetworks.Contains(networkId) || !ProcessingDeposits.Add(networkId)) return;
            ActiveRuleIds.Remove(networkId);
        }
        try
        {
            List<TraderAutomationRule> sellRules;
            lock (Sync)
                sellRules = Rules.TryGetValue(networkId, out List<TraderAutomationRule> rules)
                    ? rules.Where(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoSell
                        && IncludesNetworkDeposits(rule.AutoSellSource)).Select(rule => rule.Clone()).ToList()
                    : new List<TraderAutomationRule>();
            if (sellRules.Count == 0) return;
            Dictionary<string, long> remainingByRule = sellRules.ToDictionary(rule => rule.Id,
                rule => rule.MaximumCount == 0 ? long.MaxValue : rule.MaximumCount);
            foreach (StorageAddition addition in pending)
            {
                if (addition?.Value == null || addition.Count <= 0) continue;
                TraderAutomationRule rule = sellRules.FirstOrDefault(candidate => Matches(candidate, addition.Value));
                if (rule == null) continue;
                if (!remainingByRule.TryGetValue(rule.Id, out long remaining) || remaining <= 0) continue;
                List<StoredEntry> entries = NetworkStorage.GetEntries(player);
                long matchingCount = entries.Where(entry => Matches(rule, entry.Value)).Sum(entry => entry.Count);
                if (rule.NetworkCountComparison != TraderRuleComparison.Ignore
                    && !Compare(matchingCount, rule.NetworkCountComparison, rule.MinimumNetworkCount)) continue;
                long available = NetworkStorage.GetCount(player, addition.Value);
                int count = (int)Math.Min(Math.Min(Math.Min((long)addition.Count, available), remaining), int.MaxValue);
                if (count <= 0) continue;
                lock (Sync) ActiveRuleIds[networkId] = rule.Id;
                NetworkRemoteResult result = TraderTransactionService.ExecuteVoidSell(player, null, new NetworkRemoteRequest
                {
                    EntityId = player.entityId,
                    RequestId = DateTime.UtcNow.Ticks,
                    Operation = NetworkRequestOperation.TraderVoidSell,
                    Value = addition.Value.Clone(),
                    Count = count,
                    TraderNetworkSource = true,
                    TraderAutomation = true
                });
                if (!result.Success)
                    NetworkLog.Debug("Automatic trader sale skipped after an incoming network deposit. player=" + player.entityId + "; rule=" + rule.Id + "; item=" + addition.Value.type + "; count=" + count + "; error=" + result.Error);
                else
                {
                    addition.Count -= count;
                    remainingByRule[rule.Id] -= count;
                    NetworkLog.Debug("Automatic trader sale completed after an incoming network deposit. player=" + player.entityId + "; rule=" + rule.Id + "; item=" + addition.Value.type + "; count=" + count + "; proceeds=" + result.Amount);
                }
            }
        }
        catch (Exception ex) { NetworkLog.Error("Automatic sale processing failed safely after a network deposit. player=" + player.entityId + "; network=" + networkId + "; exception=" + ex); }
        finally { lock (Sync) { ProcessingDeposits.Remove(networkId); ActiveRuleIds.Remove(networkId); } }
    }

    internal static void OnNetworkTrashDeposit(EntityPlayer player, IEnumerable<StorageAddition> additions)
    {
        if (!(bool)player || additions == null || !NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TrashEnabled)) return;
        if (!AutoScrapQueueService.HasOnlineMember(GameManager.Instance?.World, PlayerNetworkDirectory.GetNetworkId(player))) return;
        List<TraderAutomationRule> rules = GetRules(player)
            .Where(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoTrash && !rule.Personal).ToList();
        if (rules.Count == 0) return;
        foreach (StorageAddition addition in additions)
        {
            if (addition?.Value == null || addition.Count <= 0 || !NetworkItemPolicy.CanEnterOrDestroy(addition.Value)) continue;
            TraderAutomationRule rule = rules.FirstOrDefault(candidate => Matches(candidate, addition.Value));
            if (rule == null) continue;
            long available = NetworkStorage.GetCount(player, addition.Value);
            int count = (int)Math.Min(Math.Min(Math.Min(addition.Count, available), rule.MaximumCount == 0 ? long.MaxValue : rule.MaximumCount), int.MaxValue);
            if (count <= 0) continue;
            int removed = NetworkStorage.Remove(player, addition.Value, count);
            NetworkLog.Debug("Network trash rule processed a deposit after sale. network=" + PlayerNetworkDirectory.GetNetworkId(player)
                + "; rule=" + rule.Id + "; item=" + addition.Value.type + "; requested=" + count + "; removed=" + removed);
        }
    }

    private static void ExecuteGlobalTrashRule(EntityPlayer player, TraderAutomationRule rule, string reason)
    {
        if (!NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TrashEnabled)) return;
        long remaining = rule.MaximumCount == 0 ? long.MaxValue : rule.MaximumCount;
        foreach (StoredEntry entry in NetworkStorage.GetEntries(player).Where(entry => Matches(rule, entry.Value)
            && NetworkItemPolicy.CanEnterOrDestroy(entry.Value)))
        {
            if (remaining <= 0) break;
            int count = (int)Math.Min(Math.Min(remaining, entry.Count), int.MaxValue);
            int removed = NetworkStorage.Remove(player, entry.Value, count);
            remaining -= removed;
            NetworkLog.Debug("Global trash rule executed. network=" + PlayerNetworkDirectory.GetNetworkId(player)
                + "; rule=" + rule.Id + "; item=" + entry.Value.type + "; removed=" + removed + "; reason=" + reason);
        }
    }

    public static void Update(World world)
    {
        if (world == null || world.Players?.list == null) return;
        if (Time.realtimeSinceStartup >= _nextAutomationPoll)
        {
            _nextAutomationPoll = Time.realtimeSinceStartup + 0.25f;
            if (PendingRuns.Count > 0) ProcessPendingRuns(world);
        }
        if (Time.realtimeSinceStartup < _nextScheduleCheck) return;
        _nextScheduleCheck = Time.realtimeSinceStartup + 2f;
        int day = GameUtils.WorldTimeToDays(world.worldTime);
        int hour = GameUtils.WorldTimeToElements(world.worldTime).Hours;
        foreach (IGrouping<string, EntityPlayer> networkPlayers in world.Players.list.Where(player => (bool)player)
            .GroupBy(PlayerNetworkDirectory.GetNetworkId, StringComparer.OrdinalIgnoreCase))
        {
            string networkId = networkPlayers.Key;
            if (string.IsNullOrEmpty(networkId) || NetworkMembershipService.IsNetworkFrozen(networkId)) continue;
            EntityPlayer player = ResolveOnlineAdministrator(world, networkId) ?? networkPlayers.FirstOrDefault();
            if (!(bool)player) continue;
            if (!NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TraderEnabled)) continue;
            List<TraderAutomationRule> due;
            lock (Sync)
            {
                if (!EnsureLoaded() || !Rules.TryGetValue(networkId, out List<TraderAutomationRule> rules)) continue;
                List<TraderAutomationRule> previous = rules.Select(rule => rule.Clone()).ToList();
                due = new List<TraderAutomationRule>();
                bool changed = false;
                foreach (TraderAutomationRule rule in rules)
                {
                    if (!rule.Enabled || rule.Kind != TraderAutomationRuleKind.AutoBuy || !rule.Scheduled || rule.LastScheduledDay < 0) continue;
                    int scheduledDay = rule.LastScheduledDay + rule.IntervalDays;
                    if (day == scheduledDay && hour >= rule.ScheduledHour)
                    {
                        due.Add(rule.Clone());
                        rule.LastScheduledDay = day;
                        changed = true;
                    }
                    else if (day > scheduledDay)
                    {
                        // A sleeping network resumes one due run when a member returns;
                        // missed days do not create a burst of purchases.
                        due.Add(rule.Clone());
                        rule.LastScheduledDay = day;
                        changed = true;
                        NetworkLog.Debug("Automatic trader schedule resumed after a sleeping interval. player=" + player.entityId
                            + "; rule=" + rule.Id + "; scheduledDay=" + scheduledDay + "; currentDay=" + day);
                    }
                }
                if (!changed) continue;
                if (!Save())
                {
                    Rules[networkId] = previous;
                    NetworkLog.Error("Automatic trader purchase schedule could not be persisted; this run was skipped. network=" + networkId + "; day=" + day);
                    continue;
                }
            }
            RunAutomaticBuys(world, player, due, "schedule", out _);
        }
    }

    public static bool RunNow(World world, EntityPlayer player, string ruleId, out string error)
    {
        error = string.Empty;
        if (!(bool)player) { error = "PlayerUnavailable"; return false; }
        bool isAdmin = PlayerNetworkDirectory.IsNetworkAdministrator(player);
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) { error = "NetworkUnavailable"; return false; }
        List<TraderAutomationRule> rules;
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "TraderRulesUnavailable"; return false; }
            rules = isAdmin && Rules.TryGetValue(networkId, out List<TraderAutomationRule> current)
                ? current.Where(rule => rule.Enabled && (string.IsNullOrEmpty(ruleId) || rule.Id == ruleId)).Select(rule => rule.Clone()).ToList()
                : new List<TraderAutomationRule>();
        }
        bool personal = PersonalAutomationRuleService.GetRules(player).Any(rule => rule.Enabled
            && (string.IsNullOrEmpty(ruleId) || rule.Id == ruleId));
        if (rules.Count == 0) { if (personal) return true; error = "TraderRuleNotFound"; return false; }
        if (rules.Any(rule => rule.Kind is TraderAutomationRuleKind.AutoSell or TraderAutomationRuleKind.AutoBuy)
            && !NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TraderEnabled))
            rules.RemoveAll(rule => rule.Kind is TraderAutomationRuleKind.AutoSell or TraderAutomationRuleKind.AutoBuy);
        if (rules.Count == 0) { if (personal) return true; error = "FeatureDisabled"; return false; }
        lock (Sync)
        {
            if (IsNetworkExecutionActiveUnsafe(networkId))
            { error = "TraderRuleRunInProgress"; return false; }
            ManualRunNetworks.Add(networkId);
        }
        SetRunProgress(networkId, true, 1, rules.Count, "正在等待第 1 条规则执行", rules[0].Id);
        if (rules.All(rule => rule.Kind != TraderAutomationRuleKind.AutoBuy))
        {
            PendingRuns.Add(new PendingBuyRun
            {
                World = world, PlayerEntityId = player.entityId, NetworkId = networkId, SessionId = string.Empty, Rules = rules,
                Reason = "manual", StartedAt = Time.realtimeSinceStartup, NextQueryAt = Time.realtimeSinceStartup + 0.05f,
                OffersReady = true
            });
            return true;
        }
        bool queued = RunAutomaticBuys(world, player, rules, "manual", out error);
        if (!queued)
        {
            lock (Sync) ManualRunNetworks.Remove(networkId);
            SetRunProgress(networkId, false, 0, rules.Count, "规则执行未能开始：" + error);
        }
        return queued;
    }

    private static bool RunAutomaticBuys(World world, EntityPlayer player, List<TraderAutomationRule> rules, string reason, out string error)
    {
        error = string.Empty;
        if (world == null || !(bool)player || rules == null || rules.Count == 0) { error = "TraderMarketUnavailable"; return false; }
        if (TraderMarketplaceService.HasSession(player.entityId))
        {
            error = "TraderMarketBusy";
            NetworkLog.Debug("Automatic trader run blocked by an active market session. player=" + player.entityId + "; reason=" + reason);
            return false;
        }
        NetworkRemoteRequest openRequest = new NetworkRemoteRequest { EntityId = player.entityId, RequestId = DateTime.UtcNow.Ticks, Operation = NetworkRequestOperation.TraderOpenMarket, TraderAutomation = true };
        NetworkRemoteResult opened = TraderMarketplaceService.Open(world, player, openRequest);
        if (!opened.Success || string.IsNullOrEmpty(opened.TraderSessionId))
        {
            NetworkLog.Debug("Automatic trader purchase could not open market. player=" + player.entityId + "; reason=" + reason + "; error=" + opened.Error);
            if (reason == "manual")
            {
                PendingRuns.Add(new PendingBuyRun
                {
                    World = world, PlayerEntityId = player.entityId, NetworkId = PlayerNetworkDirectory.GetNetworkId(player), SessionId = string.Empty,
                    Rules = rules.Select(rule => rule.Clone()).ToList(), Reason = reason,
                    StartedAt = Time.realtimeSinceStartup, NextQueryAt = Time.realtimeSinceStartup + 0.05f,
                    OffersReady = true
                });
                SetRunProgress(PlayerNetworkDirectory.GetNetworkId(player), true, 1, rules.Count,
                    "商人市场暂不可用，购入规则将跳过并继续处理", rules[0].Id);
                return true;
            }
            error = opened.Error;
            return false;
        }
        PendingRuns.Add(new PendingBuyRun
        {
            World = world, PlayerEntityId = player.entityId, NetworkId = PlayerNetworkDirectory.GetNetworkId(player), SessionId = opened.TraderSessionId,
            Rules = rules.Select(rule => rule.Clone()).ToList(), Reason = reason,
            StartedAt = Time.realtimeSinceStartup, NextQueryAt = Time.realtimeSinceStartup + 1f
        });
        NetworkLog.Debug("Automatic trader purchase waiting for discovered trader chunks to load. player=" + player.entityId + "; session=" + opened.TraderSessionId + "; reason=" + reason);
        return true;
    }

    private static void ProcessPendingRuns(World world)
    {
        foreach (PendingBuyRun run in PendingRuns.ToArray())
        {
            if (NetworkMembershipService.IsNetworkFrozen(run.NetworkId)) continue;
            if (!AutoScrapQueueService.HasOnlineMember(world, run.NetworkId)) continue;
            if (!ReferenceEquals(run.World, world)) { TraderMarketplaceService.ForceClose(run.SessionId); FinishManualRun(run, "规则执行中断：世界已切换"); PendingRuns.Remove(run); continue; }
            EntityPlayer player = world.GetEntity(run.PlayerEntityId) as EntityPlayer;
            if (!(bool)player) { TraderMarketplaceService.ForceClose(run.SessionId); FinishManualRun(run, "规则执行中断：玩家已离线"); PendingRuns.Remove(run); continue; }
            if (run.Rules.Any(rule => rule.Kind is TraderAutomationRuleKind.AutoSell or TraderAutomationRuleKind.AutoBuy)
                && !NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TraderEnabled))
            {
                TraderMarketplaceService.ForceClose(run.SessionId);
                FinishManualRun(run, "规则执行中断：商人功能已关闭");
                PendingRuns.Remove(run);
                NetworkLog.Debug("Pending trader run cancelled because the network feature was disabled. network=" + run.NetworkId);
                continue;
            }
            if (Time.realtimeSinceStartup < run.NextQueryAt) continue;
            if (!run.OffersReady && !string.IsNullOrEmpty(run.SessionId))
            {
                NetworkRemoteResult query = TraderMarketplaceService.Query(world, player, new NetworkRemoteRequest
                {
                    EntityId = player.entityId, RequestId = DateTime.UtcNow.Ticks, Operation = NetworkRequestOperation.TraderQueryMarket,
                    TraderSessionId = run.SessionId, TraderAutomation = true
                });
                if (!query.Success)
                {
                    NetworkLog.Debug("Automatic trader market query failed. player=" + player.entityId + "; session=" + run.SessionId + "; error=" + query.Error);
                    TraderMarketplaceService.Close(player, new NetworkRemoteRequest { RequestId = DateTime.UtcNow.Ticks, TraderSessionId = run.SessionId });
                    FinishManualRun(run, "规则执行中断：商人库存读取失败");
                    PendingRuns.Remove(run);
                    continue;
                }
                int offerCount = query.TraderOffers?.Count ?? 0;
                if (offerCount == run.LastOfferCount) run.StableOfferQueries++;
                else { run.LastOfferCount = offerCount; run.StableOfferQueries = 0; }
                float elapsed = Time.realtimeSinceStartup - run.StartedAt;
                if (elapsed < 2f || offerCount == 0 && elapsed < 8f || offerCount > 0 && run.StableOfferQueries < 1)
                {
                    run.NextQueryAt = Time.realtimeSinceStartup + 1f;
                    continue;
                }
                run.CurrentOffers = query.TraderOffers ?? new List<TraderOffer>();
                run.OffersReady = true;
            }

            if (run.NextRuleIndex >= run.Rules.Count)
            {
                if (!string.IsNullOrEmpty(run.SessionId))
                    TraderMarketplaceService.Close(player, new NetworkRemoteRequest { RequestId = DateTime.UtcNow.Ticks, TraderSessionId = run.SessionId });
                FinishManualRun(run, "全部 " + run.Rules.Count + " 条规则处理完成；不满足条件的规则已跳过");
                PendingRuns.Remove(run);
                continue;
            }

            TraderAutomationRule currentRule = run.Rules[run.NextRuleIndex];
            int currentIndex = run.NextRuleIndex + 1;
            if (run.Reason == "manual")
                SetRunProgress(PlayerNetworkDirectory.GetNetworkId(player), true, currentIndex, run.Rules.Count,
                    "正在处理第 " + currentIndex + "/" + run.Rules.Count + " 条规则（条件不足将自动跳过）", currentRule.Id);
            try
            {
                if (currentRule.Kind == TraderAutomationRuleKind.AutoSell)
                    ExecuteSellRule(player, currentRule, run.Reason);
                else if (currentRule.Kind == TraderAutomationRuleKind.AutoScrap)
                    AutoScrapQueueService.ScanNetwork(player, currentRule, run.Reason);
                else if (currentRule.Kind == TraderAutomationRuleKind.AutoTrash)
                    ExecuteGlobalTrashRule(player, currentRule, run.Reason);
                else if (string.IsNullOrEmpty(run.SessionId))
                    NetworkLog.Debug("Automatic trader buy rule skipped because market could not be opened. player=" + player.entityId + "; rule=" + currentRule.Id);
                else
                    ExecuteAutomaticBuys(world, player, new PendingBuyRun
                    {
                        Rules = new List<TraderAutomationRule> { currentRule }, Reason = run.Reason,
                        SessionId = run.SessionId
                    }, run.CurrentOffers, out run.CurrentOffers);
            }
            catch (Exception ex) { NetworkLog.Error("Automatic trader rule failed safely. player=" + player.entityId + "; rule=" + currentRule.Id + "; exception=" + ex); }
            run.NextRuleIndex++;
            run.NextQueryAt = Time.realtimeSinceStartup + 0.25f;
            if (run.Reason == "manual" && run.NextRuleIndex < run.Rules.Count)
                SetRunProgress(PlayerNetworkDirectory.GetNetworkId(player), true, run.NextRuleIndex + 1, run.Rules.Count,
                    "正在等待规则 " + (run.NextRuleIndex + 1) + "/" + run.Rules.Count + " 执行", run.Rules[run.NextRuleIndex].Id);
        }
    }

    private static void FinishManualRun(PendingBuyRun run, string message)
    {
        if (run?.Reason != "manual") return;
        string networkId = run.NetworkId;
        if (string.IsNullOrEmpty(networkId) && run.World?.GetEntity(run.PlayerEntityId) is EntityPlayer player && (bool)player)
            networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) return;
        lock (Sync) { ManualRunNetworks.Remove(networkId); ActiveRuleIds.Remove(networkId); }
        SetRunProgress(networkId, false, run.Rules?.Count ?? 0, run.Rules?.Count ?? 0, message);
    }

    private static void ExecuteAutomaticBuys(World world, EntityPlayer player, PendingBuyRun run, List<TraderOffer> offers, out List<TraderOffer> updatedOffers)
    {
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        List<TraderOffer> currentOffers = offers;
        try
        {
            ItemValue currency = ItemClass.GetItem(TraderInfo.CurrencyItem, false);
            foreach (TraderAutomationRule rule in run.Rules)
            {
                if (rule.Kind == TraderAutomationRuleKind.AutoSell)
                {
                    ExecuteSellRule(player, rule, run.Reason);
                    continue;
                }
                if (rule.Kind == TraderAutomationRuleKind.AutoScrap)
                {
                    AutoScrapQueueService.ScanNetwork(player, rule, run.Reason);
                    continue;
                }
                if (rule.Kind == TraderAutomationRuleKind.AutoTrash)
                {
                    ExecuteGlobalTrashRule(player, rule, run.Reason);
                    continue;
                }
                if (currency == null || currency.IsEmpty())
                {
                    NetworkLog.Debug("Automatic trader purchase skipped because currency is unavailable. rule=" + rule.Id);
                    continue;
                }
                long currencyCount = NetworkStorage.GetCount(player, currency);
                if (!Compare(currencyCount, rule.CurrencyCountComparison, rule.MinimumCurrencyCount))
                {
                    NetworkLog.Debug("Automatic trader purchase skipped by currency threshold. rule=" + rule.Id + "; balance=" + currencyCount);
                    continue;
                }
                if (rule.Scope == TraderRuleScope.SingleItem && rule.NetworkCountComparison != TraderRuleComparison.Ignore)
                {
                    long matchingNetworkCount = NetworkStorage.GetEntries(player).Where(entry => Matches(rule, entry.Value)).Sum(entry => entry.Count);
                    if (!Compare(matchingNetworkCount, rule.NetworkCountComparison, rule.MinimumNetworkCount))
                    {
                        NetworkLog.Debug("Automatic trader purchase skipped by network count threshold. rule=" + rule.Id + "; count=" + matchingNetworkCount);
                        continue;
                    }
                }
                long spendRemaining = rule.MaximumSpend > 0 ? Math.Min(rule.MaximumSpend, currencyCount) : currencyCount;
                if (spendRemaining <= 0)
                {
                    NetworkLog.Debug("Automatic trader purchase skipped because spendable currency is zero. rule=" + rule.Id);
                    continue;
                }
                long maximumCount = AllowsUnlimitedMaximumCount(rule) ? long.MaxValue : rule.MaximumCount;
                NetworkRemoteResult bought = TraderMarketplaceService.BuyAutomaticRule(
                    world, player, run.SessionId, rule, currentOffers, maximumCount,
                    rule.MaximumSpend > 0 ? Math.Min(rule.MaximumSpend, spendRemaining) : spendRemaining);
                if (!bought.Success)
                {
                    NetworkLog.Debug("Automatic trader purchase rule failed. player=" + player.entityId + "; rule=" + rule.Id + "; error=" + bought.Error);
                    continue;
                }
                currentOffers = bought.TraderOffers ?? currentOffers;
                NetworkLog.Debug("Automatic trader purchase rule completed. player=" + player.entityId + "; network="
                    + PlayerNetworkDirectory.GetNetworkId(player) + "; rule=" + rule.Id + "; purchased=" + bought.Amount
                    + "; remainingOffers=" + currentOffers.Count + "; reason=" + run.Reason);
            }
        }
        catch (Exception ex) { NetworkLog.Error("Automatic trader purchase execution failed. player=" + player.entityId + "; reason=" + run.Reason + "; exception=" + ex); }
        finally { updatedOffers = currentOffers; }
    }

    private static void ExecuteSellRule(EntityPlayer player, TraderAutomationRule rule, string reason)
    {
        if (rule == null || !IncludesNetworkDeposits(rule.AutoSellSource))
        {
            NetworkLog.Debug("Network inventory sale skipped because the rule source excludes network items. player="
                + player?.entityId + "; rule=" + rule?.Id + "; source=" + rule?.AutoSellSource + "; reason=" + reason);
            return;
        }
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) return;
        lock (Sync) { if (!ProcessingDeposits.Add(networkId)) return; ActiveRuleIds[networkId] = rule.Id; }
        try
        {
        List<StoredEntry> entries = NetworkStorage.GetEntries(player).Where(entry => Matches(rule, entry.Value)).ToList();
        long matchingCount = entries.Sum(entry => entry.Count);
        if (rule.NetworkCountComparison != TraderRuleComparison.Ignore
            && !Compare(matchingCount, rule.NetworkCountComparison, rule.MinimumNetworkCount))
        {
            NetworkLog.Debug("Automatic trader sale skipped by network count threshold. rule=" + rule.Id + "; count=" + matchingCount);
            return;
        }
        long remaining = rule.MaximumCount == 0 ? long.MaxValue : rule.MaximumCount;
        foreach (StoredEntry entry in entries)
        {
            if (remaining <= 0) break;
            int count = (int)Math.Min(Math.Min(remaining, entry.Count), int.MaxValue);
            if (count <= 0) continue;
            NetworkRemoteResult sold = TraderTransactionService.ExecuteVoidSell(player, null, new NetworkRemoteRequest
            {
                EntityId = player.entityId, RequestId = DateTime.UtcNow.Ticks,
                Operation = NetworkRequestOperation.TraderVoidSell, Value = entry.Value.Clone(),
                Count = count, TraderNetworkSource = true, TraderAutomation = true
            });
            if (sold.Success) remaining -= count;
            NetworkLog.Debug("Manual trader sale step. player=" + player.entityId + "; rule=" + rule.Id
                + "; item=" + entry.Value.type + "; count=" + count + "; success=" + sold.Success
                + "; error=" + sold.Error + "; reason=" + reason);
        }
        }
        finally { lock (Sync) { ProcessingDeposits.Remove(networkId); ActiveRuleIds.Remove(networkId); } }
    }

    internal static bool AllowsUnlimitedMaximumCount(TraderAutomationRule rule)
        => rule != null && (rule.Kind == TraderAutomationRuleKind.AutoBuy || rule.Kind == TraderAutomationRuleKind.AutoSell
            || rule.Kind == TraderAutomationRuleKind.AutoDeposit || rule.Kind == TraderAutomationRuleKind.AutoTrash
            || rule.Kind == TraderAutomationRuleKind.AutoScrap);

    internal static bool Matches(TraderAutomationRule rule, ItemValue value)
    {
        if (!NetworkItemPolicy.CanEnterOrDestroy(value)) return false;
        if (rule.Scope == TraderRuleScope.SingleItem && rule.ItemType != value.type) return false;
        if (rule.Scope == TraderRuleScope.Category && !string.IsNullOrEmpty(rule.Category)
            && !rule.Category.Split('|').Any(category => HasTraderCategory(value, category))) return false;
        if (!string.IsNullOrEmpty(rule.ExcludedItemTypes)
            && rule.ExcludedItemTypes.Split(',').Any(item => int.TryParse(item, out int type) && type == value.type)) return false;
        if (!string.IsNullOrEmpty(rule.ExcludedCategories)
            && rule.ExcludedCategories.Split('|').Any(category => HasTraderCategory(value, category))) return false;
        if (rule.QualityMode == TraderQualityMatchMode.Any) return true;
        if (!value.HasQuality) return false;
        if (rule.QualityMode == TraderQualityMatchMode.Exact) return (int)value.Quality == rule.Quality;
        TraderRuleComparison comparison = rule.QualityMode switch
        {
            TraderQualityMatchMode.Greater => TraderRuleComparison.Greater,
            TraderQualityMatchMode.GreaterOrEqual => TraderRuleComparison.GreaterOrEqual,
            TraderQualityMatchMode.Less => TraderRuleComparison.Less,
            TraderQualityMatchMode.LessOrEqual => TraderRuleComparison.LessOrEqual,
            _ => TraderRuleComparison.Equal
        };
        return Compare((int)value.Quality, comparison, rule.Quality);
    }

    internal static bool IncludesNetworkDeposits(TraderAutoSellSource source)
        => source is TraderAutoSellSource.NetworkDeposits or TraderAutoSellSource.Both;

    internal static bool IncludesPlayerInventory(TraderAutoSellSource source)
        => source is TraderAutoSellSource.BackpackAndToolbelt or TraderAutoSellSource.Both;

    internal static bool MatchesNetworkCountRequirement(EntityPlayer player, TraderAutomationRule rule)
    {
        if (rule == null || rule.NetworkCountComparison == TraderRuleComparison.Ignore) return true;
        long count = NetworkStorage.GetEntries(player).Where(entry => Matches(rule, entry.Value)).Sum(entry => entry.Count);
        return Compare(count, rule.NetworkCountComparison, rule.MinimumNetworkCount);
    }

    internal static bool HasTraderCategory(ItemValue value, string category)
    {
        return !string.IsNullOrEmpty(category)
            && (TraderCreativeCategories.Matches(value, category) || XUiC_HyperdimensionalGrid.MatchesCategory(value, category));
    }

    private static bool Compare(long value, TraderRuleComparison comparison, long threshold)
        => comparison switch
        {
            TraderRuleComparison.Greater => value > threshold,
            TraderRuleComparison.GreaterOrEqual => value >= threshold,
            TraderRuleComparison.Equal => value == threshold,
            TraderRuleComparison.LessOrEqual => value <= threshold,
            TraderRuleComparison.Less => value < threshold,
            TraderRuleComparison.Ignore => true,
            _ => false
        };

    private static int CurrentWorldDay()
    {
        World world = GameManager.Instance?.World;
        return world == null ? -1 : GameUtils.WorldTimeToDays(world.worldTime);
    }

    private static bool Validate(TraderAutomationRule rule, out string error)
    {
        error = string.Empty;
        if (rule == null || rule.ItemType < 0 || !Enum.IsDefined(typeof(TraderAutomationRuleKind), rule.Kind)
            || !Enum.IsDefined(typeof(TraderRuleScope), rule.Scope)
            || !Enum.IsDefined(typeof(TraderAutoSellSource), rule.AutoSellSource)
            || !Enum.IsDefined(typeof(TraderQualityMatchMode), rule.QualityMode) || !Enum.IsDefined(typeof(TraderRuleComparison), rule.NetworkCountComparison)
            || !Enum.IsDefined(typeof(TraderRuleComparison), rule.CurrencyCountComparison) || rule.Quality < 0 || rule.Quality > 6
            || rule.MaximumCount < 0 || rule.MaximumCount > 1000000
            || (rule.MaximumCount == 0 && !AllowsUnlimitedMaximumCount(rule))
            || rule.MinimumNetworkCount < 0 || rule.MinimumCurrencyCount < 0 || rule.MaximumSpend < 0
            || rule.ScheduledHour < 0 || rule.ScheduledHour > 23 || rule.IntervalDays < 1 || rule.IntervalDays > 3650)
        { error = "InvalidTraderRule"; return false; }
        if (rule.Scope == TraderRuleScope.SingleItem && rule.ItemType <= 0)
        { error = "TraderRuleRequiresItem"; return false; }
        if ((rule.Category?.Length ?? 0) > 128 || (rule.ExcludedCategories?.Length ?? 0) > 1024
            || (rule.ExcludedItemTypes?.Length ?? 0) > 1024)
        { error = "InvalidTraderRuleFilter"; return false; }
        if (!string.IsNullOrEmpty(rule.Category) && rule.Category.Split('|').Any(string.IsNullOrWhiteSpace))
        { error = "InvalidTraderRuleFilter"; return false; }
        if (!string.IsNullOrEmpty(rule.ExcludedCategories) && rule.ExcludedCategories.Split('|').Any(string.IsNullOrWhiteSpace))
        { error = "InvalidTraderRuleFilter"; return false; }
        if (!string.IsNullOrEmpty(rule.ExcludedItemTypes) && rule.ExcludedItemTypes.Split(',').Any(item =>
            !int.TryParse(item, out int type) || type <= 0))
        { error = "InvalidTraderRuleFilter"; return false; }
        return true;
    }

    internal static bool ValidateAutomationRule(TraderAutomationRule rule, out string error)
    {
        if (!Validate(rule, out error)) return false;
        if (rule.Kind is not (TraderAutomationRuleKind.AutoDeposit or TraderAutomationRuleKind.AutoTrash))
        { error = "InvalidAutomationRuleKind"; return false; }
        return true;
    }

    private static bool EnsureLoaded()
    {
        if (!NetworkSaveDataDirectory.TryGetDirectory(out string directory)) return false;
        string path = Path.Combine(directory, "trader-rules.bin");
        if (_loaded && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return !_unsafe;
        Rules.Clear(); _path = path; _loaded = true; _unsafe = false;
        if (!File.Exists(path)) return true;
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new BinaryReader(stream);
            if (reader.ReadInt32() != FileMagic) throw new InvalidDataException("Unrecognized trader rule file header.");
            int version = reader.ReadInt32();
            if (version != FileVersion)
            {
                reader.Dispose();
                File.Delete(path);
                Rules.Clear();
                NetworkLog.Debug("Incompatible saved trader rules were deleted. savedVersion=" + version + "; requiredVersion=" + FileVersion);
                return true;
            }
            int networkCount = reader.ReadInt32();
            if (networkCount < 0 || networkCount > MaximumNetworks) throw new InvalidDataException("Invalid trader rule network count.");
            for (int i = 0; i < networkCount; i++)
            {
                string networkId = new Guid(reader.ReadBytes(16)).ToString("N");
                int ruleCount = reader.ReadInt32();
                if (ruleCount < 0 || ruleCount > MaximumRulesPerNetwork || Rules.ContainsKey(networkId)) throw new InvalidDataException("Invalid trader rule list.");
                List<TraderAutomationRule> rules = new List<TraderAutomationRule>(ruleCount);
                for (int j = 0; j < ruleCount; j++)
                {
                    TraderAutomationRule rule = new TraderAutomationRule
                    {
                        Id = new Guid(reader.ReadBytes(16)).ToString("N"),
                        Kind = (TraderAutomationRuleKind)reader.ReadByte(),
                        Enabled = reader.ReadBoolean(),
                        ItemType = reader.ReadInt32(),
                        QualityMode = (TraderQualityMatchMode)reader.ReadByte(),
                        Quality = reader.ReadInt32(),
                        MaximumCount = reader.ReadInt32(),
                        MinimumNetworkCount = reader.ReadInt32(),
                        NetworkCountComparison = (TraderRuleComparison)reader.ReadByte(),
                        MinimumCurrencyCount = reader.ReadInt32(),
                        CurrencyCountComparison = (TraderRuleComparison)reader.ReadByte(),
                        MaximumSpend = reader.ReadInt32(),
                        Scheduled = reader.ReadBoolean(),
                        ScheduledHour = reader.ReadInt32(),
                        IntervalDays = reader.ReadInt32(),
                        LastScheduledDay = reader.ReadInt32()
                    };
                    rule.Scope = (TraderRuleScope)reader.ReadByte();
                    rule.Category = reader.ReadString();
                    rule.ExcludedCategories = reader.ReadString();
                    rule.ExcludedItemTypes = reader.ReadString();
                    rule.AutoSellSource = (TraderAutoSellSource)reader.ReadByte();
                    if (!Validate(rule, out string error)) throw new InvalidDataException("Invalid saved trader rule: " + error);
                    rules.Add(rule);
                }
                Rules.Add(networkId, rules);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing trader rule data.");
            NetworkLog.Debug("Trader rules loaded. networks=" + Rules.Count + "; version=" + FileVersion + "; path=" + path);
            return true;
        }
        catch (Exception ex)
        {
            _unsafe = true;
            NetworkLog.Error("Trader rules could not be read; updates are disabled to protect existing rules. path=" + path + "; exception=" + ex);
            return false;
        }
    }

    private static bool Save()
    {
        string temporaryPath = _path + ".tmp";
        try
        {
            string directory = Path.GetDirectoryName(_path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            using (FileStream stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(FileMagic); writer.Write(FileVersion); writer.Write(Rules.Count);
                foreach (KeyValuePair<string, List<TraderAutomationRule>> network in Rules)
                {
                    writer.Write(Guid.ParseExact(network.Key, "N").ToByteArray());
                    writer.Write(network.Value.Count);
                    foreach (TraderAutomationRule rule in network.Value)
                    {
                        writer.Write(Guid.ParseExact(rule.Id, "N").ToByteArray());
                        writer.Write((byte)rule.Kind); writer.Write(rule.Enabled); writer.Write(rule.ItemType);
                        writer.Write((byte)rule.QualityMode); writer.Write(rule.Quality); writer.Write(rule.MaximumCount);
                        writer.Write(rule.MinimumNetworkCount); writer.Write((byte)rule.NetworkCountComparison); writer.Write(rule.MinimumCurrencyCount);
                        writer.Write((byte)rule.CurrencyCountComparison); writer.Write(rule.MaximumSpend); writer.Write(rule.Scheduled);
                        writer.Write(rule.ScheduledHour); writer.Write(rule.IntervalDays); writer.Write(rule.LastScheduledDay);
                        writer.Write((byte)rule.Scope); writer.Write(rule.Category ?? string.Empty);
                        writer.Write(rule.ExcludedCategories ?? string.Empty); writer.Write(rule.ExcludedItemTypes ?? string.Empty);
                        writer.Write((byte)rule.AutoSellSource);
                    }
                }
                writer.Flush(); stream.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(temporaryPath, _path, null); else File.Move(temporaryPath, _path);
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Trader rules could not be saved. path=" + _path + "; exception=" + ex);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            return false;
        }
    }

}
