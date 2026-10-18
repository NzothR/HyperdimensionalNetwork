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

internal sealed class NetworkStoragePage
{
    public string NetworkId;
    public long Revision;
    public int TotalEntries;
    public int Page;
    public int PageSize;
    public List<StoredEntry> Entries = new List<StoredEntry>();
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
    private static readonly Dictionary<string, Dictionary<int, HashSet<ItemValue>>> TypeIndexes = new Dictionary<string, Dictionary<int, HashSet<ItemValue>>>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<ItemValue>> SortedIndexes = new Dictionary<string, List<ItemValue>>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> Revisions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnsafePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> ValidLengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private const long MaxStoredCount = long.MaxValue;
    private const int MaxTransactionBytes = 64 * 1024 * 1024;
    private static readonly object Sync = new object();

    public static long GetRevision(EntityPlayer player)
    {
        if (!(bool)player || PlayerNetworkDirectory.IsRemoteClient()) return 0;
        lock (Sync)
        {
            string path = GetPath(player);
            if (path == null) return 0;
            return Revisions.TryGetValue(path, out long revision) ? revision : 0;
        }
    }

    public static List<StoredEntry> GetEntries(EntityPlayer player)
    {
        if (!(bool)player || PlayerNetworkDirectory.IsRemoteClient()) return new List<StoredEntry>();
        lock (Sync)
        {
            string path = GetPath(player);
            if (path == null) return new List<StoredEntry>();
            Dictionary<ItemValue, long> data = GetStorage(player);
            List<StoredEntry> entries = new List<StoredEntry>();
            foreach (ItemValue key in SortedIndexes[path])
                if (data.TryGetValue(key, out long count) && count > 0)
                    entries.Add(new StoredEntry { Value = key.Clone(), Count = count });
            return entries;
        }
    }

    public static NetworkStoragePage GetPage(EntityPlayer player, string category, string search, int page, int pageSize)
    {
        NetworkStoragePage result = new NetworkStoragePage { PageSize = Math.Max(1, Math.Min(128, pageSize)), Page = Math.Max(0, page) };
        if (!(bool)player || PlayerNetworkDirectory.IsRemoteClient()) return result;
        lock (Sync)
        {
            string path = GetPath(player);
            if (path == null) return result;
            Dictionary<ItemValue, long> data = GetStorage(player);
            List<ItemValue> sorted = SortedIndexes[path];
            string normalizedCategory = category ?? string.Empty;
            string normalizedSearch = (search ?? string.Empty).Trim();
            int total = 0;
            foreach (ItemValue key in sorted)
                if (data.TryGetValue(key, out long count) && count > 0 && MatchesFilters(key, normalizedCategory, normalizedSearch)) total++;

            result.TotalEntries = total;
            result.NetworkId = Path.GetFileNameWithoutExtension(path);
            result.Revision = Revisions.TryGetValue(path, out long revision) ? revision : 0;
            int pageCount = (int)Math.Max(1L, ((long)total + result.PageSize - 1) / result.PageSize);
            result.Page = Math.Min(result.Page, pageCount - 1);
            long first = (long)result.Page * result.PageSize;
            int matched = 0;
            foreach (ItemValue key in sorted)
            {
                if (!data.TryGetValue(key, out long count) || count <= 0 || !MatchesFilters(key, normalizedCategory, normalizedSearch)) continue;
                if (matched >= first && result.Entries.Count < result.PageSize)
                    result.Entries.Add(new StoredEntry { Value = key.Clone(), Count = count });
                matched++;
                if (result.Entries.Count >= result.PageSize) break;
            }
            NetworkLog.Debug("Network page queried. player=" + player.entityId + "; network=" + result.NetworkId + "; category=" + normalizedCategory + "; searchLength=" + normalizedSearch.Length + "; page=" + result.Page + "; pageSize=" + result.PageSize + "; matches=" + result.TotalEntries + "; revision=" + result.Revision);
            return result;
        }
    }

    public static long GetCount(EntityPlayer player, ItemValue value)
    {
        if (!(bool)player || value == null || PlayerNetworkDirectory.IsRemoteClient()) return 0;
        lock (Sync)
        {
            string path = GetPath(player);
            if (path == null) return 0;
            Dictionary<ItemValue, long> data = GetStorage(player);
            return data.TryGetValue(NormalizeKey(value), out long count) ? count : 0;
        }
    }

