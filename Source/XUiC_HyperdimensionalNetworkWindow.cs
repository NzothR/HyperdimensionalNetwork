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
        _settingsStatus = GetChildById("restockSettingsStatus");
        BindPress("btnHyperdimensionalCraftingChain", OnCraftingChainPressed);
        BindPress("btnHyperdimensionalRestock", OnManualRestockPressed);
        BindPress("btnHyperdimensionalSettings", OnSettingsPressed);
        BindPress("btnRestockSettingsClose", OnCloseSettingsPressed);
        BindPress("btnToggleAutoRestock", OnToggleAutoRestockPressed);
        BindPress("btnCycleAutoRestockTarget", OnCycleRestockTargetPressed);
        BindPress("btnToggleReplenishmentEnabled", OnToggleReplenishmentPressed);
        BindPress("btnToggleAmmoBankEnabled", OnToggleAmmoBankPressed);
        BindPress("btnToggleRetainSearch", OnToggleRetainSearchPressed);
        SetVisible(_settingsPanel, false);
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
        _settings = null;
        SetVisible(_manualRestockButton, false);
        HideNetworkAdminSettings();
        LoadRestockSettings();
        _grid?.Reload();
        RefreshPager();
        NetworkLog.Debug("Network window opened. entries=" + (_grid?.EntryCount ?? 0));
    }

    public override void OnClose()
    {
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
            }
        }
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
