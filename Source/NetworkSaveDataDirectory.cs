using System;
using System.IO;

namespace HyperdimensionalNetwork;

/// <summary>
/// Gives mod data a save incarnation identity in addition to the user-visible save path.
/// 7 Days to Die derives that path from world and game names, which can be reused after a save is deleted.
/// </summary>
internal static class NetworkSaveDataDirectory
{
    private const string ModDirectoryName = "HyperdimensionalNetwork";
    private const string IdentityFileName = "world-identity.txt";
    private static readonly object Sync = new object();
    private static string _savePath;
    private static string _worldIdentity;
    private static string _directory;
    private static bool _available;

    public static bool TryGetDirectory(out string directory)
    {
        directory = null;
        string savePath;
        string worldIdentity;
        try
        {
            savePath = GameIO.GetSaveGameDir();
            worldIdentity = GameManager.Instance?.World?.Guid;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not resolve the current world identity for network data. " + ex);
            return false;
        }

        if (string.IsNullOrWhiteSpace(savePath) || string.IsNullOrWhiteSpace(worldIdentity))
        {
            NetworkLog.Debug("Network data is unavailable until the active save and world identity are ready.");
            return false;
        }
        if (!Guid.TryParse(worldIdentity, out Guid parsedIdentity))
        {
            NetworkLog.Error("The active world has an invalid identity; network access is disabled. identity=" + worldIdentity);
            return false;
        }

        string normalizedIdentity = parsedIdentity.ToString("N");
        lock (Sync)
        {
            if (string.Equals(savePath, _savePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(normalizedIdentity, _worldIdentity, StringComparison.Ordinal))
            {
                directory = _available ? _directory : null;
                return _available;
            }

            try
            {
                string fullSavePath = Path.GetFullPath(savePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string modRoot = Path.GetFullPath(Path.Combine(fullSavePath, ModDirectoryName));
                if (!string.Equals(Path.GetDirectoryName(modRoot), fullSavePath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFileName(modRoot), ModDirectoryName, StringComparison.Ordinal))
                    throw new InvalidDataException("Resolved mod data directory is outside the active save directory.");

                string identityPath = Path.Combine(modRoot, IdentityFileName);
                if (File.Exists(identityPath))
                {
                    string previousIdentity = File.ReadAllText(identityPath).Trim();
                    if (!Guid.TryParse(previousIdentity, out Guid parsedPrevious))
                        throw new InvalidDataException("Stored world identity is invalid; existing network data was left untouched.");
                    if (!string.Equals(parsedPrevious.ToString("N"), normalizedIdentity, StringComparison.Ordinal))
                    {
                        // The vanilla save directory can be reused after deleting and recreating a save.
                        // Clear only this mod's private directory before initializing the new world.
                        Directory.Delete(modRoot, recursive: true);
                        NetworkLog.Debug("Discarded network data belonging to a deleted save. previousWorld="
                            + previousIdentity + "; currentWorld=" + normalizedIdentity);
                    }
                }

                Directory.CreateDirectory(modRoot);
                string worldDirectory = Path.Combine(modRoot, normalizedIdentity);
                Directory.CreateDirectory(worldDirectory);

                // Pre-identity releases stored files directly under HyperdimensionalNetwork. Adopt them
                // once for the currently loaded save so existing inventories and settings survive upgrade.
                if (!File.Exists(identityPath))
                {
                    foreach (string legacyFile in Directory.GetFiles(modRoot))
                    {
                        if (string.Equals(legacyFile, identityPath, StringComparison.OrdinalIgnoreCase)) continue;
                        string destination = Path.Combine(worldDirectory, Path.GetFileName(legacyFile));
                        if (File.Exists(destination))
                            throw new IOException("Both legacy and world-scoped mod data exist: " + Path.GetFileName(legacyFile));
                        File.Move(legacyFile, destination);
                    }

                    string temporaryIdentityPath = identityPath + ".tmp";
                    File.WriteAllText(temporaryIdentityPath, normalizedIdentity);
                    if (File.Exists(identityPath)) File.Delete(identityPath);
                    File.Move(temporaryIdentityPath, identityPath);
                    NetworkLog.Debug("Save identity initialized for network data. world=" + normalizedIdentity + "; path=" + worldDirectory);
                }

                _savePath = savePath;
                _worldIdentity = normalizedIdentity;
                _directory = worldDirectory;
                _available = true;
                directory = worldDirectory;
                return true;
            }
            catch (Exception ex)
            {
                _savePath = savePath;
                _worldIdentity = normalizedIdentity;
                _directory = null;
                _available = false;
                NetworkLog.Error("Could not initialize save-scoped network data; access is disabled to protect player data. save="
                    + savePath + "; world=" + normalizedIdentity + "; exception=" + ex);
                return false;
            }
        }
    }
}
