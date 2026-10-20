using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class XUiC_TraderMarketWindow : XUiController
{
    private const int PageSize = 13;
    private static bool _buyToBackpack;
    private static int _pendingReturnPlayerId = -1;
    private static XUiC_TraderMarketWindow _activeWindow;
    private readonly List<TraderOffer> _offers = new List<TraderOffer>();
    private readonly List<MarketOfferGroup> _offerGroups = new List<MarketOfferGroup>();
    private readonly List<TraderOffer> _filtered = new List<TraderOffer>();
    private readonly XUiController[] _rows = new XUiController[PageSize];
    private readonly XUiController[] _icons = new XUiController[PageSize];
    private readonly XUiController[] _names = new XUiController[PageSize];
    private readonly XUiController[] _qualities = new XUiController[PageSize];
    private readonly XUiController[] _prices = new XUiController[PageSize];
    private readonly XUiController[] _rowButtons = new XUiController[PageSize];
    private XUiC_Paging _pager;
    private XUiC_TextInput _search;
    private XUiC_CategoryList _categories;
    private XUiController _scrollArea;
    private string _category = string.Empty;
    private XUiController _status;
    private XUiController _outputButton;
    private TraderOffer _selected;
    private int _selectedCount;
    private string _sessionId = string.Empty;
    private bool _requestPending;
    private bool _closing;
    private bool _hasOfferFingerprint;
    private ulong _offerFingerprint;
    private static bool _loggedReturnWait;
    private float _nextRefreshAt;

    private struct MarketOfferGroupKey : IEquatable<MarketOfferGroupKey>
    {
        public ItemValue Value;
        public int BundleSize;
        public int BundlePrice;

        public bool Equals(MarketOfferGroupKey other)
            => BundleSize == other.BundleSize && BundlePrice == other.BundlePrice
                && Value != null && other.Value != null && Value.Equals(other.Value);

        public override bool Equals(object obj) => obj is MarketOfferGroupKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Value?.type ?? 0;
                hash = hash * 397 ^ (Value?.Quality ?? 0);
                hash = hash * 397 ^ (Value?.Seed ?? 0);
                hash = hash * 397 ^ (Value?.UseTimes.GetHashCode() ?? 0);
                hash = hash * 397 ^ (Value?.Meta ?? 0);
                hash = hash * 397 ^ (Value?.SelectedAmmoTypeIndex ?? 0);
                hash = hash * 397 ^ (Value?.Activated == true ? 1 : 0);
                hash = hash * 397 ^ BundleSize;
                return hash * 397 ^ BundlePrice;
            }
        }
    }

    private sealed class MarketOfferGroup
    {
        public ItemValue Key;
        public TraderOffer Display;
        public readonly List<TraderOffer> Sources = new List<TraderOffer>();
    }

    public override void Init()
    {
        base.Init();
        PinyinSearch.Initialize();
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
            if (_rowButtons[i] != null) _rowButtons[i].OnScroll += OnMarketScroll;
        }
        _scrollArea = GetChildById("marketRows");
        if (_scrollArea != null) _scrollArea.OnScroll += OnMarketScroll;
        _pager = GetChildByType<XUiC_Paging>();
        if (_pager != null) _pager.OnPageChanged += RefreshRows;
        _search = GetChildById("traderMarketSearch") as XUiC_TextInput;
        if (_search != null) _search.OnChangeHandler += (sender, value, code) => ApplyFilter();
        _categories = GetChildById("marketCategoryList") as XUiC_CategoryList;
        if (_categories != null) _categories.CategoryChanged += category =>
        {
            _category = category?.CategoryName ?? string.Empty;
            NetworkLog.Debug("Trader market category changed. category=" + _category);
            ApplyFilter();
        };
        _status = GetChildById("traderMarketStatus");
        _outputButton = GetChildById("btnTraderMarketOutput");
        BindPress("btnTraderMarketClose", OnClosePressed);
        BindPress("btnTraderMarketOutput", OnOutputPressed);
        NetworkLog.Debug("Trader market list initialized. rows=" + _rows.Count(row => row != null)
            + "; pager=" + (_pager != null) + "; search=" + (_search != null));
        if (_rows.Any(row => row == null) || _rowButtons.Any(button => button == null))
            NetworkLog.Error("Trader market row controls are incomplete; expected " + PageSize + " selectable rows.");
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _activeWindow = this;
        _closing = false;
        _requestPending = true;
        _sessionId = string.Empty;
        _selected = null;
        _selectedCount = 0;
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(xui?.playerUI?.entityPlayer);
        _buyToBackpack = !string.IsNullOrEmpty(playerId)
            && PlayerPrefs.GetInt("HyperdimensionalNetwork.TraderBuyToBackpack." + playerId, 0) != 0;
        RefreshOutputButtonState();
        _offers.Clear();
        _offerGroups.Clear();
        _filtered.Clear();
        _category = string.Empty;
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
        using var profile = NetworkProfiler.Measure("UI.traderMarket.update");
        base.Update(_dt);
        if (_closing || _requestPending || string.IsNullOrEmpty(_sessionId)
            || Time.realtimeSinceStartup < _nextRefreshAt) return;
        _nextRefreshAt = Time.realtimeSinceStartup + 2.5f;
        _requestPending = true;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !TraderMarketClient.Query(player, _sessionId, OnMarketRefreshed))
            _requestPending = false;
    }

    public override void OnClose()
    {
        _closing = true;
        NetworkLog.Debug("Trader market closing. player=" + (xui?.playerUI?.entityPlayer?.entityId ?? -1)
            + "; session=" + _sessionId + "; requestPending=" + _requestPending
            + "; offers=" + _offers.Count + "; selected=" + (_selected?.Value?.type ?? -1));
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if ((bool)player && !string.IsNullOrEmpty(_sessionId)) TraderMarketClient.Close(player, _sessionId);
        _sessionId = string.Empty;
        _offers.Clear();
        _filtered.Clear();
        _selected = null;
        _requestPending = false;
        if (ReferenceEquals(_activeWindow, this)) _activeWindow = null;
        RefreshRows();
        CursorControllerAbs cursorController = xui?.playerUI?.CursorController;
        if (cursorController?.navigationTarget?.Controller?.IsChildOf(this) == true)
        {
            cursorController.SetNavigationTarget(null);
            cursorController.SetNavigationLockView(null);
            NetworkLog.Debug("Trader market cursor navigation released on close. player=" + (player?.entityId ?? -1));
        }
        base.OnClose();
        NetworkLog.Debug("Trader market base close completed. player=" + (player?.entityId ?? -1)
            + "; modal=" + (xui?.playerUI?.windowManager?.GetModalWindow()?.Id ?? "none")
            + "; inputLocked=" + (xui?.playerUI?.windowManager?.IsInputLocked ?? false));
        if ((bool)player && GameManager.Instance?.World != null)
            _pendingReturnPlayerId = player.entityId;
    }

    private void SelectAt(int row)
    {
        int index = (_pager?.CurrentPageNumber ?? 0) * PageSize + row;
        if (index < 0 || index >= _filtered.Count) return;
        _selected = _filtered[index];
        int bundle = Math.Max(1, _selected.BundleSize);
        _selectedCount = _selected.Count >= bundle ? bundle : 0;
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
            info.SetInfo(new ItemStack(offer.Value.Clone(), offer.Count), this,
                XUiC_ItemActionList.ItemActionListTypes.None);
            XUiC_Counter counter = info.BuySellCounter;
            int bundle = Math.Max(1, offer.BundleSize);
            if (counter != null)
            {
                counter.Step = bundle;
                counter.MaxCount = offer.Count / bundle * bundle;
                counter.Count = counter.MaxCount > 0
                    ? Mathf.Clamp(_selectedCount, bundle, counter.MaxCount) : 0;
                counter.ForceTextRefresh();
            }
            info.traderActionItemList?.AddActionListEntry(new NetworkItemActionEntryAggregateBuy(this));
            info.traderActionItemList?.RefreshActionList();
            info.RefreshBindings();
            NetworkLog.Debug("Aggregate purchase controls attached to vanilla item details. item=" + offer.Value.type
                + "; stock=" + offer.Count + "; bundle=" + bundle + "; counter=" + counter?.Count);
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Aggregate trader item details could not be displayed. trader="
                + offer.TraderEntityId + "; item=" + offer.Value.type + "; exception=" + ex);
        }
    }

    private void OnMarketScroll(XUiController sender, float delta)
    {
        int before = _pager?.CurrentPageNumber ?? 0;
        if (delta > 0f) _pager?.PageDown();
        else _pager?.PageUp();
        NetworkLog.Debug("Trader market wheel page request. delta=" + delta + "; page=" + before + "->"
            + (_pager?.CurrentPageNumber ?? 0) + "; filtered=" + _filtered.Count + "; sender="
            + (sender?.ViewComponent?.ID ?? "<none>"));
    }

    internal static bool TryGetAggregateOffer(XUiC_ItemInfoWindow info, out XUiC_TraderMarketWindow window,
        out TraderOffer offer)
    {
        window = _activeWindow;
        offer = window?._selected;
        return window != null && !window._closing && offer?.Value != null
            && ReferenceEquals(info?.xui, window.xui);
    }

    internal void BuySelectedFromDetails(XUiC_ItemInfoWindow info)
    {
        int count = info?.BuySellCounter?.Count ?? 0;
        _selectedCount = count;
        NetworkLog.Debug("Aggregate buy action activated. player=" + (xui?.playerUI?.entityPlayer?.entityId ?? -1)
            + "; item=" + (_selected?.Value?.type ?? -1) + "; count=" + count
            + "; session=" + _sessionId + "; pending=" + _requestPending);
        if (_selected != null && count > 0) Buy(_selected, count);
    }

    internal bool CanPurchaseFromDetails(XUiC_ItemInfoWindow info, TraderOffer offer)
    {
        int count = info?.BuySellCounter?.Count ?? 0;
        int bundle = Math.Max(1, offer?.BundleSize ?? 1);
        return ReferenceEquals(_selected, offer) && !_closing && !_requestPending
            && !string.IsNullOrEmpty(_sessionId) && count >= bundle && count <= offer.Count
            && count % bundle == 0;
    }

    internal void Buy(TraderOffer offer, int count)
    {
        if (_closing || _requestPending || string.IsNullOrEmpty(_sessionId) || offer == null) return;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _requestPending = true;
        SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketPurchasing"));
        MarketOfferGroup group = _offerGroups.FirstOrDefault(entry => ReferenceEquals(entry.Display, offer));
        if (group == null) { _requestPending = false; return; }
        List<TraderOffer> sources = group.Sources.ToList();
        int remaining = count;
        int purchased = 0;
        NetworkRemoteResult lastSuccess = null;
        int attempts = 0;
        void ContinuePurchase()
        {
            int bundle = Math.Max(1, offer.BundleSize);
            TraderOffer source = sources.FirstOrDefault(candidate => candidate.Count >= bundle);
            if (remaining <= 0 || source == null || attempts++ >= 2048)
            {
                _requestPending = false;
                if (lastSuccess != null)
                {
                    ApplyPurchaseResult(player, lastSuccess, purchased);
                    NetworkLog.Debug("Grouped trader purchase completed. item=" + offer.Value.type
                        + "; requested=" + count + "; purchased=" + purchased + "; sources=" + sources.Count);
                }
                return;
            }
            int sourceCount = Math.Min(remaining, source.Count / bundle * bundle);
            if (!TraderMarketClient.Buy(player, _sessionId, source, sourceCount, _buyToBackpack, result =>
            {
                if (!result.Success)
                {
                    NetworkLog.Debug("Grouped trader purchase rejected. item=" + offer.Value.type
                        + "; trader=" + source.TraderEntityId + "; purchased=" + purchased + "; error=" + result.Error);
                    sources.Remove(source);
                    if (sources.Count > 0) { ContinuePurchase(); return; }
                    _requestPending = false;
                    if (lastSuccess != null) ApplyPurchaseResult(player, lastSuccess, purchased);
                    SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketBuyFailed") + ": " + result.Error);
                    return;
                }
                purchased += result.Amount;
                remaining -= result.Amount;
                lastSuccess = result;
                sources = result.TraderOffers?.Where(candidate => candidate?.Value != null
                    && candidate.BundlePrice == offer.BundlePrice && candidate.BundleSize == offer.BundleSize
                    && SameMarketItem(candidate.Value, offer.Value))
                    .OrderBy(candidate => candidate.TraderEntityId).ThenBy(candidate => candidate.InventoryIndex).ToList()
                    ?? new List<TraderOffer>();
                ContinuePurchase();
            }))
            {
                _requestPending = false;
                SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketBuyFailed"));
            }
        }
        ContinuePurchase();
    }

    private void ApplyPurchaseResult(EntityPlayerLocal player, NetworkRemoteResult result, int purchased)
    {
        if (result.BagData != null || result.InventoryData != null)
        {
            if (!NetworkInventorySync.Apply(player, result))
                NetworkLog.Error("Trader purchase committed but its personal inventory snapshot could not be applied. player="
                    + player.entityId + "; requestId=" + result.RequestId);
            xui.GetChildByType<XUiC_Backpack>()?.RefreshBackpackSlots();
        }
        ApplyResult(result);
        if (_selected != null) ShowOfferDetails(_selected);
        SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketPurchaseComplete") + " " + purchased);
        GameManager.ShowTooltip(player,
            Localization.Get("HyperdimensionalNetworkTraderMarketPurchaseComplete") + " " + purchased,
            false, false, 0f);
        if (TryGetAggregateOffer(xui.GetChildByType<XUiC_ItemInfoWindow>(), out _, out _))
            xui.GetChildByType<XUiC_ItemInfoWindow>()?.traderActionItemList?.RefreshActionList();
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
        _nextRefreshAt = Time.realtimeSinceStartup + 2.5f;
        NetworkLog.Debug("Trader market session opened in category list UI. session=" + _sessionId
            + "; offers=" + _offers.Count);
    }

    private void OnMarketRefreshed(NetworkRemoteResult result)
    {
        _requestPending = false;
        if (_closing) return;
        if (!result.Success)
        {
            if (result.Error == "TraderDisabled")
            {
                NetworkLog.Debug("Trader market closed after its network feature was disabled. session=" + _sessionId);
                OnClosePressed(null, 0);
                return;
            }
            SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketRefreshFailed") + ": " + result.Error);
            NetworkLog.Debug("Trader market refresh failed. session=" + _sessionId + "; error=" + result.Error);
            return;
        }
        ulong fingerprint = ComputeOfferFingerprint(result.TraderOffers);
        if (_hasOfferFingerprint && fingerprint == _offerFingerprint)
        {
            _nextRefreshAt = Time.realtimeSinceStartup + 2.5f;
            return;
        }
        ApplyResult(result);
    }

    private void ApplyResult(NetworkRemoteResult result)
    {
        float startedAt = Time.realtimeSinceStartup;
        ItemValue selectedValue = _selected?.Value;
        int selectedPrice = _selected?.BundlePrice ?? -1;
        int selectedBundle = _selected?.BundleSize ?? -1;
        _offerFingerprint = ComputeOfferFingerprint(result.TraderOffers);
        _hasOfferFingerprint = true;
        _offers.Clear();
        _offerGroups.Clear();
        Dictionary<MarketOfferGroupKey, MarketOfferGroup> groupsByKey = new Dictionary<MarketOfferGroupKey, MarketOfferGroup>();
        if (result.TraderOffers != null)
        {
            foreach (TraderOffer source in result.TraderOffers.Where(offer => offer?.Value != null && offer.Count > 0)
                .OrderBy(offer => offer.TraderEntityId).ThenBy(offer => offer.InventoryIndex))
            {
                ItemValue sourceKey = NetworkStorage.NormalizeKey(source.Value);
                MarketOfferGroupKey key = new MarketOfferGroupKey
                {
                    Value = sourceKey, BundleSize = source.BundleSize, BundlePrice = source.BundlePrice
                };
                if (!groupsByKey.TryGetValue(key, out MarketOfferGroup group))
                {
                    group = new MarketOfferGroup { Key = sourceKey, Display = new TraderOffer
                    {
                        Value = source.Value.Clone(), Count = 0, BundleSize = source.BundleSize,
                        BundlePrice = source.BundlePrice, TraderEntityId = source.TraderEntityId,
                        InventoryIndex = source.InventoryIndex
                    } };
                    groupsByKey.Add(key, group);
                    _offerGroups.Add(group);
                }
                group.Sources.Add(source);
                group.Display.Count = (int)Math.Min(int.MaxValue, (long)group.Display.Count + source.Count);
            }
        }
        _offers.AddRange(_offerGroups.Select(group => group.Display));
        // Vanilla displays PrimaryInventory in inventory-index order. Keep that order across
        // traders, but place all offers for the same item together with cheaper offers first.
        List<TraderOffer> orderedOffers = _offers
            .OrderBy(offer => offer.TraderEntityId)
            .ThenBy(offer => offer.InventoryIndex)
            .GroupBy(offer => offer.Value.type)
            .SelectMany(group => group.OrderBy(offer => offer.BundlePrice)
                .ThenBy(offer => offer.TraderEntityId)
                .ThenBy(offer => offer.InventoryIndex))
            .ToList();
        _offers.Clear();
        _offers.AddRange(orderedOffers);
        if (_categories != null)
        {
            List<TraderData.Entry> categoryOffers = _offers
                // These offers have already passed the server's stock checks. The aggregate
                // has no single trader stage, so bypass the vanilla stage filter here only.
                .Select(offer => new TraderData.Entry(new ItemStack(offer.Value.Clone(), offer.Count), 0, true))
                .ToList();
            _categories.SetupCategoriesBasedOnItems(categoryOffers, -1);
            bool keepCategory = !string.IsNullOrEmpty(_category)
                && _categories.CategoryButtons.Any(button => button.CategoryName == _category && !string.IsNullOrEmpty(button.SpriteName));
            if (keepCategory)
                _categories.TrySetCategory(_category);
            else
            {
                _category = string.Empty;
                _categories.TrySetCategory(string.Empty);
            }
            NetworkLog.Debug("Trader market categories refreshed from current offers. offers=" + _offers.Count
                + "; categories=" + _categories.CategoryButtons.Count(button => !string.IsNullOrEmpty(button.SpriteName)));
        }
        _selected = _offers.FirstOrDefault(offer => selectedValue != null && offer.BundlePrice == selectedPrice
            && offer.BundleSize == selectedBundle && SameMarketItem(offer.Value, selectedValue));
        if (_selected != null)
        {
            int bundle = Math.Max(1, _selected.BundleSize);
            int maximum = _selected.Count / bundle * bundle;
            _selectedCount = maximum > 0
                ? Mathf.Clamp(_selectedCount / bundle * bundle, bundle, maximum) : 0;
        }
        else _selectedCount = 0;
        ApplyFilter();
        if (_offers.Count == 0) SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketNoOffers"));
        else SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketOfferCount") + " " + _offers.Count);
        NetworkLog.Debug("Trader market projection applied. sourceOffers=" + (result.TraderOffers?.Count ?? 0)
            + "; groupedOffers=" + _offers.Count + "; elapsedMs="
            + ((Time.realtimeSinceStartup - startedAt) * 1000f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool SameMarketItem(ItemValue left, ItemValue right)
        => left != null && right != null
            && NetworkStorage.NormalizeKey(left).Equals(NetworkStorage.NormalizeKey(right));

    private static ulong ComputeOfferFingerprint(List<TraderOffer> offers)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            if (offers == null) return hash;
            AddFingerprint(ref hash, offers.Count);
            foreach (TraderOffer offer in offers)
            {
                ItemValue value = offer?.Value;
                AddFingerprint(ref hash, value?.type ?? 0); AddFingerprint(ref hash, value?.Quality ?? 0); AddFingerprint(ref hash, value?.Seed ?? 0);
                AddFingerprint(ref hash, value?.UseTimes.GetHashCode() ?? 0); AddFingerprint(ref hash, value?.Meta ?? 0);
                AddFingerprint(ref hash, value?.SelectedAmmoTypeIndex ?? 0); AddFingerprint(ref hash, value?.Activated == true ? 1 : 0);
                AddFingerprint(ref hash, offer?.Count ?? 0); AddFingerprint(ref hash, offer?.BundleSize ?? 0); AddFingerprint(ref hash, offer?.BundlePrice ?? 0);
                AddFingerprint(ref hash, offer?.TraderEntityId ?? 0); AddFingerprint(ref hash, offer?.InventoryIndex ?? -1);
            }
            return hash;
        }
    }

    private static void AddFingerprint(ref ulong hash, int value)
    {
        unchecked { hash ^= (uint)value; hash *= 1099511628211UL; }
    }

    private void ApplyFilter()
    {
        using var profile = NetworkProfiler.Measure("UI.traderMarket.filter");
        _filtered.Clear();
        string search = _search?.Text?.Trim();
        foreach (TraderOffer offer in _offers)
        {
            if (!string.IsNullOrEmpty(_category)
                && !XUiC_HyperdimensionalGrid.MatchesCategory(offer.Value, _category)) continue;
            string name = offer.Value.ItemClass?.GetLocalizedItemName() ?? string.Empty;
            if (string.IsNullOrEmpty(search) || name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0
                || PinyinSearch.Matches(name, search))
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
        using var profile = NetworkProfiler.Measure("UI.traderMarket.rows");
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
    }

    private void OnOutputPressed(XUiController sender, int mouseButton)
    {
        _buyToBackpack = !_buyToBackpack;
        RefreshOutputButtonState();
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
        NetworkLog.Debug("Trader market close button pressed. player="
            + (xui?.playerUI?.entityPlayer?.entityId ?? -1) + "; modal="
            + (manager.GetModalWindow()?.Id ?? "none") + "; inputLocked=" + manager.IsInputLocked);
        manager.CloseAllOpenModalWindows();
        NetworkLog.Debug("Trader market close requested through modal cleanup. player="
            + (xui?.playerUI?.entityPlayer?.entityId ?? -1) + "; marketOpen="
            + manager.IsWindowOpen("hyperdimensionalTraderMarket") + "; modal="
            + (manager.GetModalWindow()?.Id ?? "none") + "; inputLocked=" + manager.IsInputLocked);
    }

    private void RefreshOutputButtonState()
    {
        if (_outputButton?.ViewComponent is XUiV_Button button)
            button.Selected = _buyToBackpack;
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
        if (manager.IsWindowOpen("hyperdimensionalTraderMarket")
            || manager.GetModalWindow()?.Id == "hyperdimensionalTraderMarket")
        {
            if (!_loggedReturnWait)
            {
                _loggedReturnWait = true;
                NetworkLog.Debug("Trader market return deferred because market/modal is still active. player="
                    + player.entityId + "; marketOpen=" + manager.IsWindowOpen("hyperdimensionalTraderMarket")
                    + "; modal=" + (manager.GetModalWindow()?.Id ?? "none")
                    + "; inputLocked=" + manager.IsInputLocked);
            }
            return;
        }
        _loggedReturnWait = false;
        _pendingReturnPlayerId = -1;
        if (!manager.IsWindowOpen("hyperdimensionalNetwork"))
        {
            manager.Open("hyperdimensionalNetwork", true);
            NetworkLog.Debug("Trader market close returned to the network window. player=" + player.entityId
                + "; networkOpen=" + manager.IsWindowOpen("hyperdimensionalNetwork")
                + "; modal=" + (manager.GetModalWindow()?.Id ?? "none") + "; inputLocked=" + manager.IsInputLocked);
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
