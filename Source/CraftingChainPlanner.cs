using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal enum CraftingChainSupplySource : byte
{
    Player = 0,
    Network = 1,
    Forge = 2
}

internal sealed class CraftingChainItemInfo
{
    public int Id;
    public int Type;
    public int Quality;
    public string Name = string.Empty;
    public string Icon = string.Empty;
}

internal sealed class CraftingChainSupplyInfo
{
    public int ItemId;
    public long Count;
    public CraftingChainSupplySource Source;
}

internal sealed class CraftingChainIngredientInfo
{
    public int ItemId;
    public int Count;
    public int[] MatchingSupplyIndexes = Array.Empty<int>();
    public bool ForgeOnly;
}

internal sealed class CraftingChainRecipeInfo
{
    public string Key = string.Empty;
    public string Name = string.Empty;
    public string CraftingArea = string.Empty;
    public int OutputItemId;
    public int OutputCount;
    public List<CraftingChainIngredientInfo> Ingredients = new List<CraftingChainIngredientInfo>();
}

internal sealed class CraftingChainRecipeOption
{
    public string Key = string.Empty;
    public string CraftingArea = string.Empty;
    public CraftingChainRecipeOptionIngredient[] Ingredients = Array.Empty<CraftingChainRecipeOptionIngredient>();
}

internal sealed class CraftingChainRecipeOptionIngredient
{
    public int ItemId;
    public int Count;
}

internal sealed class CraftingChainPlanInput
{
    public string RootRecipeKey = string.Empty;
    public int RequestedCrafts = 1;
    public int RootOutputItemId;
    public int[] RootMatchingSupplyIndexes = Array.Empty<int>();
    public List<CraftingChainItemInfo> Items = new List<CraftingChainItemInfo>();
    public List<CraftingChainSupplyInfo> Supplies = new List<CraftingChainSupplyInfo>();
    public List<CraftingChainRecipeInfo> Recipes = new List<CraftingChainRecipeInfo>();
    // Main-thread-only execution snapshot. CloneForWorker intentionally omits this game object map.
    public Dictionary<int, ItemValue> MainThreadItemValues = new Dictionary<int, ItemValue>();
    public int[] NetworkTypeIds = Array.Empty<int>();
    public Dictionary<int, int[]> MatchingSuppliesByItem = new Dictionary<int, int[]>();
    public Dictionary<int, string> SelectedRecipeKeys = new Dictionary<int, string>();
    public Dictionary<string, List<CraftingChainWorkstationPosition>> WorkstationsByArea = new Dictionary<string, List<CraftingChainWorkstationPosition>>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CraftingChainPlanNode
{
    public int ItemId;
    public string RecipeKey = string.Empty;
    public string RecipeName = string.Empty;
    public string CraftingArea = string.Empty;
    public int Batches;
    public int OutputCount;
    public CraftingChainRecipeOption[] RecipeOptions = Array.Empty<CraftingChainRecipeOption>();
    public List<CraftingChainPlanNode> Children = new List<CraftingChainPlanNode>();
    public List<CraftingChainPlanDisplayChild> DisplayChildren = new List<CraftingChainPlanDisplayChild>();
}

internal sealed class CraftingChainPlanDisplayChild
{
    public int ItemId;
    public long RequiredCount;
    public bool ForgeOnly;
    public CraftingChainPlanNode RecipeNode;
}

internal sealed class CraftingChainPlanMaterial
{
    public int ItemId;
    public long Required;
    public long Available;
    public bool ForgeOnly;
}

internal sealed class CraftingChainPlan
{
    public string RootRecipeKey = string.Empty;
    public int RequestedCrafts;
    public int RequestedOutput;
    public int AvailableOutput;
    public int PlannedOutput;
    public CraftingChainRecipeOption[] RootRecipeOptions = Array.Empty<CraftingChainRecipeOption>();
    public List<CraftingChainItemInfo> Items = new List<CraftingChainItemInfo>();
    public List<CraftingChainPlanNode> Nodes = new List<CraftingChainPlanNode>();
    public List<CraftingChainPlanMaterial> Materials = new List<CraftingChainPlanMaterial>();
    public List<CraftingChainPlanMaterial> ReservedSupplies = new List<CraftingChainPlanMaterial>();
    public long MissingMaterials;
    public string Error = string.Empty;
}

/// <summary>
/// Captures game data on the Unity thread, then builds the dependency graph from plain snapshots.
/// The worker never reads Recipe, ItemValue, inventory, XUi, or other Unity/game objects.
/// </summary>
internal static class CraftingChainPlanner
{
    private const int MaximumGraphDepth = 32;
    private const int MaximumGraphNodes = 48;
    private const int MaximumCandidateTrials = 512;

