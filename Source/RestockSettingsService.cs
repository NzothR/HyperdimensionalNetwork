using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HyperdimensionalNetwork;

internal enum RestockSettingKey : byte
{
    AutoRestockEnabled = 1,
    AutoRestockTarget = 2,
    ReplenishmentEnabled = 3,
    AmmoBankEnabled = 4,
    RetainSearchOnClose = 5,
    ManualSaleEnabled = 6,
    TraderEnabled = 7,
    CraftingChainEnabled = 8,
    AutoDepositEnabled = 9,
    TrashEnabled = 10,
    BookReadingEnabled = 11
}

internal enum RestockTargetMode : byte
{
    ToolbeltAndLockedBackpackStacks = 0,
    LockedBackpackOnly = 1,
    ToolbeltOnly = 2
}

internal enum RestockSettingPermission : byte
{
    NetworkMember = 0,
    NetworkAdministrator = 1
}

internal sealed class RestockSettingDefinition
{
    public RestockSettingKey Key;
    public string Name;
    public string LabelKey;
    public RestockSettingPermission Permission;
}

internal sealed class RestockSettingsSnapshot
{
    public bool AutoRestockEnabled;
    public RestockTargetMode TargetMode;
    public bool IsNetworkAdministrator;
    public bool ReplenishmentEnabled = true;
    public bool AmmoBankEnabled = true;
    public bool TraderEnabled = true;
    public bool CraftingChainEnabled = true;
    public bool AutoDepositEnabled = true;
    public bool TrashEnabled = true;
    public bool BookReadingEnabled = true;
    public bool RetainSearchOnClose = true;
    public bool ManualSaleEnabled;
    public int AutoRestockCooldownMs;
}

internal static class RestockSettingCatalog
{
    private static readonly RestockSettingDefinition[] Definitions =
    {
        new RestockSettingDefinition
        {
            Key = RestockSettingKey.AutoRestockEnabled,
            Name = "AutoRestockEnabled",
            LabelKey = "HyperdimensionalNetworkSettingAutoRestock",
            Permission = RestockSettingPermission.NetworkMember
        },
        new RestockSettingDefinition
        {
            Key = RestockSettingKey.AutoRestockTarget,
            Name = "AutoRestockTarget",
            LabelKey = "HyperdimensionalNetworkSettingAutoRestockTarget",
            Permission = RestockSettingPermission.NetworkMember
        },
        new RestockSettingDefinition
        {
            Key = RestockSettingKey.ReplenishmentEnabled,
            Name = "ReplenishmentEnabled",
            LabelKey = "HyperdimensionalNetworkSettingReplenishmentEnabled",
            Permission = RestockSettingPermission.NetworkAdministrator
        },
        new RestockSettingDefinition
        {
            Key = RestockSettingKey.AmmoBankEnabled,
            Name = "AmmoBankEnabled",
            LabelKey = "HyperdimensionalNetworkSettingAmmoBankEnabled",
            Permission = RestockSettingPermission.NetworkAdministrator
        },
        new RestockSettingDefinition { Key = RestockSettingKey.TraderEnabled, Name = "TraderEnabled", LabelKey = "HyperdimensionalNetworkSettingTraderEnabled", Permission = RestockSettingPermission.NetworkAdministrator },
        new RestockSettingDefinition { Key = RestockSettingKey.CraftingChainEnabled, Name = "CraftingChainEnabled", LabelKey = "HyperdimensionalNetworkSettingCraftingChainEnabled", Permission = RestockSettingPermission.NetworkAdministrator },
        new RestockSettingDefinition { Key = RestockSettingKey.AutoDepositEnabled, Name = "AutoDepositEnabled", LabelKey = "HyperdimensionalNetworkSettingAutoDepositEnabled", Permission = RestockSettingPermission.NetworkAdministrator },
        new RestockSettingDefinition { Key = RestockSettingKey.TrashEnabled, Name = "TrashEnabled", LabelKey = "HyperdimensionalNetworkSettingTrashEnabled", Permission = RestockSettingPermission.NetworkAdministrator },
        new RestockSettingDefinition { Key = RestockSettingKey.BookReadingEnabled, Name = "BookReadingEnabled", LabelKey = "HyperdimensionalNetworkSettingBookReadingEnabled", Permission = RestockSettingPermission.NetworkAdministrator },
        new RestockSettingDefinition
        {
            Key = RestockSettingKey.RetainSearchOnClose,
            Name = "RetainSearchOnClose",
            LabelKey = "HyperdimensionalNetworkSettingRetainSearch",
            Permission = RestockSettingPermission.NetworkMember
        },
        new RestockSettingDefinition
        {
            Key = RestockSettingKey.ManualSaleEnabled,
            Name = "ManualSaleEnabled",
            LabelKey = "HyperdimensionalNetworkSettingManualSale",
            Permission = RestockSettingPermission.NetworkMember
        }
    };

