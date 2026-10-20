using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class XUiC_TraderRulesWindow : XUiController
{
    private const int RuleRows = 9;
    private const int PickerRows = 10;
    private enum PickerMode { None, TargetItem, TargetCategory, ExcludedItem, ExcludedCategory }
    private sealed class PickerSearchDocument
    {
        public int Type;
        public string DisplayName;
        public string InternalName;
    }
    private sealed class PendingSpriteBinding
    {
        public string Atlas;
        public string Sprite;
        public float RetryAt;
        public int Attempts;
    }

    private readonly List<TraderAutomationRule> _rules = new List<TraderAutomationRule>();
    private readonly List<int> _pickerTypes = new List<int>();
    private readonly Dictionary<int, string> _pickerItemNames = new Dictionary<int, string>();
    private readonly List<PickerSearchDocument> _pickerSearchDocuments = new List<PickerSearchDocument>();
    private readonly Dictionary<int, PickerSearchDocument> _pickerSearchDocumentsByType = new Dictionary<int, PickerSearchDocument>();
    private readonly Dictionary<string, int[]> _pickerSearchCache = new Dictionary<string, int[]>(StringComparer.Ordinal);
    private readonly Dictionary<XUiController, PendingSpriteBinding> _pendingSpriteBindings = new Dictionary<XUiController, PendingSpriteBinding>();
    private readonly List<string> _pinyinPrimeNames = new List<string>();
    private readonly List<TraderCreativeCategory> _pickerCategories = new List<TraderCreativeCategory>();
    private readonly List<int> _visibleTypes = new List<int>();
    private readonly HashSet<int> _stockTypes = new HashSet<int>();
    private readonly XUiController[] _ruleRows = new XUiController[RuleRows];
    private readonly XUiController[] _ruleButtons = new XUiController[RuleRows];
    private readonly XUiController[] _ruleIcons = new XUiController[RuleRows];
    private readonly XUiController[] _ruleSummaries = new XUiController[RuleRows];
    private readonly XUiController[] _buyAccents = new XUiController[RuleRows];
    private readonly XUiController[] _sellAccents = new XUiController[RuleRows];
    private readonly XUiController[] _activeAccents = new XUiController[RuleRows];
    private readonly XUiController[] _moveUpButtons = new XUiController[RuleRows];
    private readonly XUiController[] _moveDownButtons = new XUiController[RuleRows];
    private readonly XUiController[] _deleteButtons = new XUiController[RuleRows];
    private readonly XUiController[] _pickerRows = new XUiController[PickerRows];
    private readonly XUiController[] _pickerIcons = new XUiController[PickerRows];
    private readonly XUiController[] _pickerNames = new XUiController[PickerRows];

    private XUiController _status, _editorStatus, _newButton, _newLabel, _pageLabel, _main, _editor, _picker;
    private XUiController _runButton, _runLabel;
    private XUiController _editorTitle, _kindLabel, _scopeLabel, _targetTitle, _targetLabel, _targetIcon;
    private XUiController _exclusionsLabel, _qualityLabel, _scheduleLabel, _networkComparisonLabel, _currencyComparisonLabel;
    private XUiController _enabledLabel, _spendTitle, _currencyTitle, _maximumCountTitle, _pickerTitle, _pickerStockLabel, _pickerPageLabel;
    private XUiC_TextInput _quality, _maximumCount, _minimumNetworkCount, _maximumSpend;
    private XUiC_TextInput _minimumCurrencyCount, _hour, _interval, _pickerSearch;
    private TraderAutomationRule _editing;
    private PickerMode _pickerMode;
    private TraderQualityMatchMode _qualityMode;
    private TraderRuleComparison _networkComparison = TraderRuleComparison.Ignore;
    private TraderRuleComparison _currencyComparison = TraderRuleComparison.GreaterOrEqual;
    private bool _scheduled, _stockOnly, _stockLoading, _requestPending, _isAdmin;
    private bool _runActive, _runPollPending, _pinyinIndexReady, _pinyinReadyObserved;
    private bool _pickerComputedStockOnly, _pickerTaskStockOnly, _pickerQueuedStockOnly, _pickerSearchQueued;
    private int _rulePage, _pickerPage, _pinyinPrimeIndex;
    private float _nextRunPollAt;
    private int _runCurrentRule, _runTotalRules;
    private string _runRuleId = string.Empty;
    private string _pickerComputedQuery = string.Empty, _pickerTaskQuery = string.Empty;
    private string _pickerQueuedQuery = string.Empty;
    private float _pickerSearchDueAt;
    private Task<List<int>> _pickerSearchTask;
    private static int _pendingReturnPlayerId = -1;
    private static bool _loggedReturnWait;

    public override void Init()
    {
        base.Init();
        PinyinSearch.Initialize();
        for (int i = 0; i < RuleRows; i++)
        {
            int row = i;
            _ruleRows[i] = GetChildById("ruleRow" + i);
            _ruleButtons[i] = GetChildById("btnRuleRow" + i);
            _ruleIcons[i] = GetChildById("ruleIcon" + i);
            LogSpriteControllerInitialization("ruleIcon" + i, _ruleIcons[i]);
            _ruleSummaries[i] = GetChildById("ruleSummary" + i);
            _buyAccents[i] = GetChildById("ruleBuyAccent" + i);
            _sellAccents[i] = GetChildById("ruleSellAccent" + i);
            _activeAccents[i] = GetChildById("ruleActiveAccent" + i);
            _moveUpButtons[i] = GetChildById("btnRuleMoveUp" + i);
            _moveDownButtons[i] = GetChildById("btnRuleMoveDown" + i);
            _deleteButtons[i] = GetChildById("btnRuleDeleteRow" + i);
            if (_ruleButtons[i] != null)
            {
                _ruleButtons[i].OnPress += (sender, button) => OnRulePressed(row);
            }
            Bind("btnRuleMoveUp" + i, (sender, button) => MoveRule(row, -1));
            Bind("btnRuleMoveDown" + i, (sender, button) => MoveRule(row, 1));
            Bind("btnRuleDeleteRow" + i, (sender, button) => DeleteRuleFromRow(row));
        }
        for (int i = 0; i < PickerRows; i++)
        {
            int row = i;
            _pickerRows[i] = GetChildById("pickerRow" + i);
            _pickerIcons[i] = GetChildById("pickerIcon" + i);
            LogSpriteControllerInitialization("pickerIcon" + i, _pickerIcons[i]);
            _pickerNames[i] = GetChildById("pickerName" + i);
            Bind("btnPickerRow" + i, (sender, button) => OnPickerRowPressed(row));
            if (_pickerRows[i] != null) _pickerRows[i].OnScroll += OnPickerScroll;
            XUiController pickerButton = GetChildById("btnPickerRow" + i);
            if (pickerButton != null) pickerButton.OnScroll += OnPickerScroll;
        }
        _status = GetChildById("traderRulesStatus");
        _runButton = GetChildById("btnTraderRuleRunAll");
        _runLabel = GetChildById("traderRuleRunAllLabel");
        _editorStatus = GetChildById("ruleEditorStatus");
        _newButton = GetChildById("btnTraderRuleNew");
        _newLabel = GetChildById("ruleNewLabel");
        _pageLabel = GetChildById("rulePageLabel");
        _main = GetChildById("ruleMain");
        _editor = GetChildById("ruleEditor");
        _picker = GetChildById("rulePicker");
        if (_picker != null) _picker.OnScroll += OnPickerScroll;
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
        Bind("btnRulePagePrevious", (sender, button) => { if (!_runActive && !_requestPending && _rulePage > 0) { _rulePage--; RefreshList(); } });
        Bind("btnRulePageNext", (sender, button) => { if (!_runActive && !_requestPending && (_rulePage + 1) * RuleRows < _rules.Count) { _rulePage++; RefreshList(); } });
        Bind("btnTraderRuleRunAll", RunPressed);
        Bind("btnRuleEditorCancelTop", (sender, button) => CloseEditor());
        Bind("btnRuleCancel", (sender, button) => { if (!_runActive) CloseEditor(); });
        Bind("btnRuleSave", SavePressed);
        Bind("btnRuleDelete", DeletePressed);
        Bind("btnRuleEnabled", (sender, button) => { if (!_runActive && _editing != null) { _editing.Enabled = !_editing.Enabled; RefreshEditor(); } });
        Bind("btnTraderRuleCycleKind", CycleRuleKind);
        Bind("btnRuleScope", (sender, button) => { if (_editing != null) { _editing.Scope = _editing.Scope == TraderRuleScope.SingleItem ? TraderRuleScope.Category : TraderRuleScope.SingleItem; RefreshEditor(); } });
        Bind("btnRuleTarget", (sender, button) => OpenPicker(_editing?.Scope == TraderRuleScope.SingleItem ? PickerMode.TargetItem : PickerMode.TargetCategory));
        Bind("btnRuleExcludeItem", (sender, button) => OpenPicker(PickerMode.ExcludedItem));
        Bind("btnRuleExcludeCategory", (sender, button) => OpenPicker(PickerMode.ExcludedCategory));
        Bind("btnTraderRuleCycleQuality", (sender, button) => { _qualityMode = (TraderQualityMatchMode)(((int)_qualityMode + 1) % 6); RefreshEditor(); });
        Bind("btnTraderRuleCycleSchedule", (sender, button) =>
        {
            if (_editing == null) return;
            if (_editing.Kind == TraderAutomationRuleKind.AutoTrash && _isAdmin)
            {
                if (_rules.Any(rule => rule.Id == _editing.Id)) return;
                _editing.Personal = !_editing.Personal;
            }
            else _scheduled = !_scheduled;
            RefreshEditor();
        });
        Bind("btnTraderRuleNetworkComparison", (sender, button) => { _networkComparison = (TraderRuleComparison)(((int)_networkComparison + 1) % 6); RefreshEditor(); });
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
        _runActive = false;
        _runPollPending = false;
        UpdateRunButton();
        _rulePage = 0;
        SetVisible(_main, true);
        SetVisible(_editor, false);
        SetVisible(_picker, false);
        SetText(_status, Localization.Get("HyperdimensionalNetworkTraderRulesLoading"));
        RefreshList();
        QueryRules();
    }

    public override void Update(float _dt)
    {
        base.Update(_dt);
        RetryPendingSpriteBindings();
        CompletePickerSearchIfReady();
        if (PinyinSearch.IsReady && !_pinyinReadyObserved)
        {
            _pinyinReadyObserved = true;
            if (_pickerMode == PickerMode.TargetCategory || _pickerMode == PickerMode.ExcludedCategory) RefreshPicker();
        }
        if (PinyinSearch.IsReady && _pinyinPrimeIndex < _pinyinPrimeNames.Count)
        {
            int end = Math.Min(_pinyinPrimeIndex + 256, _pinyinPrimeNames.Count);
            for (; _pinyinPrimeIndex < end; _pinyinPrimeIndex++) PinyinSearch.Prime(_pinyinPrimeNames[_pinyinPrimeIndex]);
            if (_pinyinPrimeIndex >= _pinyinPrimeNames.Count)
            {
                _pinyinIndexReady = true;
                if (_pickerMode != PickerMode.None) RefreshPicker();
                NetworkLog.Debug("Trader rule pinyin index warmed. names=" + _pinyinPrimeNames.Count);
            }
        }
        StartQueuedPickerSearchIfReady();
        if (!_runActive || _runPollPending || Time.realtimeSinceStartup < _nextRunPollAt) return;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _runPollPending = true;
        _nextRunPollAt = Time.realtimeSinceStartup + 0.75f;
        if (!TraderRuleClient.Query(player, OnRunStatusResponse)) _runPollPending = false;
    }

    public override void OnClose()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if ((bool)player) _pendingReturnPlayerId = player.entityId;
        base.OnClose();
    }

    internal static void ProcessPendingReturn(LocalPlayerUI playerUI)
    {
        if (_pendingReturnPlayerId < 0) return;
        EntityPlayerLocal player = playerUI?.entityPlayer;
        GUIWindowManager manager = playerUI?.windowManager;
        if (!(bool)player || manager == null) { _pendingReturnPlayerId = -1; return; }
        if (player.entityId != _pendingReturnPlayerId) return;
        if (manager.IsWindowOpen("hyperdimensionalTraderRules")
            || manager.GetModalWindow()?.Id == "hyperdimensionalTraderRules")
        {
            if (!_loggedReturnWait)
            {
                _loggedReturnWait = true;
                NetworkLog.Debug("Waiting for trader rules window to finish closing before returning to network. player=" + player.entityId);
            }
            return;
        }
        _loggedReturnWait = false;
        _pendingReturnPlayerId = -1;
        if (!manager.IsWindowOpen("hyperdimensionalNetwork"))
        {
            manager.Open("hyperdimensionalNetwork", true);
            NetworkLog.Debug("Trader rules close returned to the network window. player=" + player.entityId
                + "; networkOpen=" + manager.IsWindowOpen("hyperdimensionalNetwork"));
        }
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
        if (result?.TraderRules != null) _rules.AddRange(result.TraderRules.Select(rule => rule.Clone()));
        if (result?.Success == true) InventoryAutomationClient.SetRules(xui?.playerUI?.entityPlayer, _rules);
        if (_rulePage * RuleRows >= _rules.Count) _rulePage = Math.Max(0, (_rules.Count - 1) / RuleRows);
        RefreshList();
        if (result != null && !result.Success) SetText(_status, result.Error);
        else SetText(_status, Localization.Get("HyperdimensionalNetworkTraderRulesHint"));
        if (result?.TraderRunProgressAvailable == true) ApplyRunProgress(result);
    }

    private void OnRunStatusResponse(NetworkRemoteResult result)
    {
        _runPollPending = false;
        if (result == null || !result.Success)
        {
            _runActive = true;
            _runCurrentRule = Math.Max(1, _runCurrentRule);
            _runTotalRules = Math.Max(1, _runTotalRules);
            SetText(_status, result?.Error ?? "读取执行进度失败");
            UpdateRunButton();
            return;
        }
        if (result.TraderRunProgressAvailable) ApplyRunProgress(result);
    }

    private void ApplyRunProgress(NetworkRemoteResult result)
    {
        _runActive = result.TraderRunActive;
        _runCurrentRule = result.TraderRunCurrentRule;
        _runTotalRules = result.TraderRunTotalRules;
        _runRuleId = result.TraderRunRuleId ?? string.Empty;
        SetText(_status, result.TraderRunMessage);
        RefreshList();
        if (_runActive)
        {
            SetVisible(_editor, false);
            SetVisible(_picker, false);
            SetVisible(_main, true);
        }
        UpdateRunButton();
    }

    private void UpdateRunButton()
    {
        if (_runButton?.ViewComponent is XUiV_Button button)
        {
            button.Enabled = !_runActive && !_requestPending;
            _runButton.IsDirty = true;
        }
        for (int i = 0; i < RuleRows; i++)
        {
            if (_ruleButtons[i]?.ViewComponent is XUiV_Button rowButton)
            { rowButton.Enabled = !_runActive && !_requestPending; _ruleButtons[i].IsDirty = true; }
            int index = _rulePage * RuleRows + i;
            if (_moveUpButtons[i]?.ViewComponent is XUiV_Button moveUp)
            { moveUp.Enabled = !_runActive && !_requestPending && index > 0 && index < _rules.Count && CanMove(index, -1); _moveUpButtons[i].IsDirty = true; }
            if (_moveDownButtons[i]?.ViewComponent is XUiV_Button moveDown)
            { moveDown.Enabled = !_runActive && !_requestPending && index >= 0 && index < _rules.Count - 1 && CanMove(index, 1); _moveDownButtons[i].IsDirty = true; }
            if (_deleteButtons[i]?.ViewComponent is XUiV_Button deleteButton)
            { deleteButton.Enabled = !_runActive && !_requestPending && index >= 0 && index < _rules.Count && CanEdit(_rules[index]); _deleteButtons[i].IsDirty = true; }
        }
        foreach (string id in new[] { "btnTraderRuleNew", "btnRulePagePrevious", "btnRulePageNext" })
            if (GetChildById(id)?.ViewComponent is XUiV_Button otherButton)
            { otherButton.Enabled = !_runActive && !_requestPending; GetChildById(id).IsDirty = true; }
        SetText(_runLabel, _runActive
            ? "正在等待规则 " + Math.Max(1, _runCurrentRule) + "/" + Math.Max(1, _runTotalRules) + " 执行结束…"
            : "立即执行全部");
    }

    private void RefreshList()
    {
        for (int i = 0; i < RuleRows; i++)
        {
            int index = _rulePage * RuleRows + i;
            TraderAutomationRule rule = index < _rules.Count ? _rules[index] : null;
            SetVisible(_ruleRows[i], rule != null);
            if (rule == null)
            {
                SetVisible(_activeAccents[i], false);
                if (_moveUpButtons[i]?.ViewComponent is XUiV_Button emptyUp) emptyUp.Enabled = false;
                if (_moveDownButtons[i]?.ViewComponent is XUiV_Button emptyDown) emptyDown.Enabled = false;
                if (_deleteButtons[i]?.ViewComponent is XUiV_Button emptyDelete) emptyDelete.Enabled = false;
                continue;
            }
            SetVisible(_buyAccents[i], rule.Kind == TraderAutomationRuleKind.AutoBuy);
            SetVisible(_sellAccents[i], rule.Kind == TraderAutomationRuleKind.AutoSell);
            int globalRuleIndex = _rulePage * RuleRows + i + 1;
            bool isActiveRule = _runActive && (_runRuleId.Length > 0
                ? string.Equals(rule.Id, _runRuleId, StringComparison.Ordinal)
                : globalRuleIndex == _runCurrentRule);
            SetVisible(_activeAccents[i], isActiveRule);
            if (_ruleButtons[i]?.ViewComponent is XUiV_Button rowButton)
            {
                Color rowColor = isActiveRule ? new Color32(104, 80, 35, 255) : new Color32(37, 47, 58, 255);
                Color hoverColor = isActiveRule ? new Color32(130, 100, 43, 255) : new Color32(57, 72, 86, 255);
                if (rowButton.DefaultSpriteColor != rowColor || rowButton.HoverSpriteColor != hoverColor)
                {
                    rowButton.DefaultSpriteColor = rowColor;
                    rowButton.HoverSpriteColor = hoverColor;
                    _ruleButtons[i].IsDirty = true;
                }
            }
            if (_moveUpButtons[i]?.ViewComponent is XUiV_Button moveUp)
            { moveUp.Enabled = !_runActive && !_requestPending && CanMove(index, -1); _moveUpButtons[i].IsDirty = true; }
            if (_moveDownButtons[i]?.ViewComponent is XUiV_Button moveDown)
            { moveDown.Enabled = !_runActive && !_requestPending && CanMove(index, 1); _moveDownButtons[i].IsDirty = true; }
            if (_deleteButtons[i]?.ViewComponent is XUiV_Button deleteButton)
            { deleteButton.Enabled = !_runActive && !_requestPending && CanEdit(rule); _deleteButtons[i].IsDirty = true; }
            SetText(_ruleSummaries[i], ShortDescription(rule));
            SetIcon(_ruleIcons[i], rule);
            if (_ruleButtons[i]?.ViewComponent != null) _ruleButtons[i].ViewComponent.ToolTip = FullDescription(rule);
        }
        bool lastPage = (_rulePage + 1) * RuleRows >= _rules.Count;
        SetVisible(_newButton, lastPage);
        SetVisible(_newLabel, lastPage);
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
            + "  ·  使用每条规则右侧箭头调整顺序");
    }

    private bool CanEdit(TraderAutomationRule rule) => rule != null && (rule.Personal || _isAdmin);

    private bool CanMove(int index, int offset)
    {
        int target = index + offset;
        return index >= 0 && target >= 0 && index < _rules.Count && target < _rules.Count
            && CanEdit(_rules[index]) && CanEdit(_rules[target]) && _rules[index].Personal == _rules[target].Personal;
    }

    private static string ShortDescription(TraderAutomationRule rule)
    {
        string target = TargetName(rule);
        string timing = rule.Kind == TraderAutomationRuleKind.AutoBuy && rule.Scheduled
            ? rule.ScheduledHour.ToString("00", CultureInfo.InvariantCulture) + ":00 / " + rule.IntervalDays + "日"
            : rule.Kind == TraderAutomationRuleKind.AutoSell ? "入库时" : rule.Kind == TraderAutomationRuleKind.AutoBuy ? "手动" : "物品变动时";
        return (rule.Enabled ? "" : "○ ") + target + "  ·  " + timing + "  ·  " + KindName(rule.Kind)
            + "  ·  " + (rule.Personal ? "个人" : "网络");
    }

    private static string KindName(TraderAutomationRuleKind kind) => kind switch
    {
        TraderAutomationRuleKind.AutoBuy => "自动购入",
        TraderAutomationRuleKind.AutoSell => "自动售出",
        TraderAutomationRuleKind.AutoDeposit => "自动存入",
        TraderAutomationRuleKind.AutoTrash => "自动销毁",
        _ => "未知规则"
    };

    private static string FullDescription(TraderAutomationRule rule)
    {
        return ShortDescription(rule) + "\n" + (rule.Scope == TraderRuleScope.Category ? "批量" : "单件")
            + "  ·  " + QualityName(rule.QualityMode) + (rule.QualityMode == TraderQualityMatchMode.Any ? "" : " " + rule.Quality)
            + "  ·  最多 " + (rule.MaximumCount == 0 ? "不限" : rule.MaximumCount.ToString())
            + (rule.Kind is TraderAutomationRuleKind.AutoDeposit or TraderAutomationRuleKind.AutoTrash
                || rule.Kind == TraderAutomationRuleKind.AutoBuy && rule.Scope == TraderRuleScope.Category ? ""
                : "\n网络数量 " + (rule.NetworkCountComparison == TraderRuleComparison.Ignore
                    ? "忽略已有库存" : ComparisonText(rule.NetworkCountComparison) + " " + rule.MinimumNetworkCount))
            + (rule.Kind == TraderAutomationRuleKind.AutoBuy ? "\n最多花费 " + (rule.MaximumSpend == 0 ? "不限" : rule.MaximumSpend.ToString())
                + "  ·  公爵币 " + ComparisonText(rule.CurrencyCountComparison) + " " + rule.MinimumCurrencyCount : "")
            + "\n分类黑名单：" + ExcludedCategoryNames(rule.ExcludedCategories)
            + "\n物品黑名单：" + ExcludedItemNames(rule.ExcludedItemTypes);
    }

    private static string TargetName(TraderAutomationRule rule)
    {
        if (rule.Scope == TraderRuleScope.Category)
            return string.IsNullOrEmpty(rule.Category) ? "全部商品" : string.Join("、", rule.Category.Split('|').Select(CategoryName));
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
        return string.Join("、", categories.Split('|').Select(CategoryName));
    }

    private static string CategoryName(string name)
        => TraderCreativeCategories.Find(name)?.DisplayName
            ?? UIDisplayInfoManager.Current?.GetTraderCategoryDisplay(name)?.DisplayName ?? name;

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
        string atlas;
        string icon;
        if (rule.Scope == TraderRuleScope.SingleItem && rule.ItemType > 0)
        {
            atlas = "ItemIconAtlas";
            icon = ItemClass.GetForId(rule.ItemType)?.GetIconName() ?? string.Empty;
        }
        else
        {
            atlas = "SymbolAtlas";
            string category = rule.Category?.Split('|').FirstOrDefault(name => !string.IsNullOrEmpty(name));
            TraderCreativeCategory definition = string.IsNullOrEmpty(category) ? null : TraderCreativeCategories.Find(category);
            string traderIcon = string.IsNullOrEmpty(category) ? null : UIDisplayInfoManager.Current?.GetTraderCategoryDisplay(category)?.Icon;
            icon = string.IsNullOrEmpty(category) ? "ui_game_symbol_shopping_cart" : definition?.Icon ?? traderIcon;
            if (string.IsNullOrEmpty(icon)) icon = "ui_game_symbol_other";
            NetworkLog.Debug("Trader rule category icon resolved. rule=" + rule.Id + "; category=" + (category ?? "<all>")
                + "; creativeDefinition=" + (definition != null) + "; creativeIcon=" + (definition?.Icon ?? "<none>")
                + "; traderIcon=" + (traderIcon ?? "<none>") + "; selected=" + icon);
        }
        SetSprite(controller, atlas, icon);
        NetworkLog.Debug("Trader rule row icon assigned. rule=" + rule.Id + "; atlas=" + atlas + "; sprite=" + icon);
    }

    private void CycleRuleKind(XUiController sender, int button)
    {
        if (_editing == null || _rules.Any(rule => rule.Id == _editing.Id)) return;
        int next = ((int)_editing.Kind % 4) + 1;
        if (!_isAdmin && next <= (int)TraderAutomationRuleKind.AutoBuy) next = (int)TraderAutomationRuleKind.AutoDeposit;
        _editing.Kind = (TraderAutomationRuleKind)next;
        _editing.Personal = _editing.Kind is TraderAutomationRuleKind.AutoDeposit or TraderAutomationRuleKind.AutoTrash;
        if (_editing.Kind == TraderAutomationRuleKind.AutoSell)
        {
            _editing.MaximumCount = 0;
            SetInput(_maximumCount, 0);
        }
        RefreshEditor();
    }

    private void OnRulePressed(int row)
    {
        if (_runActive || _requestPending) return;
        int index = _rulePage * RuleRows + row;
        if (index >= 0 && index < _rules.Count) OpenEditor(_rules[index]);
    }

    private void DeleteRuleFromRow(int row)
    {
        if (_runActive || _requestPending) return;
        int index = _rulePage * RuleRows + row;
        if (index < 0 || index >= _rules.Count) return;
        string id = _rules[index].Id;
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        NetworkLog.Debug("Trader rule row deletion requested directly. rule=" + id + "; shift=" + shift);
        DeleteRuleById(id);
    }

    private void DeleteRuleById(string id)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || _runActive || _requestPending) return;
        _requestPending = true;
        UpdateRunButton();
        if (!TraderRuleClient.Delete(player, id, result =>
        {
            OnRulesResponse(result);
            if (!result.Success) SetText(_status, result.Error);
            UpdateRunButton();
            NetworkLog.Debug("Trader rule row deletion completed. rule=" + id + "; success=" + result.Success
                + "; error=" + (result.Error ?? string.Empty));
        }))
        {
            _requestPending = false;
            UpdateRunButton();
        }
    }

    private void MoveRule(int row, int offset)
    {
        if (_runActive || _requestPending || !CanMove(_rulePage * RuleRows + row, offset)) return;
        int index = _rulePage * RuleRows + row;
        int target = index + offset;
        if (index < 0 || index >= _rules.Count || target < 0 || target >= _rules.Count) return;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _requestPending = true;
        UpdateRunButton();
        string id = _rules[index].Id;
        if (!TraderRuleClient.Move(player, id, target - index, result =>
        {
            OnRulesResponse(result);
            if (result.Success) { _rulePage = target / RuleRows; RefreshList(); }
            else SetText(_status, result.Error);
            UpdateRunButton();
            NetworkLog.Debug("Trader rule arrow reorder. rule=" + id + "; from=" + index + "; to=" + target + "; success=" + result.Success);
        })) { _requestPending = false; UpdateRunButton(); }
    }

    private void OpenEditor(TraderAutomationRule rule)
    {
        if (_requestPending || _runActive || rule != null && !CanEdit(rule)) return;
        _editing = rule?.Clone() ?? new TraderAutomationRule
        {
            Kind = _isAdmin ? TraderAutomationRuleKind.AutoSell : TraderAutomationRuleKind.AutoDeposit,
            Personal = !_isAdmin, Scope = TraderRuleScope.SingleItem,
            MaximumCount = 0, QualityMode = TraderQualityMatchMode.Any
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
        SetText(_kindLabel, KindName(_editing.Kind));
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
        SetText(_scheduleLabel, buy ? (_scheduled ? "计划：开启" : "计划：关闭")
            : _editing.Kind == TraderAutomationRuleKind.AutoTrash ? (_editing.Personal ? "归属：个人库存" : "归属：共享网络")
            : _editing.Kind == TraderAutomationRuleKind.AutoDeposit ? "个人背包变动时自动存入" : "入库时自动出售");
        SetText(_networkComparisonLabel, "网络数量条件 " + ComparisonText(_networkComparison));
        if (_networkComparisonLabel?.ViewComponent != null)
            _networkComparisonLabel.ViewComponent.ToolTip = _networkComparison == TraderRuleComparison.Ignore
                ? "忽略网络中已有的该商品数量；购入规则不检查库存阈值，直接按商人库存、预算和其他条件购入。"
                : "购入时按网络已有数量判断是否满足阈值。点击切换比较条件，也可以选择忽略已有库存。";
        SetText(_currencyComparisonLabel, "网络公爵币条件 " + ComparisonText(_currencyComparison));
        SetText(_enabledLabel, _editing.Enabled ? "已启用" : "已停用");
        bool unlimitedCount = TraderAutomationService.AllowsUnlimitedMaximumCount(_editing);
        SetText(_maximumCountTitle, unlimitedCount ? "单次最大物品数（0=不限）" : "单次最大物品数");
        string countHelp = unlimitedCount
            ? (buy ? "自动购入本次最多购买的物品总数。设为 0 时不限制数量，会持续购买直到商人售罄、公爵币不足，或其他规则条件阻止。"
                : "自动出售本次最多出售的物品总数。设为 0 时会出售所有符合条件的网络库存物品。")
            : "本规则每次执行时最多处理的物品总数，必须大于 0。";
        if (_maximumCountTitle?.ViewComponent != null) _maximumCountTitle.ViewComponent.ToolTip = countHelp;
        if (_maximumCount?.ViewComponent != null) _maximumCount.ViewComponent.ToolTip = countHelp;
        SetVisible(GetChildById("btnTraderRuleCycleSchedule"), buy || _isAdmin && _editing.Kind == TraderAutomationRuleKind.AutoTrash);
        SetVisible(GetChildById("traderRuleScheduleLabel"), true);
        SetVisible(_hour, buy && _scheduled);
        SetVisible(_interval, buy && _scheduled);
        SetVisible(GetChildById("ruleHourTitle"), buy && _scheduled);
        SetVisible(GetChildById("ruleIntervalTitle"), buy && _scheduled);
        SetVisible(_maximumSpend, buy);
        SetVisible(_spendTitle, buy);
        bool showNetworkThreshold = _editing.Kind == TraderAutomationRuleKind.AutoSell || buy && _editing.Scope == TraderRuleScope.SingleItem;
        SetVisible(_minimumNetworkCount, showNetworkThreshold && _networkComparison != TraderRuleComparison.Ignore);
        SetVisible(GetChildById("ruleNetworkCountTitle"), showNetworkThreshold && _networkComparison != TraderRuleComparison.Ignore);
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
        if (_editing == null || !(bool)player || _requestPending || _runActive) return;
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
        if (_editing == null || !(bool)player || !CanEdit(_editing) || _requestPending || _runActive
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
        if (!(bool)player || _requestPending || _runActive) return;
        _runTotalRules = _rules.Count(rule => rule.Enabled);
        _runCurrentRule = _runTotalRules > 0 ? 1 : 0;
        _runActive = _runTotalRules > 0;
        SetText(_status, "正在等待规则 " + _runCurrentRule + "/" + _runTotalRules + " 执行结束…");
        UpdateRunButton();
        _requestPending = true;
        if (!TraderRuleClient.RunAll(player, result =>
        {
            _requestPending = false;
            OnRulesResponse(result);
            if (!result.Success)
            {
                if (result.TraderRunProgressAvailable && result.TraderRunActive) ApplyRunProgress(result);
                else
                {
                    _runActive = false;
                    SetText(_status, result.Error);
                    UpdateRunButton();
                }
            }
            else if (!result.TraderRunProgressAvailable)
            {
                _runActive = false;
                SetText(_status, Localization.Get("HyperdimensionalNetworkTraderRulesRunRequested"));
                UpdateRunButton();
            }
            if (result.Success) InventoryAutomationClient.RunNow(player);
        }))
        {
            _requestPending = false;
            _runActive = false;
            UpdateRunButton();
        }
    }

    private void OpenPicker(PickerMode mode)
    {
        if (_editing == null || mode == PickerMode.None || _runActive || _requestPending) return;
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
            _pickerCategories.AddRange(TraderCreativeCategories.All.OrderBy(category => category.DisplayName, StringComparer.CurrentCultureIgnoreCase));
            SetText(_pickerTitle, mode == PickerMode.TargetCategory ? "选择创意模式分类" : "选择要排除的创意模式分类");
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
        _pickerItemNames.Clear();
        _pickerSearchDocuments.Clear();
        _pickerSearchDocumentsByType.Clear();
        _pickerSearchCache.Clear();
        _pickerComputedQuery = null;
        _pickerSearchTask = null;
        _pinyinPrimeNames.Clear();
        _pinyinPrimeIndex = 0;
        _pinyinIndexReady = false;
        ItemClass[] classes = ItemClass.list;
        if (classes == null) return;
        for (int type = 1; type < classes.Length; type++)
        {
            ItemClass item = classes[type];
            if (item == null) continue;
            // A helper block represents its material's selectable shape family.
            // Individual AutoShape variants are generated from that helper in the game.
            if (item.IsBlock() && type < Block.list.Length
                && Block.list[type]?.AutoShapeType == EAutoShapeType.Shape) continue;
            float economic = item.IsBlock() && type < Block.list.Length ? Block.list[type]?.EconomicValue ?? 0f : item.EconomicValue;
            if (economic <= 0f) continue;
            _pickerTypes.Add(type);
            string displayName = item.GetLocalizedItemName() ?? item.Name ?? string.Empty;
            _pickerItemNames[type] = displayName;
            PickerSearchDocument document = new PickerSearchDocument { Type = type, DisplayName = displayName, InternalName = item.Name ?? string.Empty };
            _pickerSearchDocuments.Add(document);
            _pickerSearchDocumentsByType[type] = document;
            _pinyinPrimeNames.Add(displayName);
            if (!string.IsNullOrEmpty(item.Name) && !string.Equals(item.Name, displayName, StringComparison.Ordinal))
                _pinyinPrimeNames.Add(item.Name);
        }
        _pickerTypes.Sort((left, right) => string.Compare(
            _pickerItemNames[left], _pickerItemNames[right],
            StringComparison.CurrentCultureIgnoreCase));
        if (PinyinSearch.IsReady && _pinyinPrimeNames.Count == 0) _pinyinIndexReady = true;
        NetworkLog.Debug("Trader rule item picker catalog built. pricedTypes=" + _pickerTypes.Count
            + "; pinyinNames=" + _pinyinPrimeNames.Count);
    }

    private void RefreshStockTypes()
    {
        _stockTypes.Clear();
        _pickerComputedQuery = null;
        _pickerSearchCache.Clear();
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
            else { _stockLoading = false; _pickerComputedQuery = null; _pickerSearchCache.Clear(); NetworkLog.Debug("Trader rule stock picker loaded network types. count=" + _stockTypes.Count); RefreshPicker(); }
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
        List<TraderCreativeCategory> categories = null;
        if (itemMode)
        {
            string cacheKey = (_stockOnly ? "1\n" : "0\n") + search;
            if (_pickerComputedQuery != search || _pickerComputedStockOnly != _stockOnly)
            {
                if (_pickerSearchCache.TryGetValue(cacheKey, out int[] cached))
                {
                    _visibleTypes.Clear();
                    _visibleTypes.AddRange(cached);
                    _pickerComputedQuery = search;
                    _pickerComputedStockOnly = _stockOnly;
                }
                else
                {
                    if (IsLatinPinyinQuery(search) && !_pinyinIndexReady)
                    {
                        for (int i = 0; i < PickerRows; i++) SetVisible(_pickerRows[i], false);
                        SetText(_pickerPageLabel, "正在准备拼音搜索索引…");
                        return;
                    }
                    QueuePickerSearch(search);
                    for (int i = 0; i < PickerRows; i++) SetVisible(_pickerRows[i], false);
                    SetText(_pickerPageLabel, "正在搜索…");
                    return;
                }
            }
        }
        else categories = _pickerCategories.Where(category => search.Length == 0
            || PinyinSearch.Matches(category.DisplayName, search)).ToList();
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
                SetText(_pickerNames[i], (_pickerMode == PickerMode.ExcludedItem && excluded ? "✓  " : "")
                    + (_pickerItemNames.TryGetValue(type, out string itemName) ? itemName : item?.GetLocalizedItemName()));
                SetSprite(_pickerIcons[i], "ItemIconAtlas", item?.GetIconName() ?? "");
            }
            else
            {
                TraderCreativeCategory category = _pickerMode == PickerMode.TargetCategory && index == 0 ? null
                    : categories[index - (_pickerMode == PickerMode.TargetCategory ? 1 : 0)];
                bool selected = category != null && ContainsCategory(_pickerMode == PickerMode.TargetCategory
                    ? _editing?.Category : _editing?.ExcludedCategories, category.Name);
                SetText(_pickerNames[i], category == null ? (string.IsNullOrEmpty(_editing?.Category) ? "✓  全部商品" : "全部商品") :
                    (selected ? "✓  " : "") + category.DisplayName);
                string icon = category == null ? "ui_game_symbol_shopping_cart" : category.Icon;
                SetSprite(_pickerIcons[i], "SymbolAtlas", icon);
                NetworkLog.Debug("Trader rule creative category icon assigned. category=" + (category?.Name ?? "<all>") + "; icon=" + icon);
            }
        }
        SetText(_pickerStockLabel, _stockOnly ? (_stockLoading ? "正在读取现有库存…" : "仅现有库存") : "全部有售价物品");
        SetText(_pickerPageLabel, (count == 0 ? "没有匹配商品" : (_pickerPage + 1) + " / " + Math.Max(1, (count + PickerRows - 1) / PickerRows)));
    }

    private void StartPickerSearch(string search)
    {
        if (_pickerSearchTask != null && !_pickerSearchTask.IsCompleted
            && string.Equals(_pickerTaskQuery, search, StringComparison.Ordinal)
            && _pickerTaskStockOnly == _stockOnly) return;
        bool stockOnly = _stockOnly;
        HashSet<int> stockTypes = stockOnly ? new HashSet<int>(_stockTypes) : null;
        PickerSearchDocument[] documents = _pickerSearchDocuments.ToArray();
        Dictionary<int, PickerSearchDocument> documentsByType = _pickerSearchDocumentsByType;
        int[] candidates = null;
        if (_pickerComputedQuery != null && _pickerComputedStockOnly == stockOnly
            && search.Length > _pickerComputedQuery.Length
            && search.StartsWith(_pickerComputedQuery, StringComparison.OrdinalIgnoreCase))
            candidates = _visibleTypes.ToArray();
        _pickerTaskQuery = search;
        _pickerTaskStockOnly = stockOnly;
        _pickerSearchTask = Task.Run(() =>
        {
            List<int> matches = new List<int>();
            if (candidates == null)
            {
                foreach (PickerSearchDocument document in documents)
                {
                    if (stockOnly && !stockTypes.Contains(document.Type)) continue;
                    if (search.Length == 0 || PinyinSearch.Matches(document.DisplayName, search)
                        || PinyinSearch.Matches(document.InternalName, search)) matches.Add(document.Type);
                }
            }
            else
            {
                foreach (int type in candidates)
                {
                    if (!documentsByType.TryGetValue(type, out PickerSearchDocument document)) continue;
                    if (search.Length == 0 || PinyinSearch.Matches(document.DisplayName, search)
                        || PinyinSearch.Matches(document.InternalName, search)) matches.Add(type);
                }
            }
            matches.Sort((left, right) =>
            {
                PickerSearchDocument a = documentsByType[left], b = documentsByType[right];
                int rank = SearchRank(a, search).CompareTo(SearchRank(b, search));
                if (rank != 0) return rank;
                rank = string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
                return rank != 0 ? rank : left.CompareTo(right);
            });
            return matches;
        });
        NetworkLog.Debug("Trader rule picker pinyin search started. query=" + search
            + "; candidateCount=" + (candidates?.Length ?? documents.Length) + "; stockOnly=" + stockOnly);
    }

    private void QueuePickerSearch(string search)
    {
        if (_pickerSearchQueued && _pickerQueuedQuery == search && _pickerQueuedStockOnly == _stockOnly) return;
        _pickerSearchQueued = true;
        _pickerQueuedQuery = search;
        _pickerQueuedStockOnly = _stockOnly;
        _pickerSearchDueAt = Time.realtimeSinceStartup + 0.12f;
    }

    private void StartQueuedPickerSearchIfReady()
    {
        if (!_pickerSearchQueued || Time.realtimeSinceStartup < _pickerSearchDueAt) return;
        bool itemMode = _pickerMode == PickerMode.TargetItem || _pickerMode == PickerMode.ExcludedItem;
        if (!itemMode || _pickerQueuedQuery != (_pickerSearch?.Text?.Trim() ?? string.Empty)
            || _pickerQueuedStockOnly != _stockOnly)
        {
            _pickerSearchQueued = false;
            return;
        }
        if (IsLatinPinyinQuery(_pickerQueuedQuery) && !_pinyinIndexReady) return;
        string query = _pickerQueuedQuery;
        _pickerSearchQueued = false;
        StartPickerSearch(query);
    }

    private void CompletePickerSearchIfReady()
    {
        Task<List<int>> task = _pickerSearchTask;
        if (task == null || !task.IsCompleted) return;
        _pickerSearchTask = null;
        if (task.IsFaulted)
        {
            NetworkLog.Error("Trader rule picker background search failed. query=" + _pickerTaskQuery
                + "; exception=" + task.Exception);
            return;
        }
        if (task.IsCanceled || _pickerMode == PickerMode.None || _pickerTaskQuery != (_pickerSearch?.Text?.Trim() ?? string.Empty)
            || _pickerTaskStockOnly != _stockOnly) return;
        _visibleTypes.Clear();
        _visibleTypes.AddRange(task.Result);
        _pickerComputedQuery = _pickerTaskQuery;
        _pickerComputedStockOnly = _pickerTaskStockOnly;
        string key = (_pickerComputedStockOnly ? "1\n" : "0\n") + _pickerComputedQuery;
        _pickerSearchCache[key] = _visibleTypes.ToArray();
        if (_pickerSearchCache.Count > 64) _pickerSearchCache.Clear();
        _pickerPage = 0;
        NetworkLog.Debug("Trader rule picker background search completed. query=" + _pickerComputedQuery
            + "; matches=" + _visibleTypes.Count);
        RefreshPicker();
    }

    private void OnPickerScroll(XUiController sender, float delta)
    {
        if (_pickerMode == PickerMode.None || delta == 0f) return;
        int pageCount = PickerPageCount();
        int next = Mathf.Clamp(_pickerPage + (delta < 0f ? 1 : -1), 0, pageCount - 1);
        if (next == _pickerPage) return;
        _pickerPage = next;
        RefreshPicker();
        NetworkLog.Debug("Trader rule picker page changed by mouse wheel. page=" + (next + 1) + "/" + pageCount
            + "; mode=" + _pickerMode + "; sender=" + (sender?.ViewComponent?.ID ?? "<none>"));
    }

    private int PickerPageCount()
    {
        bool itemMode = _pickerMode == PickerMode.TargetItem || _pickerMode == PickerMode.ExcludedItem;
        string search = _pickerSearch?.Text?.Trim() ?? string.Empty;
        int count = itemMode ? _visibleTypes.Count : _pickerCategories.Count(category => search.Length == 0
            || PinyinSearch.Matches(category.DisplayName, search)) + (_pickerMode == PickerMode.TargetCategory ? 1 : 0);
        return Math.Max(1, (count + PickerRows - 1) / PickerRows);
    }

    private static int SearchRank(PickerSearchDocument item, string search)
    {
        if (string.IsNullOrEmpty(search)) return 0;
        if (string.Equals(item.DisplayName, search, StringComparison.CurrentCultureIgnoreCase)
            || string.Equals(item.InternalName, search, StringComparison.OrdinalIgnoreCase)) return 0;
        if (item.DisplayName.StartsWith(search, StringComparison.CurrentCultureIgnoreCase)
            || item.InternalName.StartsWith(search, StringComparison.OrdinalIgnoreCase)) return 1;
        if (item.DisplayName.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0
            || item.InternalName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) return 2;
        return 3;
    }

    private static bool IsLatinPinyinQuery(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return false;
        foreach (char c in search)
            if (char.IsLetter(c) && c > 127) return false;
        return search.Any(char.IsLetter);
    }

    private void OnPickerRowPressed(int row)
    {
        if (_runActive || _requestPending) return;
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
        string search = _pickerSearch?.Text?.Trim() ?? string.Empty;
        List<TraderCreativeCategory> categories = _pickerCategories.Where(category =>
            search.Length == 0 || PinyinSearch.Matches(category.DisplayName, search)).ToList();
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
        TraderRuleComparison.Ignore => "忽略已有库存",
        _ => "<"
    };
    private static void SetText(XUiController controller, string text)
    {
        if (controller?.ViewComponent is XUiV_Label label && label.Text != (text ?? string.Empty))
        { label.Text = text ?? string.Empty; controller.IsDirty = true; }
    }
    private static void SetInput(XUiC_TextInput input, int value) { if (input != null) input.Text = value.ToString(CultureInfo.InvariantCulture); }
    private static void SetVisible(XUiController controller, bool visible) { if (controller?.ViewComponent != null) controller.ViewComponent.IsVisible = visible; }
    private void SetSprite(XUiController controller, string atlas, string name)
    {
        if (controller?.ViewComponent is XUiV_Button button)
        {
            button.UIAtlas = atlas;
            button.DefaultSpriteName = name;
            button.HoverSpriteName = name;
            button.SelectedSpriteName = name;
            button.DisabledSpriteName = name;
            BindRenderedSprite(controller, atlas, name);
            controller.IsDirty = true;
            return;
        }
        if (!(controller?.ViewComponent is XUiV_Sprite sprite))
        {
            NetworkLog.Debug("Trader rule sprite assignment skipped: XUi sprite view is missing. controller="
                + (controller?.ViewComponent?.ID ?? "<null>") + "; view=" + (controller?.ViewComponent?.GetType().Name ?? "<none>")
                + "; requestedAtlas=" + atlas + "; requestedSprite=" + name);
            return;
        }
        sprite.SpriteName = string.Empty;
        sprite.UIAtlas = atlas;
        sprite.SpriteName = name;
        BindRenderedSprite(controller, atlas, name);
        controller.IsDirty = true;
    }

    private void LogSpriteControllerInitialization(string id, XUiController controller)
    {
        XUiV_Sprite view = controller?.ViewComponent as XUiV_Sprite;
        XUiV_Button button = controller?.ViewComponent as XUiV_Button;
        UISprite rendered = controller?.ViewComponent?.UiTransform?.GetComponent<UISprite>();
        NetworkLog.Debug("Trader rule sprite controller initialized. id=" + id
            + "; controllerFound=" + (controller != null)
            + "; view=" + (controller?.ViewComponent?.GetType().Name ?? "<none>")
            + "; transformFound=" + (controller?.ViewComponent?.UiTransform != null)
            + "; renderedSpriteFound=" + (rendered != null)
            + "; initialAtlas=" + (view?.UIAtlas ?? button?.UIAtlas ?? "<none>")
            + "; initialSprite=" + (view?.SpriteName ?? button?.DefaultSpriteName ?? "<none>"));
    }

    private void BindRenderedSprite(XUiController controller, string atlasName, string spriteName)
    {
        if (string.IsNullOrEmpty(spriteName)) return;
        UISprite rendered = controller?.ViewComponent?.UiTransform?.GetComponent<UISprite>();
        var atlas = xui?.GetAtlasByName(atlasName, spriteName);
        if (rendered != null && atlas != null)
        {
            rendered.atlas = atlas;
            rendered.spriteName = spriteName;
        }
        if (controller != null)
        {
            if (atlas == null || rendered == null)
            {
                _pendingSpriteBindings.TryGetValue(controller, out PendingSpriteBinding pending);
                pending ??= new PendingSpriteBinding { Atlas = atlasName, Sprite = spriteName };
                pending.Attempts++;
                if (pending.Attempts > 12)
                {
                    _pendingSpriteBindings.Remove(controller);
                    NetworkLog.Error("Trader rule sprite binding exhausted retries. atlas=" + atlasName + "; sprite=" + spriteName
                        + "; view=" + (controller.ViewComponent?.GetType().Name ?? "<none>")
                        + "; found=" + (rendered != null) + "; atlasLoaded=" + (atlas != null));
                }
                else
                {
                    pending.Atlas = atlasName;
                    pending.Sprite = spriteName;
                    pending.RetryAt = Time.realtimeSinceStartup + 0.5f;
                    _pendingSpriteBindings[controller] = pending;
                }
            }
            else _pendingSpriteBindings.Remove(controller);
        }
        NetworkLog.Debug("Trader rule sprite rendered atlas binding. atlas=" + atlasName + "; sprite=" + spriteName
            + "; view=" + (controller?.ViewComponent?.GetType().Name ?? "<none>")
            + "; found=" + (rendered != null) + "; atlasLoaded=" + (atlas != null)
            + "; renderedAtlasType=" + (atlas?.GetType().FullName ?? "<none>")
            + "; renderedSprite=" + (rendered?.spriteName ?? "<none>")
            + "; viewAtlas=" + ((controller?.ViewComponent as XUiV_Sprite)?.UIAtlas
                ?? (controller?.ViewComponent as XUiV_Button)?.UIAtlas ?? "<none>"));
    }

    private void RetryPendingSpriteBindings()
    {
        if (_pendingSpriteBindings.Count == 0) return;
        float now = Time.realtimeSinceStartup;
        List<KeyValuePair<XUiController, PendingSpriteBinding>> due = null;
        List<XUiController> missing = null;
        foreach (KeyValuePair<XUiController, PendingSpriteBinding> entry in _pendingSpriteBindings)
        {
            if (entry.Key?.ViewComponent == null) { (missing ??= new List<XUiController>()).Add(entry.Key); continue; }
            if (now < entry.Value.RetryAt) continue;
            (due ??= new List<KeyValuePair<XUiController, PendingSpriteBinding>>()).Add(entry);
        }
        if (missing != null) foreach (XUiController controller in missing) _pendingSpriteBindings.Remove(controller);
        if (due != null) foreach (KeyValuePair<XUiController, PendingSpriteBinding> entry in due)
            BindRenderedSprite(entry.Key, entry.Value.Atlas, entry.Value.Sprite);
    }
    private void Bind(string id, XUiEvent_OnPressEventHandler handler)
    {
        XUiController child = GetChildById(id);
        if (child != null) child.OnPress += handler;
    }
}
