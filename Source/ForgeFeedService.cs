using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal sealed class ForgeFeedMaterial
{
    public string Category;
    public string Name;
    public int UnitType;
    public int Cap;
    public readonly List<ItemClass> RawItems = new List<ItemClass>();
}

internal sealed class ForgeFeedStation
{
    public TileEntityWorkstation Workstation;
    public Vector3i Position;
    public string Name;
    public ItemStack[] PlannedInput;
}

internal static class ForgeFeedService
{
    internal const float Range = 15f;
    internal const int MaximumForges = 8;
    internal const int MaximumMaterials = 7;
    internal const int VisibleMeltSlots = 2;
    internal const int DefaultFill = 5000;
    private static readonly Dictionary<string, ForgeFeedMaterial> MaterialCache =
        new Dictionary<string, ForgeFeedMaterial>(StringComparer.OrdinalIgnoreCase);

    internal static List<ForgeFeedStation> FindNearby(World world, EntityPlayer player)
    {
        List<ForgeFeedStation> found = new List<ForgeFeedStation>();
        if (world?.ChunkCache == null || !(bool)player || XUiM_Recipes.DisableSmelter) return found;
        float rangeSquared = Range * Range;
        PersistentPlayerData owner = GameManager.Instance?.persistentPlayers?.GetPlayerDataFromEntityID(player.entityId);
        int centerX = World.toChunkXZ(Mathf.FloorToInt(player.position.x));
        int centerZ = World.toChunkXZ(Mathf.FloorToInt(player.position.z));
        int chunkRadius = Mathf.CeilToInt(Range / 16f);
        for (int dx = -chunkRadius; dx <= chunkRadius; dx++)
        for (int dz = -chunkRadius; dz <= chunkRadius; dz++)
        {
            Chunk chunk = world.GetChunkSync(centerX + dx, centerZ + dz) as Chunk;
            if (chunk?.GetTileEntities()?.dict == null) continue;
            foreach (TileEntity tile in chunk.GetTileEntities().dict.Values)
            {
                if (tile is not TileEntityWorkstation forge || tile.IsRemoving || forge.Input == null
                    || forge.MaterialNames == null || forge.MaterialNames.Length == 0
                    || forge.Input.Length <= forge.MaterialNames.Length) continue;
                Vector3i position = forge.ToWorldPos();
                if ((forge.ToWorldCenterPos() - player.position).sqrMagnitude > rangeSquared) continue;
                if (world.GetLandClaimOwner(position, owner) == EnumLandClaimOwner.Other) continue;
                BlockValue block = world.GetBlock(position);
                found.Add(new ForgeFeedStation
                {
                    Workstation = forge,
                    Position = position,
                    Name = block.Block?.GetBlockName() ?? "Forge",
                    PlannedInput = ItemStack.Clone(forge.Input)
                });
            }
        }
        return found.OrderBy(station => (station.Workstation.ToWorldCenterPos() - player.position).sqrMagnitude)
            .Take(MaximumForges).ToList();
    }