    private sealed class ItemValueKeyComparer : IEqualityComparer<ItemValue>
    {
        public static readonly ItemValueKeyComparer Instance = new ItemValueKeyComparer();

        public bool Equals(ItemValue left, ItemValue right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            return NetworkStorage.NormalizeKey(left).Equals(NetworkStorage.NormalizeKey(right));
        }

        public int GetHashCode(ItemValue value)
        {
            if (value == null) return 0;
            ItemValue key = NetworkStorage.NormalizeKey(value);
            unchecked
            {
                int hash = key.type;
                hash = hash * 397 ^ key.Quality;
                hash = hash * 397 ^ key.Seed;
                hash = hash * 397 ^ key.Meta;
                hash = hash * 397 ^ key.SelectedAmmoTypeIndex;
                hash = hash * 397 ^ (key.Activated ? 1 : 0);
                return hash;
            }
        }
    }

    private sealed class PlannerState
    {
        public long[] RemainingSupplies;
        public long[] Produced;
        public Dictionary<Tuple<int, bool>, CraftingChainPlanMaterial> Materials;
        public int CreatedNodes;
        public bool LimitExceeded;

        public PlannerState(int supplyCount, int itemCount)
        {
            RemainingSupplies = new long[supplyCount];
            Produced = new long[itemCount];
            Materials = new Dictionary<Tuple<int, bool>, CraftingChainPlanMaterial>();
        }

        private PlannerState(PlannerState source)
        {
            RemainingSupplies = (long[])source.RemainingSupplies.Clone();
            Produced = (long[])source.Produced.Clone();
            Materials = source.Materials.ToDictionary(pair => pair.Key, pair => new CraftingChainPlanMaterial
            {
                ItemId = pair.Value.ItemId,
                Required = pair.Value.Required,
                Available = pair.Value.Available,
                ForgeOnly = pair.Value.ForgeOnly
            });
            CreatedNodes = source.CreatedNodes;
            LimitExceeded = source.LimitExceeded;
        }

        public PlannerState Clone() => new PlannerState(this);

        public void CopyFrom(PlannerState source)
        {
            Array.Copy(source.RemainingSupplies, RemainingSupplies, RemainingSupplies.Length);
            Array.Copy(source.Produced, Produced, Produced.Length);
            Materials = source.Materials.ToDictionary(pair => pair.Key, pair => new CraftingChainPlanMaterial
            {
                ItemId = pair.Value.ItemId,
                Required = pair.Value.Required,
                Available = pair.Value.Available,
                ForgeOnly = pair.Value.ForgeOnly
            });
            CreatedNodes = source.CreatedNodes;
            LimitExceeded = source.LimitExceeded;
        }
    }

    private sealed class PlannerContext
    {
        public CraftingChainPlanInput Input;
        public Dictionary<string, CraftingChainRecipeInfo> RecipesByKey;
        public Dictionary<int, List<CraftingChainRecipeInfo>> RecipesByOutput;
        public int CandidateTrials;
        public CancellationToken CancellationToken;
    }

