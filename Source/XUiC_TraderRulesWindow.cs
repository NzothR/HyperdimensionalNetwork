using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HyperdimensionalNetwork;

public sealed class XUiC_TraderRulesWindow : XUiController
{
    private readonly List<TraderAutomationRule> _rules = new List<TraderAutomationRule>();
    private XUiC_TextInput _itemType;
    private XUiC_TextInput _maximumCount;
    private XUiC_TextInput _minimumNetworkCount;
    private XUiC_TextInput _maximumSpend;
    private XUiC_TextInput _minimumCurrencyCount;
    private XUiC_TextInput _quality;
    private XUiC_TextInput _hour;
    private XUiC_TextInput _interval;
    private XUiController _status;
    private XUiController _selectedLabel;
    private XUiController _kindLabel;
    private XUiController _qualityModeLabel;
    private XUiController _scheduleLabel;
    private XUiController _networkComparisonLabel;
    private XUiController _currencyComparisonLabel;
    private TraderAutomationRuleKind _kind = TraderAutomationRuleKind.AutoSell;
    private TraderQualityMatchMode _qualityMode;
    private bool _scheduled;
    private TraderRuleComparison _networkComparison = TraderRuleComparison.GreaterOrEqual;
    private TraderRuleComparison _currencyComparison = TraderRuleComparison.GreaterOrEqual;
    private int _selectedIndex;
    private bool _requestPending;
    private bool _isAdmin;

    public override void Init()
    {
        base.Init();
        _itemType = GetChildById("traderRuleItemType") as XUiC_TextInput;
        _maximumCount = GetChildById("traderRuleMaximumCount") as XUiC_TextInput;
        _minimumNetworkCount = GetChildById("traderRuleMinimumNetworkCount") as XUiC_TextInput;
        _maximumSpend = GetChildById("traderRuleMaximumSpend") as XUiC_TextInput;
        _minimumCurrencyCount = GetChildById("traderRuleMinimumCurrencyCount") as XUiC_TextInput;
        _quality = GetChildById("traderRuleQuality") as XUiC_TextInput;
        _hour = GetChildById("traderRuleHour") as XUiC_TextInput;
        _interval = GetChildById("traderRuleInterval") as XUiC_TextInput;
        _status = GetChildById("traderRulesStatus");
        _selectedLabel = GetChildById("traderRulesSelected");
        _kindLabel = GetChildById("traderRuleKindLabel");
        _qualityModeLabel = GetChildById("traderRuleQualityModeLabel");
        _scheduleLabel = GetChildById("traderRuleScheduleLabel");
        _networkComparisonLabel = GetChildById("traderRuleNetworkComparisonLabel");
        _currencyComparisonLabel = GetChildById("traderRuleCurrencyComparisonLabel");
        Bind("btnTraderRulesClose", ClosePressed);
        Bind("btnTraderRuleCycleKind", CycleKindPressed);
        Bind("btnTraderRuleCycleQuality", CycleQualityPressed);
        Bind("btnTraderRuleCycleSchedule", CycleSchedulePressed);
        Bind("btnTraderRuleNetworkComparison", CycleNetworkComparisonPressed);
        Bind("btnTraderRuleCurrencyComparison", CycleCurrencyComparisonPressed);
        Bind("btnTraderRuleAdd", AddPressed);
        Bind("btnTraderRulePrevious", PreviousPressed);
        Bind("btnTraderRuleNext", NextPressed);
        Bind("btnTraderRuleUp", MoveUpPressed);
        Bind("btnTraderRuleDown", MoveDownPressed);
        Bind("btnTraderRuleToggle", TogglePressed);
        Bind("btnTraderRuleDelete", DeletePressed);
        Bind("btnTraderRuleRun", RunPressed);
        NetworkLog.Debug("Trader rules UI initialized.");
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _isAdmin = false;
        _requestPending = false;
        SetStatus(Localization.Get("HyperdimensionalNetworkTraderRulesLoading"));
        RefreshRules();
        RefreshModes();
    }

    private void RefreshRules()
    {
        _rules.Clear();
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _requestPending = true;
        if (!TraderRuleClient.Query(player, OnRulesResponse)) _requestPending = false;
    }

    private void OnRulesResponse(NetworkRemoteResult result)
    {
        _requestPending = false;
        _isAdmin = result?.IsNetworkAdministrator == true;
        _rules.Clear();
        if (_isAdmin && result?.TraderRules != null) _rules.AddRange(result.TraderRules.Select(rule => rule.Clone()));
        if (_selectedIndex >= _rules.Count) _selectedIndex = Math.Max(0, _rules.Count - 1);
        RefreshSelection();
        if (!_isAdmin) SetStatus(Localization.Get("HyperdimensionalNetworkTraderRulesPermissionDenied"));
        else if (result != null && !result.Success) SetStatus(result.Error);
        else SetStatus(Localization.Get("HyperdimensionalNetworkTraderRulesHint"));
    }

