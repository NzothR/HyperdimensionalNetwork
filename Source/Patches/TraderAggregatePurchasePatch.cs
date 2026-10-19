using HarmonyLib;

namespace HyperdimensionalNetwork;

[HarmonyPatch]
internal static class TraderAggregatePurchasePatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(XUiC_ItemInfoWindow), "GetBindingValueInternal")]
    private static bool BeforeBinding(XUiC_ItemInfoWindow __instance, ref string value, string bindingName,
        ref bool __result)
    {
        if (!XUiC_TraderMarketWindow.TryGetAggregateOffer(__instance, out _, out TraderOffer offer)) return true;
        switch (bindingName)
        {
            case "shownormaloptions":
                value = "False";
                break;
            case "showtraderoptions":
                value = "True";
                break;
            case "itemcost":
                value = GetPrice(__instance, offer).ToString();
                break;
            case "pricelabel":
                value = Localization.Get("xuiBuyPrice");
                break;
            case "markup":
                value = string.Empty;
                break;
            default:
                return true;
        }
        __result = true;
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(XUiC_ItemInfoWindow), "Counter_OnCountChanged")]
    private static void AfterCountChanged(XUiC_ItemInfoWindow __instance)
    {
        if (!XUiC_TraderMarketWindow.TryGetAggregateOffer(__instance, out _, out _)) return;
        __instance.traderActionItemList?.RefreshActionList();
        __instance.RefreshBindings();
    }

    internal static int GetPrice(XUiC_ItemInfoWindow info, TraderOffer offer)
    {
        int count = info?.BuySellCounter?.Count ?? 0;
        int bundle = offer?.BundleSize ?? 0;
        if (bundle <= 0 || count <= 0 || count > offer.Count) return 0;
        return (int)System.Math.Min(int.MaxValue, (long)offer.BundlePrice * (count / bundle));
    }
}

internal sealed class NetworkItemActionEntryAggregateBuy : BaseItemActionEntry
{
    private readonly XUiC_TraderMarketWindow _window;

    internal NetworkItemActionEntryAggregateBuy(XUiC_TraderMarketWindow window)
        : base(window, "lblContextActionBuy", "ui_game_symbol_coin")
    {
        _window = window;
    }

    public override void RefreshEnabled()
    {
        XUiC_ItemInfoWindow info = _window?.xui?.GetChildByType<XUiC_ItemInfoWindow>();
        Enabled = XUiC_TraderMarketWindow.TryGetAggregateOffer(info, out _, out TraderOffer offer)
            && _window.CanPurchaseFromDetails(info, offer);
    }

    public override void OnDisabledActivate()
    {
        EntityPlayerLocal player = _window?.xui?.playerUI?.entityPlayer;
        if ((bool)player) GameManager.ShowTooltip(player, Localization.Get("ttCannotBuyItem"));
    }

    public override void OnActivated()
    {
        RefreshEnabled();
        if (!Enabled)
        {
            OnDisabledActivate();
            return;
        }
        XUiC_ItemInfoWindow info = _window.xui.GetChildByType<XUiC_ItemInfoWindow>();
        _window.BuySelectedFromDetails(info);
    }
}
