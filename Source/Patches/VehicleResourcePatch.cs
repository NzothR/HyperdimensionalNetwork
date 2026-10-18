using System;
using Audio;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(EntityVehicle))]
internal static class VehicleFuelPatch
{
    [HarmonyPostfix, HarmonyPatch("hasGasCan")]
    private static void HasFuel(EntityVehicle __instance, EntityAlive _ea, ref bool __result)
    {
        if (__result || !(_ea is EntityPlayerLocal player)) return;
        ItemValue fuel = GetFuel(__instance);
        if (fuel != null) __result = ActionResourceService.Count(player, fuel) > 0;
    }

    [HarmonyPrefix, HarmonyPatch("takeFuel")]
    private static bool TakeFuel(EntityVehicle __instance, EntityAlive _entityFocusing, int count, ref float __result)
    {
        if (!(_entityFocusing is EntityPlayerLocal player)) return true;
        __result = 0f;
        ItemValue fuel = GetFuel(__instance);
        if (fuel == null || count <= 0) return false;
        int available = ActionResourceService.Count(player, fuel);
        int take = Math.Min(count, available);
        if (take <= 0 || !ActionResourceService.Consume(player, fuel, take)) return false;
        LocalPlayerUI ui = LocalPlayerUI.GetUIForPlayer(player);
        if ((bool)ui) ui.xui.CollectedItemList.RemoveItemStack(new ItemStack(fuel, take));
        __result = take;
        NetworkLog.Debug("Vehicle fuel consumed. item=" + fuel.type + "; requested=" + count + "; consumed=" + take);
        return false;
    }

    private static ItemValue GetFuel(EntityVehicle entity)
    {
        if (!(bool)entity || entity.GetVehicle() == null) return null;
        string name = entity.GetVehicle().GetFuelItem();
        if (string.IsNullOrEmpty(name)) return null;
        ItemValue item = ItemClass.GetItem(name);
        return item?.ItemClass != null ? item : null;
    }
}

[HarmonyPatch(typeof(XUiM_Vehicle), nameof(XUiM_Vehicle.RepairVehicle))]
internal static class VehicleRepairPatch
{
    [HarmonyPostfix]
    private static void RepairVehicle(XUi _xui, Vehicle _vehicle, ref bool __result)
    {
        if (__result || _xui?.playerUI?.entityPlayer == null) return;
        EntityPlayerLocal player = _xui.playerUI.entityPlayer;
        Vehicle vehicle = _vehicle ?? _xui.Vehicle?.CurrentVehicle?.GetVehicle();
        if (vehicle == null || vehicle.GetRepairAmountNeeded() <= 0) return;
        ItemValue kit = ItemClass.GetItem("resourceRepairKit");
        if (kit?.ItemClass == null || ActionResourceService.Count(player, kit) < 1) return;
        // The original method returns false when no physical repair kit exists.
        if (player.inventory.GetItemCount(kit) > 0 || player.bag.GetItemCount(kit) > 0) return;
        if (!ActionResourceService.Consume(player, kit, 1)) return;
        float bonus = 0f;
        ProgressionValue perk = player.Progression.GetProgressionValue("perkGreaseMonkey");
        if (perk != null) bonus = perk.Level * 0.1f;
        vehicle.RepairParts(1000, bonus);
        _xui.CollectedItemList.RemoveItemStack(new ItemStack(kit, 1));
        Manager.PlayInsidePlayerHead("craft_complete_item");
        __result = true;
        NetworkLog.Debug("Vehicle repaired with network kit. item=" + kit.type + "; repairNeeded=" + vehicle.GetRepairAmountNeeded());
    }
}
