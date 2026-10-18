using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiM_PlayerInventory))]
internal static class PlayerInventoryQueryPatch
{
    [HarmonyPostfix, HarmonyPatch(nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(ItemValue) })]
    private static void CountValue(XUiM_PlayerInventory __instance, ItemValue _itemValue, ref int __result)
    {
        if (_itemValue == null || !(bool)__instance.localPlayer) return;
        __result = NetworkItemMatcher.AddClamped(__result, NetworkStorage.GetMatchingCount(__instance.localPlayer, _itemValue));
    }

    [HarmonyPostfix, HarmonyPatch(nameof(XUiM_PlayerInventory.GetItemCountWithMods))]
    private static void CountWithMods(XUiM_PlayerInventory __instance, ItemValue _itemValue, ref int __result)
    {
        if (_itemValue == null || !(bool)__instance.localPlayer) return;
        __result = NetworkItemMatcher.AddClamped(__result, NetworkStorage.GetMatchingCount(__instance.localPlayer, _itemValue, includeModded: true));
    }

    [HarmonyPostfix, HarmonyPatch(nameof(XUiM_PlayerInventory.GetItemCount), new Type[] { typeof(int) })]
    private static void CountType(XUiM_PlayerInventory __instance, int _itemId, ref int __result)
    {
        if (_itemId <= 0 || !(bool)__instance.localPlayer) return;
        __result = NetworkItemMatcher.AddClamped(__result, NetworkStorage.GetMatchingCount(__instance.localPlayer, new ItemValue(_itemId)));
    }

    [HarmonyPostfix, HarmonyPatch(nameof(XUiM_PlayerInventory.HasItems))]
    private static void HasItems(XUiM_PlayerInventory __instance, IList<ItemStack> _itemStacks, int _multiplier, ref bool __result)
    {
        if (__result || _itemStacks == null || _multiplier <= 0 || !(bool)__instance.localPlayer) return;
        foreach (ItemStack stack in _itemStacks)
        {
            if (stack == null || stack.IsEmpty()) continue;
            long needed = (long)stack.count * _multiplier;
            long physical = (long)__instance.backpack.GetItemCount(stack.itemValue) + __instance.toolbelt.GetItemCount(stack.itemValue);
            long network = NetworkStorage.GetMatchingCount(__instance.localPlayer, stack.itemValue);
            if (physical + network < needed) return;
        }
        __result = true;
    }
}