    public static CraftingChainPlanInput Capture(XUi xui, EntityPlayerLocal player, Recipe rootRecipe,
        IDictionary<int, string> selectedRecipeKeys)
    {
        if (xui == null || !(bool)player || rootRecipe == null) return null;

        List<Recipe> recipes = (CraftingManager.recipes ?? new List<Recipe>())
            .Where(recipe => recipe != null && !recipe.IsScrap && recipe.ingredients != null
                && recipe.ingredients.Any(ingredient => ingredient != null && !ingredient.IsEmpty())
                && (recipe.IsTrackable || string.Equals(recipe.craftingArea, "forge", StringComparison.OrdinalIgnoreCase))
                && IsActiveForgeRecipe(recipe)
                && recipe.GetOutputItemClass() != null && recipe.GetOutputItemClass().Id > 0 && recipe.IsUnlocked(player))
            .Distinct()
            .ToList();
        Dictionary<string, List<CraftingChainWorkstationPosition>> workstationMap = CraftingChainWorkstationService.Capture(player,
            recipes.Select(recipe => recipe.craftingArea));
        recipes = recipes.Where(recipe => string.IsNullOrEmpty(recipe.craftingArea)
            || workstationMap.ContainsKey(recipe.craftingArea) && workstationMap[recipe.craftingArea].Count > 0).ToList();
        if (!recipes.Contains(rootRecipe)) recipes.Add(rootRecipe);
        NetworkLog.Debug("Crafting-chain usable recipe snapshot captured. recipes=" + recipes.Count
            + "; forgeRecipes=" + recipes.Count(recipe => string.Equals(recipe.craftingArea, "forge", StringComparison.OrdinalIgnoreCase))
            + "; smelterDisabled=" + XUiM_Recipes.DisableSmelter);

        Dictionary<ItemValue, int> itemIds = new Dictionary<ItemValue, int>(ItemValueKeyComparer.Instance);
        Dictionary<int, ItemValue> mainThreadItemValues = new Dictionary<int, ItemValue>();
        List<CraftingChainItemInfo> items = new List<CraftingChainItemInfo>();
        Func<ItemValue, int> addItem = value =>
        {
            if (value == null || value.type <= 0) return -1;
            ItemValue key = NetworkStorage.NormalizeKey(value);
            if (itemIds.TryGetValue(key, out int existing)) return existing;
            ItemClass itemClass = key.ItemClass;
            int id = items.Count;
            itemIds[key] = id;
            mainThreadItemValues[id] = key.Clone();
            items.Add(new CraftingChainItemInfo
            {
                Id = id,
                Type = key.type,
                Quality = key.Quality,
                Name = itemClass?.GetLocalizedItemName() ?? key.type.ToString(),
                Icon = itemClass?.GetIconName() ?? string.Empty
            });
            return id;
        };

        List<ItemValue> allRequirementValues = new List<ItemValue>();
        foreach (Recipe recipe in recipes)
        {
            foreach (ItemStack ingredient in recipe.ingredients)
            {
                if (ingredient?.itemValue == null || ingredient.IsEmpty()) continue;
                allRequirementValues.Add(ingredient.itemValue);
                addItem(ingredient.itemValue);
            }
            ItemClass outputClass = recipe.GetOutputItemClass();
            int quality = outputClass != null && outputClass.HasQuality ? recipe.GetCraftingTier(player) : 0;
            if (ReferenceEquals(recipe, rootRecipe) && outputClass != null && outputClass.HasQuality)
                quality = Math.Max(0, xui.Recipes.TrackedRecipeQuality);
            addItem(CreateOutputValue(recipe.itemValueType, quality));
        }

        List<CraftingChainSupplyInfo> supplies = new List<CraftingChainSupplyInfo>();
        List<ItemValue> supplyValues = new List<ItemValue>();
        Action<ItemValue, long, CraftingChainSupplySource> addSupply = (value, count, source) =>
        {
            if (value == null || value.type <= 0 || count <= 0) return;
            int itemId = addItem(value);
            if (itemId < 0) return;
            supplyValues.Add(value.Clone());
            supplies.Add(new CraftingChainSupplyInfo { ItemId = itemId, Count = count, Source = source });
        };

        CaptureGrid(player.inventory?.ItemGrid, addSupply);
        CaptureGrid(player.bag?.ItemGrid, addSupply);
        CaptureAccessibleForgeSupplies(workstationMap, addSupply);
        allRequirementValues.Add(CreateOutputValue(rootRecipe.itemValueType,
            rootRecipe.GetOutputItemClass()?.HasQuality == true ? Math.Max(0, xui.Recipes.TrackedRecipeQuality) : 0));
        List<StoredEntry> networkEntries = NetworkStorage.GetEntriesForRequirements(player,
            allRequirementValues.Where(value => value != null).Select(value => new ItemStack(value.Clone(), 1)));
        foreach (StoredEntry entry in networkEntries)
            if (entry?.Value != null && entry.Count > 0)
                addSupply(entry.Value, entry.Count, CraftingChainSupplySource.Network);

        List<CraftingChainRecipeInfo> recipeInfos = new List<CraftingChainRecipeInfo>();
        foreach (Recipe recipe in recipes)
        {
            ItemClass outputClass = recipe.GetOutputItemClass();
            int outputQuality = outputClass != null && outputClass.HasQuality ? recipe.GetCraftingTier(player) : 0;
            if (ReferenceEquals(recipe, rootRecipe) && outputClass != null && outputClass.HasQuality)
                outputQuality = Math.Max(0, xui.Recipes.TrackedRecipeQuality);
            ItemValue outputValue = CreateOutputValue(recipe.itemValueType, outputQuality);
            int outputItemId = addItem(outputValue);
            if (outputItemId < 0) continue;

            int outputCount;
            try { outputCount = Math.Max(1, XUiM_Recipes.GetRecipeCraftOutputCount(xui, recipe)); }
            catch (Exception ex)
            {
                outputCount = Math.Max(1, recipe.count);
                NetworkLog.Debug("Crafting-chain output modifier snapshot failed; using recipe batch count. recipe=" + recipe.GetName() + "; exception=" + ex.Message);
            }

            CraftingChainRecipeInfo info = new CraftingChainRecipeInfo
            {
                Key = BuildRecipeKey(recipe, outputQuality),
                Name = recipe.GetName() ?? string.Empty,
                CraftingArea = recipe.craftingArea ?? string.Empty,
                OutputItemId = outputItemId,
                OutputCount = outputCount
            };
            int tier = recipe.GetCraftingTier(player);
            foreach (ItemStack ingredient in recipe.ingredients)
            {
                if (ingredient?.itemValue == null || ingredient.IsEmpty()) continue;
                int ingredientCount = GetIngredientCount(xui, player, recipe, ingredient, tier);
                if (ingredientCount <= 0) continue;
                info.Ingredients.Add(new CraftingChainIngredientInfo
                {
                    ItemId = addItem(ingredient.itemValue),
                    Count = ingredientCount,
                    ForgeOnly = string.Equals(info.CraftingArea, "forge", StringComparison.OrdinalIgnoreCase)
                });
            }
            recipeInfos.Add(info);
        }

        int rootQuality = rootRecipe.GetOutputItemClass()?.HasQuality == true
            ? Math.Max(0, xui.Recipes.TrackedRecipeQuality) : 0;
        int rootOutputId = addItem(CreateOutputValue(rootRecipe.itemValueType, rootQuality));
        if (rootOutputId < 0) return null;

        Dictionary<int, int[]> matchingByItem = new Dictionary<int, int[]>();
        foreach (CraftingChainItemInfo item in items)
        {
            ItemValue required = itemIds.FirstOrDefault(pair => pair.Value == item.Id).Key;
            List<int> matches = new List<int>();
            for (int index = 0; index < supplies.Count; index++)
            {
                CraftingChainSupplyInfo supply = supplies[index];
                if (supply.Source == CraftingChainSupplySource.Forge)
                {
                    if (supply.ItemId == item.Id) matches.Add(index);
                }
                else if (index < supplyValues.Count && NetworkItemMatcher.Matches(supplyValues[index], required)) matches.Add(index);
            }
            matchingByItem[item.Id] = matches.ToArray();
        }

        foreach (CraftingChainRecipeInfo recipe in recipeInfos)
            foreach (CraftingChainIngredientInfo ingredient in recipe.Ingredients)
                ingredient.MatchingSupplyIndexes = matchingByItem.TryGetValue(ingredient.ItemId, out int[] matches) ? matches : Array.Empty<int>();

        int[] rootMatches = matchingByItem.TryGetValue(rootOutputId, out int[] rootSupplyMatches) ? rootSupplyMatches : Array.Empty<int>();
        Dictionary<int, string> choices = selectedRecipeKeys == null
            ? new Dictionary<int, string>()
            : new Dictionary<int, string>(selectedRecipeKeys);
        CraftingChainPlanInput input = new CraftingChainPlanInput
        {
            RootRecipeKey = BuildRecipeKey(rootRecipe, rootQuality),
            RequestedCrafts = Math.Max(1, xui.Recipes.TrackedRecipeCount),
            RootOutputItemId = rootOutputId,
            RootMatchingSupplyIndexes = rootMatches,
            Items = items,
            Supplies = supplies,
            Recipes = recipeInfos,
            NetworkTypeIds = allRequirementValues.Where(value => value != null && value.type > 0).Select(value => value.type).Distinct().ToArray(),
            MatchingSuppliesByItem = matchingByItem,
            SelectedRecipeKeys = choices,
            WorkstationsByArea = workstationMap,
            MainThreadItemValues = mainThreadItemValues
        };

        foreach (CraftingChainRecipeInfo recipe in recipeInfos)
        {
            foreach (CraftingChainIngredientInfo ingredient in recipe.Ingredients)
            {
                ingredient.MatchingSupplyIndexes = matchingByItem.TryGetValue(ingredient.ItemId, out int[] matches)
                    ? matches : Array.Empty<int>();
            }
        }
        return input;
    }

