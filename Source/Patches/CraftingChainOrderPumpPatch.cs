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

[HarmonyPatch(typeof(XUiC_RecipeStack), nameof(XUiC_RecipeStack.ForceCancel))]
internal static class CraftingChainQueueCancelPatch
{
    [HarmonyPrefix]
    private static bool Prefix(XUiC_RecipeStack __instance)
    {
        return !CraftingChainOrderService.TryHandleQueueCancellation(__instance);
    }
}
