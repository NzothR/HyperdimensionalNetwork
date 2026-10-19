using System;
using System.Collections.Generic;

namespace HyperdimensionalNetwork;

public sealed class XUiC_HyperdimensionalNetworkWindow : XUiController
{
    private XUiC_HyperdimensionalGrid _grid;
    private XUiC_CategoryList _categories;
    private XUiC_TextInput _search;
    private XUiC_Paging _pager;
    private XUiController _searchInputControls;
    private XUiController _pagerControls;
    private XUiController _manualRestockButton;
    private XUiController _categoryBar;
    private XUiController _settingsPanel;
    private XUiController _autoRestockLabel;
    private XUiController _autoRestockValue;
    private XUiController _autoRestockToggle;
    private XUiController _autoRestockTargetLabel;
    private XUiController _autoRestockTargetValue;
    private XUiController _autoRestockTargetButton;
    private XUiController _replenishmentEnabledLabel;
    private XUiController _replenishmentEnabledValue;
    private XUiController _replenishmentEnabledButton;
    private XUiController _ammoBankEnabledLabel;
    private XUiController _ammoBankEnabledValue;
    private XUiController _ammoBankEnabledButton;
    private XUiController _retainSearchLabel;
    private XUiController _retainSearchValue;
    private XUiController _retainSearchButton;
    private XUiController _manualSalePanel;
    private XUiController _manualSaleItem;
    private XUiController _manualSaleModeValue;
    private XUiController _manualSaleCountLabel;
    private XUiController _manualSalePriceLabel;
    private XUiC_ItemStack _manualSaleSlot;
    private int _manualSaleCount;
    private XUiController _settingsStatus;
    private RestockSettingsSnapshot _settings;
    private bool _settingsRequestPending;

