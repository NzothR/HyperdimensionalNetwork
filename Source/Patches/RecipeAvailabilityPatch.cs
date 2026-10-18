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
        if (__result || _ea is not EntityPlayerLocal player) return;
        int tier = __instance.GetCraftingTier(player);
        if (_craftingTier >= 0 && _craftingTier < tier) tier = _craftingTier;
        __result = HasIngredients(__instance, _itemStack, player, tier);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(Recipe.CanCraftAny), new Type[] { typeof(IList<ItemStack>), typeof(EntityAlive) })]
    private static void CanCraftAny(Recipe __instance, IList<ItemStack> _itemStack, EntityAlive _ea, ref bool __result)
    {
        if (__result || _ea is not EntityPlayerLocal player) return;
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
            long available = NetworkStorage.GetMatchingCount(player, ingredient.itemValue);
            foreach (ItemStack stack in source)
            {
                if (stack == null || stack.IsEmpty() || !NetworkItemMatcher.Matches(stack.itemValue, ingredient.itemValue)) continue;
                available += stack.count;
                if (available >= needed) break;
            }
            if (available < needed) return false;
        }
        return true;
    }
}
