using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal enum TraderAutomationRuleKind : byte { AutoSell = 1, AutoBuy = 2 }
internal enum TraderQualityMatchMode : byte { Any = 0, Exact = 1, Greater = 2, GreaterOrEqual = 3, Less = 4, LessOrEqual = 5 }
internal enum TraderRuleComparison : byte { Greater = 0, GreaterOrEqual = 1, Equal = 2, LessOrEqual = 3, Less = 4 }

internal sealed class TraderAutomationRule
{
    public string Id = Guid.NewGuid().ToString("N");
    public TraderAutomationRuleKind Kind;
    public bool Enabled = true;
    public int ItemType;
    public TraderQualityMatchMode QualityMode;
    public int Quality;
    public int MaximumCount = 1;
    public int MinimumNetworkCount;
    public TraderRuleComparison NetworkCountComparison = TraderRuleComparison.GreaterOrEqual;
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
        public List<TraderAutomationRule> Rules;
        public string Reason;
        public float StartedAt;
        public float NextQueryAt;
        public int LastOfferCount = -1;
        public int StableOfferQueries;
    }
    private const int FileMagic = 0x31525448; // HTR1
    private const int FileVersion = 2;
    private const int MaximumNetworks = 100000;
    private const int MaximumRulesPerNetwork = 2048;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, List<TraderAutomationRule>> Rules = new Dictionary<string, List<TraderAutomationRule>>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ProcessingDeposits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    public static NetworkRemoteResult QueryRules(EntityPlayer player, NetworkRemoteRequest request)
    {
        bool isAdmin = (bool)player && PlayerNetworkDirectory.IsNetworkAdministrator(player);
        return new NetworkRemoteResult
        {
            RequestId = request?.RequestId ?? 0,
            Success = true,
            IsNetworkAdministrator = isAdmin,
            TraderRules = isAdmin ? GetRules(player) : new List<TraderAutomationRule>()
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
        string error;
        bool success;
        switch (request.Operation)
        {
            case NetworkRequestOperation.TraderSaveRule:
                success = TrySaveRule(player, request.TraderRule, out error);
                break;
            case NetworkRequestOperation.TraderDeleteRule:
                success = TryDeleteRule(player, request.TraderRuleId, out error);
                break;
            case NetworkRequestOperation.TraderMoveRule:
                success = MoveRule(player, request.TraderRuleId, request.TraderRuleMove, out error);
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
        if (!Validate(rule, out error)) return false;
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) { error = "NetworkUnavailable"; return false; }
        bool scanExisting = rule.Kind == TraderAutomationRuleKind.AutoSell && rule.Enabled;
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "TraderRulesUnavailable"; return false; }
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
                NetworkLog.Debug("Trader automation rule saved. player=" + player.entityId + "; network=" + networkId + "; rule=" + rule.Id + "; type=" + rule.Kind + "; item=" + rule.ItemType);
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
            List<StorageAddition> existing = NetworkStorage.GetEntries(player)
                .Where(entry => Matches(rule, entry.Value))
                .Select(entry => new StorageAddition { Value = entry.Value.Clone(), Count = (int)Math.Min(int.MaxValue, entry.Count) }).ToList();
            OnNetworkDeposit(player, existing);
            NetworkLog.Debug("Existing network inventory scanned after an automatic sale rule was added or changed. player=" + player.entityId + "; rule=" + rule.Id + "; matchingVariants=" + existing.Count);
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
        if (string.IsNullOrEmpty(networkId) || Math.Abs(offset) != 1) { error = "InvalidTraderRuleMove"; return false; }
        lock (Sync)
        {
            if (!EnsureLoaded() || !Rules.TryGetValue(networkId, out List<TraderAutomationRule> rules)) { error = "TraderRulesUnavailable"; return false; }
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
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) return;
        lock (Sync)
        {
            if (!EnsureLoaded() || !ProcessingDeposits.Add(networkId)) return;
        }
        try
        {
            List<TraderAutomationRule> sellRules;
            lock (Sync)
                sellRules = Rules.TryGetValue(networkId, out List<TraderAutomationRule> rules)
                    ? rules.Where(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoSell).Select(rule => rule.Clone()).ToList()
                    : new List<TraderAutomationRule>();
            if (sellRules.Count == 0) return;
            foreach (StorageAddition addition in additions)
            {
                if (addition?.Value == null || addition.Count <= 0) continue;
                TraderAutomationRule rule = sellRules.FirstOrDefault(candidate => Matches(candidate, addition.Value));
                if (rule == null) continue;
                List<StoredEntry> entries = NetworkStorage.GetEntries(player);
                long matchingCount = entries.Where(entry => Matches(rule, entry.Value)).Sum(entry => entry.Count);
                if (!Compare(matchingCount, rule.NetworkCountComparison, rule.MinimumNetworkCount)) continue;
                long available = NetworkStorage.GetCount(player, addition.Value);
                int count = (int)Math.Min(Math.Min(Math.Min(addition.Count, available), rule.MaximumCount), int.MaxValue);
                if (count <= 0) continue;
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
                    NetworkLog.Debug("Automatic trader sale completed after an incoming network deposit. player=" + player.entityId + "; rule=" + rule.Id + "; item=" + addition.Value.type + "; count=" + count + "; proceeds=" + result.Amount);
            }
        }
        catch (Exception ex) { NetworkLog.Error("Automatic sale processing failed safely after a network deposit. player=" + player.entityId + "; network=" + networkId + "; exception=" + ex); }
        finally { lock (Sync) ProcessingDeposits.Remove(networkId); }
    }

    public static void Update(World world)
    {
        if (world == null || world.Players?.list == null) return;
        if (Time.realtimeSinceStartup >= _nextAutomationPoll)
        {
            _nextAutomationPoll = Time.realtimeSinceStartup + 1f;
            ProcessPendingRuns(world);
        }
        if (Time.realtimeSinceStartup < _nextScheduleCheck) return;
        _nextScheduleCheck = Time.realtimeSinceStartup + 2f;
        int day = GameUtils.WorldTimeToDays(world.worldTime);
        int hour = GameUtils.WorldTimeToElements(world.worldTime).Hours;
        foreach (IGrouping<string, EntityPlayer> networkPlayers in world.Players.list.Where(player => (bool)player)
            .GroupBy(PlayerNetworkDirectory.GetNetworkId, StringComparer.OrdinalIgnoreCase))
        {
            string networkId = networkPlayers.Key;
            if (string.IsNullOrEmpty(networkId)) continue;
            EntityPlayer player = ResolveOnlineAdministrator(world, networkId) ?? networkPlayers.FirstOrDefault();
            if (!(bool)player) continue;
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
                        // A missed daily window is skipped; restart the interval from today instead of catching up.
                        rule.LastScheduledDay = day;
                        changed = true;
                        NetworkLog.Debug("Automatic trader schedule window was missed and skipped. player=" + player.entityId + "; rule=" + rule.Id + "; scheduledDay=" + scheduledDay + "; currentDay=" + day);
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
        if (!(bool)player || !PlayerNetworkDirectory.IsNetworkAdministrator(player)) { error = "PermissionDenied"; return false; }
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) { error = "NetworkUnavailable"; return false; }
        List<TraderAutomationRule> rules;
        lock (Sync)
        {
            if (!EnsureLoaded() || !Rules.TryGetValue(networkId, out List<TraderAutomationRule> current)) { error = "TraderRuleNotFound"; return false; }
            rules = current.Where(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoBuy
                && (string.IsNullOrEmpty(ruleId) || rule.Id == ruleId)).Select(rule => rule.Clone()).ToList();
        }
        if (rules.Count == 0) { error = "TraderRuleNotFound"; return false; }
        return RunAutomaticBuys(world, player, rules, "manual", out error);
    }

    private static bool RunAutomaticBuys(World world, EntityPlayer player, List<TraderAutomationRule> rules, string reason, out string error)
    {
        error = string.Empty;
        if (world == null || !(bool)player || rules == null || rules.Count == 0) { error = "TraderMarketUnavailable"; return false; }
        if (TraderMarketplaceService.HasSession(player.entityId)) { error = "TraderMarketBusy"; return false; }
        NetworkRemoteRequest openRequest = new NetworkRemoteRequest { EntityId = player.entityId, RequestId = DateTime.UtcNow.Ticks, Operation = NetworkRequestOperation.TraderOpenMarket, TraderAutomation = true };
        NetworkRemoteResult opened = TraderMarketplaceService.Open(world, player, openRequest);
        if (!opened.Success || string.IsNullOrEmpty(opened.TraderSessionId))
        {
            NetworkLog.Debug("Automatic trader purchase could not open market. player=" + player.entityId + "; reason=" + reason + "; error=" + opened.Error);
            error = opened.Error;
            return false;
        }
        PendingRuns.Add(new PendingBuyRun
        {
            World = world, PlayerEntityId = player.entityId, SessionId = opened.TraderSessionId,
            Rules = rules.Select(rule => rule.Clone()).ToList(), Reason = reason,
            StartedAt = Time.realtimeSinceStartup, NextQueryAt = Time.realtimeSinceStartup + 2f
        });
        NetworkLog.Debug("Automatic trader purchase waiting for discovered trader chunks to load. player=" + player.entityId + "; session=" + opened.TraderSessionId + "; reason=" + reason);
        return true;
    }

    private static void ProcessPendingRuns(World world)
    {
        foreach (PendingBuyRun run in PendingRuns.ToArray())
        {
            if (!ReferenceEquals(run.World, world)) { TraderMarketplaceService.ForceClose(run.SessionId); PendingRuns.Remove(run); continue; }
            EntityPlayer player = world.GetEntity(run.PlayerEntityId) as EntityPlayer;
            if (!(bool)player) { TraderMarketplaceService.ForceClose(run.SessionId); PendingRuns.Remove(run); continue; }
            if (Time.realtimeSinceStartup < run.NextQueryAt) continue;
            NetworkRemoteResult query = TraderMarketplaceService.Query(world, player, new NetworkRemoteRequest
            {
                EntityId = player.entityId, RequestId = DateTime.UtcNow.Ticks, Operation = NetworkRequestOperation.TraderQueryMarket,
                TraderSessionId = run.SessionId, TraderAutomation = true
            });
            if (!query.Success)
            {
                NetworkLog.Debug("Automatic trader market query failed. player=" + player.entityId + "; session=" + run.SessionId + "; error=" + query.Error);
                TraderMarketplaceService.Close(player, new NetworkRemoteRequest { RequestId = DateTime.UtcNow.Ticks, TraderSessionId = run.SessionId });
                PendingRuns.Remove(run);
                continue;
            }
            int offerCount = query.TraderOffers?.Count ?? 0;
            if (offerCount == run.LastOfferCount) run.StableOfferQueries++;
            else { run.LastOfferCount = offerCount; run.StableOfferQueries = 0; }
            float elapsed = Time.realtimeSinceStartup - run.StartedAt;
            if (elapsed < 4f || offerCount == 0 && elapsed < 15f || offerCount > 0 && run.StableOfferQueries < 2)
            {
                run.NextQueryAt = Time.realtimeSinceStartup + 1f;
                continue;
            }
            try { ExecuteAutomaticBuys(world, player, run, query.TraderOffers ?? new List<TraderOffer>()); }
            catch (Exception ex) { NetworkLog.Error("Automatic trader purchase failed safely. player=" + player.entityId + "; reason=" + run.Reason + "; exception=" + ex); }
            finally
            {
                TraderMarketplaceService.Close(player, new NetworkRemoteRequest { RequestId = DateTime.UtcNow.Ticks, TraderSessionId = run.SessionId });
                PendingRuns.Remove(run);
            }
        }
    }

    private static void ExecuteAutomaticBuys(World world, EntityPlayer player, PendingBuyRun run, List<TraderOffer> offers)
    {
        try
        {
            ItemValue currency = ItemClass.GetItem(TraderInfo.CurrencyItem, false);
            foreach (TraderAutomationRule rule in run.Rules)
            {
                if (currency == null || currency.IsEmpty()) break;
                long currencyCount = NetworkStorage.GetCount(player, currency);
                if (!Compare(currencyCount, rule.CurrencyCountComparison, rule.MinimumCurrencyCount)) continue;
                if (rule.ItemType > 0)
                {
                    long matchingNetworkCount = NetworkStorage.GetEntries(player).Where(entry => Matches(rule, entry.Value)).Sum(entry => entry.Count);
                    if (!Compare(matchingNetworkCount, rule.NetworkCountComparison, rule.MinimumNetworkCount)) continue;
                }
                long spendRemaining = rule.MaximumSpend > 0 ? Math.Min(rule.MaximumSpend, currencyCount) : currencyCount;
                if (spendRemaining <= 0) continue;
                int purchased = 0;
                foreach (TraderOffer offer in offers.OrderBy(offer => offer.BundlePrice).ThenBy(offer => offer.TraderEntityId).ThenBy(offer => offer.InventoryIndex))
                {
                    if (purchased >= rule.MaximumCount || !Matches(rule, offer.Value) || offer.Count < offer.BundleSize || offer.BundlePrice <= 0) continue;
                    int affordableBundles = (int)Math.Min(int.MaxValue, spendRemaining / offer.BundlePrice);
                    int availableBundles = offer.Count / offer.BundleSize;
                    int desiredBundles = Math.Min(Math.Min(affordableBundles, availableBundles), (rule.MaximumCount - purchased) / offer.BundleSize);
                    if (desiredBundles <= 0) continue;
                    int count = desiredBundles * offer.BundleSize;
                    NetworkRemoteRequest buyRequest = new NetworkRemoteRequest
                    {
                        EntityId = player.entityId, RequestId = DateTime.UtcNow.Ticks, Operation = NetworkRequestOperation.TraderBuyItem,
                        TraderSessionId = run.SessionId, TraderEntityId = offer.TraderEntityId, TraderInventoryIndex = offer.InventoryIndex,
                        Value = offer.Value.Clone(), Count = count, TraderNetworkSource = true, TraderAutomation = true
                    };
                    NetworkRemoteResult bought = TraderMarketplaceService.Buy(world, player, null, buyRequest);
                    if (!bought.Success) continue;
                    purchased += bought.Amount;
                    spendRemaining -= (long)offer.BundlePrice * desiredBundles;
                    NetworkLog.Debug("Automatic trader purchase completed. player=" + player.entityId + "; network=" + PlayerNetworkDirectory.GetNetworkId(player) + "; rule=" + rule.Id + "; item=" + offer.Value.type + "; count=" + bought.Amount + "; reason=" + run.Reason);
                }
            }
        }
        catch (Exception ex) { NetworkLog.Error("Automatic trader purchase execution failed. player=" + player.entityId + "; reason=" + run.Reason + "; exception=" + ex); }
    }

    private static bool Matches(TraderAutomationRule rule, ItemValue value)
    {
        if (rule.ItemType > 0 && rule.ItemType != value.type) return false;
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

    private static bool Compare(long value, TraderRuleComparison comparison, long threshold)
        => comparison switch
        {
            TraderRuleComparison.Greater => value > threshold,
            TraderRuleComparison.GreaterOrEqual => value >= threshold,
            TraderRuleComparison.Equal => value == threshold,
            TraderRuleComparison.LessOrEqual => value <= threshold,
            TraderRuleComparison.Less => value < threshold,
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
            || !Enum.IsDefined(typeof(TraderQualityMatchMode), rule.QualityMode) || !Enum.IsDefined(typeof(TraderRuleComparison), rule.NetworkCountComparison)
            || !Enum.IsDefined(typeof(TraderRuleComparison), rule.CurrencyCountComparison) || rule.Quality < 0 || rule.Quality > 6
            || rule.MaximumCount <= 0 || rule.MaximumCount > 1000000 || rule.MinimumNetworkCount < 0 || rule.MinimumCurrencyCount < 0 || rule.MaximumSpend < 0
            || rule.ScheduledHour < 0 || rule.ScheduledHour > 23 || rule.IntervalDays < 1 || rule.IntervalDays > 3650)
        { error = "InvalidTraderRule"; return false; }
        if (rule.Kind == TraderAutomationRuleKind.AutoBuy && rule.ItemType <= 0)
        { error = "TraderBuyRuleRequiresItem"; return false; }
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
            if (version < 1 || version > FileVersion) throw new InvalidDataException("Unsupported trader rule file version: " + version);
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
                        QualityMode = version >= 2 ? (TraderQualityMatchMode)reader.ReadByte() : ReadLegacyQualityMode(reader.ReadByte()),
                        Quality = reader.ReadInt32(),
                        MaximumCount = reader.ReadInt32(),
                        MinimumNetworkCount = reader.ReadInt32(),
                        NetworkCountComparison = version >= 2 ? (TraderRuleComparison)reader.ReadByte() : TraderRuleComparison.GreaterOrEqual,
                        MinimumCurrencyCount = version >= 2 ? reader.ReadInt32() : 0,
                        CurrencyCountComparison = version >= 2 ? (TraderRuleComparison)reader.ReadByte() : TraderRuleComparison.GreaterOrEqual,
                        MaximumSpend = reader.ReadInt32(),
                        Scheduled = reader.ReadBoolean(),
                        ScheduledHour = reader.ReadInt32(),
                        IntervalDays = reader.ReadInt32(),
                        LastScheduledDay = reader.ReadInt32()
                    };
                    if (!Validate(rule, out string error)) throw new InvalidDataException("Invalid saved trader rule: " + error);
                    rules.Add(rule);
                }
                Rules.Add(networkId, rules);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing trader rule data.");
            NetworkLog.Debug("Trader rules loaded. networks=" + Rules.Count + "; path=" + path);
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

    private static TraderQualityMatchMode ReadLegacyQualityMode(byte value) => value switch
    {
        0 => TraderQualityMatchMode.Any,
        1 => TraderQualityMatchMode.Exact,
        2 => TraderQualityMatchMode.GreaterOrEqual,
        _ => (TraderQualityMatchMode)value
    };
}
