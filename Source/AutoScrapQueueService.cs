using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

// One server-owned queue per network. Remaining time is persisted, so no time elapses
// while the world is unloaded or every member of the network is offline.
internal static class AutoScrapQueueService
{
    private sealed class Job
    {
        public string Id;
        public string NetworkId;
        public string RuleId;
        public ItemValue Source;
        public int SourceCount;
        public ItemValue Output;
        public int OutputCount;
        public float RemainingSeconds;
        public byte State; // 0: prepared debit, 1: active, 2: prepared output
        public long StorageRevisionBefore;
    }

    private const int Magic = 0x31515348; // HSQ1
    private const int Version = 2;
    private const int MaximumJobs = 100000;
    private static readonly List<Job> Jobs = new List<Job>();
    private static readonly HashSet<string> AwakeNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static string _path;
    private static bool _loaded;
    private static bool _unsafe;
    private static bool _publishingOutput;
    private static float _lastUpdate;
    private static float _nextSave;

    internal static bool HasOnlineMember(World world, string networkId)
        => world?.Players?.list != null && !string.IsNullOrEmpty(networkId)
            && world.Players.list.Any(player => (bool)player
                && string.Equals(PlayerNetworkDirectory.GetNetworkId(player), networkId, StringComparison.OrdinalIgnoreCase));

    internal static void OnNetworkDeposit(EntityPlayer player, IEnumerable<StorageAddition> additions)
    {
        if (_publishingOutput || !(bool)player || !EnsureLoaded()) return;
        List<TraderAutomationRule> rules = TraderAutomationService.GetRules(player)
            .Where(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoScrap
                && TraderAutomationService.IncludesNetworkDeposits(rule.AutoSellSource)).ToList();
        foreach (StorageAddition addition in additions)
        {
            if (addition?.Value == null || addition.Count <= 0) continue;
            TraderAutomationRule rule = rules.FirstOrDefault(candidate => TraderAutomationService.Matches(candidate, addition.Value)
                && TraderAutomationService.MatchesNetworkCountRequirement(player, candidate));
            if (rule == null) continue;
            int count = (int)Math.Min(Math.Min(addition.Count, rule.MaximumCount == 0 ? int.MaxValue : rule.MaximumCount), int.MaxValue);
            addition.Count -= EnqueueNetwork(player, rule, addition.Value, count, "deposit");
        }
    }

    internal static void ScanNetwork(EntityPlayer player, TraderAutomationRule rule, string reason)
    {
        if (!(bool)player || rule == null || !EnsureLoaded()
            || !HasOnlineMember(GameManager.Instance?.World, PlayerNetworkDirectory.GetNetworkId(player))
            || !TraderAutomationService.IncludesNetworkDeposits(rule.AutoSellSource)) return;
        long remaining = rule.MaximumCount == 0 ? long.MaxValue : rule.MaximumCount;
        foreach (StoredEntry entry in NetworkStorage.GetEntries(player).Where(entry => TraderAutomationService.Matches(rule, entry.Value)))
        {
            if (remaining <= 0 || !TraderAutomationService.MatchesNetworkCountRequirement(player, rule)) break;
            int count = (int)Math.Min(Math.Min(entry.Count, remaining), int.MaxValue);
            remaining -= EnqueueNetwork(player, rule, entry.Value, count, reason);
        }
    }