    public static Task<CraftingChainPlan> BuildAsync(CraftingChainPlanInput input, CancellationToken cancellationToken)
    {
        CraftingChainPlanInput snapshot = CloneForWorker(input);
        return Task.Run(() => Build(snapshot, cancellationToken), cancellationToken);
    }

    private static CraftingChainPlanInput CloneForWorker(CraftingChainPlanInput source)
    {
        if (source == null) return null;
        return new CraftingChainPlanInput
        {
            RootRecipeKey = source.RootRecipeKey,
            RequestedCrafts = source.RequestedCrafts,
            RootOutputItemId = source.RootOutputItemId,
            RootMatchingSupplyIndexes = (int[])source.RootMatchingSupplyIndexes.Clone(),
            Items = source.Items.Select(item => new CraftingChainItemInfo
            {
                Id = item.Id, Type = item.Type, Quality = item.Quality, Name = item.Name, Icon = item.Icon
            }).ToList(),
            Supplies = source.Supplies.Select(supply => new CraftingChainSupplyInfo
            {
                ItemId = supply.ItemId, Count = supply.Count, Source = supply.Source
            }).ToList(),
            Recipes = source.Recipes.Select(recipe => new CraftingChainRecipeInfo
            {
                Key = recipe.Key,
                Name = recipe.Name,
                CraftingArea = recipe.CraftingArea,
                OutputItemId = recipe.OutputItemId,
                OutputCount = recipe.OutputCount,
                Ingredients = recipe.Ingredients.Select(ingredient => new CraftingChainIngredientInfo
                {
                    ItemId = ingredient.ItemId,
                    Count = ingredient.Count,
                    ForgeOnly = ingredient.ForgeOnly,
                    MatchingSupplyIndexes = (int[])ingredient.MatchingSupplyIndexes.Clone()
                }).ToList()
            }).ToList(),
            NetworkTypeIds = (int[])source.NetworkTypeIds.Clone(),
            MatchingSuppliesByItem = source.MatchingSuppliesByItem.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone()),
            SelectedRecipeKeys = new Dictionary<int, string>(source.SelectedRecipeKeys),
            WorkstationsByArea = source.WorkstationsByArea.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(position => new CraftingChainWorkstationPosition
                {
                    Area = position.Area,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z
                }).ToList(), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static CraftingChainPlan Build(CraftingChainPlanInput input, CancellationToken cancellationToken)
    {
        CraftingChainPlan plan = new CraftingChainPlan
        {
            RootRecipeKey = input?.RootRecipeKey ?? string.Empty,
            RequestedCrafts = input?.RequestedCrafts ?? 0,
            Items = input?.Items ?? new List<CraftingChainItemInfo>()
        };
        if (input == null) { plan.Error = "InvalidPlanInput"; return plan; }

        Dictionary<string, CraftingChainRecipeInfo> byKey = input.Recipes
            .GroupBy(recipe => recipe.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Dictionary<int, List<CraftingChainRecipeInfo>> byOutput = input.Recipes
            .GroupBy(recipe => recipe.OutputItemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(recipe => recipe.Key, StringComparer.Ordinal).ToList());
        PlannerContext context = new PlannerContext { Input = input, RecipesByKey = byKey, RecipesByOutput = byOutput, CancellationToken = cancellationToken };
        PlannerState state = new PlannerState(input.Supplies.Count, input.Items.Count);
        for (int index = 0; index < input.Supplies.Count; index++) state.RemainingSupplies[index] = Math.Max(0, input.Supplies[index].Count);

        CraftingChainRecipeInfo rootRecipe = byKey.TryGetValue(input.RootRecipeKey, out CraftingChainRecipeInfo selectedRoot)
            ? selectedRoot : null;
        if (rootRecipe == null) { plan.Error = "TrackedRecipeUnavailable"; return plan; }

        int wanted = SafeCount((long)input.RequestedCrafts * rootRecipe.OutputCount);
        plan.RequestedOutput = wanted;
        plan.RootRecipeOptions = byOutput.TryGetValue(input.RootOutputItemId, out List<CraftingChainRecipeInfo> rootOptions)
            ? BuildRecipeOptions(rootOptions) : Array.Empty<CraftingChainRecipeOption>();
        long availableRoot = ConsumeSupplies(context, state, input.RootMatchingSupplyIndexes, input.RootOutputItemId, wanted, forgeOnly: false);
        plan.AvailableOutput = SafeCount(availableRoot);
        long remaining = Math.Max(0L, wanted - availableRoot);
        if (remaining > 0)
        {
            HashSet<int> ancestors = new HashSet<int> { input.RootOutputItemId };
            CraftingChainPlanNode rootNode = BuildRecipeNode(context, state, rootRecipe, remaining, ancestors, 0, selectedRoot: true);
            if (rootNode != null) plan.Nodes.Add(rootNode);
        }
        plan.PlannedOutput = SafeCount((long)plan.AvailableOutput + (plan.Nodes.Count == 0 ? 0 : plan.Nodes[0].OutputCount));
        plan.Materials = state.Materials.Values.OrderBy(material => GetItemName(input, material.ItemId), StringComparer.CurrentCulture)
            .ThenBy(material => material.ForgeOnly).ToList();
        plan.ReservedSupplies = Enumerable.Range(0, input.Supplies.Count)
            .Select(index => new
            {
                Supply = input.Supplies[index],
                Consumed = Math.Max(0L, input.Supplies[index].Count - state.RemainingSupplies[index])
            })
            .Where(entry => entry.Consumed > 0)
            .GroupBy(entry => new { entry.Supply.ItemId, ForgeOnly = entry.Supply.Source == CraftingChainSupplySource.Forge })
            .Select(group => new CraftingChainPlanMaterial
            {
                ItemId = group.Key.ItemId,
                Required = group.Sum(entry => entry.Consumed),
                Available = group.Sum(entry => entry.Supply.Count),
                ForgeOnly = group.Key.ForgeOnly
            }).ToList();
        if (state.LimitExceeded || plan.Materials.Count > 24)
        {
            plan.Error = "PlanTooLarge";
            NetworkLog.Debug("Crafting-chain plan exceeded a safe view limit. nodes=" + state.CreatedNodes + "; materials=" + plan.Materials.Count
                + "; nodeLimit=" + MaximumGraphNodes + "; materialLimit=24");
            return plan;
        }
        plan.MissingMaterials = plan.Materials.Sum(material => Math.Max(0L, material.Required - material.Available));
        NetworkLog.Debug("Crafting-chain graph planned. rootRecipe=" + input.RootRecipeKey + "; requested=" + plan.RequestedOutput
            + "; plannedCraftNodes=" + state.CreatedNodes + "; baseMaterials=" + plan.Materials.Count + "; missing=" + plan.MissingMaterials);
        return plan;
    }

    private static CraftingChainPlanNode ResolveDemand(PlannerContext context, PlannerState state, CraftingChainIngredientInfo demand,
        long requested, HashSet<int> ancestors, int depth)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (requested <= 0) return null;
        if (depth >= MaximumGraphDepth || state.CreatedNodes >= MaximumGraphNodes)
        {
            state.LimitExceeded = true;
            AddMaterial(context, state, demand, requested, demand.ForgeOnly);
            return null;
        }

        List<CraftingChainRecipeInfo> candidates = context.RecipesByOutput.TryGetValue(demand.ItemId, out List<CraftingChainRecipeInfo> recipes)
            ? recipes : new List<CraftingChainRecipeInfo>();
        if (demand.ForgeOnly || candidates.Count == 0 || ancestors.Contains(demand.ItemId))
        {
            ConsumeSupplies(context, state, demand.MatchingSupplyIndexes, demand.ItemId, requested, demand.ForgeOnly);
            AddMaterial(context, state, demand, requested, demand.ForgeOnly);
            return null;
        }

        long fromStock = ConsumeSupplies(context, state, demand.MatchingSupplyIndexes, demand.ItemId, requested, forgeOnly: false);
        long remaining = Math.Max(0L, requested - fromStock);
        if (remaining == 0) return null;

        string preferredKey = context.Input.SelectedRecipeKeys.TryGetValue(demand.ItemId, out string key) ? key : null;
        if (!string.IsNullOrEmpty(preferredKey))
        {
            CraftingChainRecipeInfo preferred = candidates.FirstOrDefault(recipe => string.Equals(recipe.Key, preferredKey, StringComparison.Ordinal));
            if (preferred != null) candidates = new List<CraftingChainRecipeInfo> { preferred };
        }

        PlannerState bestState = null;
        CraftingChainPlanNode bestNode = null;
        long bestScore = long.MaxValue;
        foreach (CraftingChainRecipeInfo candidate in candidates)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (++context.CandidateTrials > MaximumCandidateTrials) break;
            PlannerState trial = state.Clone();
            HashSet<int> nextAncestors = new HashSet<int>(ancestors) { demand.ItemId };
            CraftingChainPlanNode node = BuildRecipeNode(context, trial, candidate, remaining, nextAncestors, depth, selectedRoot: false);
            long score = CalculateShortage(trial.Materials);
            if (bestState == null || score < bestScore || score == bestScore && string.CompareOrdinal(candidate.Key, bestNode?.RecipeKey) < 0)
            {
                bestState = trial;
                bestNode = node;
                bestScore = score;
            }
        }
        if (bestState == null)
        {
            AddMaterial(context, state, demand, requested, forgeOnly: false);
            return null;
        }
        state.CopyFrom(bestState);
        return bestNode;
    }

