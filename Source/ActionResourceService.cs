using System;
using System.Collections.Generic;

namespace HyperdimensionalNetwork;

// Material access for game actions that bypass XUiM_PlayerInventory.
internal static class ActionResourceService
{
    private sealed class SlotUse
    {
        public ItemStackGrid Grid;
        public int Index;
        public ItemStack Original;
        public int Count;
    }

    public static int Count(EntityPlayerLocal player, ItemValue requirement)
    {
        if (!(bool)player || requirement == null || requirement.type <= 0) return 0;
        long count = 0;
        foreach (ItemStackGrid grid in new[] { player.inventory.ItemGrid, player.bag.ItemGrid })
            for (int i = 0; i < grid.Length; i++)
            {
                ItemStack stack = grid[i];
                if (stack != null && !stack.IsEmpty() && NetworkItemMatcher.Matches(stack.itemValue, requirement)) count += stack.count;
            }
        return NetworkItemMatcher.AddClamped(NetworkItemMatcher.ClampCount(count), NetworkStorage.GetMatchingCount(player, requirement));
    }

    public static bool Consume(EntityPlayerLocal player, ItemValue requirement, int count)
    {
        if (!(bool)player || requirement == null || count <= 0 || Count(player, requirement) < count) return false;
        var physical = new List<SlotUse>();
        int remaining = count;
        foreach (ItemStackGrid grid in new[] { player.inventory.ItemGrid, player.bag.ItemGrid })
            for (int i = 0; i < grid.Length && remaining > 0; i++)
            {
                ItemStack stack = grid[i];
                if (stack == null || stack.IsEmpty() || !NetworkItemMatcher.Matches(stack.itemValue, requirement)) continue;
                int take = Math.Min(remaining, stack.count);
                physical.Add(new SlotUse { Grid = grid, Index = i, Original = stack.Clone(), Count = take });
                remaining -= take;
            }

        var network = new List<StorageAddition>();
        if (remaining > 0)
        {
            foreach (StoredEntry entry in NetworkStorage.GetEntriesForRequirements(player, new[] { new ItemStack(requirement, remaining) }))
            {
                if (remaining == 0) break;
                if (!NetworkItemMatcher.Matches(entry.Value, requirement)) continue;
                int take = (int)Math.Min(remaining, entry.Count);
                network.Add(new StorageAddition { Value = entry.Value.Clone(), Count = take });
                remaining -= take;
            }
            if (remaining > 0 || !NetworkStorage.RemoveBatch(player, network))
            {
                NetworkLog.Debug("Action material withdrawal rejected. item=" + requirement.type + "; requested=" + count + "; missing=" + remaining);
                return false;
            }
        }
        try
        {
            foreach (SlotUse slot in physical)
                if (slot.Grid.ChangeCount(slot.Index, -slot.Count) != -slot.Count)
                    throw new InvalidOperationException("Physical action material slot changed during withdrawal.");
            NetworkLog.Debug("Action materials consumed. item=" + requirement.type + "; count=" + count + "; physicalSlots=" + physical.Count + "; networkVariants=" + network.Count);
            return true;
        }
        catch (Exception ex)
        {
            foreach (SlotUse slot in physical) slot.Grid.SetItem(slot.Index, slot.Original);
            if (network.Count > 0 && !NetworkStorage.AddBatch(player, network))
                NetworkLog.Error("Action material rollback failed to restore network materials. " + ex);
            else NetworkLog.Error("Action material withdrawal rolled back. " + ex);
            return false;
        }
    }
}