    public static long GetMatchingCount(EntityPlayer player, ItemValue requirement, bool includeModded = false)
    {
        if (!(bool)player || requirement == null || PlayerNetworkDirectory.IsRemoteClient()) return 0;
        lock (Sync)
        {
            string path = GetPath(player);
            if (path == null) return 0;
            Dictionary<ItemValue, long> data = GetStorage(player);
            if (UnsafePaths.Contains(path)) return 0;
            if (!TypeIndexes[path].TryGetValue(requirement.type, out HashSet<ItemValue> keys)) return 0;
            long total = 0;
            foreach (ItemValue key in keys)
            {
                if (!NetworkItemMatcher.Matches(key, requirement, includeModded)) continue;
                if (data.TryGetValue(key, out long count)) total = SafeAdd(total, count);
            }
            return total;
        }
    }

    public static List<StoredEntry> GetEntriesForRequirements(EntityPlayer player, IEnumerable<ItemStack> requirements)
    {
        List<StoredEntry> result = new List<StoredEntry>();
        if (!(bool)player || requirements == null || PlayerNetworkDirectory.IsRemoteClient()) return result;
        lock (Sync)
        {
            string path = GetPath(player);
            if (path == null) return result;
            Dictionary<ItemValue, long> data = GetStorage(player);
            if (UnsafePaths.Contains(path)) return result;
            HashSet<int> types = new HashSet<int>();
            foreach (ItemStack requirement in requirements)
                if (requirement?.itemValue != null && requirement.itemValue.type > 0) types.Add(requirement.itemValue.type);
            foreach (ItemValue key in SortedIndexes[path])
            {
                if (!types.Contains(key.type)) continue;
                if (data.TryGetValue(key, out long count) && count > 0) result.Add(new StoredEntry { Value = key.Clone(), Count = count });
            }
            return result;
        }
    }

    public static bool RemoveBatch(EntityPlayer player, IEnumerable<StorageAddition> removals)
    {
        if (!(bool)player || removals == null || PlayerNetworkDirectory.IsRemoteClient()) return false;
        lock (Sync)
        {
            string path = GetPath(player);
            if (path == null || UnsafePaths.Contains(path)) return false;
            Dictionary<ItemValue, long> current = GetStorage(player);
            if (UnsafePaths.Contains(path)) return false;
            Dictionary<ItemValue, long> deltas = new Dictionary<ItemValue, long>(ItemValueComparer.Instance);
            foreach (StorageAddition removal in removals)
            {
                if (removal?.Value == null || removal.Count <= 0) return false;
                ItemValue key = NormalizeKey(removal.Value);
                long old = deltas.TryGetValue(key, out long count) ? count : 0;
                if (old > long.MaxValue - removal.Count) return false;
                deltas[key] = old + removal.Count;
            }
            if (deltas.Count == 0) return true;
            foreach (KeyValuePair<ItemValue, long> delta in deltas)
                if (!current.TryGetValue(delta.Key, out long available) || available < delta.Value) return false;
            Dictionary<ItemValue, long> negative = deltas.ToDictionary(pair => pair.Key, pair => -pair.Value, ItemValueComparer.Instance);
            if (!AppendTransaction(path, negative)) return false;
            Apply(current, negative);
            foreach (KeyValuePair<ItemValue, long> delta in deltas)
                if (!current.ContainsKey(delta.Key)) Unindex(path, delta.Key);
            BumpRevision(path);
            NetworkLog.Debug("Network material batch consumed. player=" + player.entityId + "; network=" + Path.GetFileNameWithoutExtension(path) + "; variants=" + negative.Count + "; revision=" + Revisions[path]);
            return true;
        }
    }

    public static bool AddStack(EntityPlayer player, ItemStack stack)
    {
        if (stack == null || stack.IsEmpty()) return false;
        return AddBatch(player, new[] { new StorageAddition { Value = stack.itemValue.Clone(), Count = stack.count } });
    }

