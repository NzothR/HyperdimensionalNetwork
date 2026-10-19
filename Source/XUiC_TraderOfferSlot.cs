namespace HyperdimensionalNetwork;

public sealed class XUiC_TraderOfferSlot : XUiC_ItemStack
{

    internal void SetOffer(TraderOffer offer)
    {
        ItemStack = offer == null ? ItemStack.Empty.Clone() : new ItemStack(offer.Value.Clone(), offer.Count);
        bool visible = offer != null;
        if (ViewComponent != null && ViewComponent.IsVisible != visible) ViewComponent.IsVisible = visible;
    }
}