    private static CraftingChainPlanNode BuildRecipeNode(PlannerContext context, PlannerState state,
        CraftingChainRecipeInfo recipe, long requiredOutput, HashSet<int> ancestors, int depth, bool selectedRoot)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (recipe == null || requiredOutput <= 0) return null;
        if (state.CreatedNodes >= MaximumGraphNodes)
        {
            state.LimitExceeded = true;
            return null;
        }
        int batches = SafeCount((requiredOutput + Math.Max(1, recipe.OutputCount) - 1) / Math.Max(1, recipe.OutputCount));
        long actualOutput = (long)batches * Math.Max(1, recipe.OutputCount);
        long surplus = Math.Max(0L, actualOutput - requiredOutput);
        if (recipe.OutputItemId >= 0 && recipe.OutputItemId < state.Produced.Length && surplus > 0)
            state.Produced[recipe.OutputItemId] = SafeAdd(state.Produced[recipe.OutputItemId], surplus);

        CraftingChainPlanNode node = new CraftingChainPlanNode
        {
            ItemId = recipe.OutputItemId,
            RecipeKey = recipe.Key,
            RecipeName = recipe.Name,
            CraftingArea = recipe.CraftingArea,
            Batches = batches,
            OutputCount = SafeCount(actualOutput),
            RecipeOptions = context.RecipesByOutput.TryGetValue(recipe.OutputItemId, out List<CraftingChainRecipeInfo> options)
                ? BuildRecipeOptions(options) : Array.Empty<CraftingChainRecipeOption>()
        };
        state.CreatedNodes++;
        foreach (CraftingChainIngredientInfo ingredient in recipe.Ingredients)
        {
            long ingredientCount = (long)ingredient.Count * batches;
            if (ingredientCount <= 0) continue;
            CraftingChainPlanNode child = ResolveDemand(context, state, ingredient, ingredientCount, ancestors, depth + 1);
            if (child != null) node.Children.Add(child);
            node.DisplayChildren.Add(new CraftingChainPlanDisplayChild
            {
                ItemId = ingredient.ItemId,
                RequiredCount = ingredientCount,
                ForgeOnly = ingredient.ForgeOnly,
                RecipeNode = child
            });
        }
        return node;
    }

