namespace HyperdimensionalNetwork;

public sealed class XUiC_TraderOfferSlot : XUiC_ItemStack
{
    private TraderOffer _offer;
    private XUiC_TraderMarketWindow _market;
    private XUiController _buyButton;
    private XUiController _priceLabel;

    public override void Init()
    {
        base.Init();
        _buyButton = GetChildById("btnTraderOfferBuy");
        _priceLabel = GetChildById("traderOfferPrice");
        if (_buyButton != null) _buyButton.OnPress += OnBuyPressed;
        SetOffer(null);
    }

    internal void Bind(XUiC_TraderMarketWindow market) => _market = market;

    internal void SetOffer(TraderOffer offer)
    {
        _offer = offer;
        ItemStack = offer == null ? ItemStack.Empty.Clone() : new ItemStack(offer.Value.Clone(), offer.Count);
        bool visible = offer != null;
        if (ViewComponent != null && ViewComponent.IsVisible != visible) ViewComponent.IsVisible = visible;
        if (_buyButton?.ViewComponent != null && _buyButton.ViewComponent.IsVisible != visible) _buyButton.ViewComponent.IsVisible = visible;
        if (_priceLabel?.ViewComponent is XUiV_Label label)
        {
            string text = offer == null ? string.Empty : offer.BundlePrice + " / " + offer.BundleSize;
            if (label.Text != text) { label.Text = text; _priceLabel.IsDirty = true; }
        }
    }

    private void OnBuyPressed(XUiController sender, int mouseButton)
    {
        if (_offer == null || _market == null) return;
        _market.Buy(_offer, _offer.BundleSize);
    }
}
