using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal sealed class CraftingChainWorkstationPosition
{
    public string Area = string.Empty;
    public int X;
    public int Y;
    public int Z;
}

/// <summary>
/// Main-thread-only lookup and validation for loaded vanilla workstations in chain range.
/// Worker planning receives only the resulting integer coordinates.
/// </summary>
internal static class CraftingChainWorkstationService
{
    public const float SearchRange = 64f;
    private static readonly FieldInfo ModuleFlags = typeof(TileEntityWorkstation).GetField("isModuleUsed",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static Dictionary<string, List<CraftingChainWorkstationPosition>> Capture(EntityPlayer player,
        IEnumerable<string> requestedAreas)
    {
        Dictionary<string, List<CraftingChainWorkstationPosition>> result = new Dictionary<string, List<CraftingChainWorkstationPosition>>(StringComparer.OrdinalIgnoreCase);
        if (!(bool)player || GameManager.Instance?.World?.ChunkCache == null) return result;
        HashSet<string> areas = new HashSet<string>((requestedAreas ?? Enumerable.Empty<string>())
            .Where(area => !string.IsNullOrEmpty(area)), StringComparer.OrdinalIgnoreCase);
        if (areas.Count == 0) return result;

        Vector3 origin = player.position;
        float rangeSquared = SearchRange * SearchRange;
        List<Tuple<float, CraftingChainWorkstationPosition>> found = new List<Tuple<float, CraftingChainWorkstationPosition>>();
        foreach (Chunk chunk in GameManager.Instance.World.ChunkCache.GetChunkArrayCopySync())
        {
            if (chunk?.GetTileEntities()?.dict == null) continue;
            foreach (TileEntity entity in chunk.GetTileEntities().dict.Values)
            {
                if (!(entity is TileEntityWorkstation workstation)) continue;
                Vector3i position = workstation.ToWorldPos();
                if ((workstation.ToWorldCenterPos() - origin).sqrMagnitude > rangeSquared
                    || workstation.IsUserAccessing() || !IsQueueIdle(workstation)) continue;
                BlockValue block = GameManager.Instance.World.GetBlock(position);
                string area = block.Block?.GetBlockName();
                if (string.IsNullOrEmpty(area) || !areas.Contains(area)) continue;
                if (LockManager.Instance != null && LockManager.Instance.IsLockedByLocalPlayer(workstation)) continue;
                float distance = (workstation.ToWorldCenterPos() - origin).sqrMagnitude;
                found.Add(Tuple.Create(distance, new CraftingChainWorkstationPosition
                {
                    Area = area,
                    X = position.x,
                    Y = position.y,
                    Z = position.z
                }));
            }
        }
        foreach (Tuple<float, CraftingChainWorkstationPosition> candidate in found.OrderBy(item => item.Item1))
        {
            if (!result.TryGetValue(candidate.Item2.Area, out List<CraftingChainWorkstationPosition> candidates))
            {
                candidates = new List<CraftingChainWorkstationPosition>();
                result[candidate.Item2.Area] = candidates;
            }
            if (candidates.Count < LockManager.MaxLocksPerPlayer) candidates.Add(candidate.Item2);
        }
        NetworkLog.Debug("Crafting-chain workstation snapshot captured. player=" + player.entityId + "; range=" + SearchRange
            + "; loadedCandidates=" + found.Count + "; areas=" + result.Count);
        return result;
    }

    public static TileEntityWorkstation Resolve(CraftingChainWorkstationPosition position)
    {
        if (position == null || GameManager.Instance?.World == null) return null;
        return GameManager.Instance.World.GetTileEntity(new Vector3i(position.X, position.Y, position.Z)) as TileEntityWorkstation;
    }

    public static bool IsQueueIdle(TileEntityWorkstation workstation)
        => workstation?.Queue != null && workstation.Queue.All(item => item == null || item.Recipe == null || item.Multiplier <= 0);

    public static bool CanRun(TileEntityWorkstation workstation, Recipe recipe, ItemValue outputValue, int outputCount,
        float oneItemCraftTime, int batches, out string reason)
    {
        reason = string.Empty;
        if (workstation == null || recipe == null || outputValue == null || batches <= 0)
        {
            reason = "missing-workstation-or-recipe";
            return false;
        }
        if (workstation.IsUserAccessing() || !IsQueueIdle(workstation))
        {
            reason = "workstation-busy";
            return false;
        }
        if (batches > short.MaxValue)
        {
            reason = "batch-count-exceeds-vanilla-queue-limit";
            return false;
        }
        if (recipe.craftingToolType > 0 && !(workstation.Tools ?? Array.Empty<ItemStack>())
            .Any(item => item != null && !item.IsEmpty() && item.itemValue.type == recipe.craftingToolType))
        {
            reason = "required-workstation-tool-missing";
            return false;
        }
        if (RequiresFuel(workstation))
        {
            float availableFuel = new XUiM_Workstation(workstation).GetTotalBurnTimeLeft();
            float requiredFuel = Math.Max(0f, oneItemCraftTime) * batches;
            if (availableFuel + 0.5f < requiredFuel)
            {
                reason = "workstation-fuel-insufficient";
                return false;
            }
        }
        if (!CanFitOutput(workstation.Output, outputValue, outputCount))
        {
            reason = "workstation-output-capacity-insufficient";
            return false;
        }
        return true;
    }

    public static bool RequiresFuel(TileEntityWorkstation workstation)
    {
        if (workstation == null || ModuleFlags?.GetValue(workstation) is not bool[] flags || flags.Length <= 3) return false;
        return flags[3];
    }

    public static bool CanFitOutput(ItemStack[] source, ItemValue value, int count)
    {
        if (source == null || value == null || count <= 0) return false;
        ItemStack[] slots = source.Select(item => item == null ? ItemStack.Empty.Clone() : item.Clone()).ToArray();
        int remaining = count;
        int maxCount = Math.Max(1, value.ItemClass?.MaxCount ?? count);
        for (int i = 0; i < slots.Length && remaining > 0; i++)
        {
            ItemStack stack = slots[i];
            if (stack == null || stack.IsEmpty() || !stack.CanStackWith(new ItemStack(value, 1)) || stack.count >= maxCount) continue;
            int add = Math.Min(remaining, maxCount - stack.count);
            stack.count += add;
            remaining -= add;
        }
        for (int i = 0; i < slots.Length && remaining > 0; i++)
        {
            if (slots[i] != null && !slots[i].IsEmpty()) continue;
            int add = Math.Min(remaining, maxCount);
            slots[i] = new ItemStack(value.Clone(), add);
            remaining -= add;
        }
        return remaining == 0;
    }

    public static bool IsWithinRange(EntityPlayer player, TileEntityWorkstation workstation)
        => (bool)player && workstation != null
           && (workstation.ToWorldCenterPos() - player.position).sqrMagnitude <= SearchRange * SearchRange;
}
