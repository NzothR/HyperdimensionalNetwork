using System;
using Audio;
using HarmonyLib;
using UnityEngine;

namespace HyperdimensionalNetwork.Patches;

// Shift-clicking the repair action repairs the item immediately; a normal click keeps the vanilla queue.
[HarmonyPatch(typeof(ItemActionEntryRepair), nameof(ItemActionEntryRepair.OnActivated))]
internal static class InstantRepairPatch
{
    [HarmonyPrefix]
    private static bool RepairNow(ItemActionEntryRepair __instance)
    {
        if (!InputUtils.ShiftKeyPressed) return true;
        XUiController controller = __instance?.ItemController;
        EntityPlayerLocal player = controller?.xui?.playerUI?.entityPlayer;
        if (player == null || !__instance.Enabled) return true;
        ItemStack stack = controller is XUiC_EquipmentStack equipment ? equipment.ItemStack
            : (controller is XUiC_ItemStack itemController ? itemController.ItemStack : null);
        if (stack == null || stack.IsEmpty() || stack.itemValue.UseTimes <= 0f
            || ItemAction.CanRepair(stack.itemValue) != ItemAction.CanRepairValues.Allowed) return true;
        ItemClass itemClass = stack.itemValue.ItemClass;
        if (itemClass?.RepairTools == null || itemClass.RepairTools.Length == 0) return true;
        ItemClass tool = ItemClass.GetItemClass(itemClass.RepairTools[0].Value);
        if (tool?.RepairAmount == null || tool.RepairAmount.Value <= 0) return true;
        int needed = Convert.ToInt32(Math.Ceiling((double)Mathf.CeilToInt(stack.itemValue.UseTimes) / tool.RepairAmount.Value));
        ItemValue material = new ItemValue(tool.Id);
        int count = Math.Min(needed, ActionResourceService.Count(player, material));
        if (count <= 0) return true;

        var recipe = new Recipe { count = 1, itemValueType = stack.itemValue.type };
        recipe.ingredients.Add(new ItemStack(material, count));
        int repairAmount = (int)EffectManager.GetValue(PassiveEffects.RepairAmount, null,
            count * tool.RepairAmount.Value, player, recipe, FastTags<TagGroup.Global>.Parse(recipe.GetName()));
        ItemValue repaired = stack.itemValue.Clone();
        int stackCount = stack.count;
        if (repairAmount <= 0 || !ActionResourceService.Consume(player, material, count)) return false;

        repaired.UseTimes = Mathf.Max(0f, repaired.UseTimes - repairAmount);
        if (controller is XUiC_EquipmentStack equipmentStack)
        {
            equipmentStack.ItemStack = new ItemStack(repaired, stackCount);
            controller.xui.PlayerEquipment.Equipment.SetPreferredItemSlot(equipmentStack.SlotNumber, repaired);
        }
        else if (controller is XUiC_ItemStack itemStackController)
            itemStackController.ItemStack = new ItemStack(repaired, stackCount);

        QuestEventManager.Current.RepairedItem(repaired);
        int exp = Mathf.CeilToInt(itemClass.RepairExpMultiplier * count);
        if (exp > 0) player.Progression.AddLevelExp(exp, "_xpFromCrafting", Progression.XPTypes.Crafting);
        Manager.PlayInsidePlayerHead("craft_complete_item");
        controller.WindowGroup.Controller.SetAllChildrenDirty();
        NetworkLog.Debug("Instant item repair completed. item=" + repaired.type + "; tools=" + count + "; amount=" + repairAmount);
        return false;
    }
}
