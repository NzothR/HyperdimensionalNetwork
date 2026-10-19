using System;
using System.Collections.Generic;
using System.IO;

namespace HyperdimensionalNetwork;

internal sealed class PlayerNetworkRecord
{
    public string NetworkId;
    public string AdminPlayerId;
    public long MembershipRevision = 1;
    public readonly HashSet<string> Members = new HashSet<string>(StringComparer.Ordinal);
}

internal static class PlayerNetworkDirectory
{
    private const int FileMagic = 0x31444E48; // HND1
    private const int FileVersion = 3;
    private const int MaximumNetworks = 100000;
    private const int MaximumMembersPerNetwork = 4096;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, PlayerNetworkRecord> Networks = new Dictionary<string, PlayerNetworkRecord>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PlayerToNetwork = new Dictionary<string, string>(StringComparer.Ordinal);
    private static long _directoryRevision = 1;
    private static string _directoryPath;
    private static bool _loaded;
    private static bool _unsafe;

    public static string GetNetworkId(EntityPlayer player)
    {
        if (!(bool)player || IsRemoteClient()) return null;
        string playerId = GetStablePlayerId(player);
        if (string.IsNullOrEmpty(playerId)) return null;

        lock (Sync)
        {
            if (!EnsureLoaded()) return null;
            if (PlayerToNetwork.TryGetValue(playerId, out string existingId)) return existingId;

            string networkId;
            do { networkId = Guid.NewGuid().ToString("N"); }
            while (Networks.ContainsKey(networkId));

            PlayerNetworkRecord record = new PlayerNetworkRecord { NetworkId = networkId, AdminPlayerId = playerId };
            record.Members.Add(playerId);
            Networks.Add(networkId, record);
            PlayerToNetwork.Add(playerId, networkId);
            _directoryRevision++;
            if (!Save())
            {
                Networks.Remove(networkId);
                PlayerToNetwork.Remove(playerId);
                _directoryRevision--;
                return null;
            }

            NetworkLog.Debug("Private network created. playerId=" + Summarize(playerId) + "; networkId=" + networkId);
            return networkId;
        }
    }

    public static bool IsNetworkAdministrator(EntityPlayer player)
    {
        if (!(bool)player || IsRemoteClient()) return false;
        string playerId = GetStablePlayerId(player);
        if (string.IsNullOrEmpty(playerId)) return false;
        lock (Sync)
        {
            if (!EnsureLoaded() || !PlayerToNetwork.TryGetValue(playerId, out string networkId)
                || !Networks.TryGetValue(networkId, out PlayerNetworkRecord record)) return false;
            return string.Equals(record.AdminPlayerId, playerId, StringComparison.Ordinal);
        }
    }