    public override void Init()
    {
        base.Init();
        _grid = GetChildByType<XUiC_HyperdimensionalGrid>() ?? Parent?.GetChildByType<XUiC_HyperdimensionalGrid>();
        _pager = GetChildByType<XUiC_Paging>();
        if (_pager != null) _pager.OnPageChanged += OnPageChanged;
        _categories = GetChildById("categories") as XUiC_CategoryList;
        if (_categories != null) _categories.CategoryChanged += OnCategoryChanged;
        _search = GetChildById("searchInput") as XUiC_TextInput;
        if (_search == null) _search = Parent?.GetChildById("searchInput") as XUiC_TextInput;
        if (_search != null) _search.OnChangeHandler += OnSearchChanged;
        _searchInputControls = GetChildById("searchInputControls");
        _pagerControls = GetChildById("pagerControls");
        _manualRestockButton = GetChildById("btnHyperdimensionalRestock");
        _categoryBar = GetChildById("categoryBar");
        _settingsPanel = GetChildById("restockSettingsPanel");
        _autoRestockLabel = GetChildById("labelAutoRestock");
        _autoRestockValue = GetChildById("labelAutoRestockValue");
        _autoRestockToggle = GetChildById("btnToggleAutoRestock");
        _autoRestockTargetLabel = GetChildById("labelAutoRestockTarget");
        _autoRestockTargetValue = GetChildById("labelAutoRestockTargetValue");
        _autoRestockTargetButton = GetChildById("btnCycleAutoRestockTarget");
        _replenishmentEnabledLabel = GetChildById("labelReplenishmentEnabled");
        _replenishmentEnabledValue = GetChildById("labelReplenishmentEnabledValue");
        _replenishmentEnabledButton = GetChildById("btnToggleReplenishmentEnabled");
        _ammoBankEnabledLabel = GetChildById("labelAmmoBankEnabled");
        _ammoBankEnabledValue = GetChildById("labelAmmoBankEnabledValue");
        _ammoBankEnabledButton = GetChildById("btnToggleAmmoBankEnabled");
        _retainSearchLabel = GetChildById("labelRetainSearch");
        _retainSearchValue = GetChildById("labelRetainSearchValue");
        _retainSearchButton = GetChildById("btnToggleRetainSearch");
        _manualSalePanel = GetChildById("manualSalePanel");
        _manualSaleItem = GetChildById("manualSaleItem");
        _manualSaleModeValue = GetChildById("manualSaleModeValue");
        _manualSaleCountLabel = GetChildById("manualSaleCount");
        _manualSalePriceLabel = GetChildById("manualSalePrice");
        _settingsStatus = GetChildById("restockSettingsStatus");
        BindPress("btnHyperdimensionalCraftingChain", OnCraftingChainPressed);
        BindPress("btnHyperdimensionalTraderPayout", OnTraderPayoutPressed);
        BindPress("btnHyperdimensionalTraderBuy", OnTraderMarketPressed);
        BindPress("btnHyperdimensionalTraderRules", OnTraderRulesPressed);
        BindPress("btnHyperdimensionalRestock", OnManualRestockPressed);
        BindPress("btnHyperdimensionalSettings", OnSettingsPressed);
        BindPress("btnRestockSettingsClose", OnCloseSettingsPressed);
        BindPress("btnToggleAutoRestock", OnToggleAutoRestockPressed);
        BindPress("btnCycleAutoRestockTarget", OnCycleRestockTargetPressed);
        BindPress("btnToggleReplenishmentEnabled", OnToggleReplenishmentPressed);
        BindPress("btnToggleAmmoBankEnabled", OnToggleAmmoBankPressed);
        BindPress("btnToggleRetainSearch", OnToggleRetainSearchPressed);
        BindPress("btnHyperdimensionalManualSale", OnManualSaleOpenPressed);
        BindPress("btnManualSaleToggle", OnToggleManualSalePressed);
        BindPress("btnManualSaleClose", (sender, button) => SetVisible(_manualSalePanel, false));
        BindPress("btnManualSaleMinus", (sender, button) => ChangeManualSaleCount(-1));
        BindPress("btnManualSalePlus", (sender, button) => ChangeManualSaleCount(1));
        BindPress("btnManualSaleMax", (sender, button) => SetManualSaleMaximum());
        BindPress("btnManualSaleConfirm", OnManualSaleConfirmPressed);
        SetVisible(_settingsPanel, false);
        SetVisible(_manualSalePanel, false);
        HideNetworkAdminSettings();
        List<XUiC_ItemStack> slots = new List<XUiC_ItemStack>();
        GetChildrenByType(slots);
        foreach (XUiC_ItemStack slot in slots) slot.OnScroll += OnNetworkScroll;
        OnScroll += OnNetworkScroll;
        NetworkLog.Debug("Window controller initialized. grid=" + (_grid != null) + "; categories=" + (_categories != null) + "; search=" + (_search != null));
    }

    public override void OnOpen()
    {
        base.OnOpen();
        SetSettingsVisible(false);
        SetVisible(_manualSalePanel, false);
        _manualSaleSlot = null;
        _settings = null;
        TraderTransactionService.DisableVoidSaleMode();
        SetVisible(_manualRestockButton, false);
        HideNetworkAdminSettings();
        TraderTransactionService.LoadPersonalPayoutMode(xui?.playerUI?.entityPlayer);
        LoadRestockSettings();
        _grid?.Reload();
        RefreshPager();
        NetworkLog.Debug("Network window opened. entries=" + (_grid?.EntryCount ?? 0));
    }

    public override void OnClose()
    {
        TraderTransactionService.DisableVoidSaleMode();
        SetVisible(_manualSalePanel, false);
        _manualSaleSlot = null;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (_search != null && !AutoRestockClient.ShouldRetainSearchOnClose(player))
        {
            _search.Text = string.Empty;
            _grid?.SetSearch(string.Empty);
            NetworkLog.Debug("Network search cleared on close by the player's preference. player=" + (player?.entityId ?? -1));
        }
        base.OnClose();
    }

    private void OnCategoryChanged(XUiC_CategoryEntry category) => _grid?.SetCategory(category?.CategoryName);
    private void OnSearchChanged(XUiController controller, string text, bool changeFromCode) => _grid?.SetSearch(text);
    private void OnPageChanged() { if (_pager != null) _grid?.SetPage(_pager.CurrentPageNumber); }

