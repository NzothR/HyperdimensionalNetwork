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
            XUiC_WindowSelector selector = xui.FindWindowGroupByName("windowpaging")?.GetChildByType<XUiC_WindowSelector>();
            selector?.SetSelected("hyperdimensionalNetwork");
            if (manager != null && !manager.IsWindowOpen("windowpaging") && manager.TryGetWindow("windowpaging", out GUIWindow paging)) manager.Open(paging, false);
            selector?.SetSelected("hyperdimensionalNetwork");
            NetworkLog.Debug("Network group modal state after open. player=" + player.entityId
                + "; networkOpen=" + (manager?.IsWindowOpen("hyperdimensionalNetwork") ?? false)
                + "; modal=" + (manager?.GetModalWindow()?.Id ?? "none")
                + "; inputLocked=" + (manager?.IsInputLocked ?? false));
        }
    }

    public override void OnClose()
    {
        GUIWindowManager manager = xui?.playerUI?.windowManager;
        base.OnClose();
        if (manager?.IsWindowOpen("windowpaging") == true) manager.Close("windowpaging");
        NetworkLog.Debug("Network window group closed. player="
            + (xui?.playerUI?.entityPlayer?.entityId ?? -1)
            + "; pagingOpen=" + (manager?.IsWindowOpen("windowpaging") ?? false)
            + "; networkOpen=" + (manager?.IsWindowOpen("hyperdimensionalNetwork") ?? false)
            + "; modal=" + (manager?.GetModalWindow()?.Id ?? "none")
            + "; inputLocked=" + (manager?.IsInputLocked ?? false));
    }
}
