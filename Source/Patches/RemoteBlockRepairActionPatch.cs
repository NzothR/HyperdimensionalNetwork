using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(ItemActionRepair))]
internal static class RemoteBlockRepairActionPatch
{
    private static readonly System.Reflection.FieldInfo UpgradeChanged = AccessTools.Field(typeof(ItemActionRepair), "bUpgradeCountChanged");
    private static readonly System.Reflection.FieldInfo UpgradeCount = AccessTools.Field(typeof(ItemActionRepair), "blockUpgradeCount");
    private static readonly System.Reflection.FieldInfo UpgradePosition = AccessTools.Field(typeof(ItemActionRepair), "blockTargetPos");

    [HarmonyPrefix, HarmonyPatch(nameof(ItemActionRepair.ExecuteAction))]
    private static bool ExecuteAction(ItemActionRepair __instance, ItemActionData _actionData, bool _bReleased)
    {
        EntityPlayerLocal player = _actionData?.invData?.holdingEntity as EntityPlayerLocal;
        if (!NetworkRemoteClient.IsRemoteClient || !(bool)player || _bReleased || NetworkRemoteClient.IsAuthorizedConsumption(player)) return true;
        if (Time.time - _actionData.lastUseTime < __instance.Delay) return true;
        WorldRayHitInfo hitInfo = player.HitInfo;
        if (!hitInfo.bHitValid || hitInfo.hit.distanceSq > Constants.cDigAndBuildDistance * Constants.cDigAndBuildDistance) return true;
        BlockValue blockValue = _actionData.invData.world.GetBlock(hitInfo.hit.blockPos);
        if (blockValue.ischild)
        {
            Vector3i parent = blockValue.Block.multiBlockPos.GetParentPos(hitInfo.hit.blockPos, blockValue);
            blockValue = _actionData.invData.world.GetBlock(parent);
        }
        Block block = blockValue.Block;
        if (block == null || !block.CanRepair(blockValue)) return true;
        List<Block.SItemNameCount> repairItems = block.RepairItems;
        int repairAmount = Mathf.Min((int)__instance.GetRepairAmount(), blockValue.damage);
        float damageRatio = block.MaxDamage <= 0 ? 0f : (float)repairAmount / block.MaxDamage;
        if (block.RepairItemsMeshDamage != null && block.shape.UseRepairDamageState(blockValue))
        {
            damageRatio = 1f;
            repairItems = block.RepairItemsMeshDamage;
        }
        if (repairItems == null) return true;
        float[] alreadyCharged = GetLastRepairFractions(_actionData);
        List<ItemStack> requirements = new List<ItemStack>();
        for (int i = 0; i < repairItems.Count; i++)
        {
            if (alreadyCharged != null && i < alreadyCharged.Length && alreadyCharged[i] > 0f) continue;
            Block.SItemNameCount material = repairItems[i];
            ItemValue value = ItemClass.GetItem(material.ItemName);
            int count = Math.Max((int)(material.Count * damageRatio * block.ResourceScale), 1);
            if (value?.ItemClass == null || count <= 0) continue;
            requirements.Add(new ItemStack(value, count));
        }
        if (!NeedsNetworkMaterials(player, requirements)) return true;
        NetworkLog.Debug("Deferring remote block repair until the server reserves materials. player=" + player.entityId + "; block=" + block.GetBlockName() + "; requirements=" + requirements.Count);
        NetworkRemoteClient.RequestConsumption(player, requirements, NetworkConsumptionSource.PlayerInventory, Vector3i.zero,
            () => __instance.ExecuteAction(_actionData, false),
            error =>
            {
                NetworkLog.Debug("Remote block repair did not proceed. player=" + player.entityId + "; block=" + block.GetBlockName() + "; error=" + error);
                if (error == "InsufficientMaterials") GameManager.ShowTooltip(player, Localization.Get("ttMissingCraftingResources"));
            });
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(ItemActionRepair.OnHoldingUpdate))]
    private static bool OnHoldingUpdate(ItemActionRepair __instance, ItemActionData _actionData)
    {
        EntityPlayerLocal player = _actionData?.invData?.holdingEntity as EntityPlayerLocal;
        if (!NetworkRemoteClient.IsRemoteClient || !(bool)player || NetworkRemoteClient.IsAuthorizedConsumption(player)
            || UpgradeChanged?.GetValue(__instance) is not bool changed || !changed) return true;
        int completedHits = UpgradeCount?.GetValue(__instance) is int value ? value : 0;
        Vector3i position = UpgradePosition?.GetValue(__instance) is Vector3i target ? target : Vector3i.zero;
        BlockValue blockValue = _actionData.invData.world.GetBlock(position);
        Block block = blockValue.Block;
        if (block == null || !ActionRepairPatch.TryUpgradeRequirement(__instance, blockValue, out ItemValue item, out int count)) return true;
        if (!int.TryParse(block.Properties.GetString("UpgradeBlock", "UpgradeHitCount"), out int hitCount)) return true;
        hitCount = Math.Max(1, (int)(hitCount + __instance.hitCountOffset));
        if (completedHits < hitCount || !__instance.CanRemoveRequiredResource(_actionData.invData, blockValue)) return true;
        List<ItemStack> requirements = new List<ItemStack> { new ItemStack(item, count) };
        if (!NeedsNetworkMaterials(player, requirements)) return true;

        NetworkLog.Debug("Deferring remote block upgrade until the server reserves materials. player=" + player.entityId + "; block=" + block.GetBlockName() + "; item=" + item.type + "; count=" + count);
        NetworkRemoteClient.RequestConsumption(player, requirements, NetworkConsumptionSource.PlayerInventory, Vector3i.zero,
            () => __instance.OnHoldingUpdate(_actionData),
            error =>
            {
                UpgradeChanged?.SetValue(__instance, false);
                UpgradeCount?.SetValue(__instance, 0);
                NetworkLog.Debug("Remote block upgrade did not proceed. player=" + player.entityId + "; block=" + block.GetBlockName() + "; error=" + error);
            });
        return false;
    }

    private static float[] GetLastRepairFractions(ItemActionData actionData)
    {
        return AccessTools.Field(actionData.GetType(), "lastRepairItemsPercents")?.GetValue(actionData) as float[];
    }

    private static bool NeedsNetworkMaterials(EntityPlayerLocal player, IList<ItemStack> requirements)
    {
        if (requirements == null || requirements.Count == 0) return false;
        bool needsNetwork = false;
        List<StoredEntry> physical = new List<StoredEntry>();
        foreach (ItemStackGrid grid in new[] { player.inventory.ItemGrid, player.bag.ItemGrid })
            for (int i = 0; i < grid.Length; i++)
            {
                ItemStack stack = grid[i];
                if (stack != null && !stack.IsEmpty()) physical.Add(new StoredEntry { Value = stack.itemValue, Count = stack.count });
            }
        List<StoredEntry> network = NetworkStorage.GetEntriesForRequirements(player, requirements);
        foreach (ItemStack requirement in requirements)
        {
            if (requirement == null || requirement.IsEmpty()) continue;
            long remaining = requirement.count;
            foreach (StoredEntry source in physical)
            {
                if (remaining == 0) break;
                if (source.Count <= 0 || !NetworkItemMatcher.Matches(source.Value, requirement.itemValue)) continue;
                long take = Math.Min(remaining, source.Count);
                source.Count -= take;
                remaining -= take;
            }
            foreach (StoredEntry source in network)
            {
                if (remaining == 0) break;
                if (source.Count <= 0 || !NetworkItemMatcher.Matches(source.Value, requirement.itemValue)) continue;
                long take = Math.Min(remaining, source.Count);
                source.Count -= take;
                remaining -= take;
                needsNetwork = true;
            }
            if (remaining > 0) return false;
        }
        return needsNetwork;
    }
}
