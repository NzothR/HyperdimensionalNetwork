using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace HyperdimensionalNetwork;

internal sealed class StoredEntry
{
    public ItemValue Value;
    public long Count;
}

internal sealed class StorageAddition
{
    public ItemValue Value;
    public long Count;
}

internal static class NetworkStorage
{
    private const int FileMagic = 0x314E5748; // HWN1
    private const int FileVersion = 1;
    private static readonly Dictionary<string, Dictionary<ItemValue, long>> Storages = new Dictionary<string, Dictionary<ItemValue, long>>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> Revisions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnsafePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> ValidLengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private const long MaxStoredCount = long.MaxValue;
    private const int MaxTransactionBytes = 64 * 1024 * 1024;

    public static long GetRevision(EntityPlayer player)
    {
        if (!(bool)player) return 0;
        return Revisions.TryGetValue(GetPath(player), out long revision) ? revision : 0;
    }

    public static List<StoredEntry> GetEntries(EntityPlayer player)
    {
        Dictionary<ItemValue, long> data = GetStorage(player);
        return data.Where(pair => pair.Key != null && pair.Value > 0)
            .Select(pair => new StoredEntry { Value = pair.Key.Clone(), Count = pair.Value })
            .OrderBy(entry => entry.Value.type)
            .ThenBy(entry => entry.Value.Quality)
            .ToList();
    }

    public static bool AddStack(EntityPlayer player, ItemStack stack)
    {
        if (stack == null || stack.IsEmpty()) return false;
        return AddBatch(player, new[] { new StorageAddition { Value = stack.itemValue.Clone(), Count = stack.count } });
    }

    public static bool AddBatch(EntityPlayer player, IEnumerable<StorageAddition> additions)
    {
        if (!(bool)player || additions == null) return false;
        string path = GetPath(player);
        if (UnsafePaths.Contains(path)) return false;

        Dictionary<ItemValue, long> deltas = new Dictionary<ItemValue, long>(ItemValueComparer.Instance);
        foreach (StorageAddition addition in additions)
        {
            if (addition?.Value == null || addition.Value.type == 0 || addition.Count <= 0) continue;
            ItemValue key = addition.Value.Clone();
            deltas[key] = SafeAdd(deltas.TryGetValue(key, out long oldDelta) ? oldDelta : 0, addition.Count);
        }
        if (deltas.Count == 0) return false;

        Dictionary<ItemValue, long> current = GetStorage(player);
        foreach (KeyValuePair<ItemValue, long> delta in deltas)
        {
            long old = current.TryGetValue(delta.Key, out long count) ? count : 0;
            if (old > MaxStoredCount - delta.Value)
            {
                NetworkLog.Error("Add refused: long count overflow. player=" + player.PlayerDisplayName + "; item=" + delta.Key.type + "; current=" + old + "; delta=" + delta.Value);
                return false;
            }
        }
        if (!AppendTransaction(path, deltas)) return false;
        Apply(current, deltas);
        BumpRevision(path);
        return true;
    }

    public static int Remove(EntityPlayer player, ItemValue value, int requested)
    {
        if (!(bool)player || value == null || requested <= 0) return 0;
        string path = GetPath(player);
        if (UnsafePaths.Contains(path)) return 0;
        Dictionary<ItemValue, long> current = GetStorage(player);
        ItemValue lookup = value.Clone();
        if (!current.TryGetValue(lookup, out long available) || available <= 0) return 0;
        int amount = (int)Math.Min(Math.Min((long)requested, available), int.MaxValue);
        Dictionary<ItemValue, long> deltas = new Dictionary<ItemValue, long>(ItemValueComparer.Instance) { [lookup] = -amount };
        if (!AppendTransaction(path, deltas)) return 0;
        Apply(current, deltas);
        BumpRevision(path);
        return amount;
    }