    public static bool AddBatch(EntityPlayer player, IEnumerable<StorageAddition> additions)
    {
        if (!(bool)player || additions == null || PlayerNetworkDirectory.IsRemoteClient()) return false;
        lock (Sync)
        {
            string path = GetPath(player);
            if (path == null || UnsafePaths.Contains(path)) return false;

            Dictionary<ItemValue, long> deltas = new Dictionary<ItemValue, long>(ItemValueComparer.Instance);
            foreach (StorageAddition addition in additions)
            {
                if (addition?.Value == null || addition.Value.type == 0 || addition.Count <= 0) continue;
                ItemValue key = NormalizeKey(addition.Value);
                deltas[key] = SafeAdd(deltas.TryGetValue(key, out long oldDelta) ? oldDelta : 0, addition.Count);
            }
            if (deltas.Count == 0) return false;

            Dictionary<ItemValue, long> current = GetStorage(player);
            if (UnsafePaths.Contains(path)) return false;
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
            foreach (KeyValuePair<ItemValue, long> delta in deltas)
                if (!current.ContainsKey(delta.Key)) Index(path, delta.Key);
            Apply(current, deltas);
            BumpRevision(path);
            NetworkLog.Debug("Network batch deposited. player=" + player.entityId + "; network=" + Path.GetFileNameWithoutExtension(path) + "; variants=" + deltas.Count + "; revision=" + Revisions[path]);
            return true;
        }
    }

    public static int Remove(EntityPlayer player, ItemValue value, int requested)
    {
        if (!(bool)player || value == null || requested <= 0 || PlayerNetworkDirectory.IsRemoteClient()) return 0;
        lock (Sync)
        {
            string path = GetPath(player);
            if (path == null || UnsafePaths.Contains(path)) return 0;
            Dictionary<ItemValue, long> current = GetStorage(player);
            if (UnsafePaths.Contains(path)) return 0;
            ItemValue lookup = NormalizeKey(value);
            if (!current.TryGetValue(lookup, out long available) || available <= 0) return 0;
            int amount = (int)Math.Min(Math.Min((long)requested, available), int.MaxValue);
            Dictionary<ItemValue, long> deltas = new Dictionary<ItemValue, long>(ItemValueComparer.Instance) { [lookup] = -amount };
            if (!AppendTransaction(path, deltas)) return 0;
            Apply(current, deltas);
            if (!current.ContainsKey(lookup)) Unindex(path, lookup);
            BumpRevision(path);
            NetworkLog.Debug("Network withdrawal committed. player=" + player.entityId + "; network=" + Path.GetFileNameWithoutExtension(path) + "; item=" + lookup.type + "; count=" + amount + "; revision=" + Revisions[path]);
            return amount;
        }
    }

    private static Dictionary<ItemValue, long> GetStorage(EntityPlayer player)
    {
        string path = GetPath(player);
        if (path == null) return new Dictionary<ItemValue, long>(ItemValueComparer.Instance);
        if (Storages.TryGetValue(path, out Dictionary<ItemValue, long> data)) return data;
        data = Load(path);
        Storages[path] = data;
        Dictionary<int, HashSet<ItemValue>> index = new Dictionary<int, HashSet<ItemValue>>();
        List<ItemValue> sorted = new List<ItemValue>(data.Keys);
        sorted.Sort(CompareItemOrder);
        SortedIndexes[path] = sorted;
        foreach (ItemValue key in data.Keys)
        {
            if (!index.TryGetValue(key.type, out HashSet<ItemValue> keys)) index[key.type] = keys = new HashSet<ItemValue>(ItemValueComparer.Instance);
            keys.Add(key);
        }
        TypeIndexes[path] = index;
        return data;
    }

