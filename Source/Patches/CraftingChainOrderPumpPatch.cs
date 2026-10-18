using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_CraftingQueue), nameof(XUiC_CraftingQueue.Update))]
internal static class CraftingChainOrderPumpPatch
{
    [HarmonyPostfix]
    private static void Postfix(XUiC_CraftingQueue __instance)
    {
        CraftingChainOrderService.Update(__instance?.xui);
    }
}
