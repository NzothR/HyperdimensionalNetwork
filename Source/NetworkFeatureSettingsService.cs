using System;
using System.Collections.Generic;
using System.IO;

namespace HyperdimensionalNetwork;

internal enum NetworkFeatureSetting : byte
{
    ReplenishmentEnabled = 1,
    AmmoBankEnabled = 2
}

internal sealed class NetworkFeatureSettings
{
    public bool ReplenishmentEnabled = true;
    public bool AmmoBankEnabled = true;
}

/// <summary>Persists feature switches by NetworkId so every network owns independent settings.</summary>
internal static class NetworkFeatureSettingsService
{
    private const int FileMagic = 0x31465348; // HSF1
    private const int FileVersion = 1;
    private const int MaximumNetworks = 100000;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, NetworkFeatureSettings> Settings = new Dictionary<string, NetworkFeatureSettings>(StringComparer.OrdinalIgnoreCase);
    private static string _path;
    private static bool _loaded;
    private static bool _unsafe;

    public static bool TryGet(EntityPlayer player, out NetworkFeatureSettings settings, out string error)
    {
        settings = null;
        error = string.Empty;
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) { error = "NetworkUnavailable"; return false; }
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "NetworkFeatureSettingsUnavailable"; return false; }
            if (!Settings.TryGetValue(networkId, out NetworkFeatureSettings stored)) stored = new NetworkFeatureSettings();
            settings = Clone(stored);
            return true;
        }
    }

    public static bool TrySet(EntityPlayer player, NetworkFeatureSetting key, bool enabled, out string error)
    {
        error = string.Empty;
        if (!PlayerNetworkDirectory.IsNetworkAdministrator(player)) { error = "PermissionDenied"; return false; }
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) { error = "NetworkUnavailable"; return false; }
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "NetworkFeatureSettingsUnavailable"; return false; }
            bool hadEntry = Settings.TryGetValue(networkId, out NetworkFeatureSettings current);
            current = current ?? new NetworkFeatureSettings();
            NetworkFeatureSettings updated = Clone(current);
            switch (key)
            {
                case NetworkFeatureSetting.ReplenishmentEnabled: updated.ReplenishmentEnabled = enabled; break;
                case NetworkFeatureSetting.AmmoBankEnabled: updated.AmmoBankEnabled = enabled; break;
                default: error = "UnknownSetting"; return false;
            }

            Settings[networkId] = updated;
            if (!Save())
            {
                if (hadEntry) Settings[networkId] = current;
                else Settings.Remove(networkId);
                error = "SettingsSaveFailed";
                return false;
            }
            NetworkLog.Debug("Network feature setting updated. player=" + player.entityId + "; network=" + networkId + "; setting=" + key + "; enabled=" + enabled);
            return true;
        }
    }

    private static NetworkFeatureSettings Clone(NetworkFeatureSettings source)
    {
        return new NetworkFeatureSettings
        {
            ReplenishmentEnabled = source?.ReplenishmentEnabled ?? true,
            AmmoBankEnabled = source?.AmmoBankEnabled ?? true
        };
    }

    private static bool EnsureLoaded()
    {
        string saveDirectory;
        try { saveDirectory = GameIO.GetSaveGameDir(); }
        catch (Exception ex) { NetworkLog.Error("Could not resolve the save directory for network feature settings. " + ex); return false; }
        if (string.IsNullOrEmpty(saveDirectory)) return false;
        string path = Path.Combine(saveDirectory, "HyperdimensionalNetwork", "network-feature-settings.bin");
        if (_loaded && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return !_unsafe;

        Settings.Clear();
        _path = path;
        _loaded = true;
        _unsafe = false;
        if (!File.Exists(path)) return true;
        try
        {
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != FileMagic || reader.ReadInt32() != FileVersion)
                    throw new InvalidDataException("Unrecognized network feature settings header.");
                int count = reader.ReadInt32();
                if (count < 0 || count > MaximumNetworks) throw new InvalidDataException("Invalid network feature settings count: " + count);
                for (int i = 0; i < count; i++)
                {
                    string networkId = new Guid(reader.ReadBytes(16)).ToString("N");
                    NetworkFeatureSettings value = new NetworkFeatureSettings
                    {
                        ReplenishmentEnabled = reader.ReadBoolean(),
                        AmmoBankEnabled = reader.ReadBoolean()
                    };
                    if (Settings.ContainsKey(networkId)) throw new InvalidDataException("Duplicate network feature settings record: " + networkId);
                    Settings.Add(networkId, value);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing network feature settings data.");
            }
            NetworkLog.Debug("Network feature settings loaded. networks=" + Settings.Count + "; path=" + _path);
            return true;
        }
        catch (Exception ex)
        {
            _unsafe = true;
            NetworkLog.Error("Network feature settings could not be read; updates are disabled to protect existing settings. path=" + path + "; exception=" + ex);
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
                writer.Write(FileMagic);
                writer.Write(FileVersion);
                writer.Write(Settings.Count);
                foreach (KeyValuePair<string, NetworkFeatureSettings> entry in Settings)
                {
                    writer.Write(Guid.ParseExact(entry.Key, "N").ToByteArray());
                    writer.Write(entry.Value.ReplenishmentEnabled);
                    writer.Write(entry.Value.AmmoBankEnabled);
                }
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(temporaryPath, _path, null);
            else File.Move(temporaryPath, _path);
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Network feature settings could not be saved. path=" + _path + "; exception=" + ex);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            return false;
        }
    }
}
