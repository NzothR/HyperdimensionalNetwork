using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class XUiC_CraftingChainWindow : XUiController
{
    private const int NodeSlotCount = 48;
    private const int MaterialSlotCount = 24;
    private const int OptionSlotCount = 8;
    private XUiController _treeViewport;
    private XUiController _treeCanvas;
    private XUiController _recipeDropdown;
    private XUiController _status;
    private XUiController _targetName;
    private XUiController _targetIcon;
    private XUiController _emptyHint;
    private XUiController _confirmCancelPanel;
    private XUiController _startLabel;
    private XUiController _cancelLabel;
    private XUiC_Button _startButton;
    private XUiC_Button _cancelButton;
    private XUiC_Button[] _nodeButtons;
    private XUiController[] _nodeCounts;
    private XUiController[] _edges;
    private XUiC_Button[] _materialButtons;
    private XUiController[] _materialCounts;
    private XUiController[] _forgeIcons;
    private XUiC_Button[] _optionButtons;
    private XUiController[] _optionLabels;
    private Recipe _trackedRecipe;
    private CraftingChainPlanInput _planInput;
    private CraftingChainPlan _plan;
    private CraftingChainViewState _viewState;
    private Task<CraftingChainPlan> _planTask;
    private CancellationTokenSource _planCancellation;
    private int _selectedItemId = -1;
    private CraftingChainRecipeOption[] _activeOptions = Array.Empty<CraftingChainRecipeOption>();
    private Vector3 _lastMousePosition;
    private bool _isPanning;
    private bool _waitingForNetworkSnapshot;
    private bool _orderWasActive;
    private float _nextNetworkSnapshotCheck;
    private float _nextStartCheck;
    private bool _startCheckValid;
    private bool _startAvailable;
    private float _zoom = 1f;
    private Vector2 _pan;
    private string _trackedRootKey = string.Empty;
    private readonly List<LocatedPlanNode> _locatedNodes = new List<LocatedPlanNode>();

    private sealed class LocatedPlanNode
    {
        public CraftingChainPlanNode Node;
        public int Depth;
        public int ParentIndex;
        public Vector3 Position;
    }

    public override void Init()
    {
        base.Init();
        _treeViewport = GetChildById("craftingChainTreeViewport");
        _treeCanvas = GetChildById("craftingChainTreeCanvas");
        _recipeDropdown = GetChildById("craftingChainRecipeDropdown");
        _status = GetChildById("craftingChainStatus");
        _targetName = GetChildById("craftingChainTargetName");
        _targetIcon = GetChildById("craftingChainTargetIcon");
        _emptyHint = GetChildById("craftingChainEmptyHint");
        _confirmCancelPanel = GetChildById("craftingChainConfirmCancel");
        _startLabel = GetChildById("craftingChainStartLabel");
        _cancelLabel = GetChildById("craftingChainCancelLabel");
        _startButton = GetChildById("btnCraftingChainStart") as XUiC_Button;
        _cancelButton = GetChildById("btnCraftingChainCancel") as XUiC_Button;

        _nodeButtons = new XUiC_Button[NodeSlotCount];
        _nodeCounts = new XUiController[NodeSlotCount];
        _edges = new XUiController[NodeSlotCount - 1];
        for (int index = 0; index < _edges.Length; index++)
            _edges[index] = GetChildById("craftingChainEdge" + index.ToString("00"));
        for (int index = 0; index < NodeSlotCount; index++)
        {
            int slot = index;
            _nodeButtons[index] = GetChildById("craftingChainNode" + index.ToString("00")) as XUiC_Button;
            _nodeCounts[index] = GetChildById("craftingChainNodeCount" + index.ToString("00"));
            if (_nodeButtons[index] != null) _nodeButtons[index].OnPress += (sender, mouseButton) => OnNodePressed(slot);
        }

        _materialButtons = new XUiC_Button[MaterialSlotCount];
        _materialCounts = new XUiController[MaterialSlotCount];
        _forgeIcons = new XUiController[MaterialSlotCount];
        for (int index = 0; index < MaterialSlotCount; index++)
        {
            _materialButtons[index] = GetChildById("craftingChainMaterial" + index.ToString("00")) as XUiC_Button;
            _materialCounts[index] = GetChildById("craftingChainMaterialCount" + index.ToString("00"));
            _forgeIcons[index] = GetChildById("craftingChainForgeIcon" + index.ToString("00"));
        }

        _optionButtons = new XUiC_Button[OptionSlotCount];
        _optionLabels = new XUiController[OptionSlotCount];
        for (int index = 0; index < OptionSlotCount; index++)
        {
            int slot = index;
            _optionButtons[index] = GetChildById("craftingChainRecipeOption" + index.ToString("00")) as XUiC_Button;
            _optionLabels[index] = GetChildById("craftingChainRecipeOptionLabel" + index.ToString("00"));
            if (_optionButtons[index] != null) _optionButtons[index].OnPress += (sender, mouseButton) => OnRecipeOptionPressed(slot);
        }

        if (_treeViewport != null) _treeViewport.OnScroll += OnTreeScroll;
        if (_startButton != null) _startButton.OnPress += OnStartPressed;
        if (_cancelButton != null) _cancelButton.OnPress += OnCancelPressed;
        BindPress("btnCraftingChainConfirmCancelYes", OnConfirmCancelPressed);
        BindPress("btnCraftingChainConfirmCancelNo", OnDismissCancelPressed);
        BindPress("btnCraftingChainClose", OnClosePressed);
        SetVisible(_recipeDropdown, false);
        SetVisible(_confirmCancelPanel, false);
        SetVisible(_emptyHint, true);
        ClearNodeSlots();
        ClearMaterialSlots();
        SetStartEnabled(false);
        NetworkLog.Debug("Crafting-chain view initialized. nodeSlots=" + _nodeButtons.Length + "; materialSlots=" + _materialButtons.Length);
    }

    public override void OnOpen()
    {
        base.OnOpen();
        CraftingChainOrderService.ClearLastStatus(xui?.playerUI?.entityPlayer);
        _orderWasActive = CraftingChainOrderService.IsActive(xui?.playerUI?.entityPlayer);
        _viewState = CraftingChainCacheService.Load(xui?.playerUI?.entityPlayer);
        _pan = new Vector2(_viewState.PanX, _viewState.PanY);
        _zoom = _viewState.Zoom;
        ApplyCanvasTransform();
        if (xui?.Recipes != null) xui.Recipes.OnTrackedRecipeChanged += OnTrackedRecipeChanged;
        RefreshTrackedRecipe("window-open");
        NetworkLog.Debug("Crafting-chain window opened. tracked=" + (xui?.Recipes?.TrackedRecipe != null) + "; zoom=" + _zoom.ToString("F2"));
    }

    public override void OnClose()
    {
        if (xui?.Recipes != null) xui.Recipes.OnTrackedRecipeChanged -= OnTrackedRecipeChanged;
        CancelPendingPlan();
        SaveViewState();
        base.OnClose();
        NetworkLog.Debug("Crafting-chain window closed; personal tree view was cached.");
    }

    public override void Update(float _dt)
    {
        base.Update(_dt);
        if (_waitingForNetworkSnapshot && Time.realtimeSinceStartup >= _nextNetworkSnapshotCheck)
        {
            _nextNetworkSnapshotCheck = Time.realtimeSinceStartup + 0.25f;
            if (_planInput != null && NetworkRemoteClient.AreMatchingTypesReady(_planInput.NetworkTypeIds))
            {
                NetworkLog.Debug("Crafting-chain inventory snapshot became ready; recapturing before planning.");
                RefreshTrackedRecipe("network-snapshot-ready");
            }
        }

        if (_planTask != null && _planTask.IsCompleted)
        {
            Task<CraftingChainPlan> task = _planTask;
            _planTask = null;
            _planCancellation?.Dispose();
            _planCancellation = null;
            if (task.IsCanceled) return;
            if (task.IsFaulted)
            {
                NetworkLog.Error("Crafting-chain background planning failed. exception=" + task.Exception?.GetBaseException());
                SetLabel(_status, Localization.Get("HyperdimensionalNetworkCraftingChainPlanFailed"));
                return;
            }
            _plan = task.Result;
            _startCheckValid = false;
            RenderPlan();
        }
        UpdateOrderPresentation();
        UpdatePanning();
    }

    private void OnTrackedRecipeChanged()
    {
        Recipe recipe = xui?.Recipes?.TrackedRecipe;
        string recipeKey = string.Empty;
        if (recipe != null)
        {
            int quality = recipe.GetOutputItemClass()?.HasQuality == true
                ? Math.Max(0, xui.Recipes.TrackedRecipeQuality) : 0;
            recipeKey = CraftingChainPlanner.BuildRecipeKey(recipe, quality);
        }
        CraftingChainOrderService.CancelIfTrackingChanged(xui?.playerUI?.entityPlayer, recipeKey);
        RefreshTrackedRecipe("tracking-changed");
    }

    private void RefreshTrackedRecipe(string reason)
    {
        CancelPendingPlan();
        _trackedRecipe = xui?.Recipes?.TrackedRecipe;
        _plan = null;
        _planInput = null;
        _startCheckValid = false;
        _selectedItemId = -1;
        _activeOptions = Array.Empty<CraftingChainRecipeOption>();
        SetVisible(_recipeDropdown, false);
        ClearNodeSlots();
        ClearMaterialSlots();

        if (_trackedRecipe == null)
        {
            _trackedRootKey = string.Empty;
            SetLabel(_targetName, string.Empty);
            SetIcon(_targetIcon, string.Empty, string.Empty);
            SetVisible(_emptyHint, true);
            SetLabel(_status, Localization.Get("HyperdimensionalNetworkCraftingChainNoTarget"));
            _waitingForNetworkSnapshot = false;
            NetworkLog.Debug("Crafting-chain preview cleared because no recipe is tracked. reason=" + reason);
            return;
        }

        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        ItemClass outputClass = _trackedRecipe.GetOutputItemClass();
        if (!(bool)player || outputClass == null)
        {
            SetLabel(_status, Localization.Get("HyperdimensionalNetworkCraftingChainPlanFailed"));
            return;
        }
        _planInput = CraftingChainPlanner.Capture(xui, player, _trackedRecipe, null);
        if (_planInput == null)
        {
            SetLabel(_status, Localization.Get("HyperdimensionalNetworkCraftingChainPlanFailed"));
            return;
        }
        _trackedRootKey = _planInput.RootRecipeKey;
        _viewState ??= new CraftingChainViewState();
        if (!string.Equals(_viewState.RootRecipeKey, _trackedRootKey, StringComparison.Ordinal))
        {
            _viewState = new CraftingChainViewState { RootRecipeKey = _trackedRootKey };
            _pan = Vector2.zero;
            _zoom = 1f;
            ApplyCanvasTransform();
        }
        ApplySavedRecipeChoices(_planInput);
        _viewState.RootRecipeKey = _trackedRootKey;
        SetLabel(_targetName, outputClass.GetLocalizedItemName() + " × " + _planInput.RequestedCrafts);
        SetIcon(_targetIcon, _trackedRecipe.GetIcon(), outputClass.GetLocalizedItemName());
        SetVisible(_emptyHint, false);

        if (NetworkRemoteClient.IsRemoteClient && !NetworkRemoteClient.AreMatchingTypesReady(_planInput.NetworkTypeIds))
        {
            _waitingForNetworkSnapshot = true;
            _nextNetworkSnapshotCheck = Time.realtimeSinceStartup + 0.25f;
            SetLabel(_status, Localization.Get("HyperdimensionalNetworkCraftingChainSyncing"));
            NetworkLog.Debug("Crafting-chain planning waits for current server inventory. types=" + _planInput.NetworkTypeIds.Length);
            return;
        }
        _waitingForNetworkSnapshot = false;
        StartPlanCalculation(_planInput, reason);
    }

    private void ApplySavedRecipeChoices(CraftingChainPlanInput input)
    {
        if (_viewState?.RecipeChoices == null || input?.Items == null) return;
        input.SelectedRecipeKeys.Clear();
        foreach (CraftingChainItemInfo item in input.Items)
            if (_viewState.RecipeChoices.TryGetValue(CraftingChainCacheService.GetStableItemKey(item), out string recipeKey))
                input.SelectedRecipeKeys[item.Id] = recipeKey;
        if (input.SelectedRecipeKeys.TryGetValue(input.RootOutputItemId, out string rootChoice)
            && input.Recipes.Any(recipe => recipe.OutputItemId == input.RootOutputItemId && recipe.Key == rootChoice))
            input.RootRecipeKey = rootChoice;
    }

    private void StartPlanCalculation(CraftingChainPlanInput input, string reason)
    {
        CancelPendingPlan();
        _planInput = input;
        _plan = null;
        _planCancellation = new CancellationTokenSource();
        _planTask = CraftingChainPlanner.BuildAsync(input, _planCancellation.Token);
        SetLabel(_status, Localization.Get("HyperdimensionalNetworkCraftingChainPlanning"));
        NetworkLog.Debug("Crafting-chain graph calculation started on a worker thread. reason=" + reason + "; recipes=" + input.Recipes.Count
            + "; supplies=" + input.Supplies.Count + "; selectedPaths=" + input.SelectedRecipeKeys.Count);
    }

    private void RenderPlan()
    {
        ClearNodeSlots();
        ClearMaterialSlots();
        if (_plan == null || !string.IsNullOrEmpty(_plan.Error))
        {
            SetLabel(_status, Localization.Get(_plan?.Error == "PlanTooLarge"
                ? "HyperdimensionalNetworkCraftingChainPlanTooLarge"
                : "HyperdimensionalNetworkCraftingChainPlanFailed"));
            SetVisible(_emptyHint, true);
            return;
        }
        SetVisible(_emptyHint, false);
        BuildLocatedNodes();
        for (int index = 0; index < Math.Min(_locatedNodes.Count, _nodeButtons.Length); index++)
        {
            LocatedPlanNode located = _locatedNodes[index];
            CraftingChainItemInfo item = GetItem(located.Node.ItemId);
            SetIcon(_nodeButtons[index], item?.Icon, item?.Name);
            SetVisible(_nodeButtons[index], true);
            _nodeButtons[index].ViewComponent.UiTransform.localPosition = located.Position;
            SetLabel(_nodeCounts[index], located.Node.Batches > 0 ? "×" + located.Node.Batches : string.Empty);
            SetPosition(_nodeCounts[index], located.Position + new Vector3(22f, -25f, 0f));
            SetVisible(_nodeCounts[index], located.Node.Batches > 0);
        }
        RenderEdges();

        for (int index = 0; index < Math.Min(_plan.Materials.Count, _materialButtons.Length); index++)
        {
            CraftingChainPlanMaterial material = _plan.Materials[index];
            CraftingChainItemInfo item = GetItem(material.ItemId);
            float materialX = 12f + (index % 8) * 107f;
            float materialY = -17f - (index / 8) * 32f;
            SetIcon(_materialButtons[index], item?.Icon, item?.Name);
            SetVisible(_materialButtons[index], true);
            SetPosition(_materialButtons[index], new Vector3(materialX, materialY, 0f));
            SetLabel(_materialCounts[index], material.Required + "/" + material.Available);
            SetLabelColor(_materialCounts[index], material.Required > material.Available ? Color.red : Color.white);
            SetPosition(_materialCounts[index], new Vector3(materialX + 38f, materialY - 6f, 0f));
            SetVisible(_materialCounts[index], true);
            if (material.ForgeOnly)
            {
                string forgeIcon = CraftingManager.GetWorkstationData("forge")?.CraftIcon ?? string.Empty;
                SetIcon(_forgeIcons[index], forgeIcon, Localization.Get("HyperdimensionalNetworkCraftingChainForgeSource"), "UIAtlas");
                SetPosition(_forgeIcons[index], new Vector3(materialX + 1f, materialY - 1f, 0f));
                SetVisible(_forgeIcons[index], !string.IsNullOrEmpty(forgeIcon));
            }
            else SetVisible(_forgeIcons[index], false);
        }

        string status = Localization.Get(_plan.MissingMaterials > 0
            ? "HyperdimensionalNetworkCraftingChainMissingMaterials"
            : "HyperdimensionalNetworkCraftingChainPlanReady");
        if (_plan.MissingMaterials > 0) status += " (" + _plan.MissingMaterials + ")";
        SetLabel(_status, status);
        UpdateStartAvailability();
        ApplyCanvasTransform();
        NetworkLog.Debug("Crafting-chain plan rendered. nodes=" + _locatedNodes.Count + "; materials=" + _plan.Materials.Count
            + "; missing=" + _plan.MissingMaterials + "; zoom=" + _zoom.ToString("F2"));
    }

    private void BuildLocatedNodes()
    {
        _locatedNodes.Clear();
        foreach (CraftingChainPlanNode root in _plan.Nodes) AddLocatedNode(root, 0);
        Dictionary<int, int> countsByDepth = _locatedNodes.GroupBy(node => node.Depth).ToDictionary(group => group.Key, group => group.Count());
        Dictionary<int, int> rowsByDepth = new Dictionary<int, int>();
        foreach (LocatedPlanNode node in _locatedNodes)
        {
            int row = rowsByDepth.TryGetValue(node.Depth, out int current) ? current : 0;
            rowsByDepth[node.Depth] = row + 1;
            float centeredRow = row - ((countsByDepth[node.Depth] - 1) / 2f);
            node.Position = new Vector3(760f - node.Depth * 145f, -210f - centeredRow * 82f, 0f);
        }
    }

    private void AddLocatedNode(CraftingChainPlanNode node, int depth, int parentIndex = -1)
    {
        if (node == null || _locatedNodes.Count >= NodeSlotCount) return;
        int index = _locatedNodes.Count;
        _locatedNodes.Add(new LocatedPlanNode { Node = node, Depth = depth, ParentIndex = parentIndex });
        foreach (CraftingChainPlanNode child in node.Children) AddLocatedNode(child, depth + 1, index);
    }

    private void RenderEdges()
    {
        if (_edges == null) return;
        for (int childIndex = 1; childIndex < _locatedNodes.Count && childIndex - 1 < _edges.Length; childIndex++)
        {
            LocatedPlanNode child = _locatedNodes[childIndex];
            if (child.ParentIndex < 0 || child.ParentIndex >= _locatedNodes.Count) continue;
            Vector3 start = _locatedNodes[child.ParentIndex].Position;
            Vector3 end = child.Position;
            Vector3 delta = end - start;
            XUiController edge = _edges[childIndex - 1];
            SetPosition(edge, (start + end) * 0.5f);
            if (edge?.ViewComponent?.UiTransform != null)
            {
                edge.ViewComponent.UiTransform.localScale = new Vector3(Mathf.Max(0.1f, delta.magnitude / 100f), 1f, 1f);
                edge.ViewComponent.UiTransform.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            }
            SetVisible(edge, true);
        }
    }

    private void OnNodePressed(int slot)
    {
        if (_plan == null || slot < 0 || slot >= _locatedNodes.Count) return;
        LocatedPlanNode located = _locatedNodes[slot];
        _selectedItemId = located.Node.ItemId;
        ShowRecipeOptions(located.Node.RecipeOptions, located.Position);
    }

    private void ShowRecipeOptions(CraftingChainRecipeOption[] options, Vector3 nodePosition)
    {
        ClearRecipeOptions();
        if (options == null || options.Length == 0)
        {
            SetVisible(_recipeDropdown, false);
            return;
        }
        SetPosition(_recipeDropdown, nodePosition + new Vector3(-145f, -52f, 0f));
        for (int index = 0; index < Math.Min(options.Length, _optionButtons.Length); index++)
        {
            SetVisible(_optionButtons[index], true);
            SetVisible(_optionLabels[index], true);
            SetLabel(_optionLabels[index], options[index].Label);
            _optionButtons[index].ViewComponent.ToolTip = options[index].Label;
            SetPosition(_optionButtons[index], new Vector3(15f, -22f - index * 35f, 0f));
            SetPosition(_optionLabels[index], new Vector3(42f, -22f - index * 35f, 0f));
        }
        _activeOptions = options;
        SetVisible(_recipeDropdown, true);
    }

    private void OnRecipeOptionPressed(int slot)
    {
        if (_planInput == null || slot < 0 || slot >= _activeOptions.Length || _selectedItemId < 0) return;
        string choice = _activeOptions[slot].Key;
        _planInput.SelectedRecipeKeys[_selectedItemId] = choice;
        if (_viewState != null && _selectedItemId < _planInput.Items.Count)
            _viewState.RecipeChoices[CraftingChainCacheService.GetStableItemKey(_planInput.Items[_selectedItemId])] = choice;
        if (_selectedItemId == _planInput.RootOutputItemId) _planInput.RootRecipeKey = choice;
        SetVisible(_recipeDropdown, false);
        NetworkLog.Debug("Crafting-chain recipe path selected. item=" + _selectedItemId + "; recipe=" + choice);
        StartPlanCalculation(_planInput, "recipe-path-selected");
    }

    private void OnStartPressed(XUiController sender, int mouseButton)
    {
        if (_plan == null || _planInput == null) return;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!CraftingChainOrderService.Start(xui, player, _planInput, _plan, out string error))
        {
            SetLabel(_status, Localization.Get(string.IsNullOrEmpty(error)
                ? "HyperdimensionalNetworkCraftingChainPlanFailed" : error));
            UpdateStartAvailability();
            return;
        }
        _orderWasActive = true;
        _startCheckValid = false;
        UpdateOrderPresentation();
        NetworkLog.Debug("Crafting-chain order accepted from its planning window. player=" + player.entityId + "; root=" + _trackedRootKey);
    }

    private void OnCancelPressed(XUiController sender, int mouseButton)
    {
        if (!CraftingChainOrderService.IsActive(xui?.playerUI?.entityPlayer))
        {
            xui?.playerUI?.windowManager?.Close("windowHyperdimensionalCraftingChain");
            return;
        }
        SetVisible(_confirmCancelPanel, true);
    }

    private void OnConfirmCancelPressed(XUiController sender, int mouseButton)
    {
        SetVisible(_confirmCancelPanel, false);
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!CraftingChainOrderService.RequestCancel(player))
            xui?.playerUI?.windowManager?.Close("windowHyperdimensionalCraftingChain");
        else
            UpdateOrderPresentation();
    }

    private void OnDismissCancelPressed(XUiController sender, int mouseButton)
    {
        SetVisible(_confirmCancelPanel, false);
    }

    private void OnClosePressed(XUiController sender, int mouseButton)
    {
        xui?.playerUI?.windowManager?.Close("windowHyperdimensionalCraftingChain");
    }

    private void UpdateStartAvailability()
    {
        if (CraftingChainOrderService.IsActive(xui?.playerUI?.entityPlayer))
        {
            _startAvailable = false;
            _startCheckValid = true;
            SetStartEnabled(false);
            return;
        }
        if (!_startCheckValid || Time.realtimeSinceStartup >= _nextStartCheck)
        {
            _nextStartCheck = Time.realtimeSinceStartup + 0.5f;
            _startAvailable = _plan != null && _planInput != null
                && CraftingChainOrderService.CanStart(xui, xui?.playerUI?.entityPlayer, _planInput, _plan, out _);
            _startCheckValid = true;
        }
        SetStartEnabled(_startAvailable);
    }

    private void UpdateOrderPresentation()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (CraftingChainOrderService.TryGetStatus(player, out string status, out int step, out int totalSteps, out bool cancellationPending))
        {
            _orderWasActive = true;
            SetLabel(_status, status);
            SetVisible(_startButton, false);
            SetVisible(_startLabel, false);
            SetVisible(_cancelButton, true);
            SetVisible(_cancelLabel, true);
            SetLabel(_cancelLabel, Localization.Get("HyperdimensionalNetworkCraftingChainCancel"));
            if (cancellationPending) SetVisible(_cancelButton, false);
            return;
        }

        SetVisible(_cancelButton, true);
        SetVisible(_cancelLabel, true);
        SetLabel(_cancelLabel, Localization.Get("xuiClose"));
        SetVisible(_startButton, true);
        SetVisible(_startLabel, true);
        UpdateStartAvailability();
        if (_orderWasActive && CraftingChainOrderService.TryGetLastStatus(player, out string lastStatus))
        {
            SetLabel(_status, lastStatus);
            _orderWasActive = false;
        }
    }

    private void CloseWindow()
    {
        xui?.playerUI?.windowManager?.Close("windowHyperdimensionalCraftingChain");
    }

    private void OnTreeScroll(XUiController sender, float delta)
    {
        if (_treeViewport?.ViewComponent?.UiTransformIsHovered != true || delta == 0f) return;
        _zoom = Mathf.Clamp(_zoom + Mathf.Sign(delta) * 0.12f, 0.55f, 1.8f);
        ApplyCanvasTransform();
        NetworkLog.Debug("Crafting-chain tree zoom changed. zoom=" + _zoom.ToString("F2"));
    }

    private void UpdatePanning()
    {
        if (xui?.playerUI?.CursorController == null || _treeViewport?.ViewComponent?.UiTransformIsHovered != true)
        {
            _isPanning = false;
            return;
        }
        CursorControllerAbs cursor = xui.playerUI.CursorController;
        Vector3 current = cursor.GetScreenPosition();
        if (cursor.GetMouseButtonDown(UICamera.MouseButton.LeftButton))
        {
            _isPanning = true;
            _lastMousePosition = current;
        }
        if (_isPanning && cursor.GetMouseButton(UICamera.MouseButton.LeftButton))
        {
            Vector3 delta = current - _lastMousePosition;
            float scale = Mathf.Max(0.5f, Screen.height / 1080f);
            _pan += new Vector2(delta.x / scale, delta.y / scale);
            _lastMousePosition = current;
            ApplyCanvasTransform();
        }
        if (cursor.GetMouseButtonUp(UICamera.MouseButton.LeftButton)) _isPanning = false;
    }

    private void ApplyCanvasTransform()
    {
        if (_treeCanvas?.ViewComponent?.UiTransform == null) return;
        Transform transform = _treeCanvas.ViewComponent.UiTransform;
        transform.localPosition = new Vector3(_pan.x, _pan.y, transform.localPosition.z);
        transform.localScale = Vector3.one * _zoom;
    }

    private void SaveViewState()
    {
        if (_viewState == null) return;
        _viewState.PanX = _pan.x;
        _viewState.PanY = _pan.y;
        _viewState.Zoom = _zoom;
        if (!string.IsNullOrEmpty(_trackedRootKey)) _viewState.RootRecipeKey = _trackedRootKey;
        CraftingChainCacheService.Save(xui?.playerUI?.entityPlayer, _viewState);
    }

    private void CancelPendingPlan()
    {
        _waitingForNetworkSnapshot = false;
        if (_planCancellation != null)
        {
            _planCancellation.Cancel();
            _planCancellation.Dispose();
            _planCancellation = null;
        }
        _planTask = null;
    }

    private void ClearNodeSlots()
    {
        if (_nodeButtons == null) return;
        for (int index = 0; index < _nodeButtons.Length; index++)
        {
            SetVisible(_nodeButtons[index], false);
            SetVisible(_nodeCounts[index], false);
        }
        if (_edges != null)
            foreach (XUiController edge in _edges) SetVisible(edge, false);
    }

    private void ClearMaterialSlots()
    {
        if (_materialButtons == null) return;
        for (int index = 0; index < _materialButtons.Length; index++)
        {
            SetVisible(_materialButtons[index], false);
            SetVisible(_materialCounts[index], false);
            SetVisible(_forgeIcons[index], false);
        }
    }

    private void ClearRecipeOptions()
    {
        if (_optionButtons == null) return;
        for (int index = 0; index < _optionButtons.Length; index++)
        {
            SetVisible(_optionButtons[index], false);
            SetVisible(_optionLabels[index], false);
        }
    }

    private CraftingChainItemInfo GetItem(int itemId)
        => _plan?.Items != null && itemId >= 0 && itemId < _plan.Items.Count ? _plan.Items[itemId] : null;

    private static void SetIcon(XUiController controller, string spriteName, string tooltip, string atlas = "ItemIconAtlas")
    {
        if (controller?.ViewComponent == null) return;
        if (controller.ViewComponent is XUiV_Button button)
        {
            button.UIAtlas = atlas;
            button.DefaultSpriteName = spriteName ?? string.Empty;
            button.HoverSpriteName = spriteName ?? string.Empty;
            button.SelectedSpriteName = spriteName ?? string.Empty;
            button.DisabledSpriteName = spriteName ?? string.Empty;
            button.ToolTip = tooltip ?? string.Empty;
        }
        else if (controller.ViewComponent is XUiV_Sprite sprite)
        {
            sprite.UIAtlas = atlas;
            sprite.SpriteName = spriteName ?? string.Empty;
            sprite.ToolTip = tooltip ?? string.Empty;
        }
    }

    private static void SetLabel(XUiController controller, string text)
    {
        if (controller?.ViewComponent is XUiV_Label label)
        {
            label.Text = text ?? string.Empty;
            controller.IsDirty = true;
        }
    }

    private static void SetLabelColor(XUiController controller, Color color)
    {
        if (controller?.ViewComponent is XUiV_Label label)
        {
            label.Color = color;
            controller.IsDirty = true;
        }
    }

    private static void SetPosition(XUiController controller, Vector3 position)
    {
        if (controller?.ViewComponent?.UiTransform != null) controller.ViewComponent.UiTransform.localPosition = position;
    }

    private static void SetVisible(XUiController controller, bool visible)
    {
        if (controller?.ViewComponent != null) controller.ViewComponent.IsVisible = visible;
    }

    private void SetStartEnabled(bool enabled)
    {
        if (_startButton?.ViewComponent != null) _startButton.ViewComponent.Enabled = enabled;
    }

    private void BindPress(string id, XUiEvent_OnPressEventHandler handler)
    {
        XUiController control = GetChildById(id);
        if (control != null) control.OnPress += handler;
        else NetworkLog.Error("Crafting-chain button was not found. id=" + id);
    }
}
