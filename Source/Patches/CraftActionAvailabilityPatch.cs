using System;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(ItemActionEntryCraft), nameof(ItemActionEntryCraft.hasItems))]
internal static class CraftActionAvailabilityPatch
{
    [HarmonyPostfix]
    private static void Postfix(ItemActionEntryCraft __instance, XUi _xui, Recipe _recipe, ref bool __result)
    {
        if (__result || _recipe?.ingredients == null || _xui?.playerUI?.entityPlayer == null) return;
        bool hasQualityIngredient = false;
        foreach (ItemStack ingredient in _recipe.ingredients)
            if (ingredient?.itemValue?.HasQuality == true) { hasQualityIngredient = true; break; }
        if (!hasQualityIngredient) return;

        EntityPlayerLocal player = _xui.playerUI.entityPlayer;
        var list = __instance.tempIngredientList;
        list.Clear();
        float inputModifier = XUiM_Recipes.GetCraftingInputModifier(_recipe);
        if (inputModifier == 0f) { __result = true; return; }
        foreach (ItemStack ingredient in _recipe.ingredients)
        {
            int count = ingredient.count;
            if (_recipe.UseIngredientModifier)
            {
                count = (int)EffectManager.GetValue(PassiveEffects.CraftingIngredientCount, null, count, player, _recipe,
                    FastTags<TagGroup.Global>.Parse(ingredient.itemValue.ItemClass.GetItemName()),
                    calcEquipment: true, calcHoldingItem: true, calcProgression: true, calcBuffs: true,
                    calcChallenges: true, __instance.craftingTier);
                if (count > 0)
                {
                    count = (int)(count * inputModifier);
                    if (XUiM_Recipes.CraftingInputModifier > 0f) count = Math.Max(1, count);
                }
            }
            list.Add(new ItemStack(ingredient.itemValue.Clone(), count));
            if (!ingredient.itemValue.HasQuality) continue;
            long available = NetworkStorage.GetMatchingCount(player, ingredient.itemValue);
            foreach (ItemStack stack in _xui.PlayerInventory.GetAllItemStacks())
                if (stack != null && !stack.IsEmpty() && NetworkItemMatcher.Matches(stack.itemValue, ingredient.itemValue)) available += stack.count;
            if (available < count) return;
        }
        __result = _xui.PlayerInventory.HasItems(list, __instance.craftCountControl.Count);
        XUiC_WorkstationInputGrid input = __instance.craftCountControl.WindowGroup.Controller.GetChildByType<XUiC_WorkstationInputGrid>();
        if (input != null) __result |= input.HasItems(list, __instance.craftCountControl.Count);
        NetworkLog.Debug("Quality recipe availability recalculated with network. result=" + __result + "; ingredients=" + list.Count);
    }
}
