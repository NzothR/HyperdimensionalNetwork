using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_RecipeCraftCount), nameof(XUiC_RecipeCraftCount.calcMaxCraftable))]
internal static class RecipeCraftCountPatch
{
    [HarmonyPostfix]
    private static void Postfix(XUiC_RecipeCraftCount __instance, ref int __result)
    {
        Recipe recipe = __instance?.recipe;
        EntityPlayerLocal player = __instance?.xui?.playerUI?.entityPlayer;
        if (recipe?.ingredients == null || !(bool)player) return;
        if (XUiM_Recipes.GetCraftingInputModifier(recipe) == 0f) { __result = 10000; return; }
        XUiC_WorkstationInputGrid input = __instance.windowGroup?.Controller?.GetChildByType<XUiC_WorkstationInputGrid>();
        IList<ItemStack> source = input != null ? input.GetSlots() : __instance.xui.PlayerInventory.GetAllItemStacks();
        int tier = recipe.craftingTier == -1 ? recipe.GetCraftingTier(player) : recipe.craftingTier;
        long max = 10000;
        bool counted = false;
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
            if (required <= 0) continue;
            counted = true;
            long available = NetworkStorage.GetMatchingCount(player, ingredient.itemValue);
            foreach (ItemStack stack in source)
                if (stack != null && !stack.IsEmpty() && NetworkItemMatcher.Matches(stack.itemValue, ingredient.itemValue)) available += stack.count;
            max = Math.Min(max, available / required);
            if (max == 0) break;
        }
        if (!counted) return;
        __result = Math.Max(__result, (int)Math.Min(max, 10000));
    }
}