    public static IReadOnlyList<RestockSettingDefinition> All => Definitions;

    public static RestockSettingDefinition Get(RestockSettingKey key)
    {
        return Definitions.FirstOrDefault(definition => definition.Key == key);
    }
}

internal static class RestockSettingsService
{
    private sealed class PlayerSettings
    {
        public bool AutoRestockEnabled;
        public RestockTargetMode TargetMode = RestockTargetMode.ToolbeltAndLockedBackpackStacks;
        public bool RetainSearchOnClose = true;
        public bool ManualSaleEnabled;
    }

    private const int FileMagic = 0x31545352; // RST1
    private const int FileVersion = 4;
    private const int MaximumPlayers = 100000;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, PlayerSettings> Settings = new Dictionary<string, PlayerSettings>(StringComparer.Ordinal);
    private static string _path;
    private static bool _loaded;
    private static bool _unsafe;
    private static int _cooldownMs = -1;

    public static RestockSettingsSnapshot Get(EntityPlayer player)
    {
        return TryGet(player, out RestockSettingsSnapshot snapshot, out _) ? snapshot : Defaults();
    }

    public static bool TryGet(EntityPlayer player, out RestockSettingsSnapshot snapshot, out string error)
    {
        snapshot = null;
        error = string.Empty;
        if (!(bool)player) { error = "PlayerUnavailable"; return false; }
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(playerId)) { error = "PlayerIdentityUnavailable"; return false; }
        if (!NetworkFeatureSettingsService.TryGet(player, out NetworkFeatureSettings networkSettings, out error)) return false;
        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "SettingsUnavailable"; return false; }
            if (!Settings.TryGetValue(playerId, out PlayerSettings value)) value = new PlayerSettings();
            snapshot = Snapshot(value, PlayerNetworkDirectory.IsNetworkAdministrator(player), networkSettings);
            return true;
        }
    }

    public static bool TrySet(EntityPlayer player, RestockSettingKey key, int value, out string error, out RestockSettingsSnapshot snapshot)
    {
        error = string.Empty;
        snapshot = null;
        RestockSettingDefinition definition = RestockSettingCatalog.Get(key);
        if (definition == null) { error = "UnknownSetting"; return false; }
        if (!CanConfigure(player, definition.Permission)) { error = "PermissionDenied"; return false; }
        if (definition.Permission == RestockSettingPermission.NetworkAdministrator)
        {
            if (value != 0 && value != 1) { error = "InvalidSettingValue"; return false; }
            NetworkFeatureSetting networkKey = key switch
            {
                RestockSettingKey.ReplenishmentEnabled => NetworkFeatureSetting.ReplenishmentEnabled,
                RestockSettingKey.AmmoBankEnabled => NetworkFeatureSetting.AmmoBankEnabled,
                RestockSettingKey.TraderEnabled => NetworkFeatureSetting.TraderEnabled,
                RestockSettingKey.CraftingChainEnabled => NetworkFeatureSetting.CraftingChainEnabled,
                RestockSettingKey.AutoDepositEnabled => NetworkFeatureSetting.AutoDepositEnabled,
                RestockSettingKey.TrashEnabled => NetworkFeatureSetting.TrashEnabled,
                RestockSettingKey.BookReadingEnabled => NetworkFeatureSetting.BookReadingEnabled,
                _ => throw new InvalidOperationException("Unknown administrator setting: " + key)
            };
            if (!NetworkFeatureSettingsService.TrySet(player, networkKey, value == 1, out error)) return false;
            if (key == RestockSettingKey.TraderEnabled && value == 0)
            {
                string networkId = PlayerNetworkDirectory.GetNetworkId(player);
                TraderAutomationService.CancelNetworkRuntime(networkId, "feature-disabled");
                TraderMarketplaceService.CloseForDisabledNetwork(networkId);
            }
            return TryGet(player, out snapshot, out error);
        }
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(playerId)) { error = "PlayerIdentityUnavailable"; return false; }

        lock (Sync)
        {
            if (!EnsureLoaded()) { error = "SettingsUnavailable"; return false; }
            bool hadEntry = Settings.TryGetValue(playerId, out PlayerSettings current);
            current = current ?? new PlayerSettings();
            PlayerSettings updated = new PlayerSettings
            {
                AutoRestockEnabled = current.AutoRestockEnabled,
                TargetMode = current.TargetMode,
                RetainSearchOnClose = current.RetainSearchOnClose,
                ManualSaleEnabled = current.ManualSaleEnabled
            };
            switch (key)
            {
                case RestockSettingKey.AutoRestockEnabled:
                    if (value != 0 && value != 1) { error = "InvalidSettingValue"; return false; }
                    updated.AutoRestockEnabled = value == 1;
                    break;
                case RestockSettingKey.AutoRestockTarget:
                    if (value != (int)RestockTargetMode.ToolbeltAndLockedBackpackStacks
                        && value != (int)RestockTargetMode.LockedBackpackOnly
                        && value != (int)RestockTargetMode.ToolbeltOnly)
                    { error = "InvalidSettingValue"; return false; }
                    updated.TargetMode = (RestockTargetMode)value;
                    break;
                case RestockSettingKey.RetainSearchOnClose:
                    if (value != 0 && value != 1) { error = "InvalidSettingValue"; return false; }
                    updated.RetainSearchOnClose = value == 1;
                    break;
                case RestockSettingKey.ManualSaleEnabled:
                    if (value != 0 && value != 1) { error = "InvalidSettingValue"; return false; }
                    updated.ManualSaleEnabled = value == 1;
                    break;
            }

            Settings[playerId] = updated;
            if (!Save())
            {
                if (hadEntry) Settings[playerId] = current;
                else Settings.Remove(playerId);
                error = "SettingsSaveFailed";
                return false;
            }
            if (!NetworkFeatureSettingsService.TryGet(player, out NetworkFeatureSettings networkSettings, out error))
            {
                if (hadEntry) Settings[playerId] = current;
                else Settings.Remove(playerId);
                Save();
                return false;
            }
            snapshot = Snapshot(updated, PlayerNetworkDirectory.IsNetworkAdministrator(player), networkSettings);
            NetworkLog.Debug("Personal restock setting updated. player=" + player.entityId + "; setting=" + definition.Name + "; value=" + value);
            return true;
        }
    }

    public static bool CanConfigure(EntityPlayer player, RestockSettingPermission permission)
    {
        if (!(bool)player || string.IsNullOrEmpty(PlayerNetworkDirectory.GetNetworkId(player))) return false;
        return permission == RestockSettingPermission.NetworkMember
            || permission == RestockSettingPermission.NetworkAdministrator && PlayerNetworkDirectory.IsNetworkAdministrator(player);
    }

    public static bool GetRetainSearchOnClose(EntityPlayer player)
    {
        return TryGet(player, out RestockSettingsSnapshot snapshot, out _) ? snapshot.RetainSearchOnClose : true;
    }

    public static int AutoRestockCooldownMs
    {
        get
        {
            if (_cooldownMs >= 0) return _cooldownMs;
            int configured = 1000;
            try
            {
                string path = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "Mods", "HyperdimensionalNetwork", "HyperdimensionalNetwork.json"));
                if (File.Exists(path))
                {
                    Match match = Regex.Match(File.ReadAllText(path), "\\\"AutoRestockCooldownMs\\\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int value)) configured = Math.Max(0, Math.Min(60000, value));
                }
            }
            catch (Exception ex) { NetworkLog.Error("Could not read AutoRestockCooldownMs; using 1000 ms. " + ex); }
            _cooldownMs = configured;
            NetworkLog.Debug("Restock server settings loaded. AutoRestockCooldownMs=" + _cooldownMs);
            return _cooldownMs;
        }
    }

    private static RestockSettingsSnapshot Defaults()
    {
        return new RestockSettingsSnapshot { TargetMode = RestockTargetMode.ToolbeltAndLockedBackpackStacks, RetainSearchOnClose = true, AutoRestockCooldownMs = AutoRestockCooldownMs };
    }

    private static RestockSettingsSnapshot Snapshot(PlayerSettings value, bool isAdmin, NetworkFeatureSettings networkSettings)
    {
        return new RestockSettingsSnapshot
        {
            AutoRestockEnabled = value.AutoRestockEnabled,
            TargetMode = value.TargetMode,
            IsNetworkAdministrator = isAdmin,
            ReplenishmentEnabled = networkSettings?.ReplenishmentEnabled ?? true,
            AmmoBankEnabled = networkSettings?.AmmoBankEnabled ?? true,
            TraderEnabled = networkSettings?.TraderEnabled ?? true,
            CraftingChainEnabled = networkSettings?.CraftingChainEnabled ?? true,
            AutoDepositEnabled = networkSettings?.AutoDepositEnabled ?? true,
            TrashEnabled = networkSettings?.TrashEnabled ?? true,
            BookReadingEnabled = networkSettings?.BookReadingEnabled ?? true,
            RetainSearchOnClose = value.RetainSearchOnClose,
            ManualSaleEnabled = value.ManualSaleEnabled,
            AutoRestockCooldownMs = AutoRestockCooldownMs
        };
    }

    private static bool EnsureLoaded()
    {
        if (!NetworkSaveDataDirectory.TryGetDirectory(out string modDirectory)) return false;
        string path = Path.Combine(modDirectory, "restock-settings.bin");
        if (_loaded && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return !_unsafe;

        Settings.Clear();
        _path = path;
        _loaded = true;
        _unsafe = false;
        if (!File.Exists(path)) return true;
        bool migratedSettings = false;
        try
        {
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != FileMagic) throw new InvalidDataException("Unrecognized restock settings file magic.");
                int version = reader.ReadInt32();
                if (version < 1 || version > FileVersion) throw new InvalidDataException("Unsupported restock settings file version: " + version);
                migratedSettings = version != FileVersion;
                int count = reader.ReadInt32();
                if (count < 0 || count > MaximumPlayers) throw new InvalidDataException("Invalid restock settings player count: " + count);
                for (int i = 0; i < count; i++)
                {
                    string playerId = reader.ReadString();
                    bool enabled = reader.ReadBoolean();
                    byte storedMode = reader.ReadByte();
                    RestockTargetMode mode;
                    if (version == 1)
                    {
                        // Old mode 0 was hotbar + locked slots; old mode 1 included every backpack slot.
                        // Both migrate to the closest safe supported range, hotbar + locked slots.
                        if (storedMode == 0 || storedMode == 1) mode = RestockTargetMode.ToolbeltAndLockedBackpackStacks;
                        else if (storedMode == 2) mode = RestockTargetMode.ToolbeltOnly;
                        else throw new InvalidDataException("Invalid legacy restock target mode: " + storedMode);
                    }
                    else
                    {
                        mode = (RestockTargetMode)storedMode;
                    }
                    if (string.IsNullOrWhiteSpace(playerId) || mode > RestockTargetMode.ToolbeltOnly || Settings.ContainsKey(playerId))
                        throw new InvalidDataException("Invalid or duplicate restock settings record.");
                    bool retainSearch = version < 3 || reader.ReadBoolean();
                    bool manualSaleEnabled = version >= 4 && reader.ReadBoolean();
                    Settings.Add(playerId, new PlayerSettings { AutoRestockEnabled = enabled, TargetMode = mode, RetainSearchOnClose = retainSearch, ManualSaleEnabled = manualSaleEnabled });
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing restock settings data.");
            }
            if (migratedSettings)
            {
                if (Save()) NetworkLog.Debug("Personal restock settings migrated to the three supported target ranges. path=" + _path);
            }
            NetworkLog.Debug("Personal restock settings loaded. players=" + Settings.Count + "; path=" + _path);
            return true;
        }
        catch (Exception ex)
        {
            _unsafe = true;
            NetworkLog.Error("Personal restock settings could not be read; updates are disabled to protect existing settings. path=" + path + "; exception=" + ex);
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
                foreach (KeyValuePair<string, PlayerSettings> entry in Settings)
                {
                    writer.Write(entry.Key);
                    writer.Write(entry.Value.AutoRestockEnabled);
                    writer.Write((byte)entry.Value.TargetMode);
                    writer.Write(entry.Value.RetainSearchOnClose);
                    writer.Write(entry.Value.ManualSaleEnabled);
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
            NetworkLog.Error("Personal restock settings could not be saved. path=" + _path + "; exception=" + ex);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            return false;
        }
    }
}