    private static long ConsumeSupplies(PlannerContext context, PlannerState state, int[] indexes, int itemId, long requested, bool forgeOnly)
    {
        if (requested <= 0) return 0;
        long taken = 0;
        if (indexes != null)
        {
            foreach (int index in indexes.Where(index => index >= 0 && index < context.Input.Supplies.Count)
                         .OrderBy(index => SourcePriority(context.Input.Supplies[index].Source, forgeOnly)))
            {
                if (index < 0 || index >= state.RemainingSupplies.Length) continue;
                CraftingChainSupplyInfo supply = context.Input.Supplies[index];
                if (forgeOnly != (supply.Source == CraftingChainSupplySource.Forge)) continue;
                if (!forgeOnly && supply.Source == CraftingChainSupplySource.Forge) continue;
                long available = state.RemainingSupplies[index];
                if (available <= 0) continue;
                long amount = Math.Min(requested - taken, available);
                state.RemainingSupplies[index] -= amount;
                taken += amount;
                if (taken >= requested) break;
            }
        }

        if (!forgeOnly && taken < requested && indexes != null)
        {
            if (itemId >= 0 && itemId < state.Produced.Length)
            {
                long fromProduced = Math.Min(requested - taken, state.Produced[itemId]);
                state.Produced[itemId] -= fromProduced;
                taken += fromProduced;
            }
        }
        return taken;
    }