    internal static int EnqueueNetwork(EntityPlayer player, TraderAutomationRule rule, ItemValue value, int count, string reason)
    {
        if (!(bool)player || rule == null || count <= 0 || !EnsureLoaded()) return 0;
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (!HasOnlineMember(GameManager.Instance?.World, networkId)
            || NetworkMembershipService.IsNetworkFrozen(networkId)) return 0;
        Job job = CreateJob(player, rule, value, count, out int consumed);
        if (job == null) return 0;
        if (consumed <= 0 || NetworkStorage.GetCount(player, value) < consumed) return 0;
        job.StorageRevisionBefore = NetworkStorage.GetRevision(player);
        Jobs.Add(job);
        if (!Save()) { Jobs.Remove(job); return 0; }
        int removed = NetworkStorage.Remove(player, value, consumed);
        if (removed != consumed)
        {
            if (removed > 0 && !NetworkStorage.AddStack(player, new ItemStack(value.Clone(), removed)))
                NetworkLog.Error("Automatic scrap could not restore a partial network debit. network=" + networkId + "; item=" + value.type);
            Jobs.Remove(job);
            if (!Save()) NetworkLog.Error("Automatic scrap prepared debit cleanup could not be saved. network=" + networkId + "; job=" + job.Id);
            return 0;
        }
        job.State = 1;
        if (!Save())
        {
            // The durable prepared record and the storage revision identify the
            // completed debit after restart. Keep the in-memory job running.
            NetworkLog.Error("Automatic scrap active state could not be saved; prepared journal record remains. network=" + networkId + "; job=" + job.Id);
        }
        NetworkLog.Debug("Automatic scrap queued. network=" + networkId + "; job=" + job.Id + "; rule=" + rule.Id
            + "; source=" + value.type + "; consumed=" + consumed + "; output=" + job.Output.type
            + "; outputCount=" + job.OutputCount + "; seconds=" + job.RemainingSeconds + "; reason=" + reason);
        return consumed;
    }

    internal static int GetPlayerConsumption(EntityPlayer player, TraderAutomationRule rule, ItemValue value, int count)
        => CreateJob(player, rule, value, count, out int consumed) == null ? 0 : consumed;

    internal static bool CommitPlayerConsumption(EntityPlayer player, TraderAutomationRule rule, ItemValue value,
        int count, int expectedConsumption)
    {
        Job job = CreateJob(player, rule, value, count, out int consumed);
        if (job == null || consumed != expectedConsumption) return false;
        job.State = 1;
        Jobs.Add(job);
        if (Save())
        {
            NetworkLog.Debug("Automatic scrap queued from player inventory. network=" + job.NetworkId + "; job=" + job.Id
                + "; source=" + value.type + "; consumed=" + consumed + "; seconds=" + job.RemainingSeconds);
            return true;
        }
        Jobs.Remove(job);
        return false;
    }

    private static Job CreateJob(EntityPlayer player, TraderAutomationRule rule, ItemValue value, int count, out int consumed)
    {
        consumed = 0;
        if (!(bool)player || rule == null || value == null || count <= 0 || !EnsureLoaded()
            || Jobs.Count >= MaximumJobs || NetworkScrapService.HasAttachment(value)) return null;
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (!HasOnlineMember(GameManager.Instance?.World, networkId)
            || NetworkMembershipService.IsNetworkFrozen(networkId)) return null;
        EntityPlayer administrator = TraderAutomationService.ResolveOnlineAdministrator(GameManager.Instance?.World, networkId);
        // Shared automation uses the administrator's live effects. When the
        // administrator is offline, the vanilla base duration is used.
        EntityPlayer effectPlayer = (bool)administrator ? administrator : null;
        if (!NetworkScrapService.TryCreateScrapRecipe(effectPlayer, new ItemStack(value.Clone(), count), out Recipe recipe, out consumed)) return null;
        Job job = new Job
        {
            Id = Guid.NewGuid().ToString("N"), NetworkId = networkId, RuleId = rule.Id,
            Source = value.Clone(), SourceCount = consumed, Output = new ItemValue(recipe.itemValueType),
            OutputCount = recipe.count, RemainingSeconds = Math.Max(0.05f, recipe.craftingTime)
        };
        if (job.Output.IsEmpty() || job.OutputCount <= 0) return null;
        NetworkLog.Debug("Automatic scrap recipe evaluated. network=" + networkId + "; rule=" + rule.Id
            + "; administratorOnline=" + ((bool)administrator) + "; effectPlayer=" + (effectPlayer?.entityId ?? -1)
            + "; seconds=" + job.RemainingSeconds + "; consumed=" + consumed);
        return job;
    }

