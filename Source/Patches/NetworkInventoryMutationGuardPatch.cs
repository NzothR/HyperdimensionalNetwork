using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_ItemStack), nameof(XUiC_ItemStack.Update))]
internal static class NetworkInventoryMutationGuardPatch
{
    [HarmonyPrefix]
    private static bool UpdatePrefix()
    {
        return !NetworkRemoteClient.IsRemoteClient || !NetworkRemoteClient.IsMutationPending;
    }
}
