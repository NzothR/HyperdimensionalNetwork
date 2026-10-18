using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class XUiC_CraftingChainWindow : XUiController
{
    private const int NodeSlotCount = 128;
    private const int EdgeSlotCount = 256;
    private const int MaterialSlotCount = 24;
    private const int OptionSlotCount = 8;
    private const int PreviewIngredientSlotCount = 10;
    private XUiController _treeViewport;
    private XUiController _treeCanvas;
    private XUiController _recipeDropdown;
    private XUiController _recipeDropdownBackground;
    private XUiController _recipeDropdownFrame;
    private XUiController _recipePreview;
    private XUiController _recipePreviewBackground;
    private XUiController _recipePreviewFrame;
    private XUiController _recipePreviewTitle;
    private XUiController _status;
    private XUiController _materialsTitle;
    private XUiController _totalElapsed;
    private XUiController _targetName;
    private XUiController _targetIcon;
    private XUiController _emptyHint;
    private XUiController _confirmCancelPanel;
    private XUiController _startLabel;
    private XUiController _cancelLabel;
    private XUiController _startButton;
    private XUiController _cancelButton;
    private XUiC_TextInput _quantityInput;
    private XUiController[] _quantityControls;
    private XUiController[] _nodeButtons;
    private XUiController[] _nodeFrames;
    private XUiController[] _nodeCounts;
    private XUiController[] _edges;
    private XUiController[] _materialButtons;
    private XUiController[] _materialCounts;
    private XUiController[] _forgeIcons;
    private XUiController[] _optionButtons;
    private XUiController[] _optionLabels;
    private XUiController[] _previewRows;
    private XUiController[] _previewIcons;
    private XUiController[] _previewNames;
    private XUiController[] _previewCounts;
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
    private bool _showingCompletedOrder;
    private int _visibleCompletedStages = -1;
    private readonly HashSet<CraftingChainPlanNode> _completedNodes = new HashSet<CraftingChainPlanNode>();
    private float _nextNetworkSnapshotCheck;
    private float _nextStartCheck;
    private float _nextPresentationCheck;
    private float _quantityRefreshAt = -1f;
    private int _desiredOutputCount = 1;
    private int _recipePopupOpenedFrame = -1;
    private int _recipePopupDismissedFrame = -1;
    private float _recipePreviewX;
    private float _recipePopupY;
    private float _nextPerformanceLog;
    private float _maxObservedFrameTime;
    private float _observedFrameTimeTotal;
    private int _observedFrames;
    private bool _startCheckValid;
    private bool _startAvailable;
    private float _zoom = 1f;
    private Vector2 _pan;
    private string _trackedRootKey = string.Empty;
    private bool _treeTruncated;
    private readonly List<LocatedPlanNode> _locatedNodes = new List<LocatedPlanNode>();

    private sealed class LocatedPlanNode
    {
        public CraftingChainPlanNode RecipeNode;
        public int ItemId;
        public long Count;
        public bool ForgeOnly;
        public int Depth;
        public int ParentIndex;
        public float LeafX;
        public Vector3 Position;
    }

    public override void Init()
    {
        base.Init();
        _treeViewport = GetChildById("craftingChainTreeViewport");
        _treeCanvas = GetChildById("craftingChainTreeCanvas");
        _recipeDropdown = GetChildById("craftingChainRecipeDropdown");
        _recipeDropdownBackground = GetChildById("recipeDropdownBackground");
        _recipeDropdownFrame = GetChildById("recipeDropdownFrame");
        _recipePreview = GetChildById("craftingChainRecipePreview");
        _recipePreviewBackground = GetChildById("recipePreviewBackground");
        _recipePreviewFrame = GetChildById("recipePreviewFrame");
        _recipePreviewTitle = GetChildById("craftingChainRecipePreviewTitle");
        _status = GetChildById("craftingChainStatus");
        _materialsTitle = GetChildById("materialsTitle");
        _totalElapsed = GetChildById("craftingChainTotalElapsed");
        _targetName = GetChildById("craftingChainTargetName");
        _targetIcon = GetChildById("craftingChainTargetIcon");
        _emptyHint = GetChildById("craftingChainEmptyHint");
        _confirmCancelPanel = GetChildById("craftingChainConfirmCancel");
        _startLabel = GetChildById("craftingChainStartLabel");
        _cancelLabel = GetChildById("craftingChainCancelLabel");
        _startButton = GetChildById("btnCraftingChainStart");
        _cancelButton = GetChildById("btnCraftingChainCancel");
        _quantityInput = GetChildById("craftingChainQuantityInput") as XUiC_TextInput;
        _quantityControls = new[] { "craftingChainQuantityLabel", "btnCraftingChainQuantityMinus",
            "craftingChainQuantityMinusLabel", "craftingChainQuantityInput",
            "btnCraftingChainQuantityPlus", "craftingChainQuantityPlusLabel" }
            .Select(GetChildById).ToArray();
        if (_quantityInput != null) _quantityInput.OnChangeHandler += OnQuantityChanged;

        _nodeButtons = new XUiController[NodeSlotCount];
        _nodeFrames = new XUiController[NodeSlotCount];
        _nodeCounts = new XUiController[NodeSlotCount];
        _edges = new XUiController[EdgeSlotCount];
        for (int index = 0; index < _edges.Length; index++)
            _edges[index] = GetChildById("craftingChainEdge" + index.ToString("00"));
        for (int index = 0; index < NodeSlotCount; index++)
        {
            int slot = index;
            _nodeFrames[index] = GetChildById("craftingChainNodeFrame" + index.ToString("00"));
            _nodeButtons[index] = GetChildById("craftingChainNode" + index.ToString("00"));
            _nodeCounts[index] = GetChildById("craftingChainNodeCount" + index.ToString("00"));
            if (_nodeButtons[index] != null) _nodeButtons[index].OnPress += (sender, mouseButton) => OnNodePressed(slot);
        }

        _materialButtons = new XUiController[MaterialSlotCount];
        _materialCounts = new XUiController[MaterialSlotCount];
        _forgeIcons = new XUiController[MaterialSlotCount];
        for (int index = 0; index < MaterialSlotCount; index++)
        {
            _materialButtons[index] = GetChildById("craftingChainMaterial" + index.ToString("00"));
            _materialCounts[index] = GetChildById("craftingChainMaterialCount" + index.ToString("00"));
            _forgeIcons[index] = GetChildById("craftingChainForgeIcon" + index.ToString("00"));
        }

        _optionButtons = new XUiController[OptionSlotCount];
        _optionLabels = new XUiController[OptionSlotCount];
        for (int index = 0; index < OptionSlotCount; index++)
        {
            int slot = index;
            _optionButtons[index] = GetChildById("craftingChainRecipeOption" + index.ToString("00"));
            _optionLabels[index] = GetChildById("craftingChainRecipeOptionLabel" + index.ToString("00"));
            if (_optionButtons[index] != null) _optionButtons[index].OnPress += (sender, mouseButton) => OnRecipeOptionPressed(slot);
            if (_optionButtons[index] != null) _optionButtons[index].OnHover += (sender, isOver) => OnRecipeOptionHovered(slot, isOver);
        }

        _previewRows = new XUiController[PreviewIngredientSlotCount];
        _previewIcons = new XUiController[PreviewIngredientSlotCount];
        _previewNames = new XUiController[PreviewIngredientSlotCount];
        _previewCounts = new XUiController[PreviewIngredientSlotCount];
        for (int index = 0; index < PreviewIngredientSlotCount; index++)
        {
            string suffix = index.ToString("00");
            _previewRows[index] = GetChildById("craftingChainPreviewRow" + suffix);
            _previewIcons[index] = GetChildById("craftingChainPreviewIcon" + suffix);
            _previewNames[index] = GetChildById("craftingChainPreviewName" + suffix);
            _previewCounts[index] = GetChildById("craftingChainPreviewCount" + suffix);
        }

        if (_treeViewport != null) _treeViewport.OnScroll += OnTreeScroll;
        if (_startButton != null) _startButton.OnPress += OnStartPressed;
        if (_cancelButton != null) _cancelButton.OnPress += OnCancelPressed;
        BindPress("btnCraftingChainConfirmCancelYes", OnConfirmCancelPressed);
        BindPress("btnCraftingChainConfirmCancelNo", OnDismissCancelPressed);
        BindPress("btnCraftingChainClose", OnClosePressed);
        BindPress("btnCraftingChainQuantityMinus", (sender, button) => ChangeOutputCount(-1));
        BindPress("btnCraftingChainQuantityPlus", (sender, button) => ChangeOutputCount(1));
        SetVisible(_recipeDropdown, false);
        SetVisible(_recipePreview, false);
        SetVisible(_confirmCancelPanel, false);
        SetVisible(_emptyHint, true);
        ClearNodeSlots();
        ClearMaterialSlots();
        SetStartEnabled(false);
        NetworkLog.Debug("Crafting-chain view initialized. nodeSlots=" + _nodeButtons.Length + "; materialSlots=" + _materialButtons.Length
            + "; firstNode=" + (_nodeButtons[0]?.GetType().Name ?? "<missing>")
            + "; firstMaterial=" + (_materialButtons[0]?.GetType().Name ?? "<missing>"));
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _nextPerformanceLog = Time.realtimeSinceStartup + 30f;
        _maxObservedFrameTime = 0f;
        _observedFrameTimeTotal = 0f;
        _observedFrames = 0;
        _orderWasActive = CraftingChainOrderService.IsActive(xui?.playerUI?.entityPlayer);
        _showingCompletedOrder = false;
        _viewState = CraftingChainCacheService.Load(xui?.playerUI?.entityPlayer);
        _quantityRefreshAt = -1f;
        _desiredOutputCount = GetTrackedDefaultOutputCount();
        SetQuantityInput(_desiredOutputCount);
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
        float frameTime = Time.unscaledDeltaTime;
        _maxObservedFrameTime = Mathf.Max(_maxObservedFrameTime, frameTime);
        _observedFrameTimeTotal += frameTime;
        _observedFrames++;
        if (Time.realtimeSinceStartup >= _nextPerformanceLog)
        {
            NetworkLog.Debug("Crafting-chain view frame sample. frames=" + _observedFrames
                + "; averageMs=" + (_observedFrameTimeTotal * 1000f / Math.Max(1, _observedFrames)).ToString("F1")
                + "; maxMs=" + (_maxObservedFrameTime * 1000f).ToString("F1")
                + "; managedMB=" + (GC.GetTotalMemory(false) / 1048576L)
                + "; nodes=" + _locatedNodes.Count + "; popup=" + (_recipeDropdown?.ViewComponent?.IsVisible == true));
            _nextPerformanceLog = Time.realtimeSinceStartup + 30f;
            _maxObservedFrameTime = 0f;
            _observedFrameTimeTotal = 0f;
            _observedFrames = 0;
        }
        if (_waitingForNetworkSnapshot && Time.realtimeSinceStartup >= _nextNetworkSnapshotCheck)
        {
            _nextNetworkSnapshotCheck = Time.realtimeSinceStartup + 0.25f;
            if (_planInput != null && NetworkRemoteClient.AreMatchingTypesReady(_planInput.NetworkTypeIds))
            {
                NetworkLog.Debug("Crafting-chain inventory snapshot became ready; recapturing before planning.");
                RefreshTrackedRecipe("network-snapshot-ready");
            }
        }

        if (_quantityRefreshAt >= 0f && Time.realtimeSinceStartup >= _quantityRefreshAt)
        {
            _quantityRefreshAt = -1f;
            RefreshTrackedRecipe("output-count-changed");
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
            if (_showingCompletedOrder) return;
            try
            {
                RenderPlan();
            }
            catch (Exception ex)
            {
                NetworkLog.Error("Crafting-chain plan rendering failed. root=" + _trackedRootKey
                    + "; nodes=" + (_plan?.Nodes?.Count ?? 0)
                    + "; materials=" + (_plan?.Materials?.Count ?? 0)
                    + "; exception=" + ex);
                _plan = null;
                _startCheckValid = false;
                SetStartEnabled(false);
                SetVisible(_emptyHint, false);
                SetLabel(_status, Localization.Get("HyperdimensionalNetworkCraftingChainPlanFailed"));
            }
        }
        if (Time.realtimeSinceStartup >= _nextPresentationCheck)
        {
            _nextPresentationCheck = Time.realtimeSinceStartup + 0.2f;
            UpdateOrderPresentation();
            UpdateProgressVisuals();
        }
        UpdateRecipePopupDismissal();
        UpdatePanning();
    }

    private void OnTrackedRecipeChanged()
    {
        _quantityRefreshAt = -1f;
        _desiredOutputCount = GetTrackedDefaultOutputCount();
        SetQuantityInput(_desiredOutputCount);
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
        if (ShowActiveOrder()) return;
        _completedNodes.Clear();
        _visibleCompletedStages = -1;
        SetLabel(_materialsTitle, Localization.Get("HyperdimensionalNetworkCraftingChainMaterials"));
        SetVisible(_totalElapsed, false);
        _trackedRecipe = xui?.Recipes?.TrackedRecipe;
        _plan = null;
        _planInput = null;
        _startCheckValid = false;
        _selectedItemId = -1;
        _activeOptions = Array.Empty<CraftingChainRecipeOption>();
        SetVisible(_recipeDropdown, false);
        SetVisible(_recipePreview, false);
        ClearNodeSlots();
        ClearMaterialSlots();

        if (_trackedRecipe == null)
        {
            _trackedRootKey = string.Empty;
            SetLabel(_targetName, string.Empty);
            SetIcon(_targetIcon, string.Empty, string.Empty);
            SetVisible(_emptyHint, true);
            SetLabel(_emptyHint, Localization.Get("HyperdimensionalNetworkCraftingChainNoTarget"));
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
        _planInput = CraftingChainPlanner.Capture(xui, player, _trackedRecipe, null, _desiredOutputCount);
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
        SetLabel(_targetName, outputClass.GetLocalizedItemName() + " × " + _planInput.RequestedOutputCount);
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

    private bool ShowActiveOrder()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!CraftingChainOrderService.TryGetActiveView(player, out CraftingChainPlanInput input,
            out CraftingChainPlan plan)) return false;
        _planInput = input;
        _plan = plan;
        _showingCompletedOrder = false;
        _trackedRootKey = plan.RootRecipeKey;
        _desiredOutputCount = plan.RequestedOutputCount;
        SetQuantityInput(_desiredOutputCount);
        CraftingChainItemInfo item = GetItem(input.RootOutputItemId);
        SetLabel(_targetName, (item?.Name ?? string.Empty) + " × " + plan.RequestedOutputCount);
        SetIcon(_targetIcon, item?.Icon, item?.Name);
        SetVisible(_emptyHint, false);
        _startCheckValid = false;
        _visibleCompletedStages = -1;
        UpdateProgressVisuals();
        NetworkLog.Debug("Crafting-chain view restored from active order. player=" + player.entityId
            + "; stages=" + plan.Nodes.Count);
        return true;
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
        bool running = CraftingChainOrderService.IsActive(xui?.playerUI?.entityPlayer);
        SetQuantityControlsVisible(!running);
        SetLabel(_materialsTitle, Localization.Get(running
            ? "HyperdimensionalNetworkCraftingChainTotalElapsed" : "HyperdimensionalNetworkCraftingChainMaterials"));
        SetVisible(_totalElapsed, running);
        BuildLocatedNodes();
        if (_treeTruncated)
        {
            SetStartEnabled(false);
            SetLabel(_status, Localization.Get("HyperdimensionalNetworkCraftingChainPlanTooLarge"));
            return;
        }
        bool nodeViewProblemLogged = false;
        for (int index = 0; index < Math.Min(_locatedNodes.Count, _nodeButtons.Length); index++)
        {
            LocatedPlanNode located = _locatedNodes[index];
            CraftingChainItemInfo item = GetItem(located.ItemId);
            XUiController nodeButton = _nodeButtons[index];
            SetIcon(nodeButton, item?.Icon, item?.Name);
            if (!running)
            {
                SetSpriteColor(_nodeFrames[index], new Color32(94, 151, 176, 255));
                SetButtonColor(nodeButton, Color.white);
            }
            SetVisible(nodeButton, true);
            if (nodeButton?.ViewComponent == null && !nodeViewProblemLogged)
            {
                nodeViewProblemLogged = true;
                NetworkLog.Error("Crafting-chain node view is missing. slot=" + index
                    + "; itemId=" + located.ItemId
                    + "; controller=" + (nodeButton?.GetType().Name ?? "<missing>")
                    + "; view=" + (nodeButton?.ViewComponent?.GetType().Name ?? "<missing>"));
            }
            SetPosition(_nodeFrames[index], located.Position);
            SetVisible(_nodeFrames[index], true);
            SetPosition(nodeButton, located.Position + new Vector3(8f, -8f, 0f));
            SetLabel(_nodeCounts[index], "×" + located.Count);
            SetPosition(_nodeCounts[index], located.Position + new Vector3(18f, -54f, 0f));
            SetVisible(_nodeCounts[index], true);
        }
        RenderEdges();

        if (running)
        {
            ClearMaterialSlots();
            UpdateStartAvailability();
            ApplyCanvasTransform();
            return;
        }

        for (int index = 0; index < Math.Min(_plan.Materials.Count, _materialButtons.Length); index++)
        {
            CraftingChainPlanMaterial material = _plan.Materials[index];
            CraftingChainItemInfo item = GetItem(material.ItemId);
            float materialX = 12f + (index % 8) * 107f;
            bool compact = _plan.Materials.Count > 16;
            float materialY = -4f - (index / 8) * (compact ? 35f : 55f);
            int iconSize = compact ? 30 : 42;
            if (_materialButtons[index]?.ViewComponent != null)
            {
                _materialButtons[index].ViewComponent.Width = iconSize;
                _materialButtons[index].ViewComponent.Height = iconSize;
            }
            SetIcon(_materialButtons[index], item?.Icon, item?.Name);
            SetVisible(_materialButtons[index], true);
            SetPosition(_materialButtons[index], new Vector3(materialX, materialY, 0f));
            SetLabel(_materialCounts[index], material.Required + "/" + material.Available);
            SetLabelColor(_materialCounts[index], material.Required > material.Available ? Color.red : Color.white);
            SetPosition(_materialCounts[index], new Vector3(materialX + (compact ? 29f : 38f), materialY - 6f, 0f));
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
        _treeTruncated = false;
        float nextLeaf = 0f;
        foreach (CraftingChainPlanNode root in _plan.Nodes)
            if (!_completedNodes.Contains(root))
                AddLocatedNode(root, root.ItemId, root.OutputCount, false, 0, -1, ref nextLeaf);
        if (_locatedNodes.Count == 0 && _planInput != null && !CraftingChainOrderService.IsActive(xui?.playerUI?.entityPlayer))
            AddLocatedNode(null, _planInput.RootOutputItemId, _plan.RequestedOutput, false, 0, -1, ref nextLeaf);
        float centerLeaf = (nextLeaf - 1f) * 0.5f;
        foreach (LocatedPlanNode node in _locatedNodes)
        {
            node.Position = new Vector3(407f + (node.LeafX - centerLeaf) * 112f, -20f - node.Depth * 120f, 0f);
        }
        if (_treeTruncated)
            NetworkLog.Debug("Crafting-chain tree reached the visible node limit. limit=" + NodeSlotCount + "; root=" + _trackedRootKey);
    }

    private int AddLocatedNode(CraftingChainPlanNode recipeNode, int itemId, long count, bool forgeOnly,
        int depth, int parentIndex, ref float nextLeaf)
    {
        if (_locatedNodes.Count >= NodeSlotCount)
        {
            _treeTruncated = true;
            return -1;
        }
        int index = _locatedNodes.Count;
        LocatedPlanNode located = new LocatedPlanNode
        {
            RecipeNode = recipeNode, ItemId = itemId, Count = count, ForgeOnly = forgeOnly,
            Depth = depth, ParentIndex = parentIndex
        };
        _locatedNodes.Add(located);
        float firstChildX = 0f;
        float lastChildX = 0f;
        int childCount = 0;
        if (recipeNode != null)
            foreach (CraftingChainPlanDisplayChild child in recipeNode.DisplayChildren)
            {
                if (child.RecipeNode != null && _completedNodes.Contains(child.RecipeNode)) continue;
                int childIndex = AddLocatedNode(child.RecipeNode, child.ItemId, child.RequiredCount,
                    child.ForgeOnly, depth + 1, index, ref nextLeaf);
                if (childIndex < 0) break;
                float childX = _locatedNodes[childIndex].LeafX;
                if (childCount++ == 0) firstChildX = childX;
                lastChildX = childX;
            }
        located.LeafX = childCount > 0 ? (firstChildX + lastChildX) * 0.5f : nextLeaf++;
        return index;
    }

    private void RenderEdges()
    {
        if (_edges == null) return;
        int edgeIndex = 0;
        for (int parentIndex = 0; parentIndex < _locatedNodes.Count; parentIndex++)
        {
            List<LocatedPlanNode> children = new List<LocatedPlanNode>();
            for (int index = parentIndex + 1; index < _locatedNodes.Count; index++)
                if (_locatedNodes[index].ParentIndex == parentIndex) children.Add(_locatedNodes[index]);
            if (children.Count == 0) continue;
            LocatedPlanNode parent = _locatedNodes[parentIndex];
            float parentX = parent.Position.x + 38f;
            float branchY = parent.Position.y - 98f;
            DrawEdge(ref edgeIndex, parentX, parent.Position.y - 76f, parentX, branchY);
            float left = children.Min(child => child.Position.x + 38f);
            float right = children.Max(child => child.Position.x + 38f);
            if (right > left) DrawEdge(ref edgeIndex, left, branchY, right, branchY);
            foreach (LocatedPlanNode child in children)
                DrawEdge(ref edgeIndex, child.Position.x + 38f, branchY, child.Position.x + 38f, child.Position.y);
        }
        NetworkLog.Debug("Crafting-chain orthogonal connectors rendered. segments=" + edgeIndex);
    }

    private void DrawEdge(ref int edgeIndex, float x1, float y1, float x2, float y2)
    {
        if (edgeIndex >= _edges.Length) return;
        XUiController edge = _edges[edgeIndex++];
        bool horizontal = Mathf.Abs(x2 - x1) > Mathf.Abs(y2 - y1);
        SetPosition(edge, new Vector3(Mathf.Min(x1, x2), Mathf.Max(y1, y2), 0f));
        if (edge?.ViewComponent != null)
        {
            edge.ViewComponent.Width = horizontal ? Math.Max(1, Mathf.RoundToInt(Mathf.Abs(x2 - x1))) : 3;
            edge.ViewComponent.Height = horizontal ? 3 : Math.Max(1, Mathf.RoundToInt(Mathf.Abs(y2 - y1)));
            edge.ViewComponent.Rotation = 0f;
        }
        SetVisible(edge, true);
    }

    private void OnNodePressed(int slot)
    {
        if (CraftingChainOrderService.IsActive(xui?.playerUI?.entityPlayer)) return;
        if (_plan == null || slot < 0 || slot >= _locatedNodes.Count) return;
        LocatedPlanNode located = _locatedNodes[slot];
        _selectedItemId = located.ItemId;
        CraftingChainRecipeOption[] options = located.RecipeNode?.RecipeOptions;
        if (options == null || options.Length == 0)
            options = _planInput?.Recipes.Where(recipe => recipe.OutputItemId == located.ItemId)
                .Select(recipe => new CraftingChainRecipeOption
                {
                    Key = recipe.Key,
                    CraftingArea = recipe.CraftingArea,
                    Ingredients = recipe.Ingredients.Select(ingredient => new CraftingChainRecipeOptionIngredient
                    {
                        ItemId = ingredient.ItemId,
                        Count = ingredient.Count
                    }).ToArray()
                }).ToArray();
        ShowRecipeOptions(options, located.Position);
    }

    private void ShowRecipeOptions(CraftingChainRecipeOption[] options, Vector3 nodePosition)
    {
        ClearRecipeOptions();
        if (options == null || options.Length == 0)
        {
            SetVisible(_recipeDropdown, false);
            return;
        }
        int listHeight = Math.Min(options.Length, _optionButtons.Length) * 36 + 8;
        float x = Mathf.Clamp((nodePosition.x + 76f) * _zoom + _pan.x + 8f, 4f, 890f - 208f - 4f);
        float y = Mathf.Clamp(nodePosition.y * _zoom + _pan.y, -505f + listHeight + 4f, -4f);
        SetPosition(_recipeDropdown, new Vector3(x, y, 0f));
        _recipePreviewX = Mathf.Clamp(x + 216f + 430f <= 886f ? x + 216f : x - 438f, 4f, 456f);
        _recipePopupY = y;
        SetPosition(_recipePreview, new Vector3(_recipePreviewX, Mathf.Clamp(y, -89f, -4f), 0f));
        SetHeight(_recipeDropdown, listHeight);
        SetHeight(_recipeDropdownBackground, listHeight);
        SetHeight(_recipeDropdownFrame, listHeight);
        for (int index = 0; index < Math.Min(options.Length, _optionButtons.Length); index++)
        {
            SetVisible(_optionButtons[index], true);
            SetVisible(_optionLabels[index], true);
            SetLabel(_optionLabels[index], GetCraftingAreaName(options[index].CraftingArea));
            if (_optionButtons[index]?.ViewComponent != null) _optionButtons[index].ViewComponent.ToolTip = string.Empty;
            SetPosition(_optionButtons[index], new Vector3(7f, -7f - index * 36f, 0f));
            SetPosition(_optionLabels[index], new Vector3(18f, -10f - index * 36f, 0f));
        }
        _activeOptions = options;
        _recipePopupOpenedFrame = Time.frameCount;
        SetVisible(_recipeDropdown, true);
        NetworkLog.Debug("Crafting-chain recipe choices opened. item=" + _selectedItemId + "; choices=" + options.Length
            + "; position=" + Mathf.RoundToInt(x) + "," + Mathf.RoundToInt(y));
    }

    private void UpdateRecipePopupDismissal()
    {
        if (_recipeDropdown?.ViewComponent?.IsVisible != true || xui?.playerUI?.CursorController == null) return;
        CursorControllerAbs cursor = xui.playerUI.CursorController;
        if (Time.frameCount == _recipePopupOpenedFrame || !cursor.GetMouseButtonDown(UICamera.MouseButton.LeftButton)) return;
        if (IsPointerInside(_recipeDropdown) || IsPointerInside(_recipePreview)) return;
        SetVisible(_recipeDropdown, false);
        SetVisible(_recipePreview, false);
        _activeOptions = Array.Empty<CraftingChainRecipeOption>();
        _recipePopupDismissedFrame = Time.frameCount;
        NetworkLog.Debug("Crafting-chain recipe choices dismissed by outside click.");
    }

    private static bool IsPointerInside(XUiController controller)
    {
        GameObject hovered = UICamera.hoveredObject;
        Transform root = controller?.ViewComponent?.UiTransform;
        return hovered != null && root != null && hovered.transform.IsChildOf(root);
    }

    private void OnRecipeOptionHovered(int slot, bool isOver)
    {
        if (!isOver || slot < 0 || slot >= _activeOptions.Length)
        {
            SetVisible(_recipePreview, false);
            return;
        }
        CraftingChainRecipeOption option = _activeOptions[slot];
        SetLabel(_recipePreviewTitle, GetCraftingAreaName(option.CraftingArea));
        int previewHeight = 48 + Math.Min(option.Ingredients.Length, _previewRows.Length) * 36;
        SetPosition(_recipePreview, new Vector3(_recipePreviewX,
            Mathf.Clamp(_recipePopupY, -505f + previewHeight + 4f, -4f), 0f));
        SetHeight(_recipePreview, previewHeight);
        SetHeight(_recipePreviewBackground, previewHeight);
        SetHeight(_recipePreviewFrame, previewHeight);
        for (int index = 0; index < _previewRows.Length; index++)
        {
            bool present = index < option.Ingredients.Length;
            SetVisible(_previewRows[index], present);
            SetVisible(_previewIcons[index], present);
            SetVisible(_previewNames[index], present);
            SetVisible(_previewCounts[index], present);
            if (!present) continue;
            CraftingChainRecipeOptionIngredient ingredient = option.Ingredients[index];
            CraftingChainItemInfo item = GetItem(ingredient.ItemId);
            SetIcon(_previewIcons[index], item?.Icon, item?.Name);
            SetLabel(_previewNames[index], item?.Name ?? string.Empty);
            SetLabel(_previewCounts[index], "×" + ingredient.Count);
        }
        SetVisible(_recipePreview, true);
    }

    private static string GetCraftingAreaName(string area)
    {
        if (string.IsNullOrEmpty(area)) return Localization.Get("HyperdimensionalNetworkCraftingChainInventory");
        Block block = Block.GetBlockByName(area);
        if (block != null) return block.GetLocalizedBlockName();
        string localized = Localization.Get(area);
        return string.IsNullOrEmpty(localized) || localized == area ? area : localized;
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
        SetVisible(_recipePreview, false);
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
        GUIWindowManager manager = xui?.playerUI?.windowManager;
        if (manager != null && !manager.IsWindowOpen("hyperdimensionalCraftingChain"))
        {
            NetworkLog.Debug("Crafting-chain reopening the planning window after vanilla queue submission closed it.");
            manager.Open("hyperdimensionalCraftingChain", _bModal: true);
        }
        ShowActiveOrder();
        UpdateOrderPresentation();
        NetworkLog.Debug("Crafting-chain order accepted from its planning window. player=" + player.entityId + "; root=" + _trackedRootKey);
    }

    private int GetTrackedDefaultOutputCount()
    {
        Recipe recipe = xui?.Recipes?.TrackedRecipe;
        if (recipe == null) return 1;
        try { return Mathf.Clamp(XUiM_Recipes.GetRecipeCraftOutputCount(xui, recipe), 1, 9999); }
        catch { return Mathf.Clamp(recipe.count, 1, 9999); }
    }

    private void SetQuantityInput(int count)
    {
        if (_quantityInput != null) _quantityInput.Text = count.ToString();
    }

    private void ChangeOutputCount(int delta)
    {
        SetDesiredOutputCount(Mathf.Clamp(_desiredOutputCount + delta, 1, 9999));
    }

    private void OnQuantityChanged(XUiController sender, string text, bool changeFromCode)
    {
        if (changeFromCode) return;
        if (!int.TryParse(text, out int count) || count < 1 || count > 9999)
        {
            _quantityRefreshAt = -1f;
            CancelPendingPlan();
            _plan = null;
            _startCheckValid = false;
            SetStartEnabled(false);
            return;
        }
        SetDesiredOutputCount(count);
    }

    private void SetDesiredOutputCount(int count)
    {
        if (_desiredOutputCount == count && (_plan != null || _planTask != null || _quantityRefreshAt >= 0f))
        {
            SetQuantityInput(count);
            return;
        }
        _desiredOutputCount = count;
        SetQuantityInput(count);
        CancelPendingPlan();
        _plan = null;
        _startCheckValid = false;
        SetStartEnabled(false);
        _quantityRefreshAt = Time.realtimeSinceStartup + 0.2f;
        NetworkLog.Debug("Crafting-chain requested output count changed. count=" + count);
    }

    private void OnCancelPressed(XUiController sender, int mouseButton)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!CraftingChainOrderService.IsActive(player))
        {
            xui?.playerUI?.windowManager?.Close("hyperdimensionalCraftingChain");
            return;
        }
        bool accepted = CraftingChainOrderService.RequestCancel(player);
        NetworkLog.Debug("Crafting-chain cancel button pressed. player=" + player.entityId + "; accepted=" + accepted);
        if (accepted) UpdateOrderPresentation();
    }

    private void OnConfirmCancelPressed(XUiController sender, int mouseButton)
    {
        SetVisible(_confirmCancelPanel, false);
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!CraftingChainOrderService.RequestCancel(player))
            xui?.playerUI?.windowManager?.Close("hyperdimensionalCraftingChain");
        else
            UpdateOrderPresentation();
    }

    private void OnDismissCancelPressed(XUiController sender, int mouseButton)
    {
        SetVisible(_confirmCancelPanel, false);
    }

    private void OnClosePressed(XUiController sender, int mouseButton)
    {
        xui?.playerUI?.windowManager?.Close("hyperdimensionalCraftingChain");
    }

    private void UpdateStartAvailability()
    {
        if (_showingCompletedOrder)
        {
            SetStartEnabled(false);
            return;
        }
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
            _startAvailable = !_treeTruncated && _plan != null && _planInput != null
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
            SetVisible(_cancelButton, !cancellationPending);
            SetVisible(_cancelLabel, !cancellationPending);
            return;
        }

        SetVisible(_cancelButton, true);
        SetVisible(_cancelLabel, true);
        SetLabel(_cancelLabel, Localization.Get("xuiClose"));
        SetVisible(_startButton, !_showingCompletedOrder);
        SetVisible(_startLabel, !_showingCompletedOrder);
        SetQuantityControlsVisible(!_showingCompletedOrder);
        UpdateStartAvailability();
        if (_orderWasActive && CraftingChainOrderService.TryGetLastStatus(player, out string lastStatus))
        {
            SetLabel(_status, lastStatus);
            _orderWasActive = false;
            if (lastStatus == Localization.Get("HyperdimensionalNetworkCraftingChainCompleted"))
            {
                _showingCompletedOrder = true;
                ClearNodeSlots();
                ClearMaterialSlots();
                SetVisible(_emptyHint, true);
                SetLabel(_emptyHint, lastStatus);
                SetLabel(_materialsTitle, Localization.Get("HyperdimensionalNetworkCraftingChainTotalElapsed"));
                if (CraftingChainOrderService.TryGetLastElapsed(player, out float seconds))
                {
                    SetLabel(_totalElapsed, FormatElapsed(seconds));
                    SetVisible(_totalElapsed, true);
                }
                SetVisible(_startButton, false);
                SetVisible(_startLabel, false);
            }
            else RefreshTrackedRecipe("order-ended");
        }
    }

    private void UpdateProgressVisuals()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!CraftingChainOrderService.TryGetProgress(player, out float elapsed,
            out List<CraftingChainOrderService.StageProgress> stages)) return;
        int completed = stages.Count(stage => stage.Completed);
        if (completed != _visibleCompletedStages)
        {
            _completedNodes.Clear();
            foreach (CraftingChainOrderService.StageProgress stage in stages.Where(stage => stage.Completed))
                _completedNodes.Add(stage.Node);
            _visibleCompletedStages = completed;
            if (_plan != null) RenderPlan();
            NetworkLog.Debug("Crafting-chain progress tree refreshed. player=" + player.entityId
                + "; completed=" + completed + "/" + stages.Count);
        }
        if (CraftingChainOrderService.TryGetStatus(player, out string runningStatus, out _, out _, out _))
            SetLabel(_status, runningStatus);
        SetLabel(_totalElapsed, FormatElapsed(elapsed));
        SetVisible(_totalElapsed, true);
        for (int index = 0; index < _locatedNodes.Count; index++)
        {
            LocatedPlanNode located = _locatedNodes[index];
            CraftingChainOrderService.StageProgress stage = stages.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Node, located.RecipeNode));
            bool active = stage?.Active == true;
            bool warning = active && !string.IsNullOrEmpty(stage.WarningKey);
            Color frameColor = warning ? new Color32(255, 198, 48, 255)
                : active ? new Color32(82, 222, 245, 255) : new Color32(99, 105, 113, 255);
            Color iconColor = warning ? new Color32(255, 236, 145, 255)
                : active ? Color.white : new Color32(145, 150, 155, 255);
            SetSpriteColor(_nodeFrames[index], frameColor);
            SetButtonColor(_nodeButtons[index], iconColor);
            if (active && _nodeButtons[index]?.ViewComponent != null)
            {
                string tooltip = (GetItem(located.ItemId)?.Name ?? string.Empty) + "\n"
                    + string.Format(Localization.Get("HyperdimensionalNetworkCraftingChainNodeElapsed"),
                        FormatElapsed(Math.Max(0f, Time.realtimeSinceStartup - stage.StartedAt)));
                if (warning) tooltip += "\n" + Localization.Get(stage.WarningKey);
                if (_nodeButtons[index].ViewComponent.ToolTip != tooltip)
                    _nodeButtons[index].ViewComponent.ToolTip = tooltip;
            }
        }
    }

    private static string FormatElapsed(float seconds)
    {
        int total = Math.Max(0, Mathf.FloorToInt(seconds));
        return string.Format("{0:00}:{1:00}:{2:00}", total / 3600, total / 60 % 60, total % 60);
    }

    private void SetQuantityControlsVisible(bool visible)
    {
        foreach (XUiController control in _quantityControls ?? Array.Empty<XUiController>())
            SetVisible(control, visible);
    }

    private static void SetSpriteColor(XUiController controller, Color color)
    {
        if (controller?.ViewComponent is XUiV_Sprite sprite && sprite.Color != color) sprite.Color = color;
    }

    private static void SetButtonColor(XUiController controller, Color color)
    {
        if (controller?.ViewComponent is XUiV_Button button && button.DefaultSpriteColor != color)
        {
            button.DefaultSpriteColor = color;
            button.HoverSpriteColor = color;
        }
    }

    private void CloseWindow()
    {
        xui?.playerUI?.windowManager?.Close("hyperdimensionalCraftingChain");
    }

    private void OnTreeScroll(XUiController sender, float delta)
    {
        if (!IsPointerInside(_treeViewport) || IsPointerInside(_recipeDropdown)
            || IsPointerInside(_recipePreview) || delta == 0f) return;
        _zoom = Mathf.Clamp(_zoom + Mathf.Sign(delta) * 0.12f, 0.55f, 1.8f);
        ApplyCanvasTransform();
        NetworkLog.Debug("Crafting-chain tree zoom changed. zoom=" + _zoom.ToString("F2"));
    }

    private void UpdatePanning()
    {
        if (xui?.playerUI?.CursorController == null || !IsPointerInside(_treeViewport)
            || IsPointerInside(_recipeDropdown) || IsPointerInside(_recipePreview)
            || Time.frameCount == _recipePopupDismissedFrame)
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
            if (delta.sqrMagnitude < 0.01f) return;
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
            SetVisible(_nodeFrames[index], false);
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
        SetVisible(_recipePreview, false);
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
            string value = text ?? string.Empty;
            if (label.Text == value) return;
            label.Text = value;
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
        if (controller?.ViewComponent != null)
            controller.ViewComponent.Position = new Vector2i(Mathf.RoundToInt(position.x), Mathf.RoundToInt(position.y));
    }

    private static void SetHeight(XUiController controller, int height)
    {
        if (controller?.ViewComponent != null) controller.ViewComponent.Height = height;
    }

    private static void SetVisible(XUiController controller, bool visible)
    {
        if (controller?.ViewComponent != null && controller.ViewComponent.IsVisible != visible)
            controller.ViewComponent.IsVisible = visible;
    }

    private void SetStartEnabled(bool enabled)
    {
        if (_startButton?.ViewComponent != null && _startButton.ViewComponent.Enabled != enabled)
            _startButton.ViewComponent.Enabled = enabled;
    }

    private void BindPress(string id, XUiEvent_OnPressEventHandler handler)
    {
        XUiController control = GetChildById(id);
        if (control != null) control.OnPress += handler;
        else NetworkLog.Error("Crafting-chain button was not found. id=" + id);
    }
}
