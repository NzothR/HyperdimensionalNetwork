using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.RemoveItems))]
internal static class PlayerInventoryConsumePatch
{
    [ThreadStatic] private static bool _preferNetworkForChainReservation;

    private sealed class ChainReservationScope : IDisposable
    {
        public void Dispose() => _preferNetworkForChainReservation = false;
    }

    internal static IDisposable PreferNetworkForChainReservation()
    {
        _preferNetworkForChainReservation = true;
        return new ChainReservationScope();
    }

    private sealed class SlotWithdrawal
    {
        public ItemStackGrid Grid;
        public int Index;
        public ItemStack Original;
        public int Remaining;
        public int Take;
    }

    [HarmonyPrefix]
    private static bool Prefix(XUiM_PlayerInventory __instance, IList<ItemStack> _itemStacks, int _multiplier, IList<ItemStack> _removedItems)
    {
        EntityPlayerLocal player = __instance.localPlayer;
        if (!(bool)player || _itemStacks == null || _multiplier <= 0) return true;
        bool remoteAuthorized = NetworkRemoteClient.IsRemoteClient && NetworkRemoteClient.IsAuthorizedConsumption(player);
        bool remoteClient = NetworkRemoteClient.IsRemoteClient;
        List<SlotWithdrawal> slots = new List<SlotWithdrawal>();
        AddSlots(slots, __instance.backpack.ItemGrid);
        AddSlots(slots, __instance.toolbelt.ItemGrid);
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
                NetworkLog.Error("Player material consumption blocked: requirement exceeds int32. item=" + required.itemValue.type + "; count=" + needed);
                return false;
            }
            if (_preferNetworkForChainReservation)
            {
                if (network == null) network = remoteClient && !remoteAuthorized
                    ? new List<StoredEntry>()
                    : remoteAuthorized
                        ? NetworkRemoteClient.GetAuthorizedEntriesForRequirements(player, _itemStacks)
                        : NetworkStorage.GetEntriesForRequirements(player, _itemStacks);
                TakeNetwork(required, network, networkRequests, ref needed);
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
            if (network == null) network = remoteClient && !remoteAuthorized
                ? new List<StoredEntry>()
                : remoteAuthorized
                    ? NetworkRemoteClient.GetAuthorizedEntriesForRequirements(player, _itemStacks)
                    : NetworkStorage.GetEntriesForRequirements(player, _itemStacks);
            TakeNetwork(required, network, networkRequests, ref needed);
            if (needed > 0)
            {
                NetworkLog.Error("Player material consumption blocked: exact material unavailable. item=" + required.itemValue.type + "; quality=" + required.itemValue.Quality + "; missing=" + needed);
                return false;
            }
        }
        if (networkRequests.Count == 0 && !exactQuality) return true;
        bool networkRemoved = networkRequests.Count == 0 || (remoteAuthorized
            ? NetworkRemoteClient.ConsumeAuthorized(player, networkRequests)
            : NetworkStorage.RemoveBatch(player, networkRequests));
        if (!networkRemoved)
        {
            NetworkLog.Error("Player material consumption blocked: network batch commit failed. variants=" + networkRequests.Count);
            return false;
        }
        try
        {
            bool changedPhysical = false;
            foreach (SlotWithdrawal slot in slots)
            {
                if (slot.Take == 0) continue;
                if (slot.Grid.ChangeCount(slot.Index, -slot.Take) != -slot.Take)
                    throw new InvalidOperationException("Physical inventory slot changed during material consumption.");
                changedPhysical = true;
            }
            if (_removedItems != null)
            {
                foreach (SlotWithdrawal slot in slots)
                    if (slot.Take > 0) _removedItems.Add(new ItemStack(slot.Original.itemValue.Clone(), slot.Take));
                foreach (StorageAddition removal in networkRequests)
                    _removedItems.Add(new ItemStack(removal.Value.Clone(), (int)removal.Count));
            }
            if (!changedPhysical)
            {
                __instance.dispatchBackpackItemsChanged();
                __instance.dispatchToolbeltItemsChanged();
            }
            NetworkRemoteClient.RecordCraftingChainNetworkConsumption(player, networkRequests);
            NetworkLog.Debug("Player materials consumed. physicalSlots=" + slots.FindAll(slot => slot.Take > 0).Count + "; networkVariants=" + networkRequests.Count);
        }
        catch (Exception ex)
        {
            foreach (SlotWithdrawal slot in slots)
                if (slot.Take > 0) slot.Grid.SetItem(slot.Index, slot.Original);
            bool networkRestored = remoteAuthorized
                ? RestoreRemoteAuthorization(player, networkRequests)
                : networkRequests.Count == 0 || NetworkStorage.AddBatch(player, networkRequests);
            if (!networkRestored)
                NetworkLog.Error("Player material rollback could not restore network items. " + ex);
            else NetworkLog.Error("Player material consumption rolled back after slot error. " + ex);
        }
        return false;
    }

    private static void TakeNetwork(ItemStack required, List<StoredEntry> network,
        List<StorageAddition> requests, ref long needed)
    {
        foreach (StoredEntry entry in network)
        {
            if (needed == 0) break;
            if (entry.Count == 0 || !NetworkItemMatcher.Matches(entry.Value, required.itemValue)) continue;
            long take = Math.Min(needed, entry.Count);
            entry.Count -= take;
            needed -= take;
            requests.Add(new StorageAddition { Value = entry.Value.Clone(), Count = take });
        }
    }

    private static bool RestoreRemoteAuthorization(EntityPlayer player, IEnumerable<StorageAddition> networkRequests)
    {
        NetworkRemoteClient.RestoreAuthorized(player, networkRequests);
        return true;
    }

    private static void AddSlots(List<SlotWithdrawal> slots, ItemStackGrid grid)
    {
        for (int index = 0; index < grid.Length; index++)
        {
            ItemStack stack = grid[index];
            if (stack == null || stack.IsEmpty()) continue;
            slots.Add(new SlotWithdrawal { Grid = grid, Index = index, Original = stack.Clone(), Remaining = stack.count });
        }
    }
}