    private void OnManualRestockPressed(XUiController sender, int mouseButton)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        NetworkLog.Debug("Manual restock requested from network window. player=" + player.entityId);
        bool sent = AutoRestockClient.RequestManual(player, result =>
        {
            if (!result.Success)
            {
                NetworkLog.Debug("Manual restock did not complete. player=" + player.entityId + "; error=" + result.Error);
                if (result.Error != "BackpackFull") GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkRestockFailed"));
            }
            else if (result.Amount > 0)
            {
                NetworkLog.Debug("Manual restock completed. player=" + player.entityId + "; amount=" + result.Amount);
                GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkRestockComplete") + " " + result.Amount);
            }
            _grid?.Reload();
        });
        if (!sent) NetworkLog.Debug("Manual restock was not queued because an inventory transaction is pending. player=" + player.entityId);
    }

    private void OnTraderPayoutPressed(XUiController sender, int mouseButton)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        bool personal = TraderTransactionService.TogglePersonalPayoutMode(player);
        if ((bool)player)
            GameManager.ShowTooltip(player, Localization.Get(personal ? "HyperdimensionalNetworkTraderPayoutPersonal" : "HyperdimensionalNetworkTraderPayoutNetwork"), false, false, 0f);
    }

    private void OnTraderMarketPressed(XUiController sender, int mouseButton)
    {
        GUIWindowManager windowManager = xui?.playerUI?.windowManager;
        if (windowManager == null || windowManager.IsWindowOpen("hyperdimensionalTraderMarket")) return;
        NetworkLog.Debug("Trader market window requested by player=" + (xui?.playerUI?.entityPlayer?.entityId ?? -1));
        windowManager.Close("hyperdimensionalNetwork");
        windowManager.Close("windowpaging");
        windowManager.Open("hyperdimensionalTraderMarket", _bModal: false);
        if (!windowManager.IsWindowOpen("hyperdimensionalTraderMarket"))
        {
            NetworkLog.Error("Trader market window group failed to open.");
            windowManager.Open("hyperdimensionalNetwork", _bModal: false);
        }
    }

    private void OnTraderRulesPressed(XUiController sender, int mouseButton)
    {
        var windowManager = xui?.playerUI?.windowManager;
        if (windowManager == null || windowManager.IsWindowOpen("hyperdimensionalTraderRules")) return;
        windowManager.Open("hyperdimensionalTraderRules", _bModal: true);
        if (!windowManager.IsWindowOpen("hyperdimensionalTraderRules"))
            NetworkLog.Debug("Trader automation rules window could not be opened by the network toolbar.");
    }

    private void OnCraftingChainPressed(XUiController sender, int mouseButton)
    {
        GUIWindowManager windowManager = xui?.playerUI?.windowManager;
        if (windowManager == null) return;
        const string windowGroupName = "hyperdimensionalCraftingChain";
        if (windowManager.IsWindowOpen(windowGroupName)) return;
        NetworkLog.Debug("Crafting-chain window requested. trackedRecipe=" + (xui?.Recipes?.TrackedRecipe?.GetName() ?? "<none>"));
        windowManager.Open(windowGroupName, _bModal: true);
        if (!windowManager.IsWindowOpen(windowGroupName))
            NetworkLog.Error("Crafting-chain window group failed to open. group=" + windowGroupName);
        else
            NetworkLog.Debug("Crafting-chain window group opened. group=" + windowGroupName);
    }

    private void OnSettingsPressed(XUiController sender, int mouseButton)
    {
        bool visible = _settingsPanel?.ViewComponent?.IsVisible != true;
        SetSettingsVisible(visible);
        if (visible)
        {
            _settings = null;
            HideNetworkAdminSettings();
            LoadRestockSettings();
        }
    }

    private void OnCloseSettingsPressed(XUiController sender, int mouseButton) => SetSettingsVisible(false);

    private void OnToggleAutoRestockPressed(XUiController sender, int mouseButton)
    {
        if (_settings == null || _settingsRequestPending) return;
        SetSetting(RestockSettingKey.AutoRestockEnabled, _settings.AutoRestockEnabled ? 0 : 1);
    }

    private void OnCycleRestockTargetPressed(XUiController sender, int mouseButton)
    {
        if (_settings == null || _settingsRequestPending) return;
        int next = _settings.TargetMode == RestockTargetMode.ToolbeltOnly
            ? (int)RestockTargetMode.LockedBackpackOnly
            : _settings.TargetMode == RestockTargetMode.LockedBackpackOnly
                ? (int)RestockTargetMode.ToolbeltAndLockedBackpackStacks
                : (int)RestockTargetMode.ToolbeltOnly;
        SetSetting(RestockSettingKey.AutoRestockTarget, next);
    }

    private void OnToggleReplenishmentPressed(XUiController sender, int mouseButton)
    {
        if (_settings == null || _settingsRequestPending || !_settings.IsNetworkAdministrator) return;
        SetSetting(RestockSettingKey.ReplenishmentEnabled, _settings.ReplenishmentEnabled ? 0 : 1);
    }

    private void OnToggleAmmoBankPressed(XUiController sender, int mouseButton)
    {
        if (_settings == null || _settingsRequestPending || !_settings.IsNetworkAdministrator) return;
        SetSetting(RestockSettingKey.AmmoBankEnabled, _settings.AmmoBankEnabled ? 0 : 1);
    }

    private void OnToggleRetainSearchPressed(XUiController sender, int mouseButton)
    {
        if (_settings == null || _settingsRequestPending) return;
        SetSetting(RestockSettingKey.RetainSearchOnClose, _settings.RetainSearchOnClose ? 0 : 1);
    }

    private void OnManualSaleOpenPressed(XUiController sender, int mouseButton)
    {
        if (_settingsPanel?.ViewComponent?.IsVisible == true) SetSettingsVisible(false);
        if (_manualSalePanel?.ViewComponent?.IsVisible == true)
        {
            SetVisible(_manualSalePanel, false);
            return;
        }
        XUiC_ItemStack selected = xui?.GetChildByType<XUiC_ItemInfoWindow>()?.selectedItemStack;
        if (selected?.ItemStack == null || selected.ItemStack.IsEmpty()
            || (selected is not XUiC_HyperdimensionalSlot
                && selected.StackLocation != XUiC_ItemStack.StackLocationTypes.Backpack
                && selected.StackLocation != XUiC_ItemStack.StackLocationTypes.ToolBelt))
        {
            EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
            if ((bool)player) GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkManualSaleSelectItem"));
            NetworkLog.Debug("Manual sale panel was not opened because no eligible network or carried item is selected.");
            return;
        }
        _manualSaleSlot = selected;
        _manualSaleCount = Math.Max(1, GetSaleBundleSize(selected.ItemStack));
        SetVisible(_manualSalePanel, true);
        RefreshManualSalePanel();
        NetworkLog.Debug("Manual sale panel opened. item=" + selected.ItemStack.itemValue.type
            + "; available=" + selected.ItemStack.count + "; networkSource=" + (selected is XUiC_HyperdimensionalSlot));
    }

    private void ChangeManualSaleCount(int steps)
    {
        if (_manualSaleSlot?.ItemStack == null || _manualSaleSlot.ItemStack.IsEmpty()) return;
        int bundle = GetSaleBundleSize(_manualSaleSlot.ItemStack);
        int maximum = _manualSaleSlot.ItemStack.count / bundle * bundle;
        _manualSaleCount = Math.Max(bundle, Math.Min(maximum, _manualSaleCount + steps * bundle));
        RefreshManualSalePanel();
    }

    private void SetManualSaleMaximum()
    {
        if (_manualSaleSlot?.ItemStack == null || _manualSaleSlot.ItemStack.IsEmpty()) return;
        int bundle = GetSaleBundleSize(_manualSaleSlot.ItemStack);
        _manualSaleCount = _manualSaleSlot.ItemStack.count / bundle * bundle;
        RefreshManualSalePanel();
    }

    private static int GetSaleBundleSize(ItemStack stack)
    {
        ItemClass itemClass = stack?.itemValue?.ItemClass;
        if (itemClass == null) return 1;
        try { return Math.Max(1, itemClass.IsBlock()
            ? Block.list[stack.itemValue.type].EconomicBundleSize : itemClass.EconomicBundleSize); }
        catch (Exception ex)
        {
            NetworkLog.Debug("Manual sale bundle size fell back to one. item=" + stack.itemValue.type
                + "; exception=" + ex.Message);
            return 1;
        }
    }

    private void RefreshManualSalePanel()
    {
        if (_manualSalePanel?.ViewComponent?.IsVisible != true) return;
        SetLabel(_manualSaleModeValue, Localization.Get(_settings?.ManualSaleEnabled == true
            ? "HyperdimensionalNetworkSettingOn" : "HyperdimensionalNetworkSettingOff"));
        ItemStack stack = _manualSaleSlot?.ItemStack;
        if (stack == null || stack.IsEmpty())
        {
            SetLabel(_manualSaleItem, Localization.Get("HyperdimensionalNetworkManualSaleSelectItem"));
            SetLabel(_manualSaleCountLabel, "-");
            SetLabel(_manualSalePriceLabel, string.Empty);
            return;
        }
        int bundle = GetSaleBundleSize(stack);
        int maximum = stack.count / bundle * bundle;
        _manualSaleCount = maximum > 0 ? Math.Max(bundle, Math.Min(maximum, _manualSaleCount / bundle * bundle)) : 0;
        SetLabel(_manualSaleItem, stack.itemValue.ItemClass?.GetLocalizedItemName() + " × " + stack.count);
        SetLabel(_manualSaleCountLabel, _manualSaleCount.ToString());
        int price = 0;
        if (_manualSaleCount > 0 && TraderTransactionService.CanSell(stack.itemValue))
        {
            try { price = TraderTransactionService.GetVirtualSellPrice(xui?.playerUI?.entityPlayer,
                stack.itemValue, _manualSaleCount, stack.itemValue.ItemClass); }
            catch (Exception ex)
            {
                NetworkLog.Debug("Manual sale preview price could not be calculated. item="
                    + stack.itemValue.type + "; count=" + _manualSaleCount + "; exception=" + ex.Message);
            }
        }
        SetLabel(_manualSalePriceLabel, price > 0 ? price + " $" : Localization.Get("xuiNoSellPrice"));
    }

    private void OnManualSaleConfirmPressed(XUiController sender, int mouseButton)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        XUiC_ItemStack slot = _manualSaleSlot;
        if (!(bool)player || _settings?.ManualSaleEnabled != true || slot?.ItemStack == null
            || slot.ItemStack.IsEmpty() || _manualSaleCount <= 0 || NetworkRemoteClient.IsMutationPending) return;
        int itemType = slot.ItemStack.itemValue.type;
        int count = _manualSaleCount;
        bool networkSource = slot is XUiC_HyperdimensionalSlot;
        if (count > slot.ItemStack.count || !TraderTransactionService.CanSell(slot.ItemStack.itemValue)) return;
        NetworkLog.Debug("Manual sale requested from external panel. player=" + player.entityId
            + "; item=" + itemType + "; count=" + count + "; networkSource=" + networkSource);
        bool sent = TraderTransactionService.SellFromSlot(player, slot, networkSource, count, result =>
        {
            if (!result.Success)
            {
                GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkTraderSaleFailed") + ": " + result.Error);
                NetworkLog.Debug("Manual sale rejected. player=" + player.entityId + "; item=" + itemType
                    + "; count=" + count + "; error=" + result.Error);
                RefreshManualSalePanel();
                return;
            }
            if (result.BagData != null || result.InventoryData != null)
            {
                if (!NetworkInventorySync.Apply(player, result))
                    NetworkLog.Error("Manual sale committed but inventory snapshot could not be applied. player="
                        + player.entityId + "; requestId=" + result.RequestId);
                xui.GetChildByType<XUiC_Backpack>()?.RefreshBackpackSlots();
            }
            _grid?.Reload();
            SetVisible(_manualSalePanel, false);
            _manualSaleSlot = null;
            GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkTraderSaleComplete") + " " + result.Amount);
            NetworkLog.Debug("Manual sale completed. player=" + player.entityId + "; item=" + itemType
                + "; count=" + count + "; proceeds=" + result.Amount + "; requestId=" + result.RequestId);
        });
        if (!sent) NetworkLog.Debug("Manual sale could not be queued. player=" + player.entityId + "; item=" + itemType);
    }

    private void OnToggleManualSalePressed(XUiController sender, int mouseButton)
    {
        if (_settings == null || _settingsRequestPending) return;
        SetSetting(RestockSettingKey.ManualSaleEnabled, _settings.ManualSaleEnabled ? 0 : 1);
    }

    private void LoadRestockSettings()
    {
        if (_settingsRequestPending) return;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _settingsRequestPending = true;
        SetLabel(_settingsStatus, Localization.Get("HyperdimensionalNetworkSettingsLoading"));
        AutoRestockClient.GetSettings(player, (snapshot, error) =>
        {
            _settingsRequestPending = false;
            if (snapshot == null)
            {
                SetLabel(_settingsStatus, Localization.Get("HyperdimensionalNetworkSettingsUnavailable"));
                NetworkLog.Debug("Restock settings could not be displayed. player=" + player.entityId + "; error=" + error);
                return;
            }
            _settings = snapshot;
            RefreshRestockSettings();
        });
    }

    private void SetSetting(RestockSettingKey key, int value)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _settingsRequestPending = true;
        SetLabel(_settingsStatus, Localization.Get("HyperdimensionalNetworkSettingsSaving"));
        bool sent = AutoRestockClient.SetSetting(player, key, value, (snapshot, error) =>
        {
            _settingsRequestPending = false;
            if (snapshot == null)
            {
                SetLabel(_settingsStatus, Localization.Get("HyperdimensionalNetworkSettingsSaveFailed"));
                NetworkLog.Debug("Restock setting change failed. player=" + player.entityId + "; setting=" + key + "; error=" + error);
                return;
            }
            _settings = snapshot;
            RefreshRestockSettings();
        });
        if (!sent)
        {
            _settingsRequestPending = false;
            SetLabel(_settingsStatus, Localization.Get("HyperdimensionalNetworkSettingsSaveFailed"));
        }
    }

    private void RefreshRestockSettings()
    {
        if (_settings == null) return;
        SetLabel(_autoRestockValue, Localization.Get(_settings.AutoRestockEnabled
            ? "HyperdimensionalNetworkSettingOn" : "HyperdimensionalNetworkSettingOff"));
        string targetKey = _settings.TargetMode == RestockTargetMode.ToolbeltAndLockedBackpackStacks
            ? "HyperdimensionalNetworkSettingHotbarAndLocked"
            : _settings.TargetMode == RestockTargetMode.LockedBackpackOnly
                ? "HyperdimensionalNetworkSettingLockedOnly"
                : "HyperdimensionalNetworkSettingHotbarOnly";
        SetLabel(_autoRestockTargetValue, Localization.Get(targetKey));
        SetLabel(_replenishmentEnabledValue, Localization.Get(_settings.ReplenishmentEnabled
            ? "HyperdimensionalNetworkSettingOn" : "HyperdimensionalNetworkSettingOff"));
        SetLabel(_ammoBankEnabledValue, Localization.Get(_settings.AmmoBankEnabled
            ? "HyperdimensionalNetworkSettingOn" : "HyperdimensionalNetworkSettingOff"));
        SetLabel(_retainSearchValue, Localization.Get(_settings.RetainSearchOnClose
            ? "HyperdimensionalNetworkSettingOn" : "HyperdimensionalNetworkSettingOff"));
        SetLabel(_manualSaleModeValue, Localization.Get(_settings.ManualSaleEnabled
            ? "HyperdimensionalNetworkSettingOn" : "HyperdimensionalNetworkSettingOff"));
        TraderTransactionService.SetVoidSaleMode(_settings.ManualSaleEnabled);
        RefreshManualSalePanel();
        SetVisible(_manualRestockButton, _settings.ReplenishmentEnabled);
        string status = Localization.Get("HyperdimensionalNetworkSettingsReady");
        if (_settings.AutoRestockCooldownMs > 0)
            status += " (" + _settings.AutoRestockCooldownMs + " ms)";
        SetLabel(_settingsStatus, status);
        foreach (RestockSettingDefinition definition in RestockSettingCatalog.All)
        {
            bool visible = definition.Permission == RestockSettingPermission.NetworkMember || _settings.IsNetworkAdministrator;
            switch (definition.Key)
            {
                case RestockSettingKey.AutoRestockEnabled:
                    SetVisible(_autoRestockLabel, visible);
                    SetVisible(_autoRestockValue, visible);
                    SetVisible(_autoRestockToggle, visible);
                    break;
                case RestockSettingKey.AutoRestockTarget:
                    SetVisible(_autoRestockTargetLabel, visible);
                    SetVisible(_autoRestockTargetValue, visible);
                    SetVisible(_autoRestockTargetButton, visible);
                    break;
                case RestockSettingKey.ReplenishmentEnabled:
                    SetVisible(_replenishmentEnabledLabel, visible);
                    SetVisible(_replenishmentEnabledValue, visible);
                    SetVisible(_replenishmentEnabledButton, visible);
                    break;
                case RestockSettingKey.AmmoBankEnabled:
                    SetVisible(_ammoBankEnabledLabel, visible);
                    SetVisible(_ammoBankEnabledValue, visible);
                    SetVisible(_ammoBankEnabledButton, visible);
                    break;
                case RestockSettingKey.RetainSearchOnClose:
                    SetVisible(_retainSearchLabel, visible);
                    SetVisible(_retainSearchValue, visible);
                    SetVisible(_retainSearchButton, visible);
                    break;
                case RestockSettingKey.ManualSaleEnabled:
                    break;
            }
        }
    }

    private void SetSettingsVisible(bool visible)
    {
        SetVisible(_settingsPanel, visible);
        if (visible) SetVisible(_manualSalePanel, false);
        SetVisible(_grid, !visible);
        SetVisible(_categoryBar, !visible);
        SetVisible(_searchInputControls, !visible);
        SetVisible(_pagerControls, !visible);
        if (visible && _settings == null) SetLabel(_settingsStatus, Localization.Get("HyperdimensionalNetworkSettingsLoading"));
    }

    private void HideNetworkAdminSettings()
    {
        SetVisible(_replenishmentEnabledLabel, false);
        SetVisible(_replenishmentEnabledValue, false);
        SetVisible(_replenishmentEnabledButton, false);
        SetVisible(_ammoBankEnabledLabel, false);
        SetVisible(_ammoBankEnabledValue, false);
        SetVisible(_ammoBankEnabledButton, false);
    }

    private void BindPress(string id, XUiEvent_OnPressEventHandler handler)
    {
        XUiController control = GetChildById(id);
        if (control != null) control.OnPress += handler;
        else NetworkLog.Error("Network window button was not found. id=" + id);
    }

    private static void SetVisible(XUiController controller, bool visible)
    {
        if (controller?.ViewComponent != null) controller.ViewComponent.IsVisible = visible;
    }

    private static void SetLabel(XUiController controller, string text)
    {
        if (controller?.ViewComponent is XUiV_Label label)
        {
            label.Text = text ?? string.Empty;
            controller.IsDirty = true;
        }
    }

    private void OnNetworkScroll(XUiController sender, float delta)
    {
        if (_grid == null || delta == 0f) return;
        int next = _grid.Page + (delta < 0f ? 1 : -1);
        if (next == _grid.Page || next < 0 || next >= _grid.PageCount) return;
        _grid.SetPage(next);
        NetworkLog.Debug("Network page changed by mouse wheel. page=" + next + "; total=" + _grid.PageCount);
    }

    private void RefreshPager()
    {
        if (_pager == null || _grid == null) return;
        _pager.SetLastPageByElementsAndPageLength(_grid.PagingElementCount, Math.Max(1, _grid.VisibleSlotCount));
        _pager.SetPage(_grid.Page);
    }
}