    private static Dictionary<ItemValue, long> GetStorage(EntityPlayer player)
    {
        string path = GetPath(player);
        if (Storages.TryGetValue(path, out Dictionary<ItemValue, long> data)) return data;
        data = Load(path);
        Storages[path] = data;
        return data;
    }

    private static Dictionary<ItemValue, long> Load(string path)
    {
        Dictionary<ItemValue, long> result = new Dictionary<ItemValue, long>(ItemValueComparer.Instance);
        if (!File.Exists(path)) { ValidLengths[path] = 0; return result; }
        Stopwatch timer = Stopwatch.StartNew();
        int transactions = 0;
        long entriesSeen = 0;
        try
        {
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
            {
                if (reader.ReadInt32() != FileMagic || reader.ReadInt32() != FileVersion) throw new InvalidDataException("Unrecognized HyperdimensionalNetwork journal header.");
                long validLength = stream.Position;
                while (stream.Position + sizeof(int) <= stream.Length)
                {
                    int length = reader.ReadInt32();
                    if (length < sizeof(int) || length > MaxTransactionBytes || stream.Position + length + sizeof(uint) > stream.Length)
                    {
                        NetworkLog.Error("Journal tail is incomplete or invalid; replay stopped safely. path=" + path + "; offset=" + stream.Position + "; length=" + length + "; fileLength=" + stream.Length);
                        break;
                    }
                    byte[] payload = reader.ReadBytes(length);
                    uint expected = reader.ReadUInt32();
                    if (Hash(payload) != expected)
                    {
                        NetworkLog.Error("Journal checksum mismatch; replay stopped safely. path=" + path + "; transaction=" + transactions);
                        break;
                    }
                    ApplyPayload(result, payload);
                    transactions++;
                    entriesSeen += BitConverter.ToInt32(payload, 0);
                    validLength = stream.Position;
                }
                ValidLengths[path] = validLength;
            }
            Revisions[path] = transactions;
            NetworkLog.Debug("Journal loaded. transactions=" + transactions + "; itemDeltas=" + entriesSeen + "; distinctVariants=" + result.Count + "; elapsedMs=" + timer.Elapsed.TotalMilliseconds.ToString("F2") + "; path=" + path);
        }
        catch (Exception ex)
        {
            UnsafePaths.Add(path);
            NetworkLog.Error("Storage journal could not be read; writes blocked to protect data. path=" + path + "; exception=" + ex);
        }
        return result;
    }

    private static bool AppendTransaction(string path, Dictionary<ItemValue, long> deltas)
    {
        Stopwatch timer = Stopwatch.StartNew();
        string directory = Path.GetDirectoryName(path);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        try
        {
            byte[] payload = SerializeDeltas(deltas);
            if (payload.Length > MaxTransactionBytes) throw new InvalidDataException("Transaction payload exceeds configured maximum.");
            using (FileStream stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                long validLength = ValidLengths.TryGetValue(path, out long knownLength) ? knownLength : 0;
                stream.SetLength(validLength);
                stream.Position = validLength;
                if (validLength == 0) { writer.Write(FileMagic); writer.Write(FileVersion); }
                writer.Write(payload.Length);
                writer.Write(payload);
                writer.Write(Hash(payload));
                writer.Flush();
                stream.Flush(true);
                ValidLengths[path] = stream.Position;
            }
            NetworkLog.Debug("Journal transaction committed. variants=" + deltas.Count + "; payloadBytes=" + payload.Length + "; elapsedMs=" + timer.Elapsed.TotalMilliseconds.ToString("F2"));
            return true;
        }
        catch (Exception ex)
        {
            UnsafePaths.Add(path);
            NetworkLog.Error("Journal transaction failed. path=" + path + "; variants=" + deltas.Count + "; exception=" + ex);
            return false;
        }
    }