    internal static void Update(World world)
    {
        float now = Time.realtimeSinceStartup;
        float delta = _lastUpdate <= 0f ? 0f : Math.Min(1f, Math.Max(0f, now - _lastUpdate));
        _lastUpdate = now;
        if (world == null || PlayerNetworkDirectory.IsRemoteClient() || !EnsureLoaded() || Jobs.Count == 0) return;
        bool changed = false;
        HashSet<string> advancedNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Job job in Jobs.ToArray())
        {
            if (!advancedNetworks.Add(job.NetworkId)) continue;
            bool awake = HasOnlineMember(world, job.NetworkId) && !NetworkMembershipService.IsNetworkFrozen(job.NetworkId);
            if (!awake)
            {
                if (AwakeNetworks.Remove(job.NetworkId))
                    NetworkLog.Debug("Automatic scrap network asleep; queue timer paused. network=" + job.NetworkId);
                continue;
            }
            if (AwakeNetworks.Add(job.NetworkId))
                NetworkLog.Debug("Automatic scrap network awake; queue timer resumed. network=" + job.NetworkId);
            EntityPlayer actor = world.Players.list.FirstOrDefault(player => (bool)player
                && string.Equals(PlayerNetworkDirectory.GetNetworkId(player), job.NetworkId, StringComparison.OrdinalIgnoreCase));
            if (!(bool)actor) continue;
            if (job.State == 0 || job.State == 2)
            {
                // Loading storage before reading its revision replays the journal.
                NetworkStorage.GetCount(actor, job.State == 0 ? job.Source : job.Output);
                long revision = NetworkStorage.GetRevision(actor);
                if (job.State == 0)
                {
                    if (revision > job.StorageRevisionBefore) job.State = 1;
                    else Jobs.Remove(job);
                }
                else if (revision > job.StorageRevisionBefore) Jobs.Remove(job);
                else job.State = 1;
                if (!Save()) NetworkLog.Error("Automatic scrap recovery state could not be persisted. network=" + job.NetworkId + "; job=" + job.Id);
                NetworkLog.Debug("Automatic scrap job reconciled after interruption. network=" + job.NetworkId
                    + "; job=" + job.Id + "; revision=" + revision + "; before=" + job.StorageRevisionBefore);
                continue;
            }
            if (job.RemainingSeconds > 0f)
            {
                job.RemainingSeconds = Math.Max(0f, job.RemainingSeconds - delta);
                changed = true;
            }
            if (job.RemainingSeconds > 0f) continue;
            // Keep the job if the output cannot be deposited; retries are safe while the
            // source remains held by this queue.
            job.State = 2;
            NetworkStorage.GetCount(actor, job.Output);
            job.StorageRevisionBefore = NetworkStorage.GetRevision(actor);
            if (!Save()) { job.State = 1; continue; }
            _publishingOutput = true;
            bool added = false;
            try { added = NetworkStorage.AddStack(actor, new ItemStack(job.Output.Clone(), job.OutputCount)); }
            catch (Exception ex) { NetworkLog.Error("Automatic scrap output deposit threw. network=" + job.NetworkId + "; job=" + job.Id + "; exception=" + ex); }
            finally { _publishingOutput = false; }
            if (!added && NetworkStorage.GetRevision(actor) > job.StorageRevisionBefore) added = true;
            if (!added)
            {
                job.State = 1;
                if (!Save()) NetworkLog.Error("Automatic scrap failed output state could not be persisted. network=" + job.NetworkId + "; job=" + job.Id);
                NetworkLog.Debug("Automatic scrap output deposit deferred. network=" + job.NetworkId + "; job=" + job.Id);
                continue;
            }
            Jobs.Remove(job);
            changed = true;
            NetworkLog.Debug("Automatic scrap completed. network=" + job.NetworkId + "; job=" + job.Id
                + "; output=" + job.Output.type + "; count=" + job.OutputCount);
            if (!Save()) NetworkLog.Error("Automatic scrap completion could not be persisted. network=" + job.NetworkId + "; job=" + job.Id);
        }
        if (changed && now >= _nextSave)
        {
            Save();
            _nextSave = now + 1f;
        }
    }

    internal static void OnWorldShuttingDown()
    {
        if (_loaded && !_unsafe) Save();
        Jobs.Clear(); AwakeNetworks.Clear(); _path = null; _loaded = false; _unsafe = false; _lastUpdate = 0f;
    }

    internal static bool MigrateNetwork(string sourceNetworkId, string targetNetworkId)
    {
        if (!EnsureLoaded()) return false;
        List<Job> migrated = Jobs.Where(job => string.Equals(job.NetworkId, sourceNetworkId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (migrated.Count == 0) return true;
        foreach (Job job in migrated) job.NetworkId = targetNetworkId;
        if (Save())
        {
            NetworkLog.Debug("Automatic scrap jobs migrated with network merge. source=" + sourceNetworkId
                + "; target=" + targetNetworkId + "; jobs=" + migrated.Count);
            return true;
        }
        foreach (Job job in migrated) job.NetworkId = sourceNetworkId;
        return false;
    }

    private static bool EnsureLoaded()
    {
        if (!NetworkSaveDataDirectory.TryGetDirectory(out string directory)) return false;
        string path = Path.Combine(directory, "auto-scrap-queue.bin");
        if (_loaded && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return !_unsafe;
        Jobs.Clear(); AwakeNetworks.Clear(); _path = path; _loaded = true; _unsafe = false; _lastUpdate = 0f;
        if (!File.Exists(path)) return true;
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using PooledBinaryReader reader = MemoryPools.poolBinaryReader.AllocSync(false);
            reader.SetBaseStream(stream);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) throw new InvalidDataException("Invalid auto scrap queue header.");
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumJobs) throw new InvalidDataException("Invalid auto scrap job count.");
            for (int i = 0; i < count; i++)
            {
                Job job = new Job { Id = reader.ReadString(), NetworkId = reader.ReadString(), RuleId = reader.ReadString(), Source = new ItemValue() };
                job.Source.Read(reader); job.SourceCount = reader.ReadInt32(); job.Output = new ItemValue(); job.Output.Read(reader);
                job.OutputCount = reader.ReadInt32(); job.RemainingSeconds = reader.ReadSingle();
                job.State = reader.ReadByte(); job.StorageRevisionBefore = reader.ReadInt64();
                if (!Guid.TryParse(job.Id, out _) || !Guid.TryParse(job.NetworkId, out _) || job.SourceCount <= 0
                    || job.OutputCount <= 0 || job.Source.IsEmpty() || job.Output.IsEmpty()
                    || float.IsNaN(job.RemainingSeconds) || job.RemainingSeconds < 0f || job.State > 2
                    || job.StorageRevisionBefore < 0)
                    throw new InvalidDataException("Invalid auto scrap job.");
                Jobs.Add(job);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing auto scrap queue data.");
            NetworkLog.Debug("Automatic scrap queue loaded. jobs=" + Jobs.Count + "; path=" + path);
            return true;
        }
        catch (Exception ex)
        {
            Jobs.Clear(); _unsafe = true;
            NetworkLog.Error("Automatic scrap queue could not be read; processing disabled. path=" + path + "; exception=" + ex);
            return false;
        }
    }

    private static bool Save()
    {
        if (_unsafe || string.IsNullOrEmpty(_path)) return false;
        string temporary = _path + ".tmp";
        try
        {
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (PooledBinaryWriter writer = MemoryPools.poolBinaryWriter.AllocSync(false))
            {
                writer.SetBaseStream(stream);
                BinaryWriter binary = writer;
                binary.Write(Magic); binary.Write(Version); binary.Write(Jobs.Count);
                foreach (Job job in Jobs)
                {
                    binary.Write(job.Id); binary.Write(job.NetworkId); binary.Write(job.RuleId);
                    job.Source.Write(writer); binary.Write(job.SourceCount);
                    job.Output.Write(writer); binary.Write(job.OutputCount); binary.Write(job.RemainingSeconds);
                    binary.Write(job.State); binary.Write(job.StorageRevisionBefore);
                }
                writer.Flush(); stream.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Automatic scrap queue could not be saved. path=" + _path + "; exception=" + ex);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return false;
        }
    }
}
