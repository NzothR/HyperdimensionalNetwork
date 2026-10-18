using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class NetworkLog
{
    private static bool _initialized;
    private static bool _debugEnabled;

    public static void Info(string message) => Log.Out("[HyperdimensionalNetwork] " + message);

    public static void Debug(string message)
    {
        EnsureInitialized();
        if (_debugEnabled) Log.Out("[HyperdimensionalNetwork][Debug] " + message);
    }

    public static void Error(string message) => Log.Error("[HyperdimensionalNetwork][Error] " + message);

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Mods", "HyperdimensionalNetwork", "HyperdimensionalNetwork.json"));
            if (!File.Exists(path))
            {
                _debugEnabled = false;
                Info("Config missing; DebugLog defaults to false. Path=" + path);
                return;
            }
            Match match = Regex.Match(File.ReadAllText(path), "\\\"DebugLog\\\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            _debugEnabled = match.Success && string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            Info("Config loaded. DebugLog=" + _debugEnabled + "; path=" + path);
        }
        catch (Exception ex)
        {
            _debugEnabled = false;
            Error("Could not read config; DebugLog defaults to false. " + ex);
        }
    }
}
