using System;
using System.IO;
using System.Text.RegularExpressions;
using HyperdimensionalNetwork.Patches;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class NetworkHotkeyService
{
    private static bool _loaded;
    private static KeyCode _openNetworkKey = KeyCode.O;
    private static KeyCode _depositContainerKey = KeyCode.D;

    public static void Update(LocalPlayerUI playerUI)
    {
        if (!_loaded) LoadConfig();
        if (!(bool)playerUI || playerUI.xui == null || playerUI.windowManager == null
            || playerUI.windowManager.IsInputActive()) return;

        if (_openNetworkKey != KeyCode.None && Input.GetKeyDown(_openNetworkKey)) ToggleNetwork(playerUI);
        if (_depositContainerKey != KeyCode.None && Input.GetKeyDown(_depositContainerKey)) DepositOpenContainer(playerUI);
    }

    private static void LoadConfig()
    {
        _loaded = true;
        try
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Mods", "HyperdimensionalNetwork", "HyperdimensionalNetwork.json"));
            if (!File.Exists(path))
            {
                NetworkLog.Debug("Hotkey config missing; defaults are used. open=O; deposit=D; path=" + path);
                return;
            }

            string json = File.ReadAllText(path);
            _openNetworkKey = ReadKey(json, "OpenNetworkKey", KeyCode.O, path);
            _depositContainerKey = ReadKey(json, "DepositContainerKey", KeyCode.D, path);
            NetworkLog.Debug("Hotkeys loaded. open=" + _openNetworkKey + "; depositContainer=" + _depositContainerKey + "; path=" + path);
        }
        catch (Exception ex)
        {
            _openNetworkKey = KeyCode.O;
            _depositContainerKey = KeyCode.D;
            NetworkLog.Error("Could not load hotkey config; defaults are used. " + ex);
        }
    }

    private static KeyCode ReadKey(string json, string field, KeyCode fallback, string path)
    {
        Match match = Regex.Match(json, "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"", RegexOptions.IgnoreCase);
        if (match.Success && Enum.TryParse(match.Groups[1].Value.Trim(), true, out KeyCode parsed)) return parsed;
        if (match.Success)
            NetworkLog.Error("Invalid hotkey value; using default. field=" + field + "; value=" + match.Groups[1].Value + "; default=" + fallback + "; path=" + path);
        return fallback;
    }

    private static void ToggleNetwork(LocalPlayerUI playerUI)
    {
        GUIWindowManager manager = playerUI.windowManager;
        string[] subWindows = { "hyperdimensionalNetworkManagement", "hyperdimensionalTraderMarket", "hyperdimensionalTraderRules", "hyperdimensionalCraftingChain" };
        bool wasOpen = manager.IsWindowOpen("hyperdimensionalNetwork");
        foreach (string subWindow in subWindows)
            if (manager.IsWindowOpen(subWindow)) manager.Close(subWindow);

        if (wasOpen)
        {
            manager.Close("hyperdimensionalNetwork");
            NetworkLog.Debug("Network hotkey closed the network window. player=" + playerUI.entityPlayer.entityId);
            return;
        }

        manager.Open("hyperdimensionalNetwork", _bModal: true);
        NetworkLog.Debug("Network hotkey opened the network window. player=" + playerUI.entityPlayer.entityId
            + "; open=" + manager.IsWindowOpen("hyperdimensionalNetwork"));
    }

    private static void DepositOpenContainer(LocalPlayerUI playerUI)
    {
        GUIWindowManager manager = playerUI.windowManager;
        string lootWindowId = XUiC_LootWindowGroup.ID;
        if (!manager.IsWindowOpen(lootWindowId)) return;

        XUiC_LootWindow window = playerUI.xui.FindWindowGroupByName(lootWindowId)?.GetChildByType<XUiC_LootWindow>();
        bool started = ContainerDepositPatch.DepositLootFromWindow(window);
        NetworkLog.Debug("Container deposit hotkey processed. player=" + playerUI.entityPlayer.entityId + "; started=" + started);
    }
}
