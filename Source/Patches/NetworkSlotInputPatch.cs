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
        NetworkLog.Debug("Preferred-location withdrawal routed to player inventory. slot=" + slot.SlotNumber);
        slot.MoveFromNetworkToPlayer();
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(XUiC_ItemStack.HandleStackSwap))]
    private static bool StackSwapPrefix(XUiC_ItemStack __instance)
    {
        if (__instance is not XUiC_HyperdimensionalSlot slot) return true;
        NetworkLog.Debug("Stack swap routed through network transaction. slot=" + slot.SlotNumber);
        slot.HandleStackSwapFromNetwork();
        return false;
    }
}
