using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HyperdimensionalNetwork;

// Personal rules follow the stable player identity, including when networks are merged.
internal static class PersonalAutomationRuleService
{
    private const int Magic = 0x31524150; // PAR1
    private const int Version = 1;
    private const int MaximumPlayers = 100000;
    private const int MaximumRules = 256;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, List<TraderAutomationRule>> Rules = new Dictionary<string, List<TraderAutomationRule>>(StringComparer.Ordinal);
    private static string _path;
    private static bool _loaded;
    private static bool _unsafe;

    internal static List<TraderAutomationRule> GetRules(EntityPlayer player)
    {
        string id = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(id)) return new List<TraderAutomationRule>();
        lock (Sync) return EnsureLoaded() && Rules.TryGetValue(id, out List<TraderAutomationRule> rules)
            ? rules.Select(rule => rule.Clone()).ToList() : new List<TraderAutomationRule>();
    }

    internal static bool HasEnabledRules(EntityPlayer player)
    {
        string id = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(id)) return false;
        lock (Sync) return EnsureLoaded() && Rules.TryGetValue(id, out List<TraderAutomationRule> rules) && rules.Any(rule => rule.Enabled);
    }

    internal static bool TrySave(EntityPlayer player, TraderAutomationRule rule, out string error)
    {
        error = string.Empty;
        string id = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(PlayerNetworkDirectory.GetNetworkId(player))) { error = "PlayerIdentityUnavailable"; return false; }
        if (rule == null || !rule.Personal || rule.Kind is not (TraderAutomationRuleKind.AutoDeposit or TraderAutomationRuleKind.AutoTrash)
            || !TraderAutomationService.ValidateAutomationRule(rule, out error))
        { if (string.IsNullOrEmpty(error)) error = "InvalidAutomationRule"; return false; }
        if (rule.Kind == TraderAutomationRuleKind.AutoDeposit && !NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.AutoDepositEnabled)
            || rule.Kind == TraderAutomationRuleKind.AutoTrash && !NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TrashEnabled))
        { error = "FeatureDisabled"; return false; }
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "PersonalRulesUnavailable"; return false; }
            List<TraderAutomationRule> previous = Rules.TryGetValue(id, out List<TraderAutomationRule> existing)
                ? existing.Select(item => item.Clone()).ToList() : new List<TraderAutomationRule>();
            List<TraderAutomationRule> updated = previous.Select(item => item.Clone()).ToList();
            int index = updated.FindIndex(item => item.Id == rule.Id);
            TraderAutomationRule copy = rule.Clone();
            copy.Personal = true;
            if (index < 0)
            {
                if (updated.Count >= MaximumRules) { error = "PersonalRuleLimitReached"; return false; }
                copy.Id = Guid.NewGuid().ToString("N");
                updated.Add(copy);
            }
            else updated[index] = copy;
            Rules[id] = updated;
            if (!Save()) { if (previous.Count == 0) Rules.Remove(id); else Rules[id] = previous; error = "PersonalRuleSaveFailed"; return false; }
            NetworkLog.Debug("Personal automation rule saved. player=" + player.entityId + "; rule=" + copy.Id + "; kind=" + copy.Kind + "; scope=" + copy.Scope);
            return true;
        }
    }

    internal static bool TryDelete(EntityPlayer player, string ruleId, out string error)
    {
        error = string.Empty;
        string id = PlayerNetworkDirectory.GetStablePlayerId(player);
        lock (Sync)
        {
            if (string.IsNullOrEmpty(id) || !EnsureLoaded() || !Rules.TryGetValue(id, out List<TraderAutomationRule> current))
            { error = "PersonalRuleNotFound"; return false; }
            List<TraderAutomationRule> previous = current.Select(item => item.Clone()).ToList();
            if (current.RemoveAll(item => item.Id == ruleId) == 0) { error = "PersonalRuleNotFound"; return false; }
            if (Save()) { NetworkLog.Debug("Personal automation rule deleted. player=" + player.entityId + "; rule=" + ruleId); return true; }
            Rules[id] = previous; error = "PersonalRuleSaveFailed"; return false;
        }
    }

    internal static bool TryMove(EntityPlayer player, string ruleId, int offset, out string error)
    {
        error = string.Empty;
        string id = PlayerNetworkDirectory.GetStablePlayerId(player);
        lock (Sync)
        {
            if (string.IsNullOrEmpty(id) || !EnsureLoaded() || !Rules.TryGetValue(id, out List<TraderAutomationRule> rules))
            { error = "PersonalRuleNotFound"; return false; }
            int index = rules.FindIndex(rule => rule.Id == ruleId);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= rules.Count) { error = "PersonalRuleMoveUnavailable"; return false; }
            TraderAutomationRule moving = rules[index]; rules.RemoveAt(index); rules.Insert(target, moving);
            if (Save()) return true;
            rules.Remove(moving); rules.Insert(index, moving); error = "PersonalRuleSaveFailed"; return false;
        }
    }

    private static bool EnsureLoaded()
    {
        if (!NetworkSaveDataDirectory.TryGetDirectory(out string directory)) return false;
        string path = Path.Combine(directory, "personal-automation-rules.bin");
        if (_loaded && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return !_unsafe;
        _loaded = true; _path = path; _unsafe = false; Rules.Clear();
        if (!File.Exists(path)) return true;
        try
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) throw new InvalidDataException("Invalid personal rule file version.");
            int playerCount = reader.ReadInt32();
            if (playerCount < 0 || playerCount > MaximumPlayers) throw new InvalidDataException("Invalid personal rule player count.");
            for (int i = 0; i < playerCount; i++)
            {
                string id = reader.ReadString();
                int count = reader.ReadInt32();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || count < 0 || count > MaximumRules || Rules.ContainsKey(id))
                    throw new InvalidDataException("Invalid personal rule owner.");
                List<TraderAutomationRule> rules = new List<TraderAutomationRule>(count);
                for (int j = 0; j < count; j++)
                {
                    TraderAutomationRule rule = new TraderAutomationRule
                    {
                        Id = new Guid(reader.ReadBytes(16)).ToString("N"), Kind = (TraderAutomationRuleKind)reader.ReadByte(),
                        Personal = true, Enabled = reader.ReadBoolean(), ItemType = reader.ReadInt32(), Scope = (TraderRuleScope)reader.ReadByte(),
                        Category = reader.ReadString(), ExcludedCategories = reader.ReadString(), ExcludedItemTypes = reader.ReadString(),
                        QualityMode = (TraderQualityMatchMode)reader.ReadByte(), Quality = reader.ReadInt32(), MaximumCount = reader.ReadInt32()
                    };
                    string error = string.Empty;
                    if (rule.Kind is not (TraderAutomationRuleKind.AutoDeposit or TraderAutomationRuleKind.AutoTrash)
                        || !TraderAutomationService.ValidateAutomationRule(rule, out error))
                        throw new InvalidDataException("Invalid personal automation rule: " + error);
                    rules.Add(rule);
                }
                Rules.Add(id, rules);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected personal rule file data.");
            NetworkLog.Debug("Personal automation rules loaded. players=" + Rules.Count + "; path=" + path);
            return true;
        }
        catch (Exception ex)
        {
            _unsafe = true;
            NetworkLog.Error("Personal automation rules could not be read; updates are disabled. path=" + path + "; exception=" + ex);
            return false;
        }
    }

    private static bool Save()
    {
        string temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(Version); writer.Write(Rules.Count);
                foreach (KeyValuePair<string, List<TraderAutomationRule>> player in Rules)
                {
                    writer.Write(player.Key); writer.Write(player.Value.Count);
                    foreach (TraderAutomationRule rule in player.Value)
                    {
                        writer.Write(Guid.ParseExact(rule.Id, "N").ToByteArray()); writer.Write((byte)rule.Kind);
                        writer.Write(rule.Enabled); writer.Write(rule.ItemType); writer.Write((byte)rule.Scope);
                        writer.Write(rule.Category ?? string.Empty); writer.Write(rule.ExcludedCategories ?? string.Empty);
                        writer.Write(rule.ExcludedItemTypes ?? string.Empty); writer.Write((byte)rule.QualityMode);
                        writer.Write(rule.Quality); writer.Write(rule.MaximumCount);
                    }
                }
                writer.Flush(); stream.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Personal automation rules could not be saved. path=" + _path + "; exception=" + ex);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return false;
        }
    }
}
