using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(ItemActionEntryScrap), nameof(ItemActionEntryScrap.OnActivated))]
internal static class NetworkScrapActionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemActionEntryScrap __instance)
    {
        if (__instance?.ItemController is not XUiC_HyperdimensionalSlot slot) return true;
        NetworkScrapService.Activate(slot, __instance);
        return false;
    }
}