    private void RefreshSelection()
    {
        string text = _rules.Count == 0 ? Localization.Get("HyperdimensionalNetworkTraderRulesNone")
            : ("[" + (_selectedIndex + 1) + "/" + _rules.Count + "] " + Describe(_rules[_selectedIndex]));
        if (_selectedLabel?.ViewComponent is XUiV_Label label && label.Text != text)
        { label.Text = text; _selectedLabel.IsDirty = true; }
    }

    private static string Describe(TraderAutomationRule rule)
    {
        ItemClass itemClass = rule.ItemType > 0 ? ItemClass.GetForId(rule.ItemType) : null;
        string item = itemClass?.GetLocalizedItemName() ?? (rule.ItemType > 0 ? "#" + rule.ItemType : Localization.Get("HyperdimensionalNetworkTraderRuleAnyItem"));
        return (rule.Enabled ? "● " : "○ ") + rule.Kind + " | " + item + " | max " + rule.MaximumCount
            + (rule.Kind == TraderAutomationRuleKind.AutoBuy && rule.Scheduled ? " | " + rule.ScheduledHour.ToString("00", CultureInfo.InvariantCulture) + ":00 / " + rule.IntervalDays + "d" : string.Empty);
    }

    private void AddPressed(XUiController sender, int mouseButton)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !_isAdmin || _requestPending) return;
        TraderAutomationRule rule = new TraderAutomationRule
        {
            Kind = _kind,
            ItemType = ReadInt(_itemType, 0),
            MaximumCount = ReadInt(_maximumCount, 1),
            MinimumNetworkCount = ReadInt(_minimumNetworkCount, 0),
            NetworkCountComparison = _networkComparison,
            MinimumCurrencyCount = ReadInt(_minimumCurrencyCount, 0),
            CurrencyCountComparison = _currencyComparison,
            MaximumSpend = ReadInt(_maximumSpend, 0),
            QualityMode = _qualityMode,
            Quality = ReadInt(_quality, 1),
            Scheduled = _kind == TraderAutomationRuleKind.AutoBuy && _scheduled,
            ScheduledHour = ReadInt(_hour, 5),
            IntervalDays = ReadInt(_interval, 1)
        };
        _requestPending = true;
        if (!TraderRuleClient.Save(player, rule, result =>
        {
            OnRulesResponse(result);
            if (result.Success) { _selectedIndex = Math.Max(0, _rules.Count - 1); RefreshSelection(); SetStatus(Localization.Get("HyperdimensionalNetworkTraderRulesSaved")); }
            else SetStatus(Localization.Get("HyperdimensionalNetworkTraderRulesSaveFailed") + ": " + result.Error);
        })) _requestPending = false;
    }

    private void TogglePressed(XUiController sender, int mouseButton)
    {
        TraderAutomationRule selected = Selected();
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (selected == null || !(bool)player || !_isAdmin || _requestPending) return;
        selected.Enabled = !selected.Enabled;
        SubmitSave(player, selected, Localization.Get("HyperdimensionalNetworkTraderRulesSaved"));
    }

    private void DeletePressed(XUiController sender, int mouseButton)
    {
        TraderAutomationRule selected = Selected();
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (selected == null || !(bool)player || !_isAdmin || _requestPending) return;
        _requestPending = true;
        if (!TraderRuleClient.Delete(player, selected.Id, result =>
        { OnRulesResponse(result); SetStatus(result.Success ? Localization.Get("HyperdimensionalNetworkTraderRulesDeleted") : result.Error); })) _requestPending = false;
    }

    private void RunPressed(XUiController sender, int mouseButton)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !_isAdmin || _requestPending) return;
        _requestPending = true;
        if (!TraderRuleClient.RunAll(player, result =>
        { OnRulesResponse(result); SetStatus(result.Success ? Localization.Get("HyperdimensionalNetworkTraderRulesRunRequested") : result.Error); })) _requestPending = false;
    }

    private void MoveUpPressed(XUiController sender, int mouseButton) => MoveSelected(-1);
    private void MoveDownPressed(XUiController sender, int mouseButton) => MoveSelected(1);
    private void MoveSelected(int offset)
    {
        TraderAutomationRule selected = Selected();
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (selected == null || !(bool)player || !_isAdmin || _requestPending) return;
        _requestPending = true;
        if (!TraderRuleClient.Move(player, selected.Id, offset, result =>
        { _selectedIndex = Math.Max(0, Math.Min(_selectedIndex + offset, _rules.Count - 1)); OnRulesResponse(result); })) _requestPending = false;
    }

    private void SubmitSave(EntityPlayerLocal player, TraderAutomationRule rule, string successMessage)
    {
        _requestPending = true;
        if (!TraderRuleClient.Save(player, rule, result =>
        { OnRulesResponse(result); SetStatus(result.Success ? successMessage : result.Error); })) _requestPending = false;
    }

    private TraderAutomationRule Selected() => _selectedIndex >= 0 && _selectedIndex < _rules.Count ? _rules[_selectedIndex].Clone() : null;
    private static int ReadInt(XUiC_TextInput input, int fallback) => int.TryParse(input?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    private void CycleKindPressed(XUiController sender, int mouseButton) { _kind = _kind == TraderAutomationRuleKind.AutoSell ? TraderAutomationRuleKind.AutoBuy : TraderAutomationRuleKind.AutoSell; RefreshModes(); }
    private void CycleQualityPressed(XUiController sender, int mouseButton) { _qualityMode = (TraderQualityMatchMode)(((int)_qualityMode + 1) % 6); RefreshModes(); }
    private void CycleSchedulePressed(XUiController sender, int mouseButton) { _scheduled = !_scheduled; RefreshModes(); }
    private void CycleNetworkComparisonPressed(XUiController sender, int mouseButton) { _networkComparison = (TraderRuleComparison)(((int)_networkComparison + 1) % 5); RefreshModes(); }
    private void CycleCurrencyComparisonPressed(XUiController sender, int mouseButton) { _currencyComparison = (TraderRuleComparison)(((int)_currencyComparison + 1) % 5); RefreshModes(); }
    private void PreviousPressed(XUiController sender, int mouseButton) { if (_rules.Count > 0) _selectedIndex = (_selectedIndex + _rules.Count - 1) % _rules.Count; RefreshSelection(); }
    private void NextPressed(XUiController sender, int mouseButton) { if (_rules.Count > 0) _selectedIndex = (_selectedIndex + 1) % _rules.Count; RefreshSelection(); }
    private void ClosePressed(XUiController sender, int mouseButton) => xui?.playerUI?.windowManager?.Close("hyperdimensionalTraderRules");
    private void SetStatus(string text) { if (_status?.ViewComponent is XUiV_Label label && label.Text != (text ?? string.Empty)) { label.Text = text ?? string.Empty; _status.IsDirty = true; } }
    private void RefreshModes()
    {
        SetLabel(_kindLabel, Localization.Get(_kind == TraderAutomationRuleKind.AutoBuy ? "HyperdimensionalNetworkTraderRuleKindAutoBuy" : "HyperdimensionalNetworkTraderRuleKindAutoSell"));
        string qualityKey = _qualityMode switch
        {
            TraderQualityMatchMode.Any => "HyperdimensionalNetworkTraderRuleQualityAny",
            TraderQualityMatchMode.Exact => "HyperdimensionalNetworkTraderRuleQualityExact",
            TraderQualityMatchMode.Greater => "HyperdimensionalNetworkTraderRuleQualityGreater",
            TraderQualityMatchMode.GreaterOrEqual => "HyperdimensionalNetworkTraderRuleQualityGreaterOrEqual",
            TraderQualityMatchMode.Less => "HyperdimensionalNetworkTraderRuleQualityLess",
            _ => "HyperdimensionalNetworkTraderRuleQualityLessOrEqual"
        };
        SetLabel(_qualityModeLabel, Localization.Get(qualityKey));
        SetLabel(_scheduleLabel, Localization.Get(_scheduled ? "HyperdimensionalNetworkTraderRuleScheduleOn" : "HyperdimensionalNetworkTraderRuleScheduleOff"));
        SetLabel(_networkComparisonLabel, Localization.Get("HyperdimensionalNetworkTraderRuleNetworkCondition") + ": " + ComparisonText(_networkComparison));
        SetLabel(_currencyComparisonLabel, Localization.Get("HyperdimensionalNetworkTraderRuleCurrencyCondition") + ": " + ComparisonText(_currencyComparison));
    }
    private static string ComparisonText(TraderRuleComparison comparison) => comparison switch
    {
        TraderRuleComparison.Greater => ">",
        TraderRuleComparison.GreaterOrEqual => ">=",
        TraderRuleComparison.Equal => "=",
        TraderRuleComparison.LessOrEqual => "<=",
        _ => "<"
    };
    private static void SetLabel(XUiController controller, string text) { if (controller?.ViewComponent is XUiV_Label label && label.Text != text) { label.Text = text; controller.IsDirty = true; } }
    private void Bind(string id, XUiEvent_OnPressEventHandler handler) { XUiController child = GetChildById(id); if (child != null) child.OnPress += handler; }
}
