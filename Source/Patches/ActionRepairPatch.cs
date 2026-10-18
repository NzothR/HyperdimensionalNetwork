using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(ItemActionRepair))]
internal static class ActionRepairPatch
{
    [HarmonyPostfix, HarmonyPatch("canRemoveRequiredItem")]
    private static void CanRepairBlock(ItemInventoryData _data, ItemStack _itemStack, ref bool __result)
    {
        if (_data?.holdingEntity is EntityPlayerLocal player && _itemStack != null && !_itemStack.IsEmpty())
            __result = ActionResourceService.Count(player, _itemStack.itemValue) >= _itemStack.count;
    }

    [HarmonyPrefix, HarmonyPatch("removeRequiredItem")]
    private static bool RepairBlock(ItemInventoryData _data, ItemStack _itemStack, ref bool __result)
    {
        if (!(_data?.holdingEntity is EntityPlayerLocal player)) return true;
        __result = _itemStack != null && !_itemStack.IsEmpty()
            && ActionResourceService.Consume(player, _itemStack.itemValue, _itemStack.count);
        return false;
    }

    [HarmonyPostfix, HarmonyPatch("CanRemoveRequiredResource")]
    private static void CanUpgradeBlock(ItemActionRepair __instance, ItemInventoryData data, BlockValue blockValue, ref bool __result)
    {
        if (__result || !(data?.holdingEntity is EntityPlayerLocal player)) return;
        if (!TryUpgradeRequirement(__instance, blockValue, out ItemValue item, out int count)) return;
        __result = ActionResourceService.Count(player, item) >= count;
    }

    [HarmonyPrefix, HarmonyPatch("RemoveRequiredResource")]
    private static bool UpgradeBlock(ItemActionRepair __instance, ItemInventoryData data, BlockValue blockValue, ref bool __result)
    {
        if (!(data?.holdingEntity is EntityPlayerLocal player)) return true;
        if (!TryUpgradeRequirement(__instance, blockValue, out ItemValue item, out int count)) return true;
        __result = __instance.CanRemoveRequiredResource(data, blockValue)
            && ActionResourceService.Consume(player, item, count);
        if (__result)
        {
            player.AddUIHarvestingItem(new ItemStack(item, -count));
            NetworkLog.Debug("Block upgrade materials consumed. item=" + item.type + "; count=" + count);
        }
        return false;
    }

    private static bool TryUpgradeRequirement(ItemActionRepair action, BlockValue value, out ItemValue item, out int count)
    {
        item = null;
        count = 0;
        Block block = value.Block;
        if (block == null || action == null) return false;
        string name = action.GetUpgradeItemName(block);
        if (string.IsNullOrEmpty(name) || block.GetBlockName() == null
            || (action.allowedUpgradeItems.Length > 0 && !action.allowedUpgradeItems.ContainsCaseInsensitive(name))
            || (action.restrictedUpgradeItems.Length > 0 && action.restrictedUpgradeItems.ContainsCaseInsensitive(name))
            || !int.TryParse(block.Properties.GetString("UpgradeBlock", "UpgradeHitCount"), out _)
            || !int.TryParse(block.Properties.GetString(Block.PropUpgradeBlockClass, Block.PropUpgradeBlockItemCount), out count)
            || count <= 0) return false;
        item = ItemClass.GetItem(name);
        return item?.ItemClass != null;
    }
}
