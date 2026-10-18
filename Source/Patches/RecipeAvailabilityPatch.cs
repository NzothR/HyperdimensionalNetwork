using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(Recipe))]
internal static class RecipeAvailabilityPatch
{
    [HarmonyPostfix, HarmonyPatch(nameof(Recipe.CanCraft), new Type[] { typeof(IList<ItemStack>), typeof(EntityAlive), typeof(int) })]
    private static void CanCraft(Recipe __instance, IList<ItemStack> _itemStack, EntityAlive _ea, int _craftingTier, ref bool __result)
    {
        if (string.Equals(__instance?.craftingArea, "forge", StringComparison.OrdinalIgnoreCase)) return;
        if (_ea is not EntityPlayerLocal player || (__result && !RequiresExactQuality(__instance))) return;
        int tier = __instance.GetCraftingTier(player);
        if (_craftingTier >= 0 && _craftingTier < tier) tier = _craftingTier;
        __result = HasIngredients(__instance, _itemStack, player, tier);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(Recipe.CanCraftAny), new Type[] { typeof(IList<ItemStack>), typeof(EntityAlive) })]
    private static void CanCraftAny(Recipe __instance, IList<ItemStack> _itemStack, EntityAlive _ea, ref bool __result)
    {
        if (string.Equals(__instance?.craftingArea, "forge", StringComparison.OrdinalIgnoreCase)) return;
        if (_ea is not EntityPlayerLocal player || (__result && !RequiresExactQuality(__instance))) return;
        __result = false;
        for (int tier = __instance.GetCraftingTier(player); tier >= 0; tier--)
        {
            if (!HasIngredients(__instance, _itemStack, player, tier)) continue;
            __result = true;
            return;
        }
    }

    private static bool HasIngredients(Recipe recipe, IList<ItemStack> source, EntityPlayerLocal player, int tier)
    {
        if (recipe?.ingredients == null || source == null) return false;
        List<StoredEntry> physical = new List<StoredEntry>();
        foreach (ItemStack stack in source)
            if (stack != null && !stack.IsEmpty()) physical.Add(new StoredEntry { Value = stack.itemValue, Count = stack.count });
        List<StoredEntry> network = NetworkStorage.GetEntriesForRequirements(player, recipe.ingredients);
        foreach (ItemStack ingredient in recipe.ingredients)
        {
            if (ingredient == null || ingredient.IsEmpty()) continue;
            int needed = ingredient.count;
            if (recipe.UseIngredientModifier)
            {
                needed = (int)EffectManager.GetValue(PassiveEffects.CraftingIngredientCount, null, needed, player, recipe,
                    FastTags<TagGroup.Global>.Parse(ingredient.itemValue.ItemClass.GetItemName()),
                    calcEquipment: true, calcHoldingItem: true, calcProgression: true, calcBuffs: true, calcChallenges: true, tier);
                if (needed > 0)
                {
                    needed = (int)(needed * XUiM_Recipes.GetCraftingInputModifier(recipe));
                    if (XUiM_Recipes.CraftingInputModifier > 0f) needed = Math.Max(1, needed);
                }
            }
            if (needed <= 0) continue;
            long remaining = needed;
            foreach (StoredEntry entry in physical)
            {
                if (remaining == 0) break;
                if (entry.Count <= 0 || !NetworkItemMatcher.Matches(entry.Value, ingredient.itemValue)) continue;
                long take = Math.Min(remaining, entry.Count);
                entry.Count -= take;
                remaining -= take;
            }
            foreach (StoredEntry entry in network)
            {
                if (remaining == 0) break;
                if (entry.Count <= 0 || !NetworkItemMatcher.Matches(entry.Value, ingredient.itemValue)) continue;
                long take = Math.Min(remaining, entry.Count);
                entry.Count -= take;
                remaining -= take;
            }
            if (remaining > 0) return false;
        }
        return true;
    }

    private static bool RequiresExactQuality(Recipe recipe)
    {
        if (recipe?.ingredients == null) return false;
        foreach (ItemStack ingredient in recipe.ingredients)
            if (ingredient?.itemValue?.Quality > 0) return true;
        return false;
    }
}