    private static void AddMaterial(PlannerContext context, PlannerState state, CraftingChainIngredientInfo requirement, long count, bool forgeOnly)
    {
        if (count <= 0) return;
        Tuple<int, bool> key = Tuple.Create(requirement.ItemId, forgeOnly);
        if (!state.Materials.TryGetValue(key, out CraftingChainPlanMaterial material))
        {
            long available = 0;
            if (context.Input.MatchingSuppliesByItem.TryGetValue(requirement.ItemId, out int[] indexes))
                foreach (int index in indexes)
                {
                    CraftingChainSupplyInfo supply = context.Input.Supplies[index];
                    if (forgeOnly != (supply.Source == CraftingChainSupplySource.Forge)) continue;
                    if (!forgeOnly && supply.Source == CraftingChainSupplySource.Forge) continue;
                    available = SafeAdd(available, supply.Count);
                }
            material = new CraftingChainPlanMaterial { ItemId = requirement.ItemId, Available = available, ForgeOnly = forgeOnly };
            state.Materials[key] = material;
        }
        material.Required = SafeAdd(material.Required, count);
    }

    private static long CalculateShortage(Dictionary<Tuple<int, bool>, CraftingChainPlanMaterial> materials)
    {
        long shortage = 0;
        foreach (CraftingChainPlanMaterial material in materials.Values)
            shortage = SafeAdd(shortage, Math.Max(0L, material.Required - material.Available));
        return shortage;
    }

    private static CraftingChainRecipeOption[] BuildRecipeOptions(IEnumerable<CraftingChainRecipeInfo> recipes)
    {
        return recipes.Select(recipe => new CraftingChainRecipeOption
        {
            Key = recipe.Key,
            CraftingArea = recipe.CraftingArea,
            Ingredients = recipe.Ingredients.Select(ingredient => new CraftingChainRecipeOptionIngredient
            {
                ItemId = ingredient.ItemId,
                Count = ingredient.Count
            }).ToArray()
        }).ToArray();
    }

