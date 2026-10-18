using System;
using System.Collections.Generic;
using System.IO;

namespace HyperdimensionalNetwork;

internal sealed class PlayerNetworkRecord
{
    public string NetworkId;
    public string AdminPlayerId;
    public readonly HashSet<string> Members = new HashSet<string>(StringComparer.Ordinal);
}

internal static class PlayerNetworkDirectory
{
    private const int FileMagic = 0x31444E48; // HND1
    private const int FileVersion = 1;
    private const int MaximumNetworks = 100000;
    private const int MaximumMembersPerNetwork = 4096;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, PlayerNetworkRecord> Networks = new Dictionary<string, PlayerNetworkRecord>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PlayerToNetwork = new Dictionary<string, string>(StringComparer.Ordinal);
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
            if (!Save())
            {
                Networks.Remove(networkId);
                PlayerToNetwork.Remove(playerId);
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

    public static bool IsRemoteClient()
    {
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        return (bool)connection && connection.IsClient && !connection.IsServer;
    }

    internal static string GetStablePlayerId(EntityPlayer player)
    {
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
        _directoryPath = path;
        _loaded = true;
        _unsafe = false;
        if (!File.Exists(path)) return true;

        try
        {
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != FileMagic || reader.ReadInt32() != FileVersion)
                    throw new InvalidDataException("Unrecognized network directory header.");
                int networkCount = reader.ReadInt32();
                if (networkCount < 0 || networkCount > MaximumNetworks)
                    throw new InvalidDataException("Invalid network count: " + networkCount);

                for (int i = 0; i < networkCount; i++)
                {
                    string networkId = new Guid(reader.ReadBytes(16)).ToString("N");
                    string adminPlayerId = reader.ReadString();
                    int memberCount = reader.ReadInt32();
                    if (memberCount < 1 || memberCount > MaximumMembersPerNetwork)
                        throw new InvalidDataException("Invalid member count for network " + networkId + ": " + memberCount);

                    PlayerNetworkRecord record = new PlayerNetworkRecord { NetworkId = networkId, AdminPlayerId = adminPlayerId };
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
                foreach (PlayerNetworkRecord record in Networks.Values)
                {
                    writer.Write(Guid.ParseExact(record.NetworkId, "N").ToByteArray());
                    writer.Write(record.AdminPlayerId ?? string.Empty);
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

    private static string Summarize(string playerId)
    {
        if (string.IsNullOrEmpty(playerId)) return "<none>";
        return playerId.Length <= 8 ? playerId : playerId.Substring(0, 4) + "…" + playerId.Substring(playerId.Length - 4);
    }
}