    internal static List<ForgeFeedMaterial> BuildMaterials(IEnumerable<ForgeFeedStation> stations)
    {
        List<ForgeFeedMaterial> materials = new List<ForgeFeedMaterial>();
        HashSet<string> categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ForgeFeedStation station in stations)
            foreach (string category in station.Workstation.MaterialNames)
            {
                if (string.IsNullOrWhiteSpace(category) || !categories.Add(category)) continue;
                ForgeFeedMaterial material;
                lock (MaterialCache)
                {
                    if (!MaterialCache.TryGetValue(category, out material))
                    {
                        ItemClass unit = ItemClass.GetItemClass("unit_" + category, false);
                        if (unit == null || unit.MadeOfMaterial?.ForgeCategory == null) continue;
                        material = new ForgeFeedMaterial
                        {
                            Category = category,
                            Name = LocalizedCategory(category),
                            UnitType = unit.Id,
                            Cap = Math.Max(1, unit.MaxCount)
                        };
                        AddRecipeRaw(material);
                        AddRaw(material, ItemClass.GetItemClass("resourceBrokenGlass", false));
                        MaterialCache[category] = material;
                    }
                }
                if (material.RawItems.Count > 0) materials.Add(material);
            }
        return materials.OrderBy(material => material.Category, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumMaterials).ToList();
    }

    private static string LocalizedCategory(string category)
    {
        string value = null;
        if (Localization.TryGet("lbl" + category, out value) && !string.IsNullOrWhiteSpace(value)) return value;
        return category;
    }

    private static void AddRecipeRaw(ForgeFeedMaterial material)
    {
        List<Recipe> recipes = CraftingManager.GetRecipes();
        if (recipes == null) return;
        foreach (Recipe recipe in recipes)
        {
            if (recipe == null || !recipe.materialBasedRecipe || recipe.ingredients?.Count != 1
                || recipe.ingredients[0]?.itemValue?.type != material.UnitType) continue;
            AddRaw(material, recipe.GetOutputItemClass());
            if (material.RawItems.Count > 0) return;
        }
    }

    private static void AddRaw(ForgeFeedMaterial material, ItemClass item)
    {
        if (item == null || item.GetWeight() <= 0 || item.HasQuality || !item.CanStack()
            || !string.Equals(item.MadeOfMaterial?.ForgeCategory, material.Category, StringComparison.OrdinalIgnoreCase)
            || material.RawItems.Any(candidate => candidate.Id == item.Id)) return;
        material.RawItems.Add(item);
    }

    internal static int CurrentLevel(ForgeFeedStation station, ForgeFeedMaterial material)
    {
        ItemStack[] input = station.PlannedInput;
        int meltSlots = input.Length - station.Workstation.MaterialNames.Length;
        long units = 0;
        for (int i = meltSlots; i < input.Length; i++)
            if (input[i] != null && !input[i].IsEmpty() && input[i].itemValue.type == material.UnitType)
                units += input[i].count;
        for (int i = 0; i < meltSlots; i++)
        {
            ItemStack stack = input[i];
            ItemClass item = stack?.itemValue?.ItemClass;
            if (stack == null || stack.IsEmpty() || item?.MadeOfMaterial == null
                || !string.Equals(item.MadeOfMaterial.ForgeCategory, material.Category, StringComparison.OrdinalIgnoreCase)) continue;
            units += (long)stack.count * Math.Max(0, item.GetWeight());
        }
        return (int)Math.Min(int.MaxValue, units);
    }

    internal static NetworkRemoteResult Execute(World world, EntityPlayer player, PlayerDataFile playerData,
        NetworkRemoteRequest request)
    {
        if (world == null || !(bool)player || XUiM_Recipes.DisableSmelter || request.Count < 0 || request.Count > 100000
            || string.IsNullOrWhiteSpace(request.Category)) return Failure(request, "InvalidForgeFeed");
        HashSet<string> selected = new HashSet<string>(request.Category.Split(',')
            .Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 || selected.Count > MaximumMaterials) return Failure(request, "InvalidForgeMaterials");
        List<ForgeFeedStation> forges = FindNearby(world, player);
        if (forges.Count == 0) return Failure(request, "NoNearbyForge");
        List<ForgeFeedMaterial> materials = BuildMaterials(forges);
        if (selected.Any(category => materials.All(material => !string.Equals(material.Category, category, StringComparison.OrdinalIgnoreCase))))
            return Failure(request, "InvalidForgeMaterials");
        if (player is not EntityPlayerLocal && (playerData?.bagData == null || playerData.bagData.Length == 0))
            return Failure(request, "BackpackUnavailable");
        Bag bag;
        try { bag = playerData?.bagData != null ? NetworkRpcServer.ReadBag(playerData.bagData) : player.bag; }
        catch (Exception ex) { NetworkLog.Error("Forge feed could not read player bag. player=" + player.entityId + "; exception=" + ex); return Failure(request, "BackpackUnavailable"); }
        if (bag == null) return Failure(request, "BackpackUnavailable");
        ItemStack[] originalBag = ItemStack.Clone(bag.ItemGrid.items);
        ItemStack[] plannedBag = ItemStack.Clone(originalBag);
        List<ItemStack> requirements = materials.SelectMany(material => material.RawItems)
            .GroupBy(item => item.Id).Select(group => new ItemStack(new ItemValue(group.Key), 1)).ToList();
        List<StoredEntry> network = NetworkStorage.GetEntriesForRequirements(player, requirements);
        Dictionary<int, int> available = new Dictionary<int, int>();
        for (int i = 0; i < plannedBag.Length; i++)
        {
            ItemStack stack = plannedBag[i];
            if (stack != null && !stack.IsEmpty() && IsPlainRaw(stack.itemValue)
                && (bag.LockedSlots == null || i >= bag.LockedSlots.Length || !bag.LockedSlots[i]))
                available[stack.itemValue.type] = SaturatingAdd(available.TryGetValue(stack.itemValue.type, out int count) ? count : 0, stack.count);
        }
        foreach (StoredEntry entry in network)
            if (IsPlainRaw(entry.Value))
                available[entry.Value.type] = SaturatingAdd(available.TryGetValue(entry.Value.type, out int count) ? count : 0, entry.Count);
        Dictionary<int, int> plannedByType = new Dictionary<int, int>();
        int fill = request.Count == 0 ? int.MaxValue : request.Count;
        foreach (ForgeFeedMaterial material in materials)
        {
            if (!selected.Contains(material.Category)) continue;
            foreach (ItemClass raw in material.RawItems)
            {
                if (!available.TryGetValue(raw.Id, out int remaining) || remaining <= 0) continue;
                int weight = Math.Max(1, raw.GetWeight());
                while (remaining > 0)
                {
                    ForgeFeedStation target = null;
                    int targetSlot = -1;
                    int targetLevel = int.MaxValue;
                    int targetSpace = 0;
                    foreach (ForgeFeedStation forge in forges)
                    {
                        if (!CanUse(player, forge.Workstation) || !forge.Workstation.MaterialNames.Any(name =>
                            string.Equals(name, material.Category, StringComparison.OrdinalIgnoreCase))) continue;
                        int level = CurrentLevel(forge, material);
                        int limit = Math.Min(fill, material.Cap);
                        if ((long)level + weight > limit) continue;
                        int slot = FindSlot(forge.PlannedInput, raw.Id, raw.MaxCount,
                            Math.Min(VisibleMeltSlots, forge.PlannedInput.Length - forge.Workstation.MaterialNames.Length));
                        if (slot < 0) continue;
                        ItemStack stack = forge.PlannedInput[slot];
                        int space = Math.Min(raw.MaxCount - (stack == null || stack.IsEmpty() ? 0 : stack.count), (limit - level) / weight);
                        if (space <= 0 || level >= targetLevel) continue;
                        target = forge; targetSlot = slot; targetLevel = level; targetSpace = space;
                    }
                    if (target == null) break;
                    List<int> otherLevels = forges.Where(forge => forge != target && CanUse(player, forge.Workstation)
                            && forge.Workstation.MaterialNames.Any(name =>
                                string.Equals(name, material.Category, StringComparison.OrdinalIgnoreCase))
                            && FindSlot(forge.PlannedInput, raw.Id, raw.MaxCount,
                                Math.Min(VisibleMeltSlots, forge.PlannedInput.Length - forge.Workstation.MaterialNames.Length)) >= 0)
                        .Select(forge => CurrentLevel(forge, material)).Where(level => level >= targetLevel
                            && (long)level + weight <= Math.Min(fill, material.Cap))
                        .ToList();
                    int nextLevel = otherLevels.Count == 0 ? int.MaxValue : otherLevels.Min();
                    int chunk = Math.Min(remaining, targetSpace);
                    if (nextLevel == targetLevel)
                    {
                        int equalCount = otherLevels.Count(level => level == targetLevel) + 1;
                        chunk = Math.Min(chunk, (int)Math.Max(1L, ((long)remaining + equalCount - 1) / equalCount));
                    }
                    else if (nextLevel != int.MaxValue)
                        chunk = Math.Min(chunk, Math.Max(1, (nextLevel - targetLevel + weight - 1) / weight));
                    ItemStack existing = target.PlannedInput[targetSlot];
                    if (existing == null || existing.IsEmpty()) target.PlannedInput[targetSlot] = new ItemStack(new ItemValue(raw.Id), chunk);
                    else existing.count += chunk;
                    remaining -= chunk;
                    plannedByType[raw.Id] = SaturatingAdd(plannedByType.TryGetValue(raw.Id, out int count) ? count : 0, chunk);
                }
                available[raw.Id] = remaining;
            }
        }
        if (plannedByType.Count == 0) return Failure(request, "NoForgeFeedNeeded");
        foreach (ForgeFeedStation forge in forges)
        {
            if (StacksEqual(forge.Workstation.Input, forge.PlannedInput)) continue;
            int meltSlots = Math.Min(VisibleMeltSlots, forge.PlannedInput.Length - forge.Workstation.MaterialNames.Length);
            string loads = string.Join(";", forge.PlannedInput.Take(meltSlots)
                .Where(stack => stack != null && !stack.IsEmpty())
                .Select(stack => stack.itemValue.type + "x" + stack.count));
            NetworkLog.Debug("Forge feed planned. player=" + player.entityId + "; forge=" + forge.Position
                + "; inputs=" + loads);
        }
        List<StorageAddition> networkRemovals = new List<StorageAddition>();
        int total = 0;
        foreach (KeyValuePair<int, int> planned in plannedByType)
        {
            int remaining = planned.Value;
            for (int i = 0; i < plannedBag.Length && remaining > 0; i++)
            {
                ItemStack stack = plannedBag[i];
                if (stack == null || stack.IsEmpty() || stack.itemValue.type != planned.Key || !IsPlainRaw(stack.itemValue)
                    || bag.LockedSlots != null && i < bag.LockedSlots.Length && bag.LockedSlots[i]) continue;
                int take = Math.Min(remaining, stack.count);
                stack.count -= take;
                if (stack.count == 0) plannedBag[i] = ItemStack.Empty.Clone();
                remaining -= take;
            }
            foreach (StoredEntry entry in network)
            {
                if (remaining == 0) break;
                if (entry.Count <= 0 || entry.Value.type != planned.Key || !IsPlainRaw(entry.Value)) continue;
                int take = (int)Math.Min(remaining, entry.Count);
                entry.Count -= take;
                remaining -= take;
                networkRemovals.Add(new StorageAddition { Value = entry.Value.Clone(), Count = take });
            }
            if (remaining > 0) return Failure(request, "ForgeMaterialsChanged");
            total += planned.Value;
        }
        List<ItemStack[]> originals = forges.Select(forge => ItemStack.Clone(forge.Workstation.Input)).ToList();
        List<bool> originalBurning = forges.Select(forge => forge.Workstation.isBurning).ToList();
        MemoryStream originalBagBlob = playerData?.bagData;
        if (networkRemovals.Count > 0 && !NetworkStorage.RemoveBatch(player, networkRemovals))
            return Failure(request, "ForgeNetworkCommitFailed");
        try
        {
            for (int i = 0; i < plannedBag.Length; i++)
                if (!ItemStack.Equals(originalBag[i], plannedBag[i])) bag.SetSlot(i, plannedBag[i]);
            MemoryStream updatedBag = NetworkRpcServer.WriteBag(bag);
            if (playerData != null)
            {
                NetworkRpcServer.ApplyPlayerInventorySnapshot(player, updatedBag, toolbelt: false);
                playerData.bagData = updatedBag;
            }
            for (int i = 0; i < forges.Count; i++)
            {
                TileEntityWorkstation forge = forges[i].Workstation;
                if (StacksEqual(originals[i], forges[i].PlannedInput)) continue;
                forge.Input = forges[i].PlannedInput;
                if (!forge.isBurning && forge.BurnTotalTimeLeft > 0f && !forge.IsBesideWater)
                    forge.isBurning = true;
                forge.ResetTickTime();
                forge.SetModified();
            }
            NetworkLog.Debug("Forge feed committed. player=" + player.entityId + "; forges=" + forges.Count
                + "; categories=" + request.Category + "; bagItems=" + (total - networkRemovals.Sum(item => (int)item.Count))
                + "; networkItems=" + networkRemovals.Sum(item => (int)item.Count) + "; total=" + total);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = total,
                BagData = playerData == null ? null : updatedBag.ToArray() };
        }
        catch (Exception ex)
        {
            for (int i = 0; i < forges.Count; i++)
                try
                {
                    forges[i].Workstation.Input = originals[i];
                    forges[i].Workstation.isBurning = originalBurning[i];
                    forges[i].Workstation.ResetTickTime();
                    forges[i].Workstation.SetModified();
                }
                catch (Exception restore) { NetworkLog.Error("Forge input rollback failed. " + restore); }
            try
            {
                for (int i = 0; i < originalBag.Length; i++) bag.SetSlot(i, originalBag[i]);
                if (playerData != null)
                {
                    playerData.bagData = originalBagBlob;
                    NetworkRpcServer.ApplyPlayerInventorySnapshot(player, originalBagBlob, toolbelt: false);
                }
            }
            catch (Exception restore) { NetworkLog.Error("Forge bag rollback failed. " + restore); }
            if (networkRemovals.Count > 0 && !NetworkStorage.AddBatch(player, networkRemovals))
                NetworkLog.Error("Forge network rollback failed. player=" + player.entityId);
            NetworkLog.Error("Forge feed transaction rolled back. player=" + player.entityId + "; exception=" + ex);
            return Failure(request, "ForgeFeedFailed");
        }
    }

    private static int FindSlot(ItemStack[] input, int itemType, int maxCount, int meltSlots)
    {
        for (int i = 0; i < meltSlots; i++)
            if (input[i] != null && !input[i].IsEmpty() && input[i].itemValue.type == itemType
                && IsPlainRaw(input[i].itemValue) && input[i].count < maxCount) return i;
        for (int i = 0; i < meltSlots; i++)
            if (input[i] == null || input[i].IsEmpty()) return i;
        return -1;
    }

    private static bool CanUse(EntityPlayer player, TileEntityWorkstation forge)
    {
        LockManager locks = LockManager.Instance;
        bool busy = forge.IsUserAccessing();
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        bool locked = locks != null && (bool)connection && connection.IsServer && locks.IsLockedServer(forge);
        if (!busy && !locked) return true;
        bool owned = locks != null && (player is EntityPlayerLocal
            ? locks.IsLockedByLocalPlayer(forge)
            : NetworkRpcServer.PlayerHoldsLootLock(player.entityId, forge));
        return owned;
    }

    private static bool StacksEqual(ItemStack[] left, ItemStack[] right)
        => left.Length == right.Length && !left.Where((stack, index) => !ItemStack.Equals(stack, right[index])).Any();

    private static int SaturatingAdd(int left, long right)
        => (int)Math.Min(int.MaxValue, (long)left + Math.Max(0, right));

    internal static bool IsPlainRaw(ItemValue value)
        => value != null && value.Quality <= 0
            && NetworkStorage.NormalizeKey(value).Equals(NetworkStorage.NormalizeKey(new ItemValue(value.type)));

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request.RequestId, Success = false, Error = error };
}
