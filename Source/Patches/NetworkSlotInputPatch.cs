using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_ItemStack))]
internal static class NetworkSlotInputPatch
{
    [HarmonyPrefix, HarmonyPatch(nameof(XUiC_ItemStack.HandlePartialStackPickup))]
    private static bool PartialStackPickupPrefix(XUiC_ItemStack __instance)
    {
        if (__instance is not XUiC_HyperdimensionalSlot slot) return true;
        NetworkLog.Debug("Partial drag pickup routed through network transaction. slot=" + slot.SlotNumber);
        slot.TakeFromNetwork(half: true);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(XUiC_ItemStack.HandleMoveToPreferredLocation))]
    private static bool PreferredLocationPrefix(XUiC_ItemStack __instance)
    {
        if (__instance is not XUiC_HyperdimensionalSlot slot) return true;
        NetworkLog.Debug("Preferred-location pickup routed through network transaction. slot=" + slot.SlotNumber);
        slot.TakeFromNetwork(half: false);
        return false;
    }
}
