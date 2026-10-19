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
    private XUiController _manualSaleButton;
    private XUiController _traderPayoutButton;
    private XUiController _categoryBar;
    private XUiController _settingsPanel;
    private XUiController _personalSettingsPageButton;
    private XUiController _adminSettingsPageButton;
    private XUiController _adminSettingsPageLabel;
    private readonly Dictionary<RestockSettingKey, SettingControls> _adminSettingControls = new Dictionary<RestockSettingKey, SettingControls>();
    private bool _showAdminSettings;
    private sealed class SettingControls
    {
        public XUiController Label;
        public XUiController Value;
        public XUiController Button;
    }
    private XUiController _autoRestockLabel;
    private XUiController _autoRestockValue;
    private XUiController _autoRestockToggle;
    private XUiController _autoRestockTargetLabel;
    private XUiController _autoRestockTargetValue;
    private XUiController _autoRestockTargetButton;
    private XUiController _retainSearchLabel;
    private XUiController _retainSearchValue;
    private XUiController _retainSearchButton;
    private XUiController _settingsStatus;
    private XUiController _managementButton;
    private RestockSettingsSnapshot _settings;
    private bool _settingsRequestPending;
    private float _nextSettingsRefreshAt;

    public override void Init()
    {
        base.Init();
        PinyinSearch.Initialize();
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
        _manualSaleButton = GetChildById("btnHyperdimensionalManualSale");
        _traderPayoutButton = GetChildById("btnHyperdimensionalTraderPayout");
        _categoryBar = GetChildById("categoryBar");
        _settingsPanel = GetChildById("restockSettingsPanel");
        _personalSettingsPageButton = GetChildById("btnPersonalSettingsPage");
        _adminSettingsPageButton = GetChildById("btnAdminSettingsPage");
        _adminSettingsPageLabel = GetChildById("labelAdminSettingsPage");
        _autoRestockLabel = GetChildById("labelAutoRestock");
        _autoRestockValue = GetChildById("labelAutoRestockValue");
        _autoRestockToggle = GetChildById("btnToggleAutoRestock");
        _autoRestockTargetLabel = GetChildById("labelAutoRestockTarget");
        _autoRestockTargetValue = GetChildById("labelAutoRestockTargetValue");
        _autoRestockTargetButton = GetChildById("btnCycleAutoRestockTarget");
        foreach (RestockSettingDefinition definition in RestockSettingCatalog.All)
        {
            if (definition.Permission != RestockSettingPermission.NetworkAdministrator) continue;
            string name = definition.Name;
            _adminSettingControls[definition.Key] = new SettingControls
            {
                Label = GetChildById("label" + name),
                Value = GetChildById("label" + name + "Value"),
                Button = GetChildById("btnToggle" + name)
            };
            RestockSettingKey key = definition.Key;
            BindPress("btnToggle" + name, (sender, button) => OnToggleAdminFeaturePressed(key));
        }
        _retainSearchLabel = GetChildById("labelRetainSearch");
        _retainSearchValue = GetChildById("labelRetainSearchValue");
        _retainSearchButton = GetChildById("btnToggleRetainSearch");
        _settingsStatus = GetChildById("restockSettingsStatus");
        _managementButton = GetChildById("btnNetworkManagement");
        BindPress("btnHyperdimensionalCraftingChain", OnCraftingChainPressed);
        BindPress("btnHyperdimensionalTraderPayout", OnTraderPayoutPressed);
        BindPress("btnHyperdimensionalTraderBuy", OnTraderMarketPressed);
        BindPress("btnHyperdimensionalTraderRules", OnTraderRulesPressed);
        BindPress("btnHyperdimensionalRestock", OnManualRestockPressed);
        BindPress("btnHyperdimensionalSettings", OnSettingsPressed);
        BindPress("btnRestockSettingsClose", OnCloseSettingsPressed);
        BindPress("btnPersonalSettingsPage", (sender, button) => ShowSettingsPage(false));
        BindPress("btnAdminSettingsPage", (sender, button) => ShowSettingsPage(true));
        BindPress("btnNetworkManagement", OnManagementPressed);
        BindPress("btnToggleAutoRestock", OnToggleAutoRestockPressed);
        BindPress("btnCycleAutoRestockTarget", OnCycleRestockTargetPressed);
        BindPress("btnToggleRetainSearch", OnToggleRetainSearchPressed);
        BindPress("btnHyperdimensionalManualSale", OnToggleManualSalePressed);
        SetVisible(_settingsPanel, false);
        ShowSettingsPage(false);
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
        _showAdminSettings = false;
        _settings = null;
        TraderTransactionService.DisableVoidSaleMode();
        SetVisible(_manualRestockButton, false);
        SetVisible(_manualSaleButton, false);
        SetVisible(_traderPayoutButton, false);
        SetVisible(GetChildById("btnHyperdimensionalTraderBuy"), false);
        SetVisible(GetChildById("btnHyperdimensionalTraderRules"), false);
        SetVisible(GetChildById("btnHyperdimensionalCraftingChain"), false);
        ShowSettingsPage(false);
        TraderTransactionService.LoadPersonalPayoutMode(xui?.playerUI?.entityPlayer);
        RefreshTraderPayoutButtonState();
        LoadRestockSettings();
        _nextSettingsRefreshAt = UnityEngine.Time.realtimeSinceStartup + 5f;
        _grid?.Reload();
        RefreshPager();
        NetworkLog.Debug("Network window opened. entries=" + (_grid?.EntryCount ?? 0));
    }

    public override void OnClose()
    {
        TraderTransactionService.DisableVoidSaleMode();
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (_search != null && !AutoRestockClient.ShouldRetainSearchOnClose(player))
        {
            _search.Text = string.Empty;
            _grid?.SetSearch(string.Empty);
            NetworkLog.Debug("Network search cleared on close by the player's preference. player=" + (player?.entityId ?? -1));
        }
        base.OnClose();
        TraderSellActionPatch.RefreshSelectedItem(xui);
    }

    public override void Update(float _dt)
    {
        base.Update(_dt);
        if (xui?.playerUI?.windowManager?.IsWindowOpen("hyperdimensionalNetwork") != true) return;
        if (UnityEngine.Time.realtimeSinceStartup < _nextSettingsRefreshAt) return;
        _nextSettingsRefreshAt = UnityEngine.Time.realtimeSinceStartup + 5f;
        LoadRestockSettings(quiet: true);
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
        RefreshTraderPayoutButtonState();
        if ((bool)player)
            GameManager.ShowTooltip(player, Localization.Get(personal ? "HyperdimensionalNetworkTraderPayoutPersonal" : "HyperdimensionalNetworkTraderPayoutNetwork"), false, false, 0f);
    }

    private void RefreshTraderPayoutButtonState()
    {
        if (_traderPayoutButton?.ViewComponent is XUiV_Button button)
            button.Selected = TraderTransactionService.PersonalPayoutMode;
    }

    private void OnTraderMarketPressed(XUiController sender, int mouseButton)
    {
        GUIWindowManager windowManager = xui?.playerUI?.windowManager;
        if (windowManager == null || windowManager.IsWindowOpen("hyperdimensionalTraderMarket")) return;
        NetworkLog.Debug("Trader market window requested by player=" + (xui?.playerUI?.entityPlayer?.entityId ?? -1));
        windowManager.Close("hyperdimensionalNetwork");
        windowManager.Close("windowpaging");
        windowManager.Open("hyperdimensionalTraderMarket", _bModal: true);
        if (!windowManager.IsWindowOpen("hyperdimensionalTraderMarket"))
        {
            NetworkLog.Error("Trader market window group failed to open.");
            windowManager.Open("hyperdimensionalNetwork", _bModal: true);
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
            ShowSettingsPage(false);
            LoadRestockSettings();
        }
    }

    private void OnCloseSettingsPressed(XUiController sender, int mouseButton) => SetSettingsVisible(false);

    private void OnManagementPressed(XUiController sender, int mouseButton)
    {
        GUIWindowManager windowManager = xui?.playerUI?.windowManager;
        if (windowManager == null || windowManager.IsWindowOpen("hyperdimensionalNetworkManagement")) return;
        windowManager.Open("hyperdimensionalNetworkManagement", _bModal: true);
        if (!windowManager.IsWindowOpen("hyperdimensionalNetworkManagement"))
            NetworkLog.Error("Network membership window group failed to open.");
    }

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

    private void OnToggleAdminFeaturePressed(RestockSettingKey key)
    {
        if (_settings == null || _settingsRequestPending || !_settings.IsNetworkAdministrator) return;
        bool enabled = key switch
        {
            RestockSettingKey.ReplenishmentEnabled => _settings.ReplenishmentEnabled,
            RestockSettingKey.AmmoBankEnabled => _settings.AmmoBankEnabled,
            RestockSettingKey.TraderEnabled => _settings.TraderEnabled,
            RestockSettingKey.CraftingChainEnabled => _settings.CraftingChainEnabled,
            RestockSettingKey.AutoDepositEnabled => _settings.AutoDepositEnabled,
            RestockSettingKey.TrashEnabled => _settings.TrashEnabled,
            RestockSettingKey.BookReadingEnabled => _settings.BookReadingEnabled,
            _ => true
        };
        NetworkLog.Debug("Administrator feature toggle requested. player=" + (xui?.playerUI?.entityPlayer?.entityId ?? -1) + "; feature=" + key + "; enabled=" + !enabled);
        SetSetting(key, enabled ? 0 : 1);
    }

    private void OnToggleRetainSearchPressed(XUiController sender, int mouseButton)
    {
        if (_settings == null || _settingsRequestPending) return;
        SetSetting(RestockSettingKey.RetainSearchOnClose, _settings.RetainSearchOnClose ? 0 : 1);
    }

    private void OnToggleManualSalePressed(XUiController sender, int mouseButton)
    {
        if (_settings == null || _settingsRequestPending) return;
        NetworkLog.Debug("Manual sale toggle requested. player=" + (xui?.playerUI?.entityPlayer?.entityId ?? -1)
            + "; enabled=" + !_settings.ManualSaleEnabled);
        SetSetting(RestockSettingKey.ManualSaleEnabled, _settings.ManualSaleEnabled ? 0 : 1);
    }

    private void LoadRestockSettings(bool quiet = false)
    {
        if (_settingsRequestPending) return;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _settingsRequestPending = true;
        if (!quiet) SetLabel(_settingsStatus, Localization.Get("HyperdimensionalNetworkSettingsLoading"));
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
        foreach (KeyValuePair<RestockSettingKey, SettingControls> entry in _adminSettingControls)
        {
            bool enabled = entry.Key switch
            {
                RestockSettingKey.ReplenishmentEnabled => _settings.ReplenishmentEnabled,
                RestockSettingKey.AmmoBankEnabled => _settings.AmmoBankEnabled,
                RestockSettingKey.TraderEnabled => _settings.TraderEnabled,
                RestockSettingKey.CraftingChainEnabled => _settings.CraftingChainEnabled,
                RestockSettingKey.AutoDepositEnabled => _settings.AutoDepositEnabled,
                RestockSettingKey.TrashEnabled => _settings.TrashEnabled,
                RestockSettingKey.BookReadingEnabled => _settings.BookReadingEnabled,
                _ => false
            };
            SetLabel(entry.Value.Value, Localization.Get(enabled ? "HyperdimensionalNetworkSettingOn" : "HyperdimensionalNetworkSettingOff"));
        }
        SetLabel(_retainSearchValue, Localization.Get(_settings.RetainSearchOnClose
            ? "HyperdimensionalNetworkSettingOn" : "HyperdimensionalNetworkSettingOff"));
        TraderTransactionService.SetVoidSaleMode(_settings.ManualSaleEnabled && _settings.TraderEnabled);
        TraderSellActionPatch.RefreshSelectedItem(xui);
        if (_manualSaleButton?.ViewComponent is XUiV_Button manualSaleButton)
            manualSaleButton.Selected = _settings.ManualSaleEnabled;
        NetworkLog.Debug("Manual sale setting applied. player=" + (xui?.playerUI?.entityPlayer?.entityId ?? -1)
            + "; enabled=" + _settings.ManualSaleEnabled);
        SetVisible(_manualRestockButton, _settings.ReplenishmentEnabled);
        SetVisible(_manualSaleButton, _settings.TraderEnabled);
        SetVisible(_traderPayoutButton, _settings.TraderEnabled);
        SetVisible(GetChildById("btnHyperdimensionalTraderBuy"), _settings.TraderEnabled);
        SetVisible(GetChildById("btnHyperdimensionalTraderRules"), _settings.TraderEnabled);
        SetVisible(GetChildById("btnHyperdimensionalCraftingChain"), _settings.CraftingChainEnabled);
        string status = Localization.Get("HyperdimensionalNetworkSettingsReady");
        if (_settings.AutoRestockCooldownMs > 0)
            status += " (" + _settings.AutoRestockCooldownMs + " ms)";
        SetLabel(_settingsStatus, status);
        ShowSettingsPage(_showAdminSettings);
    }

    private void SetSettingsVisible(bool visible)
    {
        SetVisible(_settingsPanel, visible);
        SetVisible(_grid, !visible);
        SetVisible(_categoryBar, !visible);
        SetVisible(_searchInputControls, !visible);
        SetVisible(_pagerControls, !visible);
        if (visible && _settings == null) SetLabel(_settingsStatus, Localization.Get("HyperdimensionalNetworkSettingsLoading"));
    }

    private void ShowSettingsPage(bool admin)
    {
        _showAdminSettings = admin && _settings?.IsNetworkAdministrator == true;
        bool personal = !_showAdminSettings;
        SetVisible(_autoRestockLabel, personal);
        SetVisible(_autoRestockValue, personal);
        SetVisible(_autoRestockToggle, personal);
        SetVisible(_autoRestockTargetLabel, personal);
        SetVisible(_autoRestockTargetValue, personal);
        SetVisible(_autoRestockTargetButton, personal);
        SetVisible(_retainSearchLabel, personal);
        SetVisible(_retainSearchValue, personal);
        SetVisible(_retainSearchButton, personal);
        SetVisible(_managementButton, personal);
        SetVisible(GetChildById("networkManagementButtonLabel"), personal);
        SetVisible(_adminSettingsPageButton, _settings?.IsNetworkAdministrator == true);
        SetVisible(_adminSettingsPageLabel, _settings?.IsNetworkAdministrator == true);
        if (_personalSettingsPageButton?.ViewComponent is XUiV_Button personalButton) personalButton.Selected = personal;
        if (_adminSettingsPageButton?.ViewComponent is XUiV_Button adminButton) adminButton.Selected = _showAdminSettings;
        foreach (SettingControls controls in _adminSettingControls.Values)
        {
            SetVisible(controls.Label, _showAdminSettings);
            SetVisible(controls.Value, _showAdminSettings);
            SetVisible(controls.Button, _showAdminSettings);
        }
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