    private static void Index(string path, ItemValue value)
    {
        Dictionary<int, HashSet<ItemValue>> index = TypeIndexes[path];
        if (!index.TryGetValue(value.type, out HashSet<ItemValue> keys)) index[value.type] = keys = new HashSet<ItemValue>(ItemValueComparer.Instance);
        keys.Add(value);
        List<ItemValue> sorted = SortedIndexes[path];
        int low = 0;
        int high = sorted.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (CompareItemOrder(sorted[middle], value) <= 0) low = middle + 1;
            else high = middle;
        }
        sorted.Insert(low, value.Clone());
    }

    private static void Unindex(string path, ItemValue value)
    {
        if (TypeIndexes[path].TryGetValue(value.type, out HashSet<ItemValue> keys))
        {
            keys.Remove(value);
            if (keys.Count == 0) TypeIndexes[path].Remove(value.type);
        }
        List<ItemValue> sorted = SortedIndexes[path];
        int low = 0;
        int high = sorted.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int order = CompareItemOrder(sorted[middle], value);
            if (order == 0)
            {
                if (sorted[middle].Equals(value)) { sorted.RemoveAt(middle); return; }
                low = middle + 1;
            }
            else if (order < 0) low = middle + 1;
            else high = middle - 1;
        }
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

    internal static ItemValue NormalizeKey(ItemValue value)
    {
        ItemValue key = value.Clone();
        // The game creates a random Seed for newly constructed values. For stackable definitions
        // that seed does not distinguish inventory stacks, so canonicalize it to keep repeated
        // deposits and stress runs in the same storage variant.
        if (key.ItemClass != null && key.ItemClass.MaxCount > 1) key.Seed = 0;
        return key;
    }

    internal static bool MatchesFilters(ItemValue value, string category, string search)
    {
        if (value == null) return false;
        ItemClass itemClass = value.ItemClass;
        if (itemClass == null) return false;
        if (!string.IsNullOrEmpty(category))
        {
            bool isBlock = itemClass.IsBlock() || (value.type > 0 && value.type < Block.ItemsStartHere);
            bool categoryMatch;
            if (category.Equals("Blocks", StringComparison.OrdinalIgnoreCase)) categoryMatch = isBlock;
            else if (category.Equals("Items", StringComparison.OrdinalIgnoreCase)) categoryMatch = !isBlock;
            else if (category.Equals("Ranged Weapons", StringComparison.OrdinalIgnoreCase)
                && itemClass.ItemTags.Test_AnySet(FastTags<TagGroup.Global>.Parse("ammo"))) categoryMatch = false;
            else
            {
                int blockId = value.type < Block.ItemsStartHere ? value.type : (itemClass.IsBlock() ? itemClass.GetBlock()?.blockID ?? -1 : -1);
                categoryMatch = isBlock && blockId >= 0 && blockId < Block.list.Length && Contains(Block.list[blockId]?.GroupNames, category)
                    || Contains(itemClass.Groups, category);
            }
            if (!categoryMatch) return false;
        }
        if (string.IsNullOrEmpty(search)) return true;
        return (itemClass.Name ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
            || (itemClass.GetLocalizedItemName() ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
            || value.type.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool Contains(string[] groups, string category) => groups != null
        && groups.Any(group => string.Equals(group, category, StringComparison.OrdinalIgnoreCase));

    private static int CompareItemOrder(ItemValue left, ItemValue right)
    {
        int type = left.type.CompareTo(right.type);
        if (type != 0) return type;
        int quality = left.Quality.CompareTo(right.Quality);
        if (quality != 0) return quality;
        int seed = left.Seed.CompareTo(right.Seed);
        if (seed != 0) return seed;
        int useTimes = left.UseTimes.CompareTo(right.UseTimes);
        if (useTimes != 0) return useTimes;
        int meta = left.Meta.CompareTo(right.Meta);
        if (meta != 0) return meta;
        int ammo = left.SelectedAmmoTypeIndex.CompareTo(right.SelectedAmmoTypeIndex);
        if (ammo != 0) return ammo;
        return left.Activated.CompareTo(right.Activated);
    }

    private static uint Hash(byte[] bytes)
    {
        uint hash = 2166136261;
        for (int i = 0; i < bytes.Length; i++) hash = (hash ^ bytes[i]) * 16777619;
        return hash;
    }

    private static void BumpRevision(string path) => Revisions[path] = Revisions.TryGetValue(path, out long revision) ? revision + 1 : 1;

    private static string GetPath(EntityPlayer player)
    {
        if (!(bool)player || PlayerNetworkDirectory.IsRemoteClient()) return null;
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId)) return null;
        string save = GameIO.GetSaveGameDir();
        if (string.IsNullOrEmpty(save)) return null;
        return Path.Combine(save, "HyperdimensionalNetwork", networkId + ".hwn");
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
