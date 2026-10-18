using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(ItemActionEntryCraft), nameof(ItemActionEntryCraft.OnActivated))]
internal static class RemoteCraftActionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemActionEntryCraft __instance)
    {
        XUi xui = __instance?.ItemController?.xui;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!NetworkRemoteClient.IsRemoteClient || !(bool)player || NetworkRemoteClient.IsAuthorizedConsumption(player)) return true;
        if (!(__instance.ItemController is XUiC_RecipeEntry recipeEntry) || recipeEntry.Recipe == null) return true;

        bool hasItems;
        try { hasItems = __instance.hasItems(xui, recipeEntry.Recipe); }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not inspect a remote crafting request. player=" + player.entityId + "; exception=" + ex);
            return false;
        }
        if (!hasItems || __instance.tempIngredientList == null || __instance.tempIngredientList.Count == 0) return true;

        XUiC_WorkstationInputGrid input = __instance.craftCountControl?.WindowGroup?.Controller?.GetChildByType<XUiC_WorkstationInputGrid>();
        if (input is XUiC_WorkstationMaterialInputGrid || string.Equals(recipeEntry.Recipe.craftingArea, "forge", StringComparison.OrdinalIgnoreCase)) return true;
        NetworkConsumptionSource source = input == null ? NetworkConsumptionSource.PlayerInventory : NetworkConsumptionSource.WorkstationInput;
        IList<ItemStack> physical = input == null ? xui.PlayerInventory.GetAllItemStacks() : input.GetSlots();
        List<ItemStack> requirements;
        if (!BuildCraftRequirements(__instance.tempIngredientList, __instance.craftCountControl?.Count ?? 1, out requirements)) return false;
        if (HasRequirements(physical, requirements)) return true;

        Vector3i position = input?.WorkstationData?.TileEntity?.ToWorldPos() ?? Vector3i.zero;
        NetworkLog.Debug("Deferring remote craft until the server reserves network materials. player=" + player.entityId + "; recipe=" + recipeEntry.Recipe.GetName() + "; source=" + source);
        NetworkRemoteClient.RequestConsumption(player, requirements, source, position,
            () => __instance.OnActivated(),
            error =>
            {
                NetworkLog.Debug("Remote craft was not started. player=" + player.entityId + "; recipe=" + recipeEntry.Recipe.GetName() + "; error=" + error);
                if (error == "InsufficientMaterials") GameManager.ShowTooltip(player, Localization.Get("ttMissingCraftingResources"));
                __instance.craftCountControl.IsDirty = true;
                if (__instance.ItemController != null) __instance.ItemController.IsDirty = true;
            });
        return false;
    }

    private static bool BuildCraftRequirements(IList<ItemStack> ingredients, int multiplier, out List<ItemStack> requirements)
    {
        requirements = new List<ItemStack>();
        if (multiplier <= 0) return false;
        foreach (ItemStack ingredient in ingredients)
        {
            if (ingredient == null || ingredient.IsEmpty()) continue;
            long count = (long)ingredient.count * multiplier;
            if (count <= 0) continue;
            if (count > int.MaxValue)
            {
                NetworkLog.Error("Remote craft requirement exceeds the supported count. item=" + ingredient.itemValue.type + "; count=" + count);
                return false;
            }
            requirements.Add(new ItemStack(ingredient.itemValue.Clone(), (int)count));
            if (requirements.Count > 64)
            {
                NetworkLog.Error("Remote craft has too many ingredient variants. count=" + requirements.Count);
                return false;
            }
        }
        return requirements.Count > 0;
    }

    private static bool HasRequirements(IList<ItemStack> physical, IList<ItemStack> requirements)
    {
        List<StoredEntry> remaining = new List<StoredEntry>();
        if (physical != null)
            foreach (ItemStack stack in physical)
                if (stack != null && !stack.IsEmpty()) remaining.Add(new StoredEntry { Value = stack.itemValue, Count = stack.count });
        foreach (ItemStack requirement in requirements)
        {
            long needed = requirement.count;
            foreach (StoredEntry stack in remaining)
            {
                if (needed == 0) break;
                if (stack.Count <= 0 || !NetworkItemMatcher.Matches(stack.Value, requirement.itemValue)) continue;
                long take = Math.Min(needed, stack.Count);
                stack.Count -= take;
                needed -= take;
            }
            if (needed > 0) return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(ItemActionEntryRepair), nameof(ItemActionEntryRepair.OnActivated))]
internal static class RemoteRepairQueueActionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemActionEntryRepair __instance)
    {
        XUi xui = __instance?.ItemController?.xui;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!NetworkRemoteClient.IsRemoteClient || !(bool)player || NetworkRemoteClient.IsAuthorizedConsumption(player)) return true;

        ItemStack target = __instance.ItemController is XUiC_EquipmentStack equipment
            ? equipment.ItemStack
            : (__instance.ItemController as IItemStackController)?.ItemStack;
        if (target == null || target.IsEmpty()) return true;
        ItemClass targetClass = ItemClass.GetForId(target.itemValue.type);
        if (targetClass?.RepairTools == null || targetClass.RepairTools.Length == 0) return true;
        ItemClass repairTool = ItemClass.GetItemClass(targetClass.RepairTools[0].Value);
        if (repairTool == null || repairTool.RepairAmount.Value <= 0) return true;
        int maximum = Convert.ToInt32(Math.Ceiling((float)Mathf.CeilToInt(target.itemValue.UseTimes) / repairTool.RepairAmount.Value));
        int count = Math.Min(xui.PlayerInventory.GetItemCount(new ItemValue(repairTool.Id)), maximum);
        if (count <= ActionResourceService.PhysicalCount(player, new ItemValue(repairTool.Id))) return true;
        List<ItemStack> requirement = new List<ItemStack> { new ItemStack(new ItemValue(repairTool.Id), count) };
        NetworkLog.Debug("Deferring remote equipment repair until the server reserves repair materials. player=" + player.entityId + "; target=" + target.itemValue.type + "; kits=" + count);
        NetworkRemoteClient.RequestConsumption(player, requirement, NetworkConsumptionSource.PlayerInventory, Vector3i.zero,
            () => __instance.OnActivated(),
            error => NetworkLog.Debug("Remote equipment repair was not started. player=" + player.entityId + "; target=" + target.itemValue.type + "; error=" + error));
        return false;
    }
}
