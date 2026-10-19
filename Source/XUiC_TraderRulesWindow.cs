using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class XUiC_TraderRulesWindow : XUiController
{
    private const int RuleRows = 9;
    private const int PickerRows = 10;
    private enum PickerMode { None, TargetItem, TargetCategory, ExcludedItem, ExcludedCategory }

    private readonly List<TraderAutomationRule> _rules = new List<TraderAutomationRule>();
    private readonly List<int> _pickerTypes = new List<int>();
    private readonly List<CraftingCategoryDisplayEntry> _pickerCategories = new List<CraftingCategoryDisplayEntry>();
    private readonly List<int> _visibleTypes = new List<int>();
    private readonly HashSet<int> _stockTypes = new HashSet<int>();
    private readonly XUiController[] _ruleRows = new XUiController[RuleRows];
    private readonly XUiController[] _ruleButtons = new XUiController[RuleRows];
    private readonly XUiController[] _ruleIcons = new XUiController[RuleRows];
    private readonly XUiController[] _ruleSummaries = new XUiController[RuleRows];
    private readonly XUiController[] _buyAccents = new XUiController[RuleRows];
    private readonly XUiController[] _sellAccents = new XUiController[RuleRows];
    private readonly XUiController[] _pickerRows = new XUiController[PickerRows];
    private readonly XUiController[] _pickerIcons = new XUiController[PickerRows];
    private readonly XUiController[] _pickerNames = new XUiController[PickerRows];

    private XUiController _status, _editorStatus, _newButton, _newLabel, _pageLabel, _main, _editor, _picker;
    private XUiController _editorTitle, _kindLabel, _scopeLabel, _targetTitle, _targetLabel, _targetIcon;
    private XUiController _exclusionsLabel, _qualityLabel, _scheduleLabel, _networkComparisonLabel, _currencyComparisonLabel;
    private XUiController _enabledLabel, _spendTitle, _currencyTitle, _maximumCountTitle, _pickerTitle, _pickerStockLabel, _pickerPageLabel;
    private XUiC_TextInput _quality, _maximumCount, _minimumNetworkCount, _maximumSpend;
    private XUiC_TextInput _minimumCurrencyCount, _hour, _interval, _pickerSearch;
    private TraderAutomationRule _editing;
    private PickerMode _pickerMode;
    private TraderQualityMatchMode _qualityMode;
    private TraderRuleComparison _networkComparison = TraderRuleComparison.GreaterOrEqual;
    private TraderRuleComparison _currencyComparison = TraderRuleComparison.GreaterOrEqual;
    private bool _scheduled, _stockOnly, _stockLoading, _requestPending, _isAdmin, _dragArmed, _suppressClick;
    private int _rulePage, _pickerPage, _dragRow = -1;
    private float _dragDistance;

    public override void Init()
    {
        base.Init();
        for (int i = 0; i < RuleRows; i++)
        {
            int row = i;
            _ruleRows[i] = GetChildById("ruleRow" + i);
            _ruleButtons[i] = GetChildById("btnRuleRow" + i);
            _ruleIcons[i] = GetChildById("ruleIcon" + i);
            _ruleSummaries[i] = GetChildById("ruleSummary" + i);
            _buyAccents[i] = GetChildById("ruleBuyAccent" + i);
            _sellAccents[i] = GetChildById("ruleSellAccent" + i);
            if (_ruleButtons[i] != null)
            {
                _ruleButtons[i].OnPress += (sender, button) => OnRulePressed(row);
                _ruleButtons[i].OnHold += (sender, hold, duration, delta) => OnRuleHeld(row, hold);
                _ruleButtons[i].OnDrag += (sender, drag, delta) => OnRuleDragged(row, drag, delta);
            }
        }
        for (int i = 0; i < PickerRows; i++)
        {
            int row = i;
            _pickerRows[i] = GetChildById("pickerRow" + i);
            _pickerIcons[i] = GetChildById("pickerIcon" + i);
            _pickerNames[i] = GetChildById("pickerName" + i);
            Bind("btnPickerRow" + i, (sender, button) => OnPickerRowPressed(row));
        }
        _status = GetChildById("traderRulesStatus");
        _editorStatus = GetChildById("ruleEditorStatus");
        _newButton = GetChildById("btnTraderRuleNew");
        _newLabel = GetChildById("ruleNewLabel");
        _pageLabel = GetChildById("rulePageLabel");
        _main = GetChildById("ruleMain");
        _editor = GetChildById("ruleEditor");
        _picker = GetChildById("rulePicker");
        _editorTitle = GetChildById("ruleEditorTitle");
        _kindLabel = GetChildById("traderRuleKindLabel");
        _scopeLabel = GetChildById("ruleScopeLabel");
        _targetTitle = GetChildById("ruleTargetTitle");
        _targetLabel = GetChildById("ruleTargetLabel");
        _targetIcon = GetChildById("ruleTargetIcon");
        _exclusionsLabel = GetChildById("ruleExclusionsLabel");
        _qualityLabel = GetChildById("traderRuleQualityModeLabel");
        _scheduleLabel = GetChildById("traderRuleScheduleLabel");
        _networkComparisonLabel = GetChildById("traderRuleNetworkComparisonLabel");
        _currencyComparisonLabel = GetChildById("traderRuleCurrencyComparisonLabel");
        _enabledLabel = GetChildById("ruleEnabledLabel");
        _spendTitle = GetChildById("ruleSpendTitle");
        _currencyTitle = GetChildById("ruleCurrencyTitle");
        _maximumCountTitle = GetChildById("ruleMaximumCountTitle");
        _pickerTitle = GetChildById("rulePickerTitle");
        _pickerStockLabel = GetChildById("rulePickerStockLabel");
        _pickerPageLabel = GetChildById("pickerPageLabel");
        _quality = GetChildById("traderRuleQuality") as XUiC_TextInput;
        _maximumCount = GetChildById("traderRuleMaximumCount") as XUiC_TextInput;
        _minimumNetworkCount = GetChildById("traderRuleMinimumNetworkCount") as XUiC_TextInput;
        _maximumSpend = GetChildById("traderRuleMaximumSpend") as XUiC_TextInput;
        _minimumCurrencyCount = GetChildById("traderRuleMinimumCurrencyCount") as XUiC_TextInput;
        _hour = GetChildById("traderRuleHour") as XUiC_TextInput;
        _interval = GetChildById("traderRuleInterval") as XUiC_TextInput;
        _pickerSearch = GetChildById("rulePickerSearch") as XUiC_TextInput;
        if (_pickerSearch != null) _pickerSearch.OnChangeHandler += (sender, value, code) => { _pickerPage = 0; RefreshPicker(); };
        foreach (XUiC_TextInput input in new[] { _quality, _maximumCount, _minimumNetworkCount, _maximumSpend, _minimumCurrencyCount, _hour, _interval })
            if (input != null) input.OnChangeHandler += (sender, value, code) => RefreshNumericValidation(false);
        Bind("btnTraderRulesClose", (sender, button) => xui?.playerUI?.windowManager?.Close("hyperdimensionalTraderRules"));
        Bind("btnTraderRuleNew", (sender, button) => OpenEditor(null));
        Bind("btnRulePagePrevious", (sender, button) => { if (_rulePage > 0) { _rulePage--; RefreshList(); } });
        Bind("btnRulePageNext", (sender, button) => { if ((_rulePage + 1) * RuleRows < _rules.Count) { _rulePage++; RefreshList(); } });
        Bind("btnTraderRuleRunAll", RunPressed);
        Bind("btnRuleEditorCancelTop", (sender, button) => CloseEditor());
        Bind("btnRuleCancel", (sender, button) => CloseEditor());
        Bind("btnRuleSave", SavePressed);
        Bind("btnRuleDelete", DeletePressed);
        Bind("btnRuleEnabled", (sender, button) => { if (_editing != null) { _editing.Enabled = !_editing.Enabled; RefreshEditor(); } });
        Bind("btnTraderRuleCycleKind", (sender, button) => { if (_editing != null) { _editing.Kind = _editing.Kind == TraderAutomationRuleKind.AutoBuy ? TraderAutomationRuleKind.AutoSell : TraderAutomationRuleKind.AutoBuy; RefreshEditor(); } });
        Bind("btnRuleScope", (sender, button) => { if (_editing != null) { _editing.Scope = _editing.Scope == TraderRuleScope.SingleItem ? TraderRuleScope.Category : TraderRuleScope.SingleItem; RefreshEditor(); } });
        Bind("btnRuleTarget", (sender, button) => OpenPicker(_editing?.Scope == TraderRuleScope.SingleItem ? PickerMode.TargetItem : PickerMode.TargetCategory));
        Bind("btnRuleExcludeItem", (sender, button) => OpenPicker(PickerMode.ExcludedItem));
        Bind("btnRuleExcludeCategory", (sender, button) => OpenPicker(PickerMode.ExcludedCategory));
        Bind("btnTraderRuleCycleQuality", (sender, button) => { _qualityMode = (TraderQualityMatchMode)(((int)_qualityMode + 1) % 6); RefreshEditor(); });
        Bind("btnTraderRuleCycleSchedule", (sender, button) => { _scheduled = !_scheduled; RefreshEditor(); });
        Bind("btnTraderRuleNetworkComparison", (sender, button) => { _networkComparison = (TraderRuleComparison)(((int)_networkComparison + 1) % 5); RefreshEditor(); });
        Bind("btnTraderRuleCurrencyComparison", (sender, button) => { _currencyComparison = (TraderRuleComparison)(((int)_currencyComparison + 1) % 5); RefreshEditor(); });
        Bind("btnRulePickerClose", (sender, button) => ClosePicker());
        Bind("btnRulePickerDone", (sender, button) => ClosePicker());
        Bind("btnRulePickerStock", (sender, button) => { _stockOnly = !_stockOnly; _pickerPage = 0; if (_stockOnly) RefreshStockTypes(); else RefreshPicker(); });
        Bind("btnPickerPrevious", (sender, button) => { if (_pickerPage > 0) { _pickerPage--; RefreshPicker(); } });
        Bind("btnPickerNext", (sender, button) => { _pickerPage++; RefreshPicker(); });
        NetworkLog.Debug("Trader rule list and visual item picker initialized. rows=" + _ruleRows.Count(row => row != null));
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _isAdmin = false;
        _requestPending = false;
        _rulePage = 0;
        SetVisible(_main, true);
        SetVisible(_editor, false);
        SetVisible(_picker, false);
        SetText(_status, Localization.Get("HyperdimensionalNetworkTraderRulesLoading"));
        RefreshList();
        QueryRules();
    }

    private void QueryRules()
    {
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
        if (_rulePage * RuleRows >= _rules.Count) _rulePage = Math.Max(0, (_rules.Count - 1) / RuleRows);
        RefreshList();
        if (!_isAdmin) SetText(_status, Localization.Get("HyperdimensionalNetworkTraderRulesPermissionDenied"));
        else if (result != null && !result.Success) SetText(_status, result.Error);
        else SetText(_status, Localization.Get("HyperdimensionalNetworkTraderRulesHint"));
    }

    private void RefreshList()
    {
        for (int i = 0; i < RuleRows; i++)
        {
            int index = _rulePage * RuleRows + i;
            TraderAutomationRule rule = index < _rules.Count ? _rules[index] : null;
            SetVisible(_ruleRows[i], rule != null);
            if (rule == null) continue;
            SetVisible(_buyAccents[i], rule.Kind == TraderAutomationRuleKind.AutoBuy);
            SetVisible(_sellAccents[i], rule.Kind == TraderAutomationRuleKind.AutoSell);
            SetText(_ruleSummaries[i], ShortDescription(rule));
            SetIcon(_ruleIcons[i], rule);
            if (_ruleButtons[i]?.ViewComponent != null) _ruleButtons[i].ViewComponent.ToolTip = FullDescription(rule);
        }
        bool lastPage = (_rulePage + 1) * RuleRows >= _rules.Count;
        SetVisible(_newButton, lastPage && _isAdmin);
        SetVisible(_newLabel, lastPage && _isAdmin);
        int used = Math.Min(RuleRows, Math.Max(0, _rules.Count - _rulePage * RuleRows));
        if (_newButton?.ViewComponent != null)
        {
            _newButton.ViewComponent.Position = new Vector2i(22, -(118 + used * 51));
            _newButton.ViewComponent.TryUpdatePosition();
        }
        if (_newLabel?.ViewComponent != null)
        {
            _newLabel.ViewComponent.Position = new Vector2i(28, -(125 + used * 51));
            _newLabel.ViewComponent.TryUpdatePosition();
        }
        SetText(_pageLabel, (_rules.Count == 0 ? "空规则列表" : (_rulePage + 1) + " / " + Math.Max(1, (_rules.Count + RuleRows - 1) / RuleRows))
            + "  ·  长按规则并上下拖动排序");
    }

    private static string ShortDescription(TraderAutomationRule rule)
    {
        string target = TargetName(rule);
        string timing = rule.Kind == TraderAutomationRuleKind.AutoBuy && rule.Scheduled
            ? rule.ScheduledHour.ToString("00", CultureInfo.InvariantCulture) + ":00 / " + rule.IntervalDays + "日"
            : rule.Kind == TraderAutomationRuleKind.AutoSell ? "入库时" : "手动";
        string kind = rule.Kind == TraderAutomationRuleKind.AutoBuy ? "自动购入" : "自动售出";
        return (rule.Enabled ? "" : "○ ") + target + "  ·  " + timing + "  ·  " + kind;
    }

    private static string FullDescription(TraderAutomationRule rule)
    {
        return ShortDescription(rule) + "\n" + (rule.Scope == TraderRuleScope.Category ? "批量" : "单件")
            + "  ·  " + QualityName(rule.QualityMode) + (rule.QualityMode == TraderQualityMatchMode.Any ? "" : " " + rule.Quality)
            + "  ·  最多 " + (rule.MaximumCount == 0 ? "不限" : rule.MaximumCount.ToString())
            + (rule.Kind == TraderAutomationRuleKind.AutoBuy && rule.Scope == TraderRuleScope.Category ? ""
                : "\n网络数量 " + ComparisonText(rule.NetworkCountComparison) + " " + rule.MinimumNetworkCount)
            + (rule.Kind == TraderAutomationRuleKind.AutoBuy ? "\n最多花费 " + (rule.MaximumSpend == 0 ? "不限" : rule.MaximumSpend.ToString())
                + "  ·  公爵币 " + ComparisonText(rule.CurrencyCountComparison) + " " + rule.MinimumCurrencyCount : "")
            + "\n分类黑名单：" + ExcludedCategoryNames(rule.ExcludedCategories)
            + "\n物品黑名单：" + ExcludedItemNames(rule.ExcludedItemTypes);
    }

    private static string TargetName(TraderAutomationRule rule)
    {
        if (rule.Scope == TraderRuleScope.Category)
            return string.IsNullOrEmpty(rule.Category) ? "全部商品" : string.Join("、", rule.Category.Split('|').Select(name =>
                UIDisplayInfoManager.Current?.GetTraderCategoryDisplay(name)?.DisplayName ?? name));
        return ItemClass.GetForId(rule.ItemType)?.GetLocalizedItemName() ?? "选择物品";
    }

    private static string ExcludedItemNames(string ids)
    {
        if (string.IsNullOrEmpty(ids)) return "无";
        return string.Join("、", ids.Split(',').Select(id => int.TryParse(id, out int type)
            ? ItemClass.GetForId(type)?.GetLocalizedItemName() ?? id : id));
    }

    private static string ExcludedCategoryNames(string categories)
    {
        if (string.IsNullOrEmpty(categories)) return "无";
        return string.Join("、", categories.Split('|').Select(name =>
            UIDisplayInfoManager.Current?.GetTraderCategoryDisplay(name)?.DisplayName ?? name));
    }

    private static string QualityName(TraderQualityMatchMode mode) => mode switch
    {
        TraderQualityMatchMode.Exact => "品质等于",
        TraderQualityMatchMode.Greater => "品质大于",
        TraderQualityMatchMode.GreaterOrEqual => "品质大于等于",
        TraderQualityMatchMode.Less => "品质小于",
        TraderQualityMatchMode.LessOrEqual => "品质小于等于",
        _ => "任意品质"
    };

    private void SetIcon(XUiController controller, TraderAutomationRule rule)
    {
        if (!(controller?.ViewComponent is XUiV_Sprite sprite)) return;
        if (rule.Scope == TraderRuleScope.SingleItem && rule.ItemType > 0)
        {
            sprite.UIAtlas = "ItemIconAtlas";
            sprite.SpriteName = ItemClass.GetForId(rule.ItemType)?.GetIconName() ?? "";
        }
        else
        {
            sprite.UIAtlas = "SymbolAtlas";
            sprite.SpriteName = "hdn_trader_buy";
        }
        controller.IsDirty = true;
    }

    private void OnRulePressed(int row)
    {
        if (_suppressClick) { _suppressClick = false; return; }
        int index = _rulePage * RuleRows + row;
        if (index >= 0 && index < _rules.Count) OpenEditor(_rules[index]);
    }

    private void OnRuleHeld(int row, EHoldType hold)
    {
        if (hold == EHoldType.HoldStart) { _dragArmed = true; _dragRow = row; _dragDistance = 0f; }
        if (hold == EHoldType.HoldEnd) _dragArmed = false;
    }

    private void OnRuleDragged(int row, EDragType drag, Vector2 delta)
    {
        if (!_dragArmed || _dragRow != row) return;
        if (drag == EDragType.Dragging) { _dragDistance += delta.y; return; }
        if (drag != EDragType.DragEnd) return;
        _dragArmed = false;
        int offset = Mathf.RoundToInt(-_dragDistance / 51f);
        int index = _rulePage * RuleRows + row;
        int target = Mathf.Clamp(index + offset, 0, _rules.Count - 1);
        if (target == index || index < 0 || index >= _rules.Count) return;
        _suppressClick = true;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !_isAdmin || _requestPending) return;
        _requestPending = true;
        string id = _rules[index].Id;
        if (!TraderRuleClient.Move(player, id, target - index, result =>
        {
            OnRulesResponse(result);
            if (result.Success) { _rulePage = target / RuleRows; RefreshList(); }
            else SetText(_status, result.Error);
            NetworkLog.Debug("Trader rule drag reorder. rule=" + id + "; from=" + index + "; to=" + target + "; success=" + result.Success);
        })) _requestPending = false;
    }

    private void OpenEditor(TraderAutomationRule rule)
    {
        if (!_isAdmin || _requestPending) return;
        _editing = rule?.Clone() ?? new TraderAutomationRule
        {
            Kind = TraderAutomationRuleKind.AutoSell, Scope = TraderRuleScope.SingleItem,
            MaximumCount = 1, QualityMode = TraderQualityMatchMode.Any
        };
        _qualityMode = _editing.QualityMode;
        _networkComparison = _editing.NetworkCountComparison;
        _currencyComparison = _editing.CurrencyCountComparison;
        _scheduled = _editing.Scheduled;
        SetInput(_quality, Math.Max(1, _editing.Quality));
        SetInput(_maximumCount, _editing.MaximumCount);
        SetInput(_minimumNetworkCount, _editing.MinimumNetworkCount);
        SetInput(_maximumSpend, _editing.MaximumSpend);
        SetInput(_minimumCurrencyCount, _editing.MinimumCurrencyCount);
        SetInput(_hour, _editing.ScheduledHour);
        SetInput(_interval, _editing.IntervalDays);
        SetVisible(_editor, true);
        SetVisible(_main, false);
        SetText(_editorStatus, string.Empty);
        RefreshEditor();
        RefreshNumericValidation(false);
    }

    private void CloseEditor()
    {
        ClosePicker();
        _editing = null;
        SetVisible(_editor, false);
        SetVisible(_main, true);
    }

    private void RefreshEditor()
    {
        if (_editing == null) return;
        bool buy = _editing.Kind == TraderAutomationRuleKind.AutoBuy;
        SetText(_editorTitle, _rules.Any(rule => rule.Id == _editing.Id) ? "编辑规则" : "新增规则");
        SetText(_kindLabel, buy ? "自动购入" : "自动售出");
        SetText(_scopeLabel, _editing.Scope == TraderRuleScope.SingleItem ? "单件商品" : "批量：分类 / 全部");
        SetText(_targetTitle, _editing.Scope == TraderRuleScope.SingleItem ? "点击选择商品" : "点击选择商人分类或全部");
        SetText(_targetLabel, TargetName(_editing));
        if (_targetLabel?.ViewComponent is XUiV_LabelBase targetLabel) targetLabel.Color = Color.white;
        SetIcon(_targetIcon, _editing);
        string categoryExclusions = ExcludedCategoryNames(_editing.ExcludedCategories);
        string itemExclusions = ExcludedItemNames(_editing.ExcludedItemTypes);
        SetText(_exclusionsLabel, "分类黑名单：" + Abbreviate(categoryExclusions, 35)
            + "\n物品黑名单：" + Abbreviate(itemExclusions, 35));
        if (_exclusionsLabel?.ViewComponent != null)
            _exclusionsLabel.ViewComponent.ToolTip = "分类：" + categoryExclusions + "\n物品：" + itemExclusions;
        SetText(_qualityLabel, QualityName(_qualityMode));
        SetText(_scheduleLabel, buy ? (_scheduled ? "计划：开启" : "计划：关闭") : "入库时自动出售");
        SetText(_networkComparisonLabel, "网络数量条件 " + ComparisonText(_networkComparison));
        SetText(_currencyComparisonLabel, "网络公爵币条件 " + ComparisonText(_currencyComparison));
        SetText(_enabledLabel, _editing.Enabled ? "已启用" : "已停用");
        bool unlimitedCount = TraderAutomationService.AllowsUnlimitedMaximumCount(_editing);
        SetText(_maximumCountTitle, unlimitedCount ? "单次最大物品数（0=不限）" : "单次最大物品数");
        string countHelp = unlimitedCount
            ? "自动购入本次最多购买的物品总数。设为 0 时不限制数量，会持续购买直到商人售罄、公爵币不足，或其他规则条件阻止。"
            : "本规则每次执行时最多处理的物品总数，必须大于 0。";
        if (_maximumCountTitle?.ViewComponent != null) _maximumCountTitle.ViewComponent.ToolTip = countHelp;
        if (_maximumCount?.ViewComponent != null) _maximumCount.ViewComponent.ToolTip = countHelp;
        SetVisible(GetChildById("btnTraderRuleCycleSchedule"), buy);
        SetVisible(GetChildById("traderRuleScheduleLabel"), true);
        SetVisible(_hour, buy && _scheduled);
        SetVisible(_interval, buy && _scheduled);
        SetVisible(GetChildById("ruleHourTitle"), buy && _scheduled);
        SetVisible(GetChildById("ruleIntervalTitle"), buy && _scheduled);
        SetVisible(_maximumSpend, buy);
        SetVisible(_spendTitle, buy);
        bool showNetworkThreshold = !buy || _editing.Scope == TraderRuleScope.SingleItem;
        SetVisible(_minimumNetworkCount, showNetworkThreshold);
        SetVisible(GetChildById("ruleNetworkCountTitle"), showNetworkThreshold);
        SetVisible(GetChildById("btnTraderRuleNetworkComparison"), showNetworkThreshold);
        SetVisible(_networkComparisonLabel, showNetworkThreshold);
        SetVisible(_minimumCurrencyCount, buy);
        SetVisible(_currencyTitle, buy);
        SetVisible(GetChildById("btnTraderRuleCurrencyComparison"), buy);
        SetVisible(_currencyComparisonLabel, buy);
        SetVisible(GetChildById("btnRuleDelete"), _rules.Any(rule => rule.Id == _editing.Id));
        RefreshNumericValidation(false);
    }

    private void SavePressed(XUiController sender, int button)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (_editing == null || !(bool)player || !_isAdmin || _requestPending) return;
        _editing.QualityMode = _qualityMode;
        _editing.NetworkCountComparison = _networkComparison;
        _editing.CurrencyCountComparison = _currencyComparison;
        _editing.Scheduled = _editing.Kind == TraderAutomationRuleKind.AutoBuy && _scheduled;
        _editing.Quality = ReadInt(_quality, 1);
        _editing.MaximumCount = ReadInt(_maximumCount, _editing.MaximumCount);
        _editing.MinimumNetworkCount = ReadInt(_minimumNetworkCount, 0);
        _editing.MaximumSpend = ReadInt(_maximumSpend, 0);
        _editing.MinimumCurrencyCount = ReadInt(_minimumCurrencyCount, 0);
        _editing.ScheduledHour = ReadInt(_hour, 5);
        _editing.IntervalDays = ReadInt(_interval, 1);
        if (!RefreshNumericValidation(true)) return;
        if (_editing.Scope == TraderRuleScope.SingleItem && _editing.ItemType <= 0)
        {
            if (_targetLabel?.ViewComponent is XUiV_LabelBase itemTargetLabel) itemTargetLabel.Color = new Color32(255, 92, 92, 255);
            SetText(_editorStatus, "请先选择红色标出的商品");
            return;
        }
        if (_editing.Scope == TraderRuleScope.Category && (_editing.Category?.Length ?? 0) > 128)
        {
            if (_targetLabel?.ViewComponent is XUiV_LabelBase categoryTargetLabel) categoryTargetLabel.Color = new Color32(255, 92, 92, 255);
            SetText(_editorStatus, "选择的分类过多，请减少红色标出的分类");
            return;
        }
        _requestPending = true;
        TraderAutomationRule saved = _editing.Clone();
        bool created = !_rules.Any(rule => rule.Id == saved.Id);
        if (!TraderRuleClient.Save(player, saved, result =>
        {
            OnRulesResponse(result);
            SetText(_status, result.Success ? Localization.Get("HyperdimensionalNetworkTraderRulesSaved") : result.Error);
            if (!result.Success) SetText(_editorStatus, result.Error);
            if (result.Success)
            {
                CloseEditor();
                if (created) { _rulePage = Math.Max(0, (_rules.Count - 1) / RuleRows); RefreshList(); }
            }
        })) _requestPending = false;
    }

    private bool RefreshNumericValidation(bool showError)
    {
        if (_editing == null) return true;
        bool unlimited = TraderAutomationService.AllowsUnlimitedMaximumCount(_editing);
        string error = null;
        CheckNumber(_quality, 0, 6, "品质等级", ref error);
        CheckNumber(_maximumCount, unlimited ? 0 : 1, 1000000, "单次最大物品数", ref error);
        CheckNumber(_minimumNetworkCount, 0, int.MaxValue, "网络数量阈值", ref error);
        CheckNumber(_maximumSpend, 0, int.MaxValue, "单次最高花费", ref error);
        CheckNumber(_minimumCurrencyCount, 0, int.MaxValue, "网络公爵币阈值", ref error);
        CheckNumber(_hour, 0, 23, "游戏小时", ref error);
        CheckNumber(_interval, 1, 3650, "间隔游戏日", ref error);
        if (showError && error != null)
        {
            SetText(_editorStatus, error + "无效，请检查红色数值");
            NetworkLog.Debug("Trader rule editor rejected invalid numeric input. field=" + error + "; kind=" + _editing.Kind + "; scope=" + _editing.Scope);
        }
        return error == null;
    }

    private static void CheckNumber(XUiC_TextInput input, int minimum, int maximum, string field, ref string error)
    {
        if (input == null) return;
        bool valid = int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value >= minimum && value <= maximum;
        Color color = valid ? Color.white : new Color32(255, 92, 92, 255);
        input.ActiveTextColor = color;
        if (input.label != null) input.label.Color = color;
        if (!valid && error == null) error = field;
    }

    private void DeletePressed(XUiController sender, int button)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (_editing == null || !(bool)player || !_isAdmin || _requestPending
            || !_rules.Any(rule => rule.Id == _editing.Id)) return;
        _requestPending = true;
        if (!TraderRuleClient.Delete(player, _editing.Id, result =>
        {
            OnRulesResponse(result);
            SetText(_status, result.Success ? Localization.Get("HyperdimensionalNetworkTraderRulesDeleted") : result.Error);
            if (result.Success) CloseEditor();
        })) _requestPending = false;
    }

    private void RunPressed(XUiController sender, int button)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !_isAdmin || _requestPending) return;
        _requestPending = true;
        if (!TraderRuleClient.RunAll(player, result =>
        {
            OnRulesResponse(result);
            SetText(_status, result.Success ? Localization.Get("HyperdimensionalNetworkTraderRulesRunRequested") : result.Error);
        })) _requestPending = false;
    }

    private void OpenPicker(PickerMode mode)
    {
        if (_editing == null || mode == PickerMode.None) return;
        _pickerMode = mode;
        _pickerPage = 0;
        _stockOnly = false;
        if (_pickerSearch != null) _pickerSearch.Text = string.Empty;
        if (mode == PickerMode.TargetItem || mode == PickerMode.ExcludedItem)
        {
            if (_pickerTypes.Count == 0) BuildItemCatalog();
            SetText(_pickerTitle, mode == PickerMode.TargetItem ? "选择规则商品" : "选择要排除的商品");
        }
        else
        {
            _pickerCategories.Clear();
            _pickerCategories.AddRange(UIDisplayInfoManager.Current?.TraderCategoryDisplayDict?.Values
                .OrderBy(category => category.DisplayName) ?? Enumerable.Empty<CraftingCategoryDisplayEntry>());
            SetText(_pickerTitle, mode == PickerMode.TargetCategory ? "选择商人分类" : "选择要排除的分类");
        }
        SetVisible(_picker, true);
        SetVisible(_editor, false);
        SetVisible(GetChildById("btnRulePickerStock"), mode == PickerMode.TargetItem || mode == PickerMode.ExcludedItem);
        SetVisible(_pickerStockLabel, mode == PickerMode.TargetItem || mode == PickerMode.ExcludedItem);
        RefreshPicker();
    }

    private void ClosePicker()
    {
        _pickerMode = PickerMode.None;
        SetVisible(_picker, false);
        SetVisible(_editor, _editing != null);
        RefreshEditor();
    }

    private void BuildItemCatalog()
    {
        _pickerTypes.Clear();
        ItemClass[] classes = ItemClass.list;
        if (classes == null) return;
        for (int type = 1; type < classes.Length; type++)
        {
            ItemClass item = classes[type];
            if (item == null) continue;
            float economic = item.IsBlock() && type < Block.list.Length ? Block.list[type]?.EconomicValue ?? 0f : item.EconomicValue;
            if (economic > 0f) _pickerTypes.Add(type);
        }
        _pickerTypes.Sort((left, right) => string.Compare(
            ItemClass.GetForId(left)?.GetLocalizedItemName(), ItemClass.GetForId(right)?.GetLocalizedItemName(),
            StringComparison.CurrentCultureIgnoreCase));
        NetworkLog.Debug("Trader rule item picker catalog built. pricedTypes=" + _pickerTypes.Count);
    }

    private void RefreshStockTypes()
    {
        _stockTypes.Clear();
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        foreach (ItemStackGrid grid in new[] { player.inventory?.ItemGrid, player.bag?.ItemGrid })
        {
            if (grid == null) continue;
            foreach (ItemStack stack in grid.items)
                if (stack != null && !stack.IsEmpty()) _stockTypes.Add(stack.itemValue.type);
        }
        if (!NetworkRemoteClient.IsRemoteClient)
        {
            foreach (StoredEntry entry in NetworkStorage.GetEntries(player))
                if (entry?.Value != null && entry.Count > 0) _stockTypes.Add(entry.Value.type);
            RefreshPicker();
            return;
        }
        _stockLoading = true;
        RequestStockPage(player, 0);
        RefreshPicker();
    }

    private void RequestStockPage(EntityPlayerLocal player, int page)
    {
        if (!_stockOnly || _pickerMode == PickerMode.None || page >= 1024) { _stockLoading = false; RefreshPicker(); return; }
        if (!NetworkRemoteClient.RequestPage(player, string.Empty, string.Empty, page, 128, result =>
        {
            if (!_stockOnly || _pickerMode == PickerMode.None) return;
            if (!result.Success) { _stockLoading = false; NetworkLog.Debug("Trader rule stock picker query failed. page=" + page + "; error=" + result.Error); RefreshPicker(); return; }
            foreach (StoredEntry entry in result.Entries ?? new List<StoredEntry>())
                if (entry?.Value != null && entry.Count > 0) _stockTypes.Add(entry.Value.type);
            if ((page + 1) * 128 < result.TotalEntries) RequestStockPage(player, page + 1);
            else { _stockLoading = false; NetworkLog.Debug("Trader rule stock picker loaded network types. count=" + _stockTypes.Count); RefreshPicker(); }
        }))
        {
            _stockLoading = false;
            RefreshPicker();
        }
    }

    private void RefreshPicker()
    {
        if (_pickerMode == PickerMode.None) return;
        bool itemMode = _pickerMode == PickerMode.TargetItem || _pickerMode == PickerMode.ExcludedItem;
        string search = _pickerSearch?.Text?.Trim() ?? string.Empty;
        _visibleTypes.Clear();
        List<CraftingCategoryDisplayEntry> categories = null;
        if (itemMode)
        {
            foreach (int type in _pickerTypes)
            {
                if (_stockOnly && !_stockTypes.Contains(type)) continue;
                ItemClass item = ItemClass.GetForId(type);
                if (item == null) continue;
                if (search.Length > 0 && (item.GetLocalizedItemName() ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                _visibleTypes.Add(type);
            }
        }
        else categories = _pickerCategories.Where(category => search.Length == 0
            || (category.DisplayName ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        int count = itemMode ? _visibleTypes.Count : categories.Count + (_pickerMode == PickerMode.TargetCategory ? 1 : 0);
        _pickerPage = Mathf.Clamp(_pickerPage, 0, Math.Max(0, (count - 1) / PickerRows));
        for (int i = 0; i < PickerRows; i++)
        {
            int index = _pickerPage * PickerRows + i;
            bool visible = index < count;
            SetVisible(_pickerRows[i], visible);
            if (!visible) continue;
            if (itemMode)
            {
                int type = _visibleTypes[index];
                ItemClass item = ItemClass.GetForId(type);
                bool excluded = ContainsId(_editing?.ExcludedItemTypes, type);
                SetText(_pickerNames[i], (_pickerMode == PickerMode.ExcludedItem && excluded ? "✓  " : "") + item?.GetLocalizedItemName());
                SetSprite(_pickerIcons[i], "ItemIconAtlas", item?.GetIconName() ?? "");
            }
            else
            {
                CraftingCategoryDisplayEntry category = _pickerMode == PickerMode.TargetCategory && index == 0 ? null
                    : categories[index - (_pickerMode == PickerMode.TargetCategory ? 1 : 0)];
                bool selected = category != null && ContainsCategory(_pickerMode == PickerMode.TargetCategory
                    ? _editing?.Category : _editing?.ExcludedCategories, category.Name);
                SetText(_pickerNames[i], category == null ? (string.IsNullOrEmpty(_editing?.Category) ? "✓  全部商品" : "全部商品") :
                    (selected ? "✓  " : "") + category.DisplayName);
                string categoryIcon = category?.Icon;
                string icon = category == null ? "ui_game_symbol_shopping_cart"
                    : string.IsNullOrEmpty(categoryIcon) ? "ui_game_symbol_other" : categoryIcon;
                SetSprite(_pickerIcons[i], "SymbolAtlas", icon);
                NetworkLog.Debug("Trader rule category icon assigned. category=" + (category?.Name ?? "<all>")
                    + "; icon=" + icon
                    + "; atlas=SymbolAtlas");
            }
        }
        SetText(_pickerStockLabel, _stockOnly ? (_stockLoading ? "正在读取现有库存…" : "仅现有库存") : "全部有售价物品");
        SetText(_pickerPageLabel, (count == 0 ? "没有匹配商品" : (_pickerPage + 1) + " / " + Math.Max(1, (count + PickerRows - 1) / PickerRows)));
    }

    private void OnPickerRowPressed(int row)
    {
        int index = _pickerPage * PickerRows + row;
        if (_editing == null) return;
        if (_pickerMode == PickerMode.TargetItem || _pickerMode == PickerMode.ExcludedItem)
        {
            if (index < 0 || index >= _visibleTypes.Count) return;
            int type = _visibleTypes[index];
            if (_pickerMode == PickerMode.TargetItem) { _editing.ItemType = type; ClosePicker(); }
            else
            {
                _editing.ExcludedItemTypes = ToggleId(_editing.ExcludedItemTypes, type);
                NetworkLog.Debug("Trader rule item exclusion toggled. rule=" + _editing.Id + "; item=" + type
                    + "; excluded=" + ContainsId(_editing.ExcludedItemTypes, type) + "; excludedItems=" + _editing.ExcludedItemTypes);
                RefreshPicker(); RefreshEditor();
            }
            return;
        }
        List<CraftingCategoryDisplayEntry> categories = _pickerCategories.Where(category =>
            string.IsNullOrEmpty(_pickerSearch?.Text) || (category.DisplayName ?? string.Empty).IndexOf(_pickerSearch.Text, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        if (_pickerMode == PickerMode.TargetCategory && index == 0)
        {
            _editing.Category = string.Empty;
            RefreshPicker();
            RefreshEditor();
            return;
        }
        int categoryIndex = index - (_pickerMode == PickerMode.TargetCategory ? 1 : 0);
        if (categoryIndex < 0 || categoryIndex >= categories.Count) return;
        string name = categories[categoryIndex].Name;
        if (_pickerMode == PickerMode.TargetCategory)
        {
            _editing.Category = ToggleCategory(_editing.Category, name);
            RefreshPicker();
            RefreshEditor();
        }
        else { _editing.ExcludedCategories = ToggleCategory(_editing.ExcludedCategories, name); RefreshPicker(); RefreshEditor(); }
    }

    private static bool ContainsId(string csv, int type) => !string.IsNullOrEmpty(csv) && csv.Split(',').Any(value => int.TryParse(value, out int id) && id == type);
    private static string Abbreviate(string text, int maximum) => text != null && text.Length > maximum ? text.Substring(0, maximum) + "…" : text;
    private static bool ContainsCategory(string csv, string name) => !string.IsNullOrEmpty(csv) && csv.Split('|').Contains(name);
    private static string ToggleId(string csv, int type)
    {
        List<int> values = (csv ?? "").Split(',').Where(value => int.TryParse(value, out int id) && id > 0)
            .Select(int.Parse).Distinct().ToList();
        if (!values.Remove(type)) values.Add(type);
        return string.Join(",", values);
    }
    private static string ToggleCategory(string csv, string name)
    {
        List<string> values = (csv ?? "").Split('|').Where(value => !string.IsNullOrEmpty(value)).Distinct().ToList();
        if (!values.Remove(name)) values.Add(name);
        return string.Join("|", values);
    }
    private static int ReadInt(XUiC_TextInput input, int fallback) =>
        int.TryParse(input?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    private static string ComparisonText(TraderRuleComparison comparison) => comparison switch
    {
        TraderRuleComparison.Greater => ">",
        TraderRuleComparison.GreaterOrEqual => ">=",
        TraderRuleComparison.Equal => "=",
        TraderRuleComparison.LessOrEqual => "<=",
        _ => "<"
    };
    private static void SetText(XUiController controller, string text)
    {
        if (controller?.ViewComponent is XUiV_Label label && label.Text != (text ?? string.Empty))
        { label.Text = text ?? string.Empty; controller.IsDirty = true; }
    }
    private static void SetInput(XUiC_TextInput input, int value) { if (input != null) input.Text = value.ToString(CultureInfo.InvariantCulture); }
    private static void SetVisible(XUiController controller, bool visible) { if (controller?.ViewComponent != null) controller.ViewComponent.IsVisible = visible; }
    private static void SetSprite(XUiController controller, string atlas, string name)
    {
        if (!(controller?.ViewComponent is XUiV_Sprite sprite)) return;
        sprite.SpriteName = string.Empty;
        sprite.UIAtlas = atlas;
        sprite.SpriteName = name;
        controller.IsDirty = true;
    }
    private void Bind(string id, XUiEvent_OnPressEventHandler handler)
    {
        XUiController child = GetChildById(id);
        if (child != null) child.OnPress += handler;
    }
}
