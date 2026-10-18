using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_RecipeCraftCount), nameof(XUiC_RecipeCraftCount.calcMaxCraftable))]
internal static class RecipeCraftCountPatch
{
    private sealed class Requirement
    {
        public ItemValue Value;
        public int PerCraft;
    }

    [HarmonyPostfix]
    private static void Postfix(XUiC_RecipeCraftCount __instance, ref int __result)
    {
        Recipe recipe = __instance?.recipe;
        if (string.Equals(recipe?.craftingArea, "forge", StringComparison.OrdinalIgnoreCase)) return;
        EntityPlayerLocal player = __instance?.xui?.playerUI?.entityPlayer;
        if (recipe?.ingredients == null || !(bool)player) return;
        if (XUiM_Recipes.GetCraftingInputModifier(recipe) == 0f) { __result = 10000; return; }
        XUiC_WorkstationInputGrid input = __instance.windowGroup?.Controller?.GetChildByType<XUiC_WorkstationInputGrid>();
        IList<ItemStack> source = input != null ? input.GetSlots() : __instance.xui.PlayerInventory.GetAllItemStacks();
        int tier = recipe.craftingTier == -1 ? recipe.GetCraftingTier(player) : recipe.craftingTier;
        List<Requirement> requirements = new List<Requirement>();
        foreach (ItemStack ingredient in recipe.ingredients)
        {
            if (ingredient == null || ingredient.IsEmpty()) continue;
            int required = ingredient.count;
            if (recipe.UseIngredientModifier)
            {
                required = (int)EffectManager.GetValue(PassiveEffects.CraftingIngredientCount, null, required, player, recipe,
                    FastTags<TagGroup.Global>.Parse(ingredient.itemValue.ItemClass.GetItemName()),
                    calcEquipment: true, calcHoldingItem: true, calcProgression: true, calcBuffs: true, calcChallenges: true, tier);
                if (required > 0)
                {
                    required = (int)(required * XUiM_Recipes.GetCraftingInputModifier(recipe));
                    if (XUiM_Recipes.CraftingInputModifier > 0f) required = Math.Max(1, required);
                }
            }
            if (required > 0) requirements.Add(new Requirement { Value = ingredient.itemValue, PerCraft = required });
        }
        if (requirements.Count == 0) return;
        List<StoredEntry> physical = new List<StoredEntry>();
        foreach (ItemStack stack in source)
            if (stack != null && !stack.IsEmpty()) physical.Add(new StoredEntry { Value = stack.itemValue, Count = stack.count });
        List<StoredEntry> network = NetworkStorage.GetEntriesForRequirements(player, recipe.ingredients);
        int low = 0, high = 10000;
        while (low < high)
        {
            int mid = low + (high - low + 1) / 2;
            if (CanCraft(mid, requirements, physical, network)) low = mid;
            else high = mid - 1;
        }
        bool exactQuality = false;
        foreach (Requirement requirement in requirements)
            if (requirement.Value.Quality > 0) { exactQuality = true; break; }
        __result = exactQuality ? Math.Max(1, low) : Math.Max(__result, low);
    }

    private static bool CanCraft(int count, List<Requirement> requirements, List<StoredEntry> physical, List<StoredEntry> network)
    {
        long[] physicalRemaining = new long[physical.Count];
        long[] networkRemaining = new long[network.Count];
        for (int i = 0; i < physical.Count; i++) physicalRemaining[i] = physical[i].Count;
        for (int i = 0; i < network.Count; i++) networkRemaining[i] = network[i].Count;
        foreach (Requirement requirement in requirements)
        {
            long needed = (long)requirement.PerCraft * count;
            for (int i = 0; i < physical.Count && needed > 0; i++)
            {
                if (physicalRemaining[i] == 0 || !NetworkItemMatcher.Matches(physical[i].Value, requirement.Value)) continue;
                long take = Math.Min(needed, physicalRemaining[i]);
                physicalRemaining[i] -= take;
                needed -= take;
            }
            for (int i = 0; i < network.Count && needed > 0; i++)
            {
                if (networkRemaining[i] == 0 || !NetworkItemMatcher.Matches(network[i].Value, requirement.Value)) continue;
                long take = Math.Min(needed, networkRemaining[i]);
                networkRemaining[i] -= take;
                needed -= take;
            }
            if (needed > 0) return false;
        }
        return true;
    }
}
