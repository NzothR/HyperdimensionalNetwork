using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(ItemStack), nameof(ItemStack.CanMoveTo))]
internal static class ManualTrashProtectedItemPatch
{
    [HarmonyPostfix]
    private static void Postfix(ItemStack __instance, XUiC_ItemStack.StackLocationTypes locationType, ref bool __result)
    {
        if (__result && locationType == XUiC_ItemStack.StackLocationTypes.LootContainer
            && ManualTrashService.IsOpen && __instance != null && !__instance.IsEmpty()
            && !NetworkItemPolicy.CanEnterOrDestroy(__instance.itemValue)) __result = false;
    }
}
