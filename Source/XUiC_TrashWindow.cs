namespace HyperdimensionalNetwork;

public sealed class XUiC_TrashWindow : XUiController
{
    public override void Init()
    {
        base.Init();
        XUiController close = GetChildById("btnHyperdimensionalTrashClose");
        if (close != null) close.OnPress += (sender, button) => xui?.playerUI?.windowManager?.Close("hyperdimensionalTrash");
    }

    public override void OnOpen()
    {
        base.OnOpen();
        GUIWindowManager manager = xui?.playerUI?.windowManager;
        if (manager?.IsWindowOpen("backpack") != true) manager?.Open("backpack", _bModal: false);
        xui?.RecenterWindowGroup(windowGroup);
        NetworkLog.Debug("Manual trash window opened. player=" + (xui?.playerUI?.entityPlayer?.entityId ?? -1));
    }
}
