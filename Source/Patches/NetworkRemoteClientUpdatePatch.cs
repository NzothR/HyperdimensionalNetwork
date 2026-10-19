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
        LocalPlayerUI playerUI = LocalPlayerUI.GetUIForPrimaryPlayer();
        XUiC_TraderMarketWindow.ProcessPendingReturn(playerUI);
        XUiC_TraderRulesWindow.ProcessPendingReturn(playerUI);
        if ((bool)playerUI) CraftingChainOrderService.Update(playerUI.xui);
        NetworkMembershipClient.Update();
    }
}