    internal static bool TryGetNetwork(string networkId, out PlayerNetworkRecord record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(networkId)) return false;
        lock (Sync)
        {
            if (!EnsureLoaded() || !Networks.TryGetValue(networkId, out PlayerNetworkRecord stored)) return false;
            record = Clone(stored);
            return true;
        }
    }

    internal static List<PlayerNetworkRecord> GetNetworksSnapshot()
    {
        List<PlayerNetworkRecord> snapshot = new List<PlayerNetworkRecord>();
        lock (Sync)
        {
            if (!EnsureLoaded()) return snapshot;
            foreach (PlayerNetworkRecord record in Networks.Values) snapshot.Add(Clone(record));
        }
        snapshot.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.NetworkId, right.NetworkId));
        return snapshot;
    }

    internal static long GetMembershipRevision()
    {
        lock (Sync) return EnsureLoaded() ? Math.Max(1, _directoryRevision) : 0;
    }

    internal static string GetNetworkIdForPlayerId(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId)) return null;
        lock (Sync)
            return EnsureLoaded() && PlayerToNetwork.TryGetValue(playerId, out string networkId) ? networkId : null;
    }

    internal static bool IsAdministrator(string playerId, string networkId)
    {
        if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(networkId)) return false;
        lock (Sync)
            return EnsureLoaded() && Networks.TryGetValue(networkId, out PlayerNetworkRecord record)
                && PlayerToNetwork.TryGetValue(playerId, out string mappedNetwork)
                && string.Equals(mappedNetwork, networkId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.AdminPlayerId, playerId, StringComparison.Ordinal);
    }

    internal static bool TryDirectSwitch(string playerId, string sourceNetworkId, string targetNetworkId, out string error)
    {
        error = string.Empty;
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "NetworkDirectoryUnavailable"; return false; }
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(sourceNetworkId)
                || string.IsNullOrWhiteSpace(targetNetworkId) || string.Equals(sourceNetworkId, targetNetworkId, StringComparison.OrdinalIgnoreCase)
                || !Networks.TryGetValue(sourceNetworkId, out PlayerNetworkRecord oldSource)
                || !Networks.TryGetValue(targetNetworkId, out PlayerNetworkRecord oldTarget)
                || !PlayerToNetwork.TryGetValue(playerId, out string mapped)
                || !string.Equals(mapped, sourceNetworkId, StringComparison.OrdinalIgnoreCase)
                || !oldSource.Members.Contains(playerId) || string.Equals(oldSource.AdminPlayerId, playerId, StringComparison.Ordinal)
                || oldTarget.Members.Contains(playerId))
            { error = "MembershipChanged"; return false; }
            if (oldTarget.Members.Count >= MaximumMembersPerNetwork) { error = "NetworkMemberLimit"; return false; }

            PlayerNetworkRecord source = Clone(oldSource);
            PlayerNetworkRecord target = Clone(oldTarget);
            source.Members.Remove(playerId);
            source.MembershipRevision++;
            target.Members.Add(playerId);
            target.MembershipRevision++;
            Networks[sourceNetworkId] = source;
            Networks[targetNetworkId] = target;
            PlayerToNetwork[playerId] = targetNetworkId;
            _directoryRevision++;
            if (Save()) return true;

            Networks[sourceNetworkId] = oldSource;
            Networks[targetNetworkId] = oldTarget;
            PlayerToNetwork[playerId] = sourceNetworkId;
            _directoryRevision--;
            error = "NetworkDirectorySaveFailed";
            return false;
        }
    }

    internal static bool TryMergeMember(string playerId, string sourceNetworkId, string targetNetworkId, out string error)
    {
        error = string.Empty;
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "NetworkDirectoryUnavailable"; return false; }
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(sourceNetworkId)
                || string.IsNullOrWhiteSpace(targetNetworkId) || string.Equals(sourceNetworkId, targetNetworkId, StringComparison.OrdinalIgnoreCase)
                || !Networks.TryGetValue(sourceNetworkId, out PlayerNetworkRecord oldSource)
                || !Networks.TryGetValue(targetNetworkId, out PlayerNetworkRecord oldTarget)
                || !PlayerToNetwork.TryGetValue(playerId, out string mapped)
                || !string.Equals(mapped, sourceNetworkId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(oldSource.AdminPlayerId, playerId, StringComparison.Ordinal)
                || oldSource.Members.Count != 1 || !oldSource.Members.Contains(playerId) || oldTarget.Members.Contains(playerId))
            { error = "MembershipChanged"; return false; }
            if (oldTarget.Members.Count >= MaximumMembersPerNetwork) { error = "NetworkMemberLimit"; return false; }

            PlayerNetworkRecord target = Clone(oldTarget);
            target.Members.Add(playerId);
            target.MembershipRevision++;
            sourceNetworkId = oldSource.NetworkId;
            targetNetworkId = oldTarget.NetworkId;
            Networks.Remove(sourceNetworkId);
            Networks[targetNetworkId] = target;
            PlayerToNetwork[playerId] = targetNetworkId;
            _directoryRevision++;
            if (Save()) return true;

            Networks[sourceNetworkId] = oldSource;
            Networks[targetNetworkId] = oldTarget;
            PlayerToNetwork[playerId] = sourceNetworkId;
            _directoryRevision--;
            error = "NetworkDirectorySaveFailed";
            return false;
        }
    }

    internal static bool TryMoveMemberToNewNetwork(string playerId, string sourceNetworkId, string actorPlayerId,
        bool requireAdministrator, out string newNetworkId, out string error)
    {
        newNetworkId = string.Empty;
        error = string.Empty;
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "NetworkDirectoryUnavailable"; return false; }
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(sourceNetworkId)
                || !Networks.TryGetValue(sourceNetworkId, out PlayerNetworkRecord oldSource)
                || !PlayerToNetwork.TryGetValue(playerId, out string mapped)
                || !string.Equals(mapped, sourceNetworkId, StringComparison.OrdinalIgnoreCase)
                || !oldSource.Members.Contains(playerId) || string.Equals(oldSource.AdminPlayerId, playerId, StringComparison.Ordinal)
                || requireAdministrator && (!string.Equals(actorPlayerId, oldSource.AdminPlayerId, StringComparison.Ordinal)
                    || string.Equals(actorPlayerId, playerId, StringComparison.Ordinal)))
            { error = requireAdministrator ? "PermissionDenied" : "MembershipChanged"; return false; }

            do { newNetworkId = Guid.NewGuid().ToString("N"); }
            while (Networks.ContainsKey(newNetworkId));

            PlayerNetworkRecord source = Clone(oldSource);
            source.Members.Remove(playerId);
            source.MembershipRevision++;
            PlayerNetworkRecord created = new PlayerNetworkRecord { NetworkId = newNetworkId, AdminPlayerId = playerId };
            created.Members.Add(playerId);
            Networks[sourceNetworkId] = source;
            Networks.Add(newNetworkId, created);
            PlayerToNetwork[playerId] = newNetworkId;
            _directoryRevision++;
            if (Save()) return true;

            Networks[sourceNetworkId] = oldSource;
            Networks.Remove(newNetworkId);
            PlayerToNetwork[playerId] = sourceNetworkId;
            _directoryRevision--;
            newNetworkId = string.Empty;
            error = "NetworkDirectorySaveFailed";
            return false;
        }
    }

    internal static bool TryTransferAdministrator(string actorPlayerId, string targetPlayerId, string networkId, out string error)
    {
        error = string.Empty;
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "NetworkDirectoryUnavailable"; return false; }
            if (!Networks.TryGetValue(networkId ?? string.Empty, out PlayerNetworkRecord oldRecord)
                || !string.Equals(oldRecord.AdminPlayerId, actorPlayerId, StringComparison.Ordinal)
                || !oldRecord.Members.Contains(targetPlayerId ?? string.Empty)
                || string.Equals(actorPlayerId, targetPlayerId, StringComparison.Ordinal))
            { error = "PermissionDenied"; return false; }

            PlayerNetworkRecord updated = Clone(oldRecord);
            updated.AdminPlayerId = targetPlayerId;
            updated.MembershipRevision++;
            Networks[networkId] = updated;
            _directoryRevision++;
            if (Save()) return true;
            Networks[networkId] = oldRecord;
            _directoryRevision--;
            error = "NetworkDirectorySaveFailed";
            return false;
        }
    }

    internal static bool TrySetAdministratorByServer(string targetPlayerId, string networkId,
        out string previousAdminPlayerId, out string error)
    {
        previousAdminPlayerId = string.Empty;
        error = string.Empty;
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "NetworkDirectoryUnavailable"; return false; }
            if (string.IsNullOrWhiteSpace(targetPlayerId) || string.IsNullOrWhiteSpace(networkId)
                || !Networks.TryGetValue(networkId, out PlayerNetworkRecord oldRecord)
                || !oldRecord.Members.Contains(targetPlayerId)
                || !PlayerToNetwork.TryGetValue(targetPlayerId, out string mappedNetworkId)
                || !string.Equals(mappedNetworkId, networkId, StringComparison.OrdinalIgnoreCase))
            { error = "MemberUnavailable"; return false; }

            previousAdminPlayerId = oldRecord.AdminPlayerId;
            if (string.Equals(previousAdminPlayerId, targetPlayerId, StringComparison.Ordinal)) return true;
            PlayerNetworkRecord updated = Clone(oldRecord);
            updated.AdminPlayerId = targetPlayerId;
            updated.MembershipRevision++;
            Networks[networkId] = updated;
            _directoryRevision++;
            if (Save()) return true;
            Networks[networkId] = oldRecord;
            _directoryRevision--;
            previousAdminPlayerId = string.Empty;
            error = "NetworkDirectorySaveFailed";
            return false;
        }
    }

    public static bool IsRemoteClient()
    {
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        return (bool)connection && connection.IsClient && !connection.IsServer;
    }

    internal static string GetStablePlayerId(EntityPlayer player)
    {
        if (!(bool)player) return null;
        try
        {
            PersistentPlayerData data = GameManager.Instance?.persistentPlayers?.GetPlayerDataFromEntityID(player.entityId);
            string id = data?.PrimaryId?.CombinedString;
            if (!string.IsNullOrWhiteSpace(id)) return id;
            NetworkLog.Error("Could not resolve a stable player ID; network access is disabled. entityId=" + player.entityId);
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Stable player ID lookup failed; network access is disabled. entityId=" + player.entityId + "; exception=" + ex);
        }
        return null;
    }

    private static bool EnsureLoaded()
    {
        if (!NetworkSaveDataDirectory.TryGetDirectory(out string modDirectory)) return false;
        string path = Path.Combine(modDirectory, "networks.hnd");
        if (_loaded && string.Equals(path, _directoryPath, StringComparison.OrdinalIgnoreCase)) return !_unsafe;

        Networks.Clear();
        PlayerToNetwork.Clear();
        _directoryRevision = 1;
        _directoryPath = path;
        _loaded = true;
        _unsafe = false;
        if (!File.Exists(path)) return true;

        try
        {
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                int magic = reader.ReadInt32();
                int version = reader.ReadInt32();
                if (magic != FileMagic || version < 1 || version > FileVersion)
                    throw new InvalidDataException("Unrecognized network directory header.");
                int networkCount = reader.ReadInt32();
                if (networkCount < 0 || networkCount > MaximumNetworks)
                    throw new InvalidDataException("Invalid network count: " + networkCount);
                if (version >= 3)
                {
                    _directoryRevision = reader.ReadInt64();
                    if (_directoryRevision < 1) throw new InvalidDataException("Invalid network membership revision.");
                }

                for (int i = 0; i < networkCount; i++)
                {
                    string networkId = new Guid(reader.ReadBytes(16)).ToString("N");
                    string adminPlayerId = reader.ReadString();
                    long membershipRevision = version >= 2 ? reader.ReadInt64() : 1;
                    int memberCount = reader.ReadInt32();
                    if (memberCount < 1 || memberCount > MaximumMembersPerNetwork)
                        throw new InvalidDataException("Invalid member count for network " + networkId + ": " + memberCount);

                    PlayerNetworkRecord record = new PlayerNetworkRecord { NetworkId = networkId, AdminPlayerId = adminPlayerId, MembershipRevision = Math.Max(1, membershipRevision) };
                    for (int member = 0; member < memberCount; member++)
                    {
                        string playerId = reader.ReadString();
                        if (string.IsNullOrWhiteSpace(playerId) || !record.Members.Add(playerId))
                            throw new InvalidDataException("Invalid or duplicate player mapping in network " + networkId);
                        if (PlayerToNetwork.ContainsKey(playerId))
                            throw new InvalidDataException("Player is mapped to more than one network: " + Summarize(playerId));
                        PlayerToNetwork.Add(playerId, networkId);
                    }
                    if (!record.Members.Contains(adminPlayerId) || Networks.ContainsKey(networkId))
                        throw new InvalidDataException("Invalid administrator or duplicate network ID: " + networkId);
                    Networks.Add(networkId, record);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing network directory data.");
            }
            NetworkLog.Debug("Network directory loaded. networks=" + Networks.Count + "; path=" + _directoryPath);
            return true;
        }
        catch (Exception ex)
        {
            _unsafe = true;
            NetworkLog.Error("Network directory could not be read; creating new networks is blocked to protect player ownership. path=" + path + "; exception=" + ex);
            return false;
        }
    }

    private static bool Save()
    {
        string temporaryPath = _directoryPath + ".tmp";
        try
        {
            string directory = Path.GetDirectoryName(_directoryPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            using (FileStream stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(FileMagic);
                writer.Write(FileVersion);
                writer.Write(Networks.Count);
                writer.Write(Math.Max(1, _directoryRevision));
                foreach (PlayerNetworkRecord record in Networks.Values)
                {
                    writer.Write(Guid.ParseExact(record.NetworkId, "N").ToByteArray());
                    writer.Write(record.AdminPlayerId ?? string.Empty);
                    writer.Write(record.MembershipRevision);
                    writer.Write(record.Members.Count);
                    foreach (string playerId in record.Members) writer.Write(playerId);
                }
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(_directoryPath)) File.Replace(temporaryPath, _directoryPath, null);
            else File.Move(temporaryPath, _directoryPath);
            return true;
        }
        catch (Exception ex)
        {
            _unsafe = true;
            NetworkLog.Error("Network directory snapshot could not be committed; network creation is blocked. path=" + _directoryPath + "; exception=" + ex);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            return false;
        }
    }

    internal static string Summarize(string playerId)
    {
        if (string.IsNullOrEmpty(playerId)) return "<none>";
        return playerId.Length <= 8 ? playerId : playerId.Substring(0, 4) + "…" + playerId.Substring(playerId.Length - 4);
    }

    private static PlayerNetworkRecord Clone(PlayerNetworkRecord source)
    {
        PlayerNetworkRecord copy = new PlayerNetworkRecord
        {
            NetworkId = source.NetworkId,
            AdminPlayerId = source.AdminPlayerId,
            MembershipRevision = source.MembershipRevision
        };
        foreach (string member in source.Members) copy.Members.Add(member);
        return copy;
    }
}
