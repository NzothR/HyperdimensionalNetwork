using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class RestockTransactionService
{
    private sealed class SlotFill
    {
        public bool Toolbelt;
        public int Index;
        public int Count;
    }

    private static readonly object CooldownSync = new object();
    private static readonly Dictionary<string, float> AutoCooldownUntil = new Dictionary<string, float>(StringComparer.Ordinal);

    public static NetworkRemoteResult Execute(World world, EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        bool automatic = request.Operation == NetworkRequestOperation.AutomaticRestock;
        if (!RestockSettingsService.TryGet(player, out RestockSettingsSnapshot settings, out string settingsError))
            return Failure(request, settingsError);
        if (!settings.ReplenishmentEnabled) return Failure(request, "ReplenishmentDisabled");
        if (automatic)
        {
            if (!settings.AutoRestockEnabled) return Failure(request, "AutoRestockDisabled");
        }

        if (!(bool)player) return Failure(request, "PlayerUnavailable");
        if (!TryGetSnapshots(player, playerData, out MemoryStream bagSnapshot, out MemoryStream toolbeltSnapshot))
            return Failure(request, "PlayerInventoryUnavailable");
        if (world == null) world = GameManager.Instance?.World;

        Bag bag;
        ItemStackGrid toolbelt;
        byte selectedSlot;
        try
        {
            bag = NetworkRpcServer.ReadBag(bagSnapshot);
            if (bag == null || !TryReadToolbelt(toolbeltSnapshot, out toolbelt, out selectedSlot))
                return Failure(request, "PlayerInventoryUnavailable");
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not parse player inventory for restock. player=" + player.entityId + "; requestId=" + request.RequestId + "; exception=" + ex);
            return Failure(request, "PlayerInventoryUnavailable");
        }

        List<int> toolbeltSlots;
        List<int> backpackSlots;
        if (automatic)
        {
            toolbeltSlots = ValidSlots(request.RestockToolbeltSlots, toolbelt.Length);
            backpackSlots = ValidSlots(request.RestockBackpackSlots, bag.SlotCount);
            if (settings.TargetMode == RestockTargetMode.ToolbeltOnly)
                backpackSlots.Clear();
            else
            {
                backpackSlots.RemoveAll(index => !IsLocked(bag, index));
                if (settings.TargetMode == RestockTargetMode.LockedBackpackOnly) toolbeltSlots.Clear();
            }
        }
        else
        {
            toolbeltSlots = Enumerable.Range(0, toolbelt.Length).ToList();
            backpackSlots = Enumerable.Range(0, bag.SlotCount).Where(index => IsLocked(bag, index)).ToList();
        }

        List<ItemStack> requirements = CollectTargets(toolbelt, toolbeltSlots, bag, backpackSlots);
        if (requirements.Count == 0)
        {
            if (automatic)
                NetworkLog.Debug("Automatic restock found no occupied target stacks. player=" + player.entityId + "; requestId=" + request.RequestId
                    + "; mode=" + settings.TargetMode + "; toolbeltTargets=" + toolbeltSlots.Count + "; backpackTargets=" + backpackSlots.Count);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = 0 };
        }

        List<StoredEntry> available;
        try { available = NetworkStorage.GetEntriesForRequirements(player, requirements); }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not read network entries for restock. player=" + player.entityId + "; requestId=" + request.RequestId + "; exception=" + ex);
            return Failure(request, "NetworkUnavailable");
        }

        List<SlotFill> fills = new List<SlotFill>();
        List<StorageAddition> removals = new List<StorageAddition>();
        PlanGrid(toolbelt, toolbeltSlots, isToolbelt: true, available, fills, removals);
        PlanGrid(bag.ItemGrid, backpackSlots, isToolbelt: false, available, fills, removals);
        long total = fills.Sum(fill => (long)fill.Count);
        if (total <= 0)
        {
            if (automatic)
                NetworkLog.Debug("Automatic restock found no replenishable capacity; cooldown was not started. player=" + player.entityId
                    + "; requestId=" + request.RequestId + "; mode=" + settings.TargetMode + "; requirements=" + requirements.Count
                    + "; availableEntries=" + available.Count + "; targetTypes=" + string.Join(",", requirements.Select(item => item.itemValue.type).Distinct())
                    + "; availableTypes=" + string.Join(",", available.Select(entry => entry.Value.type).Distinct())
                    + "; toolbeltTargets=" + toolbeltSlots.Count + "; backpackTargets=" + backpackSlots.Count);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = 0 };
        }

        // Unrelated inventory changes often point at full stacks. Only successful refill candidates should
        // consume the per-player cooldown; otherwise they can suppress the next real consumption event.
        if (automatic && !TryStartCooldown(player, request, settings.AutoRestockCooldownMs))
            return Failure(request, "AutoRestockCoolingDown");

        if (!NetworkStorage.RemoveBatch(player, removals))
        {
            NetworkLog.Debug("Restock transaction rejected because network inventory changed before commit. player=" + player.entityId + "; requestId=" + request.RequestId);
            return Failure(request, "NetworkInventoryChanged");
        }

        MemoryStream updatedBag = null;
        MemoryStream updatedToolbelt = null;
        try
        {
            ApplyFills(toolbelt, bag.ItemGrid, fills);
            updatedBag = NetworkRpcServer.WriteBag(bag);
            updatedToolbelt = WriteToolbelt(toolbelt, selectedSlot);

            EntityPlayerLocal localPlayer = player as EntityPlayerLocal;
            if (localPlayer != null)
            {
                using (AutoRestockClient.SuppressInventoryEvents(localPlayer))
                {
                    NetworkRpcServer.ApplyPlayerInventorySnapshot(player, updatedBag, toolbelt: false);
                    NetworkRpcServer.ApplyPlayerInventorySnapshot(player, updatedToolbelt, toolbelt: true);
                }
                AutoRestockClient.RefreshBaseline(localPlayer);
            }
            else
            {
                NetworkRpcServer.ApplyPlayerInventorySnapshot(player, updatedBag, toolbelt: false);
                NetworkRpcServer.ApplyPlayerInventorySnapshot(player, updatedToolbelt, toolbelt: true);
            }

            if (playerData != null)
            {
                playerData.bagData = updatedBag;
                playerData.inventoryData = updatedToolbelt;
                playerData.bModifiedSinceLastSave = true;
            }
        }
        catch (Exception ex)
        {
            bool compensated = NetworkStorage.AddBatch(player, removals);
            if (!compensated) NetworkLog.Error("Restock inventory update failed and network material compensation also failed. player=" + player.entityId + "; requestId=" + request.RequestId + "; exception=" + ex);
            else NetworkLog.Error("Restock inventory update failed; network materials were restored. player=" + player.entityId + "; requestId=" + request.RequestId + "; exception=" + ex);
            try
            {
                NetworkRpcServer.ApplyPlayerInventorySnapshot(player, bagSnapshot, toolbelt: false);
                NetworkRpcServer.ApplyPlayerInventorySnapshot(player, toolbeltSnapshot, toolbelt: true);
                if (playerData != null)
                {
                    playerData.bagData = bagSnapshot;
                    playerData.inventoryData = toolbeltSnapshot;
                }
            }
            catch (Exception restoreException)
            {
                NetworkLog.Error("Could not restore the original player inventory after a failed restock. player=" + player.entityId + "; requestId=" + request.RequestId + "; exception=" + restoreException);
            }
            return Failure(request, "PlayerInventoryUpdateFailed");
        }

        NetworkLog.Debug("Restock committed. player=" + player.entityId + "; requestId=" + request.RequestId + "; mode=" + request.Operation
            + "; toolbeltTargets=" + toolbeltSlots.Count + "; backpackTargets=" + backpackSlots.Count + "; variants=" + removals.Count + "; amount=" + total);
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            Amount = (int)Math.Min(total, int.MaxValue),
            BagData = updatedBag.ToArray(),
            InventoryData = updatedToolbelt.ToArray()
        };
    }

    private static bool TryStartCooldown(EntityPlayer player, NetworkRemoteRequest request, int cooldownMs)
    {
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(playerId)) return false;
        float now = Time.realtimeSinceStartup;
        lock (CooldownSync)
        {
            if (AutoCooldownUntil.TryGetValue(playerId, out float until) && now < until)
            {
                NetworkLog.Debug("Automatic restock ignored during player cooldown. player=" + player.entityId + "; requestId=" + request.RequestId + "; remainingMs=" + Math.Ceiling((until - now) * 1000f));
                return false;
            }
            AutoCooldownUntil[playerId] = now + Math.Max(0, cooldownMs) / 1000f;
        }
        NetworkLog.Debug("Automatic restock started cooldown. player=" + player.entityId + "; requestId=" + request.RequestId + "; cooldownMs=" + cooldownMs);
        return true;
    }

    private static bool TryGetSnapshots(EntityPlayer player, PlayerDataFile playerData, out MemoryStream bag, out MemoryStream toolbelt)
    {
        bag = null;
        toolbelt = null;
        if (playerData?.bagData != null && playerData.bagData.Length > 0 && playerData.inventoryData != null && playerData.inventoryData.Length > 0)
        {
            bag = new MemoryStream(playerData.bagData.ToArray(), writable: false);
            toolbelt = new MemoryStream(playerData.inventoryData.ToArray(), writable: false);
            return true;
        }
        if (player is EntityPlayerLocal localPlayer)
        {
            bag = StreamUtils.ToBlob(writer => localPlayer.bag.Write(writer, StreamModeWrite.Persistency));
            toolbelt = StreamUtils.ToBlob(writer => localPlayer.inventory.Write(writer, StreamModeWrite.Persistency));
            return true;
        }
        return false;
    }

    private static List<int> ValidSlots(IEnumerable<int> requested, int length)
    {
        return (requested ?? Enumerable.Empty<int>()).Where(index => index >= 0 && index < length).Distinct().OrderBy(index => index).ToList();
    }

    private static bool IsLocked(Bag bag, int index)
    {
        return bag?.LockedSlots != null && index >= 0 && index < bag.LockedSlots.Length && bag.LockedSlots[index];
    }

    private static List<ItemStack> CollectTargets(ItemStackGrid toolbelt, IList<int> toolbeltSlots, Bag bag, IList<int> backpackSlots)
    {
        List<ItemStack> requirements = new List<ItemStack>();
        foreach (int index in toolbeltSlots)
        {
            ItemStack stack = toolbelt[index];
            if (stack != null && !stack.IsEmpty()) requirements.Add(new ItemStack(stack.itemValue.Clone(), 1));
        }
        foreach (int index in backpackSlots)
        {
            ItemStack stack = bag.ItemGrid[index];
            if (stack != null && !stack.IsEmpty()) requirements.Add(new ItemStack(stack.itemValue.Clone(), 1));
        }
        return requirements;
    }

    private static void PlanGrid(ItemStackGrid grid, IList<int> indices, bool isToolbelt, List<StoredEntry> available,
        List<SlotFill> fills, List<StorageAddition> removals)
    {
        foreach (int index in indices)
        {
            ItemStack current = grid[index];
            if (current == null || current.IsEmpty() || current.itemValue?.ItemClass == null) continue;
            int maxStack = Math.Max(1, current.itemValue.ItemClass.MaxCount);
            int remainingCapacity = maxStack - current.count;
            if (remainingCapacity <= 0) continue;
            int added = 0;
            foreach (StoredEntry entry in available)
            {
                if (remainingCapacity == 0) break;
                if (entry?.Value == null || entry.Count <= 0 || entry.Value.type != current.itemValue.type) continue;
                int candidateCount = (int)Math.Min(remainingCapacity, Math.Min(entry.Count, int.MaxValue));
                if (candidateCount <= 0) continue;
                ItemStack virtualTarget = new ItemStack(current.itemValue.Clone(), current.count + added);
                ItemStack incoming = new ItemStack(entry.Value.Clone(), candidateCount);
                if (!virtualTarget.CanStackPartlyWith(incoming, out int canAdd) || canAdd <= 0) continue;
                int take = Math.Min(candidateCount, canAdd);
                entry.Count -= take;
                remainingCapacity -= take;
                added += take;
                AddRemoval(removals, entry.Value, take);
            }
            if (added > 0) fills.Add(new SlotFill { Toolbelt = isToolbelt, Index = index, Count = added });
        }
    }

    private static void AddRemoval(List<StorageAddition> removals, ItemValue value, long count)
    {
        ItemValue key = NetworkStorage.NormalizeKey(value);
        StorageAddition existing = removals.FirstOrDefault(item => NetworkStorage.NormalizeKey(item.Value).Equals(key));
        if (existing == null) removals.Add(new StorageAddition { Value = key, Count = count });
        else existing.Count += count;
    }

    private static void ApplyFills(ItemStackGrid toolbelt, ItemStackGrid backpack, IEnumerable<SlotFill> fills)
    {
        foreach (SlotFill fill in fills)
        {
            ItemStackGrid grid = fill.Toolbelt ? toolbelt : backpack;
            ItemStack current = grid[fill.Index];
            if (current == null || current.IsEmpty()) throw new InvalidOperationException("Restock target slot became empty before commit.");
            if (fill.Count <= 0 || current.count > int.MaxValue - fill.Count) throw new InvalidOperationException("Restock target count overflow.");
            grid.SetItem(fill.Index, new ItemStack(current.itemValue.Clone(), current.count + fill.Count));
        }
    }

    private static bool TryReadToolbelt(MemoryStream snapshot, out ItemStackGrid grid, out byte selectedSlot)
    {
        grid = null;
        selectedSlot = 0;
        using (MemoryStream input = new MemoryStream(snapshot.ToArray(), writable: false))
        using (PooledBinaryReader reader = MemoryPools.poolBinaryReader.AllocSync(false))
        {
            reader.SetBaseStream(input);
            if (reader.ReadByte() != 1) return false;
            grid = ItemStackGrid.Read(reader, StreamModeRead.Persistency);
            selectedSlot = reader.ReadByte();
            return input.Position == input.Length;
        }
    }

    private static MemoryStream WriteToolbelt(ItemStackGrid grid, byte selectedSlot)
    {
        using (MemoryStream output = new MemoryStream())
        using (PooledBinaryWriter writer = MemoryPools.poolBinaryWriter.AllocSync(false))
        {
            writer.SetBaseStream(output);
            BinaryWriter binary = writer;
            binary.Write((byte)1);
            grid.Write(writer, StreamModeWrite.Persistency);
            binary.Write(selectedSlot);
            writer.Flush();
            return new MemoryStream(output.ToArray(), writable: false);
        }
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
    {
        return new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
    }
}
