using System;
using System.Collections.Generic;
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

    internal static string GetDepositKeyDisplay()
    {
        if (!_loaded) LoadConfig();
        if (_depositContainerKey == KeyCode.None) return Localization.Get("HyperdimensionalNetworkHotkeyUnbound");

        string keyName = _depositContainerKey.ToString();
        if (keyName.StartsWith("Alpha", StringComparison.Ordinal)) keyName = keyName.Substring("Alpha".Length);
        else if (keyName.StartsWith("Keypad", StringComparison.Ordinal)) keyName = "Num " + keyName.Substring("Keypad".Length);
        else
        {
            keyName = keyName switch
            {
                "Return" => "Enter",
                "LeftControl" => "Left Ctrl",
                "RightControl" => "Right Ctrl",
                "LeftCommand" => "Left Cmd",
                "RightCommand" => "Right Cmd",
                _ => keyName
            };
        }
        return "<" + keyName + ">";
    }

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
        List<XUiWindowGroup> groups = playerUI.xui.WindowGroups;
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            XUiWindowGroup group = groups[i];
            if (group == null || !group.isShowing || group.Controller == null
                || string.Equals(group.Id, "backpack", StringComparison.OrdinalIgnoreCase)
                || group.Id.StartsWith("hyperdimensional", StringComparison.OrdinalIgnoreCase)) continue;

            XUiC_ContainerStandardControls controls = group.Controller.GetChildByType<XUiC_ContainerStandardControls>();
            if (controls == null) continue;
            bool started = ContainerDepositPatch.DepositFromControls(controls);
            NetworkLog.Debug("Open container deposit hotkey processed. player=" + playerUI.entityPlayer.entityId
                + "; group=" + group.Id + "; started=" + started);
            return;
        }
        NetworkLog.Debug("Open container deposit hotkey found no active container controls. player=" + playerUI.entityPlayer.entityId);
    }
}
