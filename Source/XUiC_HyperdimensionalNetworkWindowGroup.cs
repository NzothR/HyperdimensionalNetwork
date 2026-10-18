namespace HyperdimensionalNetwork;

public sealed class XUiC_HyperdimensionalNetworkWindowGroup : XUiController
{
    public override void OnOpen()
    {
        base.OnOpen();
        NetworkLog.Debug("Network window group opened. player=" + (xui?.playerUI?.entityPlayer?.PlayerDisplayName ?? "<none>"));
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if ((bool)player)
        {
            GUIWindowManager manager = player.playerUI?.windowManager;
            if (manager != null && !manager.IsWindowOpen("windowpaging") && manager.TryGetWindow("windowpaging", out GUIWindow paging)) manager.Open(paging, false);
            xui.FindWindowGroupByName("windowpaging")?.GetChildByType<XUiC_WindowSelector>()?.SetSelected("hyperdimensionalNetwork");
        }
    }
}
