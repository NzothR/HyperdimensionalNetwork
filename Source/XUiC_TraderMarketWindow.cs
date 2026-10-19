using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class XUiC_TraderMarketWindow : XUiController
{
    private const int PageSize = 12;
    private static bool _buyToBackpack;
    private static int _pendingReturnPlayerId = -1;
    private readonly List<TraderOffer> _offers = new List<TraderOffer>();
    private readonly List<TraderOffer> _filtered = new List<TraderOffer>();
    private readonly XUiController[] _rows = new XUiController[PageSize];
    private readonly XUiController[] _icons = new XUiController[PageSize];
    private readonly XUiController[] _names = new XUiController[PageSize];
    private readonly XUiController[] _qualities = new XUiController[PageSize];
    private readonly XUiController[] _prices = new XUiController[PageSize];
    private readonly XUiController[] _rowButtons = new XUiController[PageSize];
    private XUiC_Paging _pager;
    private XUiC_TextInput _search;
    private XUiController _status;
    private XUiController _selectedLabel;
    private XUiController _countLabel;
    private XUiController _totalPriceLabel;
    private TraderOffer _selected;
    private int _selectedCount;
    private string _sessionId = string.Empty;
    private bool _requestPending;
    private bool _closing;
    private float _nextRefreshAt;

    public override void Init()
    {
        base.Init();
        for (int i = 0; i < PageSize; i++)
        {
            int index = i;
            _rows[i] = GetChildById("marketRow" + i);
            _rowButtons[i] = GetChildById("btnMarketRow" + i);
            _icons[i] = GetChildById("marketRowIcon" + i);
            _names[i] = GetChildById("marketRowName" + i);
            _qualities[i] = GetChildById("marketRowQuality" + i);
            _prices[i] = GetChildById("marketRowPrice" + i);
            if (_rowButtons[i] != null) _rowButtons[i].OnPress += (sender, button) => SelectAt(index);
        }
        _pager = GetChildByType<XUiC_Paging>();
        if (_pager != null) _pager.OnPageChanged += RefreshRows;
        _search = GetChildById("traderMarketSearch") as XUiC_TextInput;
        if (_search != null) _search.OnChangeHandler += (sender, value, code) => ApplyFilter();
        _status = GetChildById("traderMarketStatus");
        _selectedLabel = GetChildById("marketSelected");
        _countLabel = GetChildById("marketCount");
        _totalPriceLabel = GetChildById("marketTotalPrice");
        BindPress("btnTraderMarketClose", OnClosePressed);
        BindPress("btnTraderMarketOutput", OnOutputPressed);
        BindPress("btnMarketCountDown", (sender, button) => ChangeCount(-1));
        BindPress("btnMarketCountUp", (sender, button) => ChangeCount(1));
        BindPress("btnMarketCountMax", (sender, button) => SetCountToMaximum());
        BindPress("btnMarketPurchase", (sender, button) => BuySelected());
        NetworkLog.Debug("Trader market list initialized. rows=" + _rows.Count(row => row != null)
            + "; pager=" + (_pager != null) + "; search=" + (_search != null));
        if (_rows.Any(row => row == null) || _rowButtons.Any(button => button == null))
            NetworkLog.Error("Trader market row controls are incomplete; expected " + PageSize + " selectable rows.");
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _closing = false;
        _requestPending = true;
        _sessionId = string.Empty;
        _selected = null;
        _selectedCount = 0;
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(xui?.playerUI?.entityPlayer);
        _buyToBackpack = !string.IsNullOrEmpty(playerId)
            && PlayerPrefs.GetInt("HyperdimensionalNetwork.TraderBuyToBackpack." + playerId, 0) != 0;
        _offers.Clear();
        _filtered.Clear();
        if (_search != null) _search.Text = string.Empty;
        RefreshRows();
        SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketLoading"));
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !TraderMarketClient.Open(player, OnMarketOpened))
        {
            _requestPending = false;
            SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketOpenFailed"));
            NetworkLog.Error("Trader market open could not be requested from the list window.");
        }
    }

    public override void Update(float _dt)
    {
        base.Update(_dt);
        if (_closing || _requestPending || string.IsNullOrEmpty(_sessionId)
            || Time.realtimeSinceStartup < _nextRefreshAt) return;
        _nextRefreshAt = Time.realtimeSinceStartup + 1f;
        _requestPending = true;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !TraderMarketClient.Query(player, _sessionId, OnMarketRefreshed))
            _requestPending = false;
    }

    public override void OnClose()
    {
        _closing = true;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if ((bool)player && !string.IsNullOrEmpty(_sessionId)) TraderMarketClient.Close(player, _sessionId);
        _sessionId = string.Empty;
        _offers.Clear();
        _filtered.Clear();
        _selected = null;
        _requestPending = false;
        RefreshRows();
        base.OnClose();
        if ((bool)player && GameManager.Instance?.World != null)
            _pendingReturnPlayerId = player.entityId;
    }

    private void SelectAt(int row)
    {
        int index = (_pager?.CurrentPageNumber ?? 0) * PageSize + row;
        if (index < 0 || index >= _filtered.Count) return;
        _selected = _filtered[index];
        _selectedCount = Math.Max(1, _selected.BundleSize);
        RefreshRows();
        ShowOfferDetails(_selected);
        NetworkLog.Debug("Aggregate trader offer selected. trader=" + _selected.TraderEntityId
            + "; inventoryIndex=" + _selected.InventoryIndex + "; item=" + _selected.Value.type);
    }

    private void ShowOfferDetails(TraderOffer offer)
    {
        XUiC_ItemInfoWindow info = xui?.GetChildByType<XUiC_ItemInfoWindow>();
        if (info == null || offer?.Value == null) return;
        try
        {
            info.ClearSelectedStacks();
            info.makeVisible(true);
            XUiController empty = xui.FindWindowGroupByName("backpack")?.GetChildById("emptyInfoPanel");
            if (empty?.ViewComponent != null) empty.ViewComponent.IsVisible = false;
            info.SetInfo(new ItemStack(offer.Value.Clone(), offer.Count), null,
                XUiC_ItemActionList.ItemActionListTypes.None);
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Aggregate trader item details could not be displayed. trader="
                + offer.TraderEntityId + "; item=" + offer.Value.type + "; exception=" + ex);
        }
    }

    private void ChangeCount(int steps)
    {
        if (_selected == null) return;
        int bundle = Math.Max(1, _selected.BundleSize);
        int maximum = _selected.Count / bundle * bundle;
        _selectedCount = Mathf.Clamp(_selectedCount + steps * bundle, bundle, Math.Max(bundle, maximum));
        RefreshPurchaseBar();
    }

    private void SetCountToMaximum()
    {
        if (_selected == null) return;
        int bundle = Math.Max(1, _selected.BundleSize);
        _selectedCount = _selected.Count / bundle * bundle;
        RefreshPurchaseBar();
    }

    private void BuySelected()
    {
        if (_selected != null && _selectedCount > 0) Buy(_selected, _selectedCount);
    }

    internal void Buy(TraderOffer offer, int count)
    {
        if (_closing || _requestPending || string.IsNullOrEmpty(_sessionId) || offer == null) return;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _requestPending = true;
        SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketPurchasing"));
        if (!TraderMarketClient.Buy(player, _sessionId, offer, count, _buyToBackpack, result =>
        {
            _requestPending = false;
            if (!result.Success)
            {
                SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketBuyFailed") + ": " + result.Error);
                NetworkLog.Debug("Aggregate trader purchase rejected. player=" + player.entityId
                    + "; trader=" + offer.TraderEntityId + "; item=" + offer.Value.type + "; error=" + result.Error);
                return;
            }
            if (result.BagData != null || result.InventoryData != null)
            {
                if (!NetworkInventorySync.Apply(player, result))
                    NetworkLog.Error("Trader purchase committed but its personal inventory snapshot could not be applied. player="
                        + player.entityId + "; requestId=" + result.RequestId);
                xui.GetChildByType<XUiC_Backpack>()?.RefreshBackpackSlots();
            }
            ApplyResult(result);
            SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketPurchaseComplete") + " " + result.Amount);
            GameManager.ShowTooltip(player,
                Localization.Get("HyperdimensionalNetworkTraderMarketPurchaseComplete") + " " + result.Amount,
                false, false, 0f);
            NetworkLog.Debug("Aggregate trader purchase displayed. player=" + player.entityId
                + "; trader=" + offer.TraderEntityId + "; item=" + offer.Value.type + "; count=" + result.Amount);
        })) _requestPending = false;
    }

    private void OnMarketOpened(NetworkRemoteResult result)
    {
        _requestPending = false;
        if (_closing) return;
        if (!result.Success || string.IsNullOrEmpty(result.TraderSessionId))
        {
            SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketOpenFailed") + ": " + result.Error);
            NetworkLog.Error("Trader market session failed to open. error=" + result.Error);
            return;
        }
        _sessionId = result.TraderSessionId;
        ApplyResult(result);
        _nextRefreshAt = Time.realtimeSinceStartup + 0.25f;
        NetworkLog.Debug("Trader market session opened in list UI. session=" + _sessionId
            + "; offers=" + _offers.Count);
    }

    private void OnMarketRefreshed(NetworkRemoteResult result)
    {
        _requestPending = false;
        if (_closing) return;
        if (!result.Success)
        {
            SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketRefreshFailed") + ": " + result.Error);
            NetworkLog.Debug("Trader market refresh failed. session=" + _sessionId + "; error=" + result.Error);
            return;
        }
        ApplyResult(result);
    }

    private void ApplyResult(NetworkRemoteResult result)
    {
        int selectedTrader = _selected?.TraderEntityId ?? -1;
        int selectedIndex = _selected?.InventoryIndex ?? -1;
        _offers.Clear();
        if (result.TraderOffers != null)
            _offers.AddRange(result.TraderOffers.Where(offer => offer?.Value != null && offer.Count > 0));
        _offers.Sort((left, right) =>
        {
            int byName = string.Compare(left.Value.ItemClass?.GetLocalizedItemName(),
                right.Value.ItemClass?.GetLocalizedItemName(), StringComparison.CurrentCultureIgnoreCase);
            if (byName != 0) return byName;
            int byPrice = left.BundlePrice.CompareTo(right.BundlePrice);
            return byPrice != 0 ? byPrice : left.TraderEntityId.CompareTo(right.TraderEntityId);
        });
        _selected = _offers.FirstOrDefault(offer => offer.TraderEntityId == selectedTrader
            && offer.InventoryIndex == selectedIndex);
        if (_selected != null)
        {
            int bundle = Math.Max(1, _selected.BundleSize);
            _selectedCount = Mathf.Clamp(_selectedCount / bundle * bundle, bundle,
                Math.Max(bundle, _selected.Count / bundle * bundle));
        }
        else _selectedCount = 0;
        ApplyFilter();
        if (_offers.Count == 0) SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketNoOffers"));
        else SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketOfferCount") + " " + _offers.Count);
    }

    private void ApplyFilter()
    {
        _filtered.Clear();
        string search = _search?.Text?.Trim();
        foreach (TraderOffer offer in _offers)
        {
            string name = offer.Value.ItemClass?.GetLocalizedItemName() ?? string.Empty;
            if (string.IsNullOrEmpty(search) || name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0)
                _filtered.Add(offer);
        }
        if (_selected != null && !_filtered.Contains(_selected))
        {
            _selected = null;
            _selectedCount = 0;
        }
        _pager?.SetLastPageByElementsAndPageLength(_filtered.Count, PageSize);
        RefreshRows();
    }

    private void RefreshRows()
    {
        int first = (_pager?.CurrentPageNumber ?? 0) * PageSize;
        for (int i = 0; i < PageSize; i++)
        {
            TraderOffer offer = first + i < _filtered.Count ? _filtered[first + i] : null;
            SetVisible(_rows[i], offer != null);
            if (offer == null) continue;
            if (_icons[i]?.ViewComponent is XUiV_Sprite icon)
                icon.SpriteName = offer.Value.ItemClass?.GetIconName() ?? string.Empty;
            SetLabel(_names[i], (offer.Value.ItemClass?.GetLocalizedItemName() ?? "?")
                + (offer.Count > 1 ? " (" + offer.Count + ")" : string.Empty));
            SetLabel(_qualities[i], offer.Value.Quality > 0 ? offer.Value.Quality.ToString() : "-");
            SetLabel(_prices[i], offer.BundlePrice.ToString());
            if (_rowButtons[i]?.ViewComponent is XUiV_Button button) button.Selected = ReferenceEquals(offer, _selected);
        }
        RefreshPurchaseBar();
    }

    private void RefreshPurchaseBar()
    {
        SetLabel(_selectedLabel, _selected?.Value?.ItemClass?.GetLocalizedItemName() ?? string.Empty);
        SetLabel(_countLabel, _selected == null ? "-" : _selectedCount.ToString());
        long total = _selected == null || _selected.BundleSize <= 0 ? 0
            : (long)_selected.BundlePrice * (_selectedCount / _selected.BundleSize);
        SetLabel(_totalPriceLabel, total > 0 ? total + " $" : string.Empty);
    }

    private void OnOutputPressed(XUiController sender, int mouseButton)
    {
        _buyToBackpack = !_buyToBackpack;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (!string.IsNullOrEmpty(playerId))
        {
            PlayerPrefs.SetInt("HyperdimensionalNetwork.TraderBuyToBackpack." + playerId,
                _buyToBackpack ? 1 : 0);
            PlayerPrefs.Save();
        }
        if ((bool)player) GameManager.ShowTooltip(player,
            Localization.Get(_buyToBackpack
                ? "HyperdimensionalNetworkTraderBuyToBackpack"
                : "HyperdimensionalNetworkTraderBuyToNetwork"), false, false, 0f);
        NetworkLog.Debug("Trader market purchase destination changed. backpack=" + _buyToBackpack);
    }

    private void OnClosePressed(XUiController sender, int mouseButton)
    {
        GUIWindowManager manager = xui?.playerUI?.windowManager;
        if (manager == null) return;
        manager.Close("hyperdimensionalTraderMarket");
        manager.Open("hyperdimensionalNetwork", false);
        NetworkLog.Debug("Trader market closed by button; network window restored. player="
            + (xui?.playerUI?.entityPlayer?.entityId ?? -1));
    }

    internal static void ProcessPendingReturn(LocalPlayerUI playerUI)
    {
        if (_pendingReturnPlayerId < 0) return;
        EntityPlayerLocal player = playerUI?.entityPlayer;
        GUIWindowManager manager = playerUI?.windowManager;
        if (!(bool)player || GameManager.Instance?.World == null)
        {
            _pendingReturnPlayerId = -1;
            return;
        }
        if (player.entityId != _pendingReturnPlayerId || manager == null) return;
        if (manager.IsWindowOpen("hyperdimensionalTraderMarket")) return;
        _pendingReturnPlayerId = -1;
        if (!manager.IsWindowOpen("hyperdimensionalNetwork"))
        {
            manager.Open("hyperdimensionalNetwork", false);
            NetworkLog.Debug("Trader market close returned to the network window. player=" + player.entityId);
        }
    }

    private void SetStatus(string value) => SetLabel(_status, value);

    private static void SetVisible(XUiController controller, bool visible)
    {
        if (controller?.ViewComponent != null && controller.ViewComponent.IsVisible != visible)
            controller.ViewComponent.IsVisible = visible;
    }

    private static void SetLabel(XUiController controller, string value)
    {
        if (controller?.ViewComponent is XUiV_Label label && label.Text != (value ?? string.Empty))
        {
            label.Text = value ?? string.Empty;
            controller.IsDirty = true;
        }
    }

    private void BindPress(string id, XUiEvent_OnPressEventHandler handler)
    {
        XUiController control = GetChildById(id);
        if (control != null) control.OnPress += handler;
        else NetworkLog.Error("Trader market control was not found. id=" + id);
    }
}