    private static bool IsActiveForgeRecipe(Recipe recipe)
    {
        if (!string.Equals(recipe.craftingArea, "forge", StringComparison.OrdinalIgnoreCase)) return true;
        if (XUiM_Recipes.DisableSmelter && recipe.tags.Test_AnySet(XUiM_Recipes.useSmelterTag)) return false;
        if (!XUiM_Recipes.DisableSmelter && recipe.tags.Test_AnySet(XUiM_Recipes.replaceSmelterTag)) return false;
        return true;
    }

    private static int SourcePriority(CraftingChainSupplySource source, bool forgeOnly)
    {
        if (forgeOnly) return source == CraftingChainSupplySource.Forge ? 0 : 1;
        return source == CraftingChainSupplySource.Player ? 0 : source == CraftingChainSupplySource.Network ? 1 : 2;
    }

    private static void CaptureGrid(ItemStackGrid grid, Action<ItemValue, long, CraftingChainSupplySource> addSupply)
    {
        if (grid == null) return;
        for (int index = 0; index < grid.Length; index++)
        {
            ItemStack stack = grid[index];
            if (stack == null || stack.IsEmpty()) continue;
            addSupply(stack.itemValue, stack.count, CraftingChainSupplySource.Player);
        }
    }

    private static void CaptureAccessibleForgeSupplies(
        IDictionary<string, List<CraftingChainWorkstationPosition>> workstationMap,
        Action<ItemValue, long, CraftingChainSupplySource> addSupply)
    {
        if (workstationMap == null || !workstationMap.TryGetValue("forge", out List<CraftingChainWorkstationPosition> forges)
            || forges.Count == 0) return;
        TileEntityWorkstation forge = CraftingChainWorkstationService.Resolve(forges[0]);
        foreach (ItemStack input in forge?.Input ?? Array.Empty<ItemStack>())
            if (input != null && !input.IsEmpty()) addSupply(input.itemValue, input.count, CraftingChainSupplySource.Forge);
        NetworkLog.Debug("Crafting-chain forge snapshot captured. accessibleLoadedForges=" + (forge == null ? 0 : 1)
            + "; position=" + (forge == null ? "<missing>" : forges[0].X + "," + forges[0].Y + "," + forges[0].Z));
    }

    internal static int GetIngredientCount(XUi xui, EntityPlayerLocal player, Recipe recipe, ItemStack ingredient, int tier)
    {
        int count = ingredient.count;
        if (!recipe.UseIngredientModifier) return count;
        try
        {
            count = (int)EffectManager.GetValue(PassiveEffects.CraftingIngredientCount, null, count, player, recipe,
                FastTags<TagGroup.Global>.Parse(ingredient.itemValue.ItemClass.GetItemName()),
                calcEquipment: true, calcHoldingItem: true, calcProgression: true, calcBuffs: true, calcChallenges: true, tier);
            if (count > 0)
            {
                count = (int)(count * XUiM_Recipes.GetCraftingInputModifier(recipe));
                if (XUiM_Recipes.CraftingInputModifier > 0f) count = Math.Max(1, count);
            }
        }
        catch (Exception ex)
        {
            NetworkLog.Debug("Crafting-chain ingredient modifier snapshot failed; using recipe count. recipe=" + recipe.GetName()
                + "; item=" + ingredient.itemValue.type + "; exception=" + ex.Message);
            count = ingredient.count;
        }
        return Math.Max(0, count);
    }

    private static ItemValue CreateOutputValue(int type, int quality)
    {
        ItemClass itemClass = ItemClass.GetForId(type);
        return itemClass != null && itemClass.HasQuality && quality > 0
            ? new ItemValue(type, quality, quality)
            : new ItemValue(type);
    }

    internal static string BuildRecipeKey(Recipe recipe, int outputQuality)
    {
        if (recipe == null) return string.Empty;
        string ingredients = string.Join(",", recipe.ingredients.Where(item => item != null && !item.IsEmpty())
            .Select(item => item.itemValue.type + ":" + item.itemValue.Quality + ":" + item.count));
        return recipe.itemValueType + ":" + outputQuality + ":" + recipe.count + ":" + (recipe.craftingArea ?? string.Empty)
            + ":" + (recipe.GetName() ?? string.Empty) + ":" + ingredients;
    }

    private static int SafeCount(long count) => (int)Math.Max(0L, Math.Min(int.MaxValue, count));
    private static long SafeAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
    private static string GetItemName(CraftingChainPlanInput input, int itemId)
        => itemId >= 0 && itemId < input.Items.Count ? input.Items[itemId].Name : itemId.ToString();
}
