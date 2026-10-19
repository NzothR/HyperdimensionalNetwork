using System;
using HarmonyLib;

namespace HyperdimensionalNetwork;

[HarmonyPatch]
internal static class TraderSellActionPatch
{
    internal static bool IsSaleContext(XUiC_ItemInfoWindow info, XUiC_ItemStack slot)
    {
        if (info == null || slot?.ItemStack == null || slot.ItemStack.IsEmpty()
            || !TraderTransactionService.VoidSaleMode || info.isOpenAsTrader) return false;
        if (slot is not XUiC_HyperdimensionalSlot
            && slot.StackLocation != XUiC_ItemStack.StackLocationTypes.Backpack
            && slot.StackLocation != XUiC_ItemStack.StackLocationTypes.ToolBelt) return false;
        return slot.xui?.playerUI?.windowManager?.IsWindowOpen("hyperdimensionalNetwork") == true;
    }

    internal static void RefreshSelectedItem(XUi xui)
    {
        XUiC_ItemInfoWindow info = xui?.GetChildByType<XUiC_ItemInfoWindow>();
        if (info?.selectedItemStack != null) info.SetItemStack(info.selectedItemStack);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(XUiC_ItemInfoWindow), "SetInfo")]
    private static void AfterSetInfo(XUiC_ItemInfoWindow __instance, ItemStack stack, XUiController controller)
    {
        if (controller is not XUiC_ItemStack slot || !IsSaleContext(__instance, slot)
            || __instance.traderActionItemList == null || __instance.BuySellCounter == null) return;
        int bundle = 1;
        try
        {
            ItemClass itemClass = stack?.itemValue?.ItemClass;
            if (itemClass != null)
                bundle = Math.Max(1, itemClass.IsBlock()
                    ? Block.list[stack.itemValue.type].EconomicBundleSize : itemClass.EconomicBundleSize);
        }
        catch (Exception ex)
        {
            NetworkLog.Debug("Manual sale count selector used a one-item step after bundle lookup failed. item="
                + stack?.itemValue?.type + "; exception=" + ex.Message);
        }
        XUiC_Counter counter = __instance.BuySellCounter;
        counter.Step = bundle;
        counter.MaxCount = stack.count / bundle * bundle;
        int chosen = __instance.SetMaxCountOnDirty ? counter.MaxCount : Math.Min(counter.MaxCount, counter.Count);
        counter.Count = chosen > 0 ? Math.Max(bundle, chosen / bundle * bundle) : 0;
        __instance.SetMaxCountOnDirty = false;
        counter.ForceTextRefresh();
        __instance.traderActionItemList.AddActionListEntry(new NetworkItemActionEntrySell(slot));
        __instance.traderActionItemList.RefreshActionList();
        __instance.RefreshBindings();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(XUiC_ItemInfoWindow), "GetBindingValueInternal")]
    private static void AfterBinding(XUiC_ItemInfoWindow __instance, ref string value, string bindingName, ref bool __result)
    {
        if (!__result || !IsSaleContext(__instance, __instance.selectedItemStack)) return;
        switch (bindingName)
        {
            case "shownormaloptions": value = "False"; break;
            case "showtraderoptions": value = "True"; break;
            case "itemcost":
            {
                int price = GetDisplayedPrice(__instance);
                value = price > 0 ? price.ToString() : Localization.Get("xuiNoSellPrice");
                break;
            }
            case "pricelabel":
                value = GetDisplayedPrice(__instance) > 0 ? Localization.Get("xuiSellPrice") : string.Empty;
                break;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(XUiC_ItemInfoWindow), "Counter_OnCountChanged")]
    private static void AfterCountChanged(XUiC_ItemInfoWindow __instance)
    {
        if (!IsSaleContext(__instance, __instance.selectedItemStack)) return;
        __instance.traderActionItemList?.RefreshActionList();
        __instance.RefreshBindings();
    }

    internal static int GetDisplayedPrice(XUiC_ItemInfoWindow info)
    {
        ItemStack stack = info?.selectedItemStack?.ItemStack;
        int count = info?.BuySellCounter?.Count ?? 0;
        if (stack == null || stack.IsEmpty() || count <= 0 || count > stack.count
            || !TraderTransactionService.CanSell(stack.itemValue)) return 0;
        try
        {
            return TraderTransactionService.GetVirtualSellPrice(info.xui?.playerUI?.entityPlayer,
                stack.itemValue, count, stack.itemValue.ItemClass);
        }
        catch (Exception ex)
        {
            NetworkLog.Debug("Virtual sale preview price could not be calculated. item=" + stack.itemValue.type
                + "; count=" + count + "; exception=" + ex.Message);
            return 0;
        }
    }
}

internal sealed class NetworkItemActionEntrySell : BaseItemActionEntry
{
    private readonly XUiC_ItemStack _slot;

    internal NetworkItemActionEntrySell(XUiC_ItemStack slot)
        : base(slot, "HyperdimensionalNetworkTraderSellAction", "ui_game_symbol_coin")
    {
        _slot = slot;
    }

    public override void RefreshEnabled()
    {
        XUiC_ItemInfoWindow info = _slot.InfoWindow;
        Enabled = TraderSellActionPatch.IsSaleContext(info, _slot)
            && !_slot.StackLock && !NetworkRemoteClient.IsMutationPending
            && TraderSellActionPatch.GetDisplayedPrice(info) > 0;
    }

    public override void OnDisabledActivate()
    {
        EntityPlayerLocal player = _slot.xui?.playerUI?.entityPlayer;
        if ((bool)player) GameManager.ShowTooltip(player, Localization.Get("ttCannotSellItem"));
    }

    public override void OnActivated()
    {
        RefreshEnabled();
        if (!Enabled) { OnDisabledActivate(); return; }
        EntityPlayerLocal player = _slot.xui?.playerUI?.entityPlayer;
        XUiC_ItemInfoWindow info = _slot.InfoWindow;
        int count = info.BuySellCounter.Count;
        bool networkSource = _slot is XUiC_HyperdimensionalSlot;
        int itemType = _slot.ItemStack.itemValue.type;
        bool sent = TraderTransactionService.SellFromSlot(player, _slot, networkSource, count, result =>
        {
            if (!result.Success)
            {
                GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkTraderSaleFailed") + ": " + result.Error);
                NetworkLog.Debug("Manual sale rejected. player=" + player.entityId + "; item=" + itemType
                    + "; count=" + count + "; requestId=" + result.RequestId + "; error=" + result.Error);
                return;
            }
            if (result.BagData != null || result.InventoryData != null)
            {
                if (!NetworkInventorySync.Apply(player, result))
                    NetworkLog.Error("Manual sale committed but inventory snapshot could not be applied. player="
                        + player.entityId + "; requestId=" + result.RequestId);
                _slot.xui.GetChildByType<XUiC_Backpack>()?.RefreshBackpackSlots();
            }
            _slot.xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
            info.IsDirty = true;
            GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkTraderSaleComplete") + " " + result.Amount);
            NetworkLog.Debug("Manual sale completed. player=" + player.entityId + "; networkSource=" + networkSource
                + "; item=" + itemType + "; count=" + count + "; proceeds=" + result.Amount + "; requestId=" + result.RequestId);
        });
        if (!sent) NetworkLog.Debug("Manual sale could not be queued. player=" + player?.entityId + "; item=" + itemType);
    }
}
