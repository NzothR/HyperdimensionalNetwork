using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HyperdimensionalNetwork;

internal sealed class CraftingChainViewState
{
    public string RootRecipeKey = string.Empty;
    public float PanX;
    public float PanY;
    public float Zoom = 1f;
    public Dictionary<string, string> RecipeChoices = new Dictionary<string, string>(StringComparer.Ordinal);
}

internal static class CraftingChainCacheService
{
    private const int FileMagic = 0x31434348; // HCC1
    private const int FileVersion = 1;
    private const int MaximumRecipeChoices = 512;
    private static readonly object Sync = new object();

    public static CraftingChainViewState Load(EntityPlayer player)
    {
        CraftingChainViewState state = new CraftingChainViewState();
        if (!(bool)player) return state;
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(playerId) || !TryGetPath(playerId, out string path) || !File.Exists(path)) return state;
        lock (Sync)
        {
            try
            {
                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadInt32() != FileMagic || reader.ReadInt32() != FileVersion)
                        throw new InvalidDataException("Unsupported crafting-chain cache header.");
                    state.RootRecipeKey = reader.ReadString();
                    state.PanX = ClampPan(reader.ReadSingle());
                    state.PanY = ClampPan(reader.ReadSingle());
                    state.Zoom = Math.Max(0.55f, Math.Min(1.8f, reader.ReadSingle()));
                    int count = reader.ReadInt32();
                    if (count < 0 || count > MaximumRecipeChoices) throw new InvalidDataException("Invalid crafting-chain choice count.");
                    for (int index = 0; index < count; index++)
                    {
                        string itemKey = reader.ReadString();
                        string recipeKey = reader.ReadString();
                        if (!string.IsNullOrEmpty(itemKey) && !string.IsNullOrEmpty(recipeKey)) state.RecipeChoices[itemKey] = recipeKey;
                    }
                }
                NetworkLog.Debug("Crafting-chain personal view cache loaded. choices=" + state.RecipeChoices.Count + "; zoom=" + state.Zoom.ToString("F2"));
            }
            catch (Exception ex)
            {
                NetworkLog.Error("Crafting-chain personal view cache could not be read; defaults will be used. path=" + path + "; exception=" + ex);
                return new CraftingChainViewState();
            }
        }
        return state;
    }

    public static bool Save(EntityPlayer player, CraftingChainViewState state)
    {
        if (!(bool)player || state == null) return false;
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(playerId) || !TryGetPath(playerId, out string path)) return false;
        lock (Sync)
        {
            string temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                KeyValuePair<string, string>[] choices = state.RecipeChoices
                    .Where(entry => !string.IsNullOrEmpty(entry.Key) && !string.IsNullOrEmpty(entry.Value))
                    .Take(MaximumRecipeChoices).ToArray();
                using (FileStream stream = File.Open(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(FileMagic);
                    writer.Write(FileVersion);
                    writer.Write(state.RootRecipeKey ?? string.Empty);
                    writer.Write(ClampPan(state.PanX));
                    writer.Write(ClampPan(state.PanY));
                    writer.Write(Math.Max(0.55f, Math.Min(1.8f, state.Zoom)));
                    writer.Write(choices.Length);
                    foreach (KeyValuePair<string, string> choice in choices)
                    {
                        writer.Write(choice.Key);
                        writer.Write(choice.Value);
                    }
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                NetworkLog.Debug("Crafting-chain personal view cache saved. choices=" + choices.Length + "; zoom=" + state.Zoom.ToString("F2"));
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                NetworkLog.Error("Crafting-chain personal view cache could not be saved. path=" + path + "; exception=" + ex);
                return false;
            }
        }
    }

    public static string GetStableItemKey(CraftingChainItemInfo item)
        => item == null ? string.Empty : item.Type + ":" + item.Quality;

    private static bool TryGetPath(string playerId, out string path)
    {
        path = null;
        if (!NetworkSaveDataDirectory.TryGetDirectory(out string directory)) return false;
        byte[] bytes = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(playerId));
        string token = BitConverter.ToString(bytes, 0, 12).Replace("-", string.Empty);
        path = Path.Combine(directory, "crafting-chain-" + token + ".bin");
        return true;
    }

    private static float ClampPan(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
        return Math.Max(-4000f, Math.Min(4000f, value));
    }
}
