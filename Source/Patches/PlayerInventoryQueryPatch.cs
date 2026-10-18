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
        if (_itemValue.Quality > 0)
        {
            long exactPhysical = 0;
            foreach (ItemStack stack in __instance.GetAllItemStacks())
                if (stack != null && !stack.IsEmpty() && NetworkItemMatcher.Matches(stack.itemValue, _itemValue)) exactPhysical += stack.count;
            __result = NetworkItemMatcher.ClampCount(exactPhysical);
        }
        __result = NetworkItemMatcher.AddClamped(__result, NetworkStorage.GetMatchingCount(__instance.localPlayer, _itemValue));
    }

    [HarmonyPostfix, HarmonyPatch(nameof(XUiM_PlayerInventory.GetItemCountWithMods))]
    private static void CountWithMods(XUiM_PlayerInventory __instance, ItemValue _itemValue, ref int __result)
    {
        if (_itemValue == null || !(bool)__instance.localPlayer) return;
        if (_itemValue.Quality > 0)
        {
            long exactPhysical = 0;
            foreach (ItemStack stack in __instance.GetAllItemStacks())
                if (stack != null && !stack.IsEmpty() && NetworkItemMatcher.Matches(stack.itemValue, _itemValue, includeModded: true)) exactPhysical += stack.count;
            __result = NetworkItemMatcher.ClampCount(exactPhysical);
        }
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
        if (_itemStacks == null || _multiplier <= 0 || !(bool)__instance.localPlayer) return;
        bool exactQuality = false;
        foreach (ItemStack stack in _itemStacks)
            if (stack?.itemValue?.Quality > 0) { exactQuality = true; break; }
        bool overflowRisk = false;
        foreach (ItemStack stack in _itemStacks)
            if (stack != null && stack.count > int.MaxValue / _multiplier) { overflowRisk = true; break; }
        if (__result && !exactQuality && !overflowRisk) return;
        List<StoredEntry> physical = new List<StoredEntry>();
        foreach (ItemStack carried in __instance.GetAllItemStacks())
            if (carried != null && !carried.IsEmpty()) physical.Add(new StoredEntry { Value = carried.itemValue, Count = carried.count });
        List<StoredEntry> network = NetworkStorage.GetEntriesForRequirements(__instance.localPlayer, _itemStacks);
        foreach (ItemStack stack in _itemStacks)
        {
            if (stack == null || stack.IsEmpty()) continue;
            long needed = (long)stack.count * _multiplier;
            foreach (StoredEntry source in physical)
            {
                if (needed == 0) break;
                if (source.Count <= 0 || !NetworkItemMatcher.Matches(source.Value, stack.itemValue)) continue;
                long take = Math.Min(needed, source.Count);
                source.Count -= take;
                needed -= take;
            }
            foreach (StoredEntry source in network)
            {
                if (needed == 0) break;
                if (source.Count <= 0 || !NetworkItemMatcher.Matches(source.Value, stack.itemValue)) continue;
                long take = Math.Min(needed, source.Count);
                source.Count -= take;
                needed -= take;
            }
            if (needed > 0) { __result = false; return; }
        }
        __result = true;
    }
}
