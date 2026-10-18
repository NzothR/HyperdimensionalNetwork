using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(GameManager), nameof(GameManager.Update))]
internal static class NetworkRemoteClientUpdatePatch
{
    [HarmonyPostfix]
    private static void UpdatePostfix()
    {
        if (NetworkRemoteClient.IsRemoteClient) NetworkRemoteClient.Update();
        AutoRestockClient.Update();
    }
}
