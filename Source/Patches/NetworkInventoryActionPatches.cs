using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(ItemActionEntryUse), nameof(ItemActionEntryUse.OnActivated))]
internal static class NetworkUseActionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemActionEntryUse __instance)
    {
        if (__instance?.ItemController is not XUiC_HyperdimensionalSlot slot) return true;
        NetworkInventoryActionBridge.MoveOneToBackpack(slot, "use:" + __instance.consumeType,
            target => NetworkInventoryActionBridge.ActivateExisting(__instance, slot, target));
        return false;
    }
}

[HarmonyPatch(typeof(ItemActionEntryRepair), nameof(ItemActionEntryRepair.OnActivated))]
internal static class NetworkRepairActionPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ItemActionEntryRepair __instance)
    {
        if (__instance?.ItemController is not XUiC_HyperdimensionalSlot slot) return true;
        NetworkInventoryActionBridge.MoveOneToBackpack(slot, "repair",
            target => NetworkInventoryActionBridge.ActivateExisting(__instance, slot, target));
        return false;
    }
}

[HarmonyPatch(typeof(ItemActionEntryAssemble), nameof(ItemActionEntryAssemble.OnActivated))]
internal static class NetworkAssembleActionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemActionEntryAssemble __instance)
    {
        if (__instance?.ItemController is not XUiC_HyperdimensionalSlot slot) return true;
        NetworkInventoryActionBridge.MoveOneToBackpack(slot, "assemble",
            target => NetworkInventoryActionBridge.ActivateExisting(__instance, slot, target));
        return false;
    }
}

[HarmonyPatch(typeof(ItemActionEntryWear), nameof(ItemActionEntryWear.OnActivated))]
internal static class NetworkWearActionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemActionEntryWear __instance)
    {
        if (__instance?.ItemController is not XUiC_HyperdimensionalSlot slot) return true;
        NetworkInventoryActionBridge.MoveOneToBackpack(slot, "wear",
            target => NetworkInventoryActionBridge.ActivateExisting(__instance, slot, target));
        return false;
    }
}

[HarmonyPatch(typeof(ItemActionEntryDrop), nameof(ItemActionEntryDrop.OnActivated))]
internal static class NetworkDropActionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemActionEntryDrop __instance)
    {
        if (__instance?.ItemController is not XUiC_HyperdimensionalSlot slot) return true;
        NetworkInventoryActionBridge.DropStack(slot);
        return false;
    }
}
