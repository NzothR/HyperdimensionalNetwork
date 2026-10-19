using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class XUiC_TraderMarketWindow : XUiController
{
    private static bool _buyToBackpack;
    private readonly List<TraderOffer> _offers = new List<TraderOffer>();
    private readonly List<XUiC_TraderOfferSlot> _slots = new List<XUiC_TraderOfferSlot>();
    private XUiC_Paging _pager;
    private XUiController _status;
    private string _sessionId = string.Empty;
    private bool _requestPending;
    private bool _closing;
    private float _nextRefreshAt;

    public override void Init()
    {
        base.Init();
        XUiC_ItemStack[] itemSlots = GetChildrenByType<XUiC_ItemStack>();
        foreach (XUiC_TraderOfferSlot slot in itemSlots.OfType<XUiC_TraderOfferSlot>())
        {
            slot.Bind(this);
            _slots.Add(slot);
        }
        _pager = GetChildByType<XUiC_Paging>();
        if (_pager != null) _pager.OnPageChanged += RefreshSlots;
        _status = GetChildById("traderMarketStatus");
        BindPress("btnTraderMarketClose", OnClosePressed);
        BindPress("btnTraderMarketOutput", OnOutputPressed);
        SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketLoading"));
        NetworkLog.Debug("Trader market UI initialized. slots=" + _slots.Count + "; pager=" + (_pager != null));
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _closing = false;
        _requestPending = true;
        _sessionId = string.Empty;
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(xui?.playerUI?.entityPlayer);
        _buyToBackpack = !string.IsNullOrEmpty(playerId) && PlayerPrefs.GetInt("HyperdimensionalNetwork.TraderBuyToBackpack." + playerId, 0) != 0;
        _offers.Clear();
        RefreshSlots();
        SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketLoading"));
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !TraderMarketClient.Open(player, OnMarketOpened))
        {
            _requestPending = false;
            SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketOpenFailed"));
            NetworkLog.Error("Trader market open could not be requested from the market window.");
        }
    }

    public override void Update(float _dt)
    {
        base.Update(_dt);
        if (_closing || _requestPending || string.IsNullOrEmpty(_sessionId) || Time.realtimeSinceStartup < _nextRefreshAt) return;
        _nextRefreshAt = Time.realtimeSinceStartup + 1f;
        _requestPending = true;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !TraderMarketClient.Query(player, _sessionId, OnMarketRefreshed)) _requestPending = false;
    }

    public override void OnClose()
    {
        _closing = true;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if ((bool)player && !string.IsNullOrEmpty(_sessionId)) TraderMarketClient.Close(player, _sessionId);
        _sessionId = string.Empty;
        _offers.Clear();
        _requestPending = false;
        RefreshSlots();
        base.OnClose();
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
                NetworkLog.Debug("Aggregate trader purchase rejected. player=" + player.entityId + "; trader=" + offer.TraderEntityId + "; item=" + offer.Value.type + "; error=" + result.Error);
                return;
            }
            if (result.BagData != null || result.InventoryData != null)
            {
                if (!NetworkInventorySync.Apply(player, result))
                    NetworkLog.Error("Trader purchase committed but its personal currency snapshot could not be applied. player=" + player.entityId + "; requestId=" + result.RequestId);
                xui.GetChildByType<XUiC_Backpack>()?.RefreshBackpackSlots();
            }
            ApplyResult(result);
            SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketPurchaseComplete") + " " + result.Amount);
            GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkTraderMarketPurchaseComplete") + " " + result.Amount, false, false, 0f);
            NetworkLog.Debug("Aggregate trader purchase displayed. player=" + player.entityId + "; trader=" + offer.TraderEntityId + "; item=" + offer.Value.type + "; count=" + result.Amount);
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
        NetworkLog.Debug("Trader market session opened in UI. session=" + _sessionId + "; offers=" + _offers.Count);
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
        _offers.Clear();
        if (result.TraderOffers != null) _offers.AddRange(result.TraderOffers.Where(offer => offer?.Value != null && offer.Count > 0));
        _offers.Sort((left, right) =>
        {
            int resultByType = string.Compare(left.Value.ItemClass?.GetLocalizedItemName(), right.Value.ItemClass?.GetLocalizedItemName(), StringComparison.CurrentCultureIgnoreCase);
            if (resultByType != 0) return resultByType;
            int resultByPrice = left.BundlePrice.CompareTo(right.BundlePrice);
            return resultByPrice != 0 ? resultByPrice : left.TraderEntityId.CompareTo(right.TraderEntityId);
        });
        _pager?.SetLastPageByElementsAndPageLength(_offers.Count, Math.Max(1, _slots.Count));
        RefreshSlots();
        if (_offers.Count == 0) SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketNoOffers"));
        else SetStatus(Localization.Get("HyperdimensionalNetworkTraderMarketOfferCount") + " " + _offers.Count);
    }

    private void RefreshSlots()
    {
        int page = _pager?.CurrentPageNumber ?? 0;
        int first = page * _slots.Count;
        for (int i = 0; i < _slots.Count; i++)
        {
            int index = first + i;
            _slots[i].SetOffer(index >= 0 && index < _offers.Count ? _offers[index] : null);
        }
    }

    private void OnClosePressed(XUiController sender, int mouseButton)
        => xui?.playerUI?.windowManager?.Close("hyperdimensionalTraderMarket");

    private void OnOutputPressed(XUiController sender, int mouseButton)
    {
        _buyToBackpack = !_buyToBackpack;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (!string.IsNullOrEmpty(playerId)) { PlayerPrefs.SetInt("HyperdimensionalNetwork.TraderBuyToBackpack." + playerId, _buyToBackpack ? 1 : 0); PlayerPrefs.Save(); }
        if ((bool)player) GameManager.ShowTooltip(player, Localization.Get(_buyToBackpack
            ? "HyperdimensionalNetworkTraderBuyToBackpack" : "HyperdimensionalNetworkTraderBuyToNetwork"), false, false, 0f);
        NetworkLog.Debug("Trader market purchase destination changed. backpack=" + _buyToBackpack);
    }

    private void SetStatus(string text)
    {
        if (_status?.ViewComponent is XUiV_Label label && label.Text != (text ?? string.Empty))
        {
            label.Text = text ?? string.Empty;
            _status.IsDirty = true;
        }
    }

    private void BindPress(string id, XUiEvent_OnPressEventHandler handler)
    {
        XUiController control = GetChildById(id);
        if (control != null) control.OnPress += handler;
    }
}