    private static byte[] SerializeDeltas(Dictionary<ItemValue, long> deltas)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            using (PooledBinaryWriter writer = MemoryPools.poolBinaryWriter.AllocSync(false))
            {
                writer.SetBaseStream(stream);
                ((BinaryWriter)writer).Write(deltas.Count);
                foreach (KeyValuePair<ItemValue, long> delta in deltas)
                {
                    delta.Key.Write(writer);
                    ((BinaryWriter)writer).Write(delta.Value);
                }
                writer.Flush();
            }
            return stream.ToArray();
        }
    }

    private static void ApplyPayload(Dictionary<ItemValue, long> target, byte[] payload)
    {
        using (MemoryStream stream = new MemoryStream(payload, writable: false))
        using (PooledBinaryReader reader = MemoryPools.poolBinaryReader.AllocSync(false))
        {
            reader.SetBaseStream(stream);
            int count = reader.ReadInt32();
            if (count < 0 || count > 100000) throw new InvalidDataException("Invalid journal delta count: " + count);
            for (int i = 0; i < count; i++)
            {
                ItemValue value = new ItemValue();
                value.Read(reader);
                long delta = reader.ReadInt64();
                ApplyOne(target, value, delta);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Journal payload contains unexpected trailing bytes.");
        }
    }

    private static void Apply(Dictionary<ItemValue, long> target, Dictionary<ItemValue, long> deltas)
    {
        foreach (KeyValuePair<ItemValue, long> delta in deltas)
            ApplyOne(target, delta.Key, delta.Value);
    }

    private static void ApplyOne(Dictionary<ItemValue, long> target, ItemValue key, long delta)
    {
        long current = target.TryGetValue(key, out long old) ? old : 0;
        long next = delta >= 0 ? SafeAdd(current, delta) : Math.Max(0, current + delta);
        if (next == 0) target.Remove(key);
        else if (target.ContainsKey(key)) target[key] = next;
        else target.Add(key.Clone(), next);
    }

    private static long SafeAdd(long left, long right) => right > long.MaxValue - left ? long.MaxValue : left + right;

    private static uint Hash(byte[] bytes)
    {
        uint hash = 2166136261;
        for (int i = 0; i < bytes.Length; i++) hash = (hash ^ bytes[i]) * 16777619;
        return hash;
    }

    private static void BumpRevision(string path) => Revisions[path] = Revisions.TryGetValue(path, out long revision) ? revision + 1 : 1;

    private static string GetPath(EntityPlayer player)
    {
        string owner = player.entityId.ToString();
        try
        {
            PersistentPlayerData data = GameManager.Instance?.persistentPlayers?.GetPlayerDataFromEntityID(player.entityId);
            if (!string.IsNullOrEmpty(data?.PrimaryId?.CombinedString)) owner = data.PrimaryId.CombinedString;
        }
        catch (Exception ex) { NetworkLog.Debug("Persistent player ID lookup fell back to entity ID. " + ex.Message); }
        foreach (char invalid in Path.GetInvalidFileNameChars()) owner = owner.Replace(invalid, '_');
        string save = GameIO.GetSaveGameDir();
        return Path.Combine(save, "HyperdimensionalNetwork", owner + ".hwn");
    }

    public static void FlushAll() => NetworkLog.Debug("Shutdown flush requested; journal writes are synchronous and already durable.");

    private sealed class ItemValueComparer : IEqualityComparer<ItemValue>
    {
        public static readonly ItemValueComparer Instance = new ItemValueComparer();
        public bool Equals(ItemValue x, ItemValue y) => ReferenceEquals(x, y) || (x != null && x.Equals(y));
        public int GetHashCode(ItemValue obj)
        {
            if (obj == null) return 0;
            unchecked
            {
                int hash = obj.type;
                hash = hash * 397 ^ obj.Quality;
                hash = hash * 397 ^ obj.Seed;
                hash = hash * 397 ^ obj.UseTimes.GetHashCode();
                hash = hash * 397 ^ obj.Meta;
                hash = hash * 397 ^ obj.SelectedAmmoTypeIndex;
                hash = hash * 397 ^ (obj.Activated ? 1 : 0);
                return hash;
            }
        }
    }
}
