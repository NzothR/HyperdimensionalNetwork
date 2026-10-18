using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_WorkstationInputGrid))]
internal static class WorkstationInputPatch
{
    private sealed class ConsumeState
    {
        public EntityPlayer Player;
        public List<StorageAddition> Removed;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(XUiC_WorkstationInputGrid.GetItemCount))]
    private static void Count(XUiC_WorkstationInputGrid __instance, ItemValue _itemValue, ref int __result)
    {
        EntityPlayer player = __instance.xui?.playerUI?.entityPlayer;
        if (!(bool)player || _itemValue == null) return;
        __result = NetworkItemMatcher.AddClamped(__result, NetworkStorage.GetMatchingCount(player, _itemValue));
    }

    [HarmonyPrefix, HarmonyPatch(nameof(XUiC_WorkstationInputGrid.RemoveItems))]
    private static bool RemovePrefix(XUiC_WorkstationInputGrid __instance, ref IList<ItemStack> _itemStacks, ref int _multiplier, out ConsumeState __state)
    {
        __state = null;
        EntityPlayer player = __instance.xui?.playerUI?.entityPlayer;
        if (!(bool)player || _itemStacks == null || _multiplier <= 0) return true;
        ItemStack[] slots = __instance.GetSlots();
        List<StoredEntry> available = null;
        Dictionary<int, long> physicalRemaining = new Dictionary<int, long>();
        List<ItemStack> physicalRequests = new List<ItemStack>();
        List<StorageAddition> networkRequests = new List<StorageAddition>();
        foreach (ItemStack required in _itemStacks)
        {
            if (required == null || required.IsEmpty()) continue;
            long needed = (long)required.count * _multiplier;
            if (needed > int.MaxValue)
            {
                NetworkLog.Error("Workstation material consumption exceeds int32 limit. item=" + required.itemValue.type + "; count=" + needed);
                return false;
            }
            int type = required.itemValue.type;
            if (!physicalRemaining.TryGetValue(type, out long physical))
            {
                physical = 0;
                foreach (ItemStack slot in slots)
                    if (slot != null && !slot.IsEmpty() && slot.itemValue.type == type) physical += slot.count;
            }
            int physicalTake = (int)Math.Min(physical, needed);
            physicalRemaining[type] = physical - physicalTake;
            if (physicalTake > 0) physicalRequests.Add(new ItemStack(required.itemValue.Clone(), physicalTake));
            long deficit = needed - physicalTake;
            if (deficit <= 0) continue;
            if (available == null) available = NetworkStorage.GetEntries(player);
            foreach (StoredEntry entry in available)
            {
                if (deficit == 0) break;
                if (entry.Count <= 0 || !NetworkItemMatcher.Matches(entry.Value, required.itemValue)) continue;
                long take = Math.Min(deficit, entry.Count);
                entry.Count -= take;
                deficit -= take;
                networkRequests.Add(new StorageAddition { Value = entry.Value.Clone(), Count = take });
            }
            if (deficit > 0)
            {
                NetworkLog.Error("Workstation material consumption blocked: available sources are insufficient. item=" + type + "; missing=" + deficit);
                return false;
            }
        }
        if (networkRequests.Count == 0) return true;
        if (!NetworkStorage.RemoveBatch(player, networkRequests))
        {
            NetworkLog.Error("Workstation material consumption blocked: network batch commit failed. variants=" + networkRequests.Count);
            return false;
        }
        __state = new ConsumeState { Player = player, Removed = networkRequests };
        _itemStacks = physicalRequests;
        _multiplier = 1;
        NetworkLog.Debug("Workstation material consumption planned. physicalStacks=" + physicalRequests.Count + "; networkVariants=" + networkRequests.Count);
        return true;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(XUiC_WorkstationInputGrid.RemoveItems))]
    private static void RemovePostfix(IList<ItemStack> _removedItems, ConsumeState __state)
    {
        if (__state?.Removed == null || _removedItems == null) return;
        foreach (StorageAddition removal in __state.Removed)
            _removedItems.Add(new ItemStack(removal.Value.Clone(), (int)removal.Count));
    }

    [HarmonyFinalizer, HarmonyPatch(nameof(XUiC_WorkstationInputGrid.RemoveItems))]
    private static Exception RemoveFinalizer(Exception __exception, ConsumeState __state)
    {
        if (__exception != null && __state?.Removed != null && !NetworkStorage.AddBatch(__state.Player, __state.Removed))
            NetworkLog.Error("Workstation material consumption rollback failed after game exception. " + __exception);
        return __exception;
    }
}
