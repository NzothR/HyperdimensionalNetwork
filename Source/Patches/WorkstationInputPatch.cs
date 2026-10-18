using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_WorkstationInputGrid))]
internal static class WorkstationInputPatch
{
    private sealed class SlotWithdrawal
    {
        public int Index;
        public ItemStack Original;
        public int Remaining;
        public int Take;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(XUiC_WorkstationInputGrid.GetItemCount))]
    private static void Count(XUiC_WorkstationInputGrid __instance, ItemValue _itemValue, ref int __result)
    {
        if (__instance is XUiC_WorkstationMaterialInputGrid) return;
        EntityPlayer player = __instance.xui?.playerUI?.entityPlayer;
        if (!(bool)player || _itemValue == null) return;
        if (_itemValue.Quality > 0)
        {
            long exactPhysical = 0;
            foreach (ItemStack stack in __instance.GetSlots())
                if (stack != null && !stack.IsEmpty() && NetworkItemMatcher.Matches(stack.itemValue, _itemValue)) exactPhysical += stack.count;
            __result = NetworkItemMatcher.ClampCount(exactPhysical);
        }
        __result = NetworkItemMatcher.AddClamped(__result, NetworkStorage.GetMatchingCount(player, _itemValue));
    }

    [HarmonyPostfix, HarmonyPatch(nameof(XUiC_WorkstationInputGrid.HasItems))]
    private static void HasItems(XUiC_WorkstationInputGrid __instance, IList<ItemStack> _itemStacks, int _multiplier, ref bool __result)
    {
        if (__instance is XUiC_WorkstationMaterialInputGrid) return;
        EntityPlayer player = __instance.xui?.playerUI?.entityPlayer;
        if (!(bool)player || _itemStacks == null || _multiplier <= 0) return;
        bool exactQuality = false;
        bool overflowRisk = false;
        foreach (ItemStack stack in _itemStacks)
        {
            if (stack?.itemValue?.Quality > 0) exactQuality = true;
            if (stack != null && stack.count > int.MaxValue / _multiplier) overflowRisk = true;
        }
        if (__result && !exactQuality && !overflowRisk) return;
        List<StoredEntry> physical = new List<StoredEntry>();
        foreach (ItemStack stack in __instance.GetSlots())
            if (stack != null && !stack.IsEmpty()) physical.Add(new StoredEntry { Value = stack.itemValue, Count = stack.count });
        List<StoredEntry> network = NetworkStorage.GetEntriesForRequirements(player, _itemStacks);
        foreach (ItemStack required in _itemStacks)
        {
            if (required == null || required.IsEmpty()) continue;
            long needed = (long)required.count * _multiplier;
            foreach (StoredEntry source in physical)
            {
                if (needed == 0) break;
                if (source.Count <= 0 || !NetworkItemMatcher.Matches(source.Value, required.itemValue)) continue;
                long take = Math.Min(needed, source.Count);
                source.Count -= take;
                needed -= take;
            }
            foreach (StoredEntry source in network)
            {
                if (needed == 0) break;
                if (source.Count <= 0 || !NetworkItemMatcher.Matches(source.Value, required.itemValue)) continue;
                long take = Math.Min(needed, source.Count);
                source.Count -= take;
                needed -= take;
            }
            if (needed > 0) { __result = false; return; }
        }
        __result = true;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(XUiC_WorkstationInputGrid.RemoveItems))]
    private static bool Remove(XUiC_WorkstationInputGrid __instance, IList<ItemStack> _itemStacks, int _multiplier, IList<ItemStack> _removedItems)
    {
        if (__instance is XUiC_WorkstationMaterialInputGrid) return true;
        EntityPlayer player = __instance.xui?.playerUI?.entityPlayer;
        if (!(bool)player || _itemStacks == null || _multiplier <= 0) return true;
        ItemStack[] source = __instance.GetSlots();
        List<SlotWithdrawal> slots = new List<SlotWithdrawal>();
        for (int index = 0; index < source.Length; index++)
        {
            ItemStack stack = source[index];
            if (stack == null || stack.IsEmpty()) continue;
            slots.Add(new SlotWithdrawal { Index = index, Original = stack.Clone(), Remaining = stack.count });
        }
        List<StoredEntry> network = null;
        List<StorageAddition> networkRequests = new List<StorageAddition>();
        bool exactQuality = false;
        foreach (ItemStack required in _itemStacks)
        {
            if (required == null || required.IsEmpty()) continue;
            if (required.itemValue.Quality > 0) exactQuality = true;
            long needed = (long)required.count * _multiplier;
            if (needed > int.MaxValue)
            {
                NetworkLog.Error("Workstation material consumption blocked: requirement exceeds int32. item=" + required.itemValue.type + "; count=" + needed);
                return false;
            }
            foreach (SlotWithdrawal slot in slots)
            {
                if (needed == 0) break;
                if (slot.Remaining == 0 || !NetworkItemMatcher.Matches(slot.Original.itemValue, required.itemValue)) continue;
                int take = (int)Math.Min(needed, slot.Remaining);
                slot.Remaining -= take;
                slot.Take += take;
                needed -= take;
            }
            if (needed == 0) continue;
            if (network == null) network = NetworkStorage.GetEntriesForRequirements(player, _itemStacks);
            foreach (StoredEntry entry in network)
            {
                if (needed == 0) break;
                if (entry.Count == 0 || !NetworkItemMatcher.Matches(entry.Value, required.itemValue)) continue;
                long take = Math.Min(needed, entry.Count);
                entry.Count -= take;
                needed -= take;
                networkRequests.Add(new StorageAddition { Value = entry.Value.Clone(), Count = take });
            }
            if (needed > 0)
            {
                NetworkLog.Error("Workstation material consumption blocked: exact material unavailable. item=" + required.itemValue.type + "; quality=" + required.itemValue.Quality + "; missing=" + needed);
                return false;
            }
        }
        if (networkRequests.Count == 0 && !exactQuality) return true;
        if (networkRequests.Count > 0 && !NetworkStorage.RemoveBatch(player, networkRequests))
        {
            NetworkLog.Error("Workstation material consumption blocked: network batch commit failed. variants=" + networkRequests.Count);
            return false;
        }
        try
        {
            ItemStack[] updated = ItemStack.CreateArray(source.Length);
            for (int i = 0; i < source.Length; i++) updated[i] = source[i]?.Clone() ?? ItemStack.Empty.Clone();
            foreach (SlotWithdrawal slot in slots)
            {
                if (slot.Take == 0) continue;
                updated[slot.Index].count -= slot.Take;
                if (updated[slot.Index].count <= 0) updated[slot.Index] = ItemStack.Empty.Clone();
            }
            __instance.SetSlots(updated);
            __instance.UpdateBackend(updated);
            if (_removedItems != null)
            {
                foreach (SlotWithdrawal slot in slots)
                    if (slot.Take > 0) _removedItems.Add(new ItemStack(slot.Original.itemValue.Clone(), slot.Take));
                foreach (StorageAddition removal in networkRequests)
                    _removedItems.Add(new ItemStack(removal.Value.Clone(), (int)removal.Count));
            }
            NetworkLog.Debug("Workstation materials consumed. physicalSlots=" + slots.FindAll(slot => slot.Take > 0).Count + "; networkVariants=" + networkRequests.Count);
        }
        catch (Exception ex)
        {
            __instance.SetSlots(source);
            __instance.UpdateBackend(source);
            if (networkRequests.Count > 0 && !NetworkStorage.AddBatch(player, networkRequests))
                NetworkLog.Error("Workstation material rollback could not restore network items. " + ex);
            else NetworkLog.Error("Workstation material consumption rolled back after slot error. " + ex);
        }
        return false;
    }
}
