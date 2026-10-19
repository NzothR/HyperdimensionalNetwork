using HarmonyLib;

namespace HyperdimensionalNetwork;

[HarmonyPatch(typeof(XUiC_ItemStack), "OnHovered")]
internal static class ItemStackHoverCleanupPatch
{
    private static bool _loggedMissingCallout;

    [HarmonyPrefix]
    private static bool BeforeHovered(XUiC_ItemStack __instance)
    {
        if (__instance?.xui?.CalloutWindow != null) return true;
        if (__instance != null) __instance.isOver = false;
        if (!_loggedMissingCallout)
        {
            _loggedMissingCallout = true;
            NetworkLog.Debug("Skipped item-stack hover callback because the callout window is unavailable; UI teardown is in progress.");
        }
        return false;
    }
}
