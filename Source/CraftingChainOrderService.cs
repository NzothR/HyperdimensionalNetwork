using System;
using System.Collections.Generic;
using System.Linq;

namespace HyperdimensionalNetwork;

/// <summary>
/// Runs one vanilla crafting-queue step at a time. Materials are consumed through the same
/// inventory/workstation entry points used by ordinary crafting; outputs always come from
/// the vanilla queue or workstation output grid.
/// </summary>
internal static class CraftingChainOrderService
{
    internal sealed class StageProgress
    {
        public CraftingChainPlanNode Node;
        public bool Active;
        public bool Completed;
        public float StartedAt;
        public string WarningKey = string.Empty;
        public string ErrorKey = string.Empty;
    }

    private sealed class WorkstationTask
    {
        public TileEntityWorkstation Workstation;
        public int QueueIndex = -1;
        public int Batches;
        public List<ItemStack> OutputBaseline = new List<ItemStack>();
        public int CollectedOutput;
        public bool QueueSubmitted;
    }

    private sealed class Stage
    {
        public CraftingChainPlanNode Node;
        public Recipe SourceRecipe;
        public Recipe QueueRecipe;
        public int OutputItemType;
        public int OutputQuality;
        public int ExpectedOutput;
        public XUiC_CraftingWindowGroup Group;
        public XUiC_WorkstationWindowGroup WorkstationGroup;
        public XUiC_CraftingQueue Queue;
        public XUiC_RecipeStack QueueStack;
        public TileEntityWorkstation Workstation;
        public int WorkstationQueueIndex = -1;
        public List<ItemStack> WorkstationOutputBaseline = new List<ItemStack>();
        public readonly List<WorkstationTask> WorkstationTasks = new List<WorkstationTask>();
        public bool QueueSubmitted;
        public bool IsActive;
        public bool IsCompleted;
        public float StartedAt;
        public XUiC_WorkstationOutputGrid OutputGrid;
        public bool UsesForgeMaterials;
        public List<ItemStack> ReservedInputs = new List<ItemStack>();
        public List<ItemStack> ReservedNetworkInputs = new List<ItemStack>();
        public int CollectedOutput;
        public long NetworkBaseline;
        public float NextNetworkCheck;
        public string ErrorKey = string.Empty;
    }

    private sealed class Order
    {
        public XUi xui;
        public EntityPlayerLocal player;
        public string RootRecipeKey;
        public string TrackedTargetKey;
        public string NetworkId;
        public ItemValue RootOutputValue;
        public int DeliverableRootOutput;
        public int NetworkRootOutput;
        public CraftingChainPlanInput PlanInput;
        public CraftingChainPlan Plan;
        public float StartedAt;
        public readonly List<Stage> Stages = new List<Stage>();
        public readonly List<ItemStack> EscrowItems = new List<ItemStack>();
        // A subset of EscrowItems that was actually removed from the shared network.
        // Only this subset may be returned through the server's original authorization.
        public readonly List<ItemStack> NetworkEscrowItems = new List<ItemStack>();
        public long ReservationRequestId;
        public int StageIndex;
        public bool PendingReservation;
        public List<ItemStack> ReservationRequirements = new List<ItemStack>();
        public bool ReturnPending;
        public bool ReturnInFlight;
        public float NextReturnAttempt;
        public long NetworkReturnRequestId;
        public List<ItemStack> NetworkReturnChunkItems;
        public float NextOutputAttempt;
        public bool CancelRequested;
        public string Status = string.Empty;
        public string TerminalStatus = string.Empty;
        public XUiEvent_TrackedQuestChanged TrackingChangedHandler;
    }

    private static readonly Dictionary<int, Order> Orders = new Dictionary<int, Order>();
    private static readonly Dictionary<int, int> LastPumpFrameByPlayer = new Dictionary<int, int>();
    private static readonly Dictionary<int, string> LastStatuses = new Dictionary<int, string>();
    private static readonly Dictionary<int, float> LastElapsedSeconds = new Dictionary<int, float>();

    public static bool CanStart(XUi xui, EntityPlayerLocal player, CraftingChainPlanInput input, CraftingChainPlan plan, out string error)
    {
        error = string.Empty;
        if (!(bool)player || xui == null || input == null || plan == null || !string.IsNullOrEmpty(plan.Error))
        {
            error = plan?.Error == "PlanTooLarge"
                ? "HyperdimensionalNetworkCraftingChainPlanTooLarge"
                : "HyperdimensionalNetworkCraftingChainPlanFailed";
            return false;
        }
        if (plan.MissingMaterials > 0)
        {
            error = "HyperdimensionalNetworkCraftingChainMissingMaterials";
            return false;
        }
        if (Orders.ContainsKey(player.entityId))
        {
            error = "HyperdimensionalNetworkCraftingChainAlreadyRunning";
            return false;
        }
        if (NetworkRemoteClient.IsMutationPending)
        {
            error = "HyperdimensionalNetworkCraftingChainNetworkBusy";
            return false;
        }
        List<ItemStack> reservedRequirements = BuildOrderRequirements(input, plan);
        if (reservedRequirements == null)
        {
            error = "HyperdimensionalNetworkCraftingChainPlanTooLarge";
            return false;
        }
        if (plan.AvailableOutput > 0 && NetworkStorage.GetMatchingCount(player,
            GetItemValue(input, input.RootOutputItemId)) < plan.AvailableOutput)
        {
            error = "HyperdimensionalNetworkCraftingChainMissingMaterials";
            NetworkLog.Debug("Crafting-chain network target stock changed before reservation. player=" + player.entityId
                + "; planned=" + plan.AvailableOutput);
            return false;
        }
        foreach (ItemStack material in reservedRequirements)
        {
            long available = ActionResourceService.Count(player, material.itemValue);
            if (available < material.count)
            {
                error = "HyperdimensionalNetworkCraftingChainMissingMaterials";
                NetworkLog.Debug("Crafting-chain start revalidation found a changed inventory. player=" + player.entityId
                    + "; item=" + material.itemValue.type + "; planned=" + material.count + "; current=" + available);
                return false;
            }
        }

        foreach (CraftingChainPlanMaterial material in plan.Materials.Where(material => material.ForgeOnly))
        {
            if (material.Required <= 0 || material.ItemId < 0 || material.ItemId >= input.Items.Count) continue;
            CraftingChainItemInfo item = input.Items[material.ItemId];
            ItemValue requirement = GetItemValue(input, material.ItemId);
            long available = CountAccessibleForgeInputs(input, requirement);
            if (available < material.Required)
            {
                error = "HyperdimensionalNetworkCraftingChainMissingMaterials";
                NetworkLog.Debug("Crafting-chain start revalidation found changed forge contents. player=" + player.entityId
                    + "; item=" + item.Type + "; planned=" + material.Required + "; current=" + available);
                return false;
            }
        }

        List<CraftingChainPlanNode> nodes = FlattenPostOrder(plan.Nodes);
        List<Stage> preparedStages = new List<Stage>();
        foreach (CraftingChainPlanNode node in nodes)
        {
            if (!TryPrepareStage(xui, player, input, node, out Stage stage, out error)) return false;
            if (stage.Workstation == null && !QueueIsIdle(stage.Queue))
            {
                error = "HyperdimensionalNetworkCraftingChainQueueBusy";
                return false;
            }
            if (stage.Workstation != null && stage.WorkstationTasks.Count == 0)
            {
                error = "HyperdimensionalNetworkCraftingChainWorkstationUnavailable";
                return false;
            }
            preparedStages.Add(stage);
        }
        return true;
    }

    public static bool Start(XUi xui, EntityPlayerLocal player, CraftingChainPlanInput input, CraftingChainPlan plan, out string error)
    {
        if (!CanStart(xui, player, input, plan, out error)) return false;
        List<CraftingChainPlanNode> nodes = FlattenPostOrder(plan.Nodes);
        List<ItemStack> requirements = BuildOrderRequirements(input, plan);
        if (requirements == null)
        {
            error = "HyperdimensionalNetworkCraftingChainPlanTooLarge";
            return false;
        }
        Order order = new Order
        {
            xui = xui,
            player = player,
            RootRecipeKey = plan.RootRecipeKey,
            TrackedTargetKey = GetTrackedRecipeKey(xui),
            NetworkId = NetworkRemoteClient.IsRemoteClient
                ? NetworkRemoteClient.GetCurrentNetworkId(player)
                : PlayerNetworkDirectory.GetNetworkId(player) ?? string.Empty,
            RootOutputValue = BuildOutputValue(input, input.RootOutputItemId),
            DeliverableRootOutput = plan.PlannedOutput,
            NetworkRootOutput = plan.AvailableOutput,
            PlanInput = input,
            Plan = plan,
            StartedAt = UnityEngine.Time.realtimeSinceStartup,
            Status = Localization.Get("HyperdimensionalNetworkCraftingChainStarting")
        };
        foreach (CraftingChainPlanNode node in nodes)
        {
            if (!TryPrepareStage(xui, player, input, node, out Stage stage, out error)) return false;
            order.Stages.Add(stage);
        }

        LastStatuses.Remove(player.entityId);
        LastElapsedSeconds.Remove(player.entityId);
        Orders[player.entityId] = order;
        AttachTrackingHandler(order);
        NetworkLog.Debug("Crafting-chain order created. player=" + player.entityId + "; root=" + order.RootRecipeKey
            + "; steps=" + order.Stages.Count + "; reservedVariants=" + requirements.Count + "; trackedOutput=" + plan.PlannedOutput);
        order.ReservationRequirements = requirements.Select(item => item.Clone()).ToList();
        BeginOrderStart(order);
        error = string.Empty;
        return true;
    }

    private static void BeginOrderStart(Order order)
    {
        if (order == null) return;
        NetworkLog.Debug("Crafting-chain starts without reserving workstation interaction. player=" + order.player.entityId);
        BeginOrderReservation(order);
    }

    private static void BeginOrderReservation(Order order)
    {
        if (order == null || !Orders.ContainsKey(order.player.entityId)) return;
        IList<ItemStack> requirements = order.ReservationRequirements;
        if (requirements == null || requirements.Count == 0)
        {
            BeginReservedOrder(order, requirements, 0, order.NetworkId);
            return;
        }
        if (NetworkRemoteClient.IsRemoteClient)
        {
            order.PendingReservation = true;
            order.Status = Localization.Get("HyperdimensionalNetworkCraftingChainReserving");
            bool sent = NetworkRemoteClient.RequestConsumption(order.player, requirements,
                NetworkConsumptionSource.NetworkPreferredPlayerInventory, Vector3i.zero,
                (NetworkRemoteResult result) => BeginReservedOrder(order, requirements, result.RequestId, result.NetworkId),
                failure =>
                {
                    order.PendingReservation = false;
                    FailBeforeReservation(order, failure == "InsufficientMaterials"
                        ? "HyperdimensionalNetworkCraftingChainMissingMaterials" : "HyperdimensionalNetworkCraftingChainPlanFailed",
                        "initial-network-reservation-rejected-" + failure);
                }, order.RootOutputValue, order.NetworkRootOutput);
            if (!sent)
                FailBeforeReservation(order, "HyperdimensionalNetworkCraftingChainNetworkBusy", "initial-network-reservation-send-failed");
            return;
        }
        if (!BeginReservedOrder(order, requirements, 0, order.NetworkId))
            NetworkLog.Error("Crafting-chain local order could not complete its initial reservation. player=" + order.player.entityId);
    }

    public static bool IsActive(EntityPlayer player)
        => (bool)player && Orders.ContainsKey(player.entityId);

    public static bool TryGetStatus(EntityPlayer player, out string status, out int currentStep, out int totalSteps, out bool cancellationPending)
    {
        status = string.Empty;
        currentStep = 0;
        totalSteps = 0;
        cancellationPending = false;
        if (!(bool)player || !Orders.TryGetValue(player.entityId, out Order order)) return false;
        status = order.Status;
        currentStep = Math.Min(order.StageIndex + (order.StageIndex < order.Stages.Count ? 1 : 0), order.Stages.Count);
        totalSteps = order.Stages.Count;
        cancellationPending = order.CancelRequested || order.ReturnPending || order.ReturnInFlight;
        return true;
    }

    public static bool TryGetLastStatus(EntityPlayer player, out string status)
    {
        status = string.Empty;
        return (bool)player && LastStatuses.TryGetValue(player.entityId, out status);
    }

    public static bool TryGetLastElapsed(EntityPlayer player, out float seconds)
    {
        seconds = 0f;
        return (bool)player && LastElapsedSeconds.TryGetValue(player.entityId, out seconds);
    }

    public static bool TryGetActiveView(EntityPlayer player, out CraftingChainPlanInput input, out CraftingChainPlan plan)
    {
        input = null;
        plan = null;
        if (!(bool)player || !Orders.TryGetValue(player.entityId, out Order order)) return false;
        input = order.PlanInput;
        plan = order.Plan;
        return input != null && plan != null;
    }

    public static bool TryGetProgress(EntityPlayer player, out float elapsed, out List<StageProgress> stages)
    {
        elapsed = 0f;
        stages = null;
        if (!(bool)player || !Orders.TryGetValue(player.entityId, out Order order)) return false;
        elapsed = Math.Max(0f, UnityEngine.Time.realtimeSinceStartup - order.StartedAt);
        stages = new List<StageProgress>(order.Stages.Count);
        foreach (Stage stage in order.Stages)
        {
            StageProgress progress = new StageProgress
            {
                Node = stage.Node,
                Active = stage.IsActive,
                Completed = stage.IsCompleted,
                StartedAt = stage.StartedAt,
                ErrorKey = stage.ErrorKey
            };
            if (stage.IsActive)
                foreach (WorkstationTask task in stage.WorkstationTasks)
                {
                    TileEntityWorkstation station = task.Workstation;
                    if (station == null || !CraftingChainWorkstationService.RequiresFuel(station) || station.IsBurning) continue;
                    progress.WarningKey = new XUiM_Workstation(station).GetTotalBurnTimeLeft() <= 0f
                        ? "HyperdimensionalNetworkCraftingChainFuelInsufficient"
                        : "HyperdimensionalNetworkCraftingChainWorkstationStopped";
                    break;
                }
            stages.Add(progress);
        }
        return true;
    }

    public static void ClearLastStatus(EntityPlayer player)
    {
        if ((bool)player) LastStatuses.Remove(player.entityId);
    }

    public static bool RequestCancel(EntityPlayer player)
    {
        if (!(bool)player || !Orders.TryGetValue(player.entityId, out Order order)) return false;
        if (order.CancelRequested || order.ReturnPending || order.ReturnInFlight) return false;
        order.CancelRequested = true;
        order.Status = Localization.Get("HyperdimensionalNetworkCraftingChainCancelling");
        if (order.PendingReservation) return true;
        CancelOrder(order, "user-cancel");
        return true;
    }

    public static void CancelIfTrackingChanged(EntityPlayer player, string currentRootRecipeKey)
    {
        if (!(bool)player || !Orders.TryGetValue(player.entityId, out Order order)) return;
        if (order.ReturnPending || order.ReturnInFlight) return;
        if (string.IsNullOrEmpty(currentRootRecipeKey) || !string.Equals(order.TrackedTargetKey, currentRootRecipeKey, StringComparison.Ordinal))
        {
            order.CancelRequested = true;
            order.Status = Localization.Get("HyperdimensionalNetworkCraftingChainCancelling");
            if (!order.PendingReservation) CancelOrder(order, "tracking-changed");
        }
    }

    public static void Update(XUi xui)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player || !Orders.TryGetValue(player.entityId, out Order order)) return;
        if (LastPumpFrameByPlayer.TryGetValue(player.entityId, out int lastFrame) && lastFrame == UnityEngine.Time.frameCount) return;
        LastPumpFrameByPlayer[player.entityId] = UnityEngine.Time.frameCount;
        if (order.CancelRequested)
        {
            if (!order.PendingReservation) CancelOrder(order, "cancel-requested");
            return;
        }
        if (order.PendingReservation) return;
        if (order.ReturnPending)
        {
            if (UnityEngine.Time.realtimeSinceStartup >= order.NextReturnAttempt) TryReturnEscrow(order);
            return;
        }
        foreach (Stage stage in order.Stages.Where(candidate => candidate.IsActive).ToList())
        {
            if (!UpdateActiveStage(order, stage))
            {
                if (!Orders.ContainsKey(player.entityId)) return;
                continue;
            }
            if (!Orders.ContainsKey(player.entityId)) return;
        }
        if (order.Stages.All(stage => stage.IsCompleted))
        {
            CompleteOrder(order);
            return;
        }
        StartNextStage(order);
    }

    private static bool UpdateActiveStage(Order order, Stage stage)
    {
        TryCollectNetworkOutput(order, stage);
        if (stage.CollectedOutput >= stage.ExpectedOutput)
        {
            StopCurrentStage(order, "network-output-satisfied", stage);
            CompleteStage(order, stage);
            return true;
        }
        if (!string.IsNullOrEmpty(stage.ErrorKey)) return false;
        if (stage.WorkstationTasks.Count > 0)
        {
            bool pendingWorkstationTask = false;
            int expectedCollectedOutput = 0;
            foreach (WorkstationTask workstationTask in stage.WorkstationTasks)
            {
                if (!workstationTask.QueueSubmitted) continue;
                TileEntityWorkstation live = GameManager.Instance?.World?.GetTileEntity(workstationTask.Workstation.ToWorldPos()) as TileEntityWorkstation;
                if (!ReferenceEquals(live, workstationTask.Workstation))
                {
                    SetStageError(order, stage, "registered-workstation-unloaded-or-replaced");
                    return false;
                }
                RecipeQueueItem[] workstationQueue = workstationTask.Workstation.Queue;
                RecipeQueueItem task = workstationTask.QueueIndex >= 0 && workstationTask.QueueIndex < (workstationQueue?.Length ?? 0)
                    ? workstationQueue[workstationTask.QueueIndex] : null;
                int completedBatches;
                if (task?.Recipe != null)
                {
                    if (!ReferenceEquals(task.Recipe, stage.QueueRecipe) || task.StartingEntityId != order.player.entityId)
                    {
                        SetStageError(order, stage, "registered-workstation-task-was-replaced");
                        return false;
                    }
                    completedBatches = Math.Max(0, workstationTask.Batches - Math.Max(0, (int)task.Multiplier));
                    pendingWorkstationTask |= task.Multiplier > 0;
                }
                else completedBatches = workstationTask.Batches;
                int completedOutput = (int)Math.Min(int.MaxValue, (long)completedBatches * stage.QueueRecipe.count);
                expectedCollectedOutput += completedOutput;
                if (completedOutput > workstationTask.CollectedOutput
                    && !CollectWorkstationTaskOutput(order, stage, workstationTask, completedOutput))
                {
                    SetStageError(order, stage,
                        "completed-workstation-output-could-not-be-collected-at-" + workstationTask.Workstation.ToWorldPos());
                    return false;
                }
            }
            if (pendingWorkstationTask) return false;
            if (expectedCollectedOutput != stage.ExpectedOutput || stage.CollectedOutput != stage.ExpectedOutput)
            {
                SetStageError(order, stage, "completed-workstation-output-count-mismatch");
                return false;
            }
        }
        else if (stage.QueueStack == null)
        {
            SetStageError(order, stage, "registered-vanilla-queue-entry-disappeared");
            return false;
        }
        else if (stage.QueueStack.HasRecipe())
        {
            if (!ReferenceEquals(stage.QueueStack.GetRecipe(), stage.QueueRecipe))
            {
                SetStageError(order, stage, "registered-vanilla-queue-entry-was-replaced");
                return false;
            }
            int completedBatches = Math.Max(0, stage.Node.Batches - Math.Max(0, stage.QueueStack.GetRecipeCount()));
            int completedOutput = (int)Math.Min(int.MaxValue, (long)completedBatches * stage.QueueRecipe.count);
            if (completedOutput > stage.CollectedOutput && !CollectOutputsToEscrow(order, stage, completedOutput))
            {
                SetStageError(order, stage, "completed-player-queue-output-could-not-be-collected");
                return false;
            }
            return false;
        }
        if (!CollectOutputsToEscrow(order, stage, stage.ExpectedOutput))
        {
            SetStageError(order, stage, "completed-stage-output-could-not-be-collected");
            return false;
        }
        CompleteStage(order, stage);
        return true;
    }

    private static void CompleteStage(Order order, Stage stage)
    {
        stage.ReservedInputs.Clear();
        stage.ReservedNetworkInputs.Clear();
        stage.ErrorKey = string.Empty;
        stage.IsActive = false;
        stage.IsCompleted = true;
        order.StageIndex++;
        order.Status = string.Format(Localization.Get("HyperdimensionalNetworkCraftingChainStepProgress"),
            Math.Min(order.StageIndex + 1, order.Stages.Count), order.Stages.Count, GetItemName(stage));
        NetworkLog.Debug("Crafting-chain stage completed. player=" + order.player.entityId + "; completedSteps=" + order.StageIndex
            + "; recipe=" + stage.SourceRecipe.GetName() + "; output=" + stage.ExpectedOutput);
    }

    private static void SetStageError(Order order, Stage stage, string reason)
    {
        if (!string.IsNullOrEmpty(stage.ErrorKey)) return;
        stage.ErrorKey = "HyperdimensionalNetworkCraftingChainNodeError";
        order.Status = Localization.Get(stage.ErrorKey);
        NetworkLog.Error("Crafting-chain stage needs network output to resume. player=" + order.player.entityId
            + "; recipe=" + stage.SourceRecipe.GetName() + "; missing=" + (stage.ExpectedOutput - stage.CollectedOutput)
            + "; reason=" + reason);
    }

    private static List<ItemStack> BuildOrderRequirements(CraftingChainPlanInput input, CraftingChainPlan plan)
    {
        Dictionary<int, long> requiredByItem = new Dictionary<int, long>();
        foreach (CraftingChainPlanMaterial material in plan.Materials.Where(material => !material.ForgeOnly))
        {
            if (material.Required <= 0 || material.ItemId < 0 || material.ItemId >= input.Items.Count) continue;
            requiredByItem[material.ItemId] = Math.Max(requiredByItem.TryGetValue(material.ItemId, out long previous) ? previous : 0L,
                material.Required);
        }
        // The plan consumes stocked intermediate items and existing target output before it
        // creates missing dependencies. Reserve those exact supplies as well as base inputs;
        // otherwise a ready plan can reach a stage whose already-counted material was never
        // moved into the order's private escrow.
        foreach (CraftingChainPlanMaterial supply in plan.ReservedSupplies.Where(material => !material.ForgeOnly))
        {
            if (supply.Required <= 0 || supply.ItemId < 0 || supply.ItemId >= input.Items.Count) continue;
            requiredByItem[supply.ItemId] = Math.Max(requiredByItem.TryGetValue(supply.ItemId, out long previous) ? previous : 0L,
                supply.Required);
        }

        List<ItemStack> requirements = new List<ItemStack>();
        foreach (KeyValuePair<int, long> requirement in requiredByItem.OrderByDescending(entry =>
            entry.Key == input.RootOutputItemId ? 1 : 0))
        {
            if (requirement.Value > int.MaxValue) return null;
            CraftingChainItemInfo item = input.Items[requirement.Key];
            ItemValue value = GetItemValue(input, requirement.Key);
            AddRequirement(requirements, value, (int)requirement.Value);
        }
        return requirements;
    }

    private static void AddRequirement(List<ItemStack> requirements, ItemValue value, int count)
    {
        if (requirements == null || value == null || count <= 0) return;
        ItemValue key = NetworkStorage.NormalizeKey(value);
        ItemStack existing = requirements.FirstOrDefault(item => NetworkStorage.NormalizeKey(item.itemValue).Equals(key));
        if (existing == null) requirements.Add(new ItemStack(value.Clone(), count));
        else existing.count = (int)Math.Min(int.MaxValue, (long)existing.count + count);
    }

    private static bool BeginReservedOrder(Order order, IList<ItemStack> requirements, long reservationRequestId, string reservationNetworkId)
    {
        if (order == null || !Orders.ContainsKey(order.player.entityId)) return false;
        if (!NetworkRemoteClient.IsRemoteClient && order.NetworkRootOutput > 0
            && NetworkStorage.GetMatchingCount(order.player, order.RootOutputValue) < order.NetworkRootOutput)
        {
            FailBeforeReservation(order, "HyperdimensionalNetworkCraftingChainMissingMaterials",
                "target-network-stock-changed-before-local-reservation");
            return false;
        }
        order.PendingReservation = false;
        order.ReservationRequestId = reservationRequestId;
        if (!string.IsNullOrEmpty(reservationNetworkId)) order.NetworkId = reservationNetworkId;
        List<ItemStack> removed = new List<ItemStack>();
        List<ItemStack> networkRemoved = new List<ItemStack>();
        if (requirements != null && requirements.Count > 0)
        {
            if (NetworkRemoteClient.IsRemoteClient)
            {
                using (NetworkRemoteClient.CaptureCraftingChainNetworkConsumption(order.player, networkRemoved))
                using (Patches.PlayerInventoryConsumePatch.PreferNetworkForChainReservation())
                    order.xui.PlayerInventory.RemoveItems(requirements, 1, removed);
            }
            else
            {
                using (NetworkRemoteClient.CaptureCraftingChainNetworkConsumption(order.player, networkRemoved))
                using (Patches.PlayerInventoryConsumePatch.PreferNetworkForChainReservation())
                    order.xui.PlayerInventory.RemoveItems(requirements, 1, removed);
            }
        }
        bool physicalAlreadyInNetwork = false;
        if (!NetworkRemoteClient.IsRemoteClient
            && !TransitInitialPhysicalInputs(order, removed, networkRemoved, out physicalAlreadyInNetwork))
        {
            AddEscrowItems(order, physicalAlreadyInNetwork ? networkRemoved : removed);
            AddNetworkEscrowItems(order, networkRemoved);
            order.TerminalStatus = Localization.Get("HyperdimensionalNetworkCraftingChainReturnFailed");
            TryReturnEscrow(order);
            return false;
        }
        AddEscrowItems(order, removed);
        AddNetworkEscrowItems(order, networkRemoved);
        if (!RequirementsCovered(requirements, removed))
        {
            NetworkLog.Error("Crafting-chain input reservation could not be reconciled with the plan. player=" + order.player.entityId
                + "; plannedVariants=" + (requirements?.Count ?? 0) + "; removedVariants=" + removed.Count);
            order.TerminalStatus = Localization.Get("HyperdimensionalNetworkCraftingChainMissingMaterials");
            TryReturnEscrow(order);
            return false;
        }
        if (order.CancelRequested)
        {
            CancelOrder(order, "cancel-after-initial-reservation");
            return true;
        }
        if (order.Stages.Count == 0) CompleteOrder(order);
        else StartNextStage(order);
        return true;
    }

    private static bool RequirementsCovered(IEnumerable<ItemStack> requirements, IEnumerable<ItemStack> removedItems)
    {
        List<StoredEntry> available = (removedItems ?? Enumerable.Empty<ItemStack>())
            .Where(item => item != null && !item.IsEmpty())
            .Select(item => new StoredEntry { Value = item.itemValue.Clone(), Count = item.count }).ToList();
        foreach (ItemStack requirement in requirements ?? Enumerable.Empty<ItemStack>())
        {
            if (requirement == null || requirement.IsEmpty()) continue;
            long remaining = requirement.count;
            foreach (StoredEntry item in available)
            {
                if (remaining <= 0) break;
                if (item.Count <= 0 || !NetworkItemMatcher.Matches(item.Value, requirement.itemValue)) continue;
                long take = Math.Min(remaining, item.Count);
                item.Count -= take;
                remaining -= take;
            }
            if (remaining > 0) return false;
        }
        return true;
    }

    private static bool TransitInitialPhysicalInputs(Order order, IEnumerable<ItemStack> removed,
        IEnumerable<ItemStack> networkRemoved, out bool physicalAlreadyInNetwork)
    {
        physicalAlreadyInNetwork = false;
        List<ItemStack> physical = removed?.Where(item => item != null && !item.IsEmpty())
            .Select(item => item.Clone()).ToList() ?? new List<ItemStack>();
        foreach (ItemStack network in networkRemoved ?? Enumerable.Empty<ItemStack>())
            TakeExactFromList(physical, network.itemValue, network.count, null);
        if (physical.Count == 0) return true;
        List<StorageAddition> batch = physical.Select(item => new StorageAddition
            { Value = item.itemValue.Clone(), Count = item.count }).ToList();
        if (!NetworkStorage.AddBatch(order.player, batch)) return false;
        if (!NetworkStorage.RemoveBatch(order.player, batch))
        {
            physicalAlreadyInNetwork = true;
            NetworkLog.Error("Crafting-chain physical reservation entered network but could not enter escrow. player="
                + order.player.entityId + "; variants=" + batch.Count);
            return false;
        }
        NetworkLog.Debug("Crafting-chain physical inputs passed through network before escrow. player="
            + order.player.entityId + "; variants=" + batch.Count);
        return true;
    }

    private static void AddEscrowItem(Order order, ItemStack item)
    {
        if (order == null || item == null || item.IsEmpty()) return;
        ItemValue key = NetworkStorage.NormalizeKey(item.itemValue);
        ItemStack existing = order.EscrowItems.FirstOrDefault(stack => NetworkStorage.NormalizeKey(stack.itemValue).Equals(key));
        if (existing == null) order.EscrowItems.Add(new ItemStack(key, item.count));
        else existing.count = NetworkItemMatcher.AddClamped(existing.count, item.count);
    }

    private static void AddEscrowItems(Order order, IEnumerable<ItemStack> items)
    {
        if (items == null) return;
        foreach (ItemStack item in items) AddEscrowItem(order, item);
    }

    private static void AddNetworkEscrowItems(Order order, IEnumerable<ItemStack> items)
    {
        if (order == null || items == null) return;
        foreach (ItemStack item in items)
        {
            if (item == null || item.IsEmpty()) continue;
            ItemValue key = NetworkStorage.NormalizeKey(item.itemValue);
            ItemStack existing = order.NetworkEscrowItems.FirstOrDefault(stack =>
                NetworkStorage.NormalizeKey(stack.itemValue).Equals(key));
            if (existing == null) order.NetworkEscrowItems.Add(new ItemStack(key, item.count));
            else existing.count = NetworkItemMatcher.AddClamped(existing.count, item.count);
        }
    }

    private static bool TakeEscrowInputs(Order order, Stage stage, out List<ItemStack> taken, out List<ItemStack> takenNetwork)
    {
        taken = new List<ItemStack>();
        takenNetwork = new List<ItemStack>();
        if (stage.UsesForgeMaterials) return true;
        if (!TryBuildStageRequirements(stage, out List<ItemStack> requirements)) return false;
        foreach (ItemStack requirement in requirements)
        {
            long remaining = requirement.count;
            foreach (ItemStack candidate in order.EscrowItems.Where(stack => stack != null && !stack.IsEmpty()
                && NetworkItemMatcher.Matches(stack.itemValue, requirement.itemValue)).ToList())
            {
                if (remaining <= 0) break;
                int networkCount = GetExactCount(order.NetworkEscrowItems, candidate.itemValue);
                int physicalCount = Math.Max(0, candidate.count - networkCount);
                int take = (int)Math.Min(remaining, physicalCount);
                if (take <= 0) continue;
                TakeExactFromList(order.EscrowItems, candidate.itemValue, take, taken);
                remaining -= take;
            }
            foreach (ItemStack candidate in order.NetworkEscrowItems.Where(stack => stack != null && !stack.IsEmpty()
                && NetworkItemMatcher.Matches(stack.itemValue, requirement.itemValue)).ToList())
            {
                if (remaining <= 0) break;
                int take = (int)Math.Min(remaining, candidate.count);
                int removedNetwork = TakeExactFromList(order.NetworkEscrowItems, candidate.itemValue, take, takenNetwork);
                int removedTotal = TakeExactFromList(order.EscrowItems, candidate.itemValue, removedNetwork, taken);
                remaining -= Math.Min(removedNetwork, removedTotal);
            }
            if (remaining > 0)
            {
                AddEscrowItems(order, taken);
                AddNetworkEscrowItems(order, takenNetwork);
                taken.Clear();
                takenNetwork.Clear();
                NetworkLog.Error("Crafting-chain private escrow did not contain the next stage inputs. player=" + order.player.entityId
                    + "; recipe=" + stage.SourceRecipe.GetName() + "; item=" + requirement.itemValue.type + "; missing=" + remaining);
                return false;
            }
        }
        return true;
    }

    private static ItemStack FindExactStack(IEnumerable<ItemStack> items, ItemValue value)
    {
        if (items == null || value == null) return null;
        ItemValue key = NetworkStorage.NormalizeKey(value);
        return items.FirstOrDefault(stack => stack != null && !stack.IsEmpty()
            && NetworkStorage.NormalizeKey(stack.itemValue).Equals(key));
    }

    private static int GetExactCount(IEnumerable<ItemStack> items, ItemValue value)
        => FindExactStack(items, value)?.count ?? 0;

    private static int TakeExactFromList(List<ItemStack> items, ItemValue value, int requested, List<ItemStack> taken)
    {
        if (items == null || value == null || requested <= 0) return 0;
        int remaining = requested;
        ItemValue key = NetworkStorage.NormalizeKey(value);
        for (int index = 0; index < items.Count && remaining > 0; index++)
        {
            ItemStack available = items[index];
            if (available == null || available.IsEmpty() || !NetworkStorage.NormalizeKey(available.itemValue).Equals(key)) continue;
            int take = Math.Min(remaining, available.count);
            if (taken != null) taken.Add(new ItemStack(available.itemValue.Clone(), take));
            available.count -= take;
            remaining -= take;
            if (available.count <= 0) items.RemoveAt(index--);
        }
        return requested - remaining;
    }

    private static long CountAccessibleForgeInputs(CraftingChainPlanInput input, ItemValue requirement)
    {
        long count = 0;
        if (input == null || requirement == null || !input.WorkstationsByArea.TryGetValue("forge", out List<CraftingChainWorkstationPosition> forges)
            || forges.Count == 0) return count;
        TileEntityWorkstation forge = CraftingChainWorkstationService.Resolve(forges[0]);
        foreach (ItemStack stack in forge?.Input ?? Array.Empty<ItemStack>())
            if (stack != null && !stack.IsEmpty() && NetworkItemMatcher.Matches(stack.itemValue, requirement)) count += stack.count;
        return count;
    }

    private static void AttachTrackingHandler(Order order)
    {
        if (order?.xui?.Recipes == null) return;
        order.TrackingChangedHandler = () => CancelIfTrackingChanged(order.player, GetTrackedRecipeKey(order.xui));
        order.xui.Recipes.OnTrackedRecipeChanged += order.TrackingChangedHandler;
    }

    private static void DetachTrackingHandler(Order order)
    {
        if (order?.TrackingChangedHandler == null || order.xui?.Recipes == null) return;
        order.xui.Recipes.OnTrackedRecipeChanged -= order.TrackingChangedHandler;
        order.TrackingChangedHandler = null;
    }

    private static string GetTrackedRecipeKey(XUi xui)
    {
        Recipe recipe = xui?.Recipes?.TrackedRecipe;
        if (recipe == null) return string.Empty;
        int quality = recipe.GetOutputItemClass()?.HasQuality == true
            ? Math.Max(0, xui.Recipes.TrackedRecipeQuality) : 0;
        return CraftingChainPlanner.BuildRecipeKey(recipe, quality);
    }

    private static void FailBeforeReservation(Order order, string statusKey, string reason)
    {
        if (order == null) return;
        order.Status = Localization.Get(statusKey);
        LastStatuses[order.player.entityId] = order.Status;
        DetachTrackingHandler(order);
        Orders.Remove(order.player.entityId);
        NetworkLog.Error("Crafting-chain could not reserve its initial inputs. player=" + order.player.entityId
            + "; root=" + order.RootRecipeKey + "; reason=" + reason);
    }

    private static void StartNextStage(Order order)
    {
        if (order == null || order.CancelRequested || !Orders.ContainsKey(order.player.entityId)) return;
        HashSet<TileEntityWorkstation> activeWorkstations = new HashSet<TileEntityWorkstation>(order.Stages
            .Where(stage => stage.IsActive).SelectMany(stage => stage.WorkstationTasks)
            .Where(task => task.Workstation != null).Select(task => task.Workstation));
        bool playerQueueActive = order.Stages.Any(stage => stage.IsActive && stage.WorkstationTasks.Count == 0);
        foreach (Stage stage in order.Stages.Where(candidate => !candidate.IsActive && !candidate.IsCompleted).ToList())
        {
            if (!DependenciesCompleted(order, stage.Node)) continue;
            if (stage.WorkstationTasks.Count == 0)
            {
                if (playerQueueActive) continue;
                if (!QueueIsIdle(stage.Queue))
                {
                    FailOrder(order, "HyperdimensionalNetworkCraftingChainQueueBusy", "craft-queue-became-busy");
                    return;
                }
            }
            else
            {
                if (stage.WorkstationTasks.Any(task => activeWorkstations.Contains(task.Workstation))) continue;
                bool unavailable = false;
                foreach (WorkstationTask task in stage.WorkstationTasks)
                {
                    TileEntityWorkstation live = GameManager.Instance?.World?.GetTileEntity(task.Workstation.ToWorldPos()) as TileEntityWorkstation;
                    if (!ReferenceEquals(live, task.Workstation) || task.Workstation.IsUserAccessing()
                        || !CraftingChainWorkstationService.IsQueueIdle(task.Workstation))
                    {
                        unavailable = true;
                        break;
                    }
                }
                if (unavailable) continue;
            }
            QueueStage(order, stage);
            if (!Orders.ContainsKey(order.player.entityId)) return;
            if (stage.WorkstationTasks.Count == 0) playerQueueActive = true;
            else foreach (WorkstationTask task in stage.WorkstationTasks) activeWorkstations.Add(task.Workstation);
        }
        if (order.Stages.All(stage => stage.IsCompleted)) CompleteOrder(order);
    }

    private static bool DependenciesCompleted(Order order, CraftingChainPlanNode node)
    {
        foreach (CraftingChainPlanNode child in node?.Children ?? new List<CraftingChainPlanNode>())
        {
            if (child == null || child.Batches <= 0) continue;
            Stage childStage = order.Stages.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, child));
            if (childStage != null && !childStage.IsCompleted) return false;
        }
        return true;
    }

    private static bool TryBuildStageRequirements(Stage stage, out List<ItemStack> requirements)
        => TryBuildStageRequirements(stage, stage?.Node?.Batches ?? 0, out requirements);

    private static bool TryBuildStageRequirements(Stage stage, int batches, out List<ItemStack> requirements)
    {
        requirements = new List<ItemStack>();
        if (stage?.QueueRecipe?.ingredients == null || batches < 0) return false;
        foreach (ItemStack ingredient in stage.QueueRecipe.ingredients)
        {
            if (ingredient == null || ingredient.IsEmpty()) continue;
            long count = (long)ingredient.count * batches;
            if (count <= 0 || count > int.MaxValue) return false;
            requirements.Add(new ItemStack(ingredient.itemValue.Clone(), (int)count));
            if (requirements.Count > 64) return false;
        }
        return true;
    }

    private static List<ItemStack> GetReservedInputsForBatches(Stage stage, int batches, out List<ItemStack> networkRefund)
    {
        networkRefund = new List<ItemStack>();
        if (batches <= 0 || stage?.ReservedInputs == null || stage.ReservedInputs.Count == 0) return new List<ItemStack>();
        if (!TryBuildStageRequirements(stage, batches, out List<ItemStack> requirements))
        {
            NetworkLog.Error("Crafting-chain could not calculate the unstarted ingredient refund. recipe=" + stage.SourceRecipe.GetName()
                + "; batches=" + batches);
            return new List<ItemStack>();
        }
        List<ItemStack> totalAvailable = stage.ReservedInputs.Where(item => item != null && !item.IsEmpty())
            .Select(item => item.Clone()).ToList();
        List<ItemStack> networkAvailable = stage.ReservedNetworkInputs.Where(item => item != null && !item.IsEmpty())
            .Select(item => item.Clone()).ToList();
        List<ItemStack> result = new List<ItemStack>();
        foreach (ItemStack requirement in requirements)
        {
            long remaining = requirement.count;
            foreach (ItemStack candidate in totalAvailable.Where(item => item != null && !item.IsEmpty()
                && NetworkItemMatcher.Matches(item.itemValue, requirement.itemValue)))
            {
                if (remaining <= 0) break;
                int networkCount = GetExactCount(networkAvailable, candidate.itemValue);
                int physicalCount = Math.Max(0, candidate.count - networkCount);
                int take = (int)Math.Min(remaining, physicalCount);
                if (take <= 0) continue;
                int actual = TakeExactFromList(totalAvailable, candidate.itemValue, take, result);
                remaining -= actual;
            }
            foreach (ItemStack candidate in networkAvailable.Where(item => item != null && !item.IsEmpty()
                && NetworkItemMatcher.Matches(item.itemValue, requirement.itemValue)))
            {
                if (remaining <= 0) break;
                int take = (int)Math.Min(remaining, candidate.count);
                if (take <= 0) continue;
                int actualNetwork = TakeExactFromList(networkAvailable, candidate.itemValue, take, networkRefund);
                int actualTotal = TakeExactFromList(totalAvailable, candidate.itemValue, actualNetwork, result);
                remaining -= Math.Min(actualNetwork, actualTotal);
            }
            if (remaining > 0)
            {
                NetworkLog.Error("Crafting-chain reserved inputs did not cover a safe cancellation refund. recipe=" + stage.SourceRecipe.GetName()
                    + "; item=" + requirement.itemValue.type + "; missing=" + remaining);
                return new List<ItemStack>();
            }
        }
        return result;
    }

    private static void QueueStage(Order order, Stage stage)
    {
        try
        {
            if (!TakeEscrowInputs(order, stage, out List<ItemStack> reservedInputs, out List<ItemStack> reservedNetworkInputs))
            {
                FailOrder(order, "HyperdimensionalNetworkCraftingChainMissingMaterials", "stage-input-not-in-private-escrow");
                return;
            }
            stage.ReservedInputs = reservedInputs;
            stage.ReservedNetworkInputs = reservedNetworkInputs;

            if (stage.UsesForgeMaterials)
            {
                if (!TryConsumeForgeInputs(stage, out List<ItemStack> forgeInputs))
                {
                    FailOrder(order, "HyperdimensionalNetworkCraftingChainMissingMaterials", "forge-inputs-changed-before-queue");
                    return;
                }
                stage.ReservedInputs = forgeInputs;
                stage.ReservedNetworkInputs = new List<ItemStack>();
            }

            if (stage.WorkstationTasks.Count > 0)
            {
                foreach (WorkstationTask task in stage.WorkstationTasks)
                {
                    TileEntityWorkstation workstation = task.Workstation;
                    if (!CraftingChainWorkstationService.CanRun(workstation, stage.SourceRecipe,
                        BuildStageOutputValue(stage), (int)Math.Min(int.MaxValue, (long)stage.QueueRecipe.count * task.Batches),
                        stage.QueueRecipe.craftingTime, task.Batches, out string workstationReason))
                    {
                        FailOrder(order, "HyperdimensionalNetworkCraftingChainWorkstationUnavailable",
                            "workstation-no-longer-runnable-" + workstationReason);
                        return;
                    }
                    task.OutputBaseline = (workstation.Output ?? Array.Empty<ItemStack>())
                        .Select(item => item == null ? ItemStack.Empty.Clone() : item.Clone()).ToList();
                    RecipeQueueItem[] workstationQueue = (workstation.Queue ?? Array.Empty<RecipeQueueItem>())
                        .Select(CloneQueueItem).ToArray();
                    if (workstationQueue.Length == 0)
                    {
                        FailOrder(order, "HyperdimensionalNetworkCraftingChainWorkstationUnavailable",
                            "workstation-has-no-vanilla-queue-slots");
                        return;
                    }
                    task.QueueIndex = workstationQueue.Length - 1;
                    workstationQueue[task.QueueIndex] = new RecipeQueueItem
                    {
                        Recipe = stage.QueueRecipe,
                        Multiplier = (short)task.Batches,
                        CraftingTimeLeft = stage.QueueRecipe.craftingTime,
                        OneItemCraftTime = stage.QueueRecipe.craftingTime,
                        IsCrafting = true,
                        Quality = (byte)Math.Max(0, Math.Min(byte.MaxValue, stage.OutputQuality)),
                        StartingEntityId = order.player.entityId
                    };
                    if (CraftingChainWorkstationService.RequiresFuel(workstation) && !workstation.IsBurning)
                        new XUiM_Workstation(workstation).SetIsBurning(true);
                    workstation.Queue = workstationQueue;
                    if (!ReferenceEquals(workstation.Queue[task.QueueIndex]?.Recipe, stage.QueueRecipe))
                    {
                        FailOrder(order, "HyperdimensionalNetworkCraftingChainPlanFailed",
                            "vanilla-workstation-rejected-queue-entry");
                        return;
                    }
                    task.QueueSubmitted = true;
                    stage.QueueSubmitted = true;
                }
            }
            else
            {
                if (!stage.Queue.AddRecipeToCraft(stage.QueueRecipe, stage.Node.Batches, isCrafting: true))
                {
                    RestoreUnstartedStageInputs(order, stage);
                    FailOrder(order, "HyperdimensionalNetworkCraftingChainQueueBusy", "native-queue-rejected-recipe");
                    return;
                }
                stage.QueueStack = stage.Queue.GetRecipesToCraft().FirstOrDefault(stack => stack.HasRecipe()
                    && ReferenceEquals(stack.GetRecipe(), stage.QueueRecipe));
                if (stage.QueueStack == null)
                {
                    foreach (XUiC_RecipeStack stack in stage.Queue.GetRecipesToCraft())
                        if (stack != null && ReferenceEquals(stack.GetRecipe(), stage.QueueRecipe)) stack.ClearRecipe();
                    RestoreUnstartedStageInputs(order, stage);
                    FailOrder(order, "HyperdimensionalNetworkCraftingChainPlanFailed", "native-queue-entry-not-found");
                    return;
                }
                stage.QueueSubmitted = true;
            }
            stage.IsActive = true;
            stage.StartedAt = UnityEngine.Time.realtimeSinceStartup;
            stage.NetworkBaseline = NetworkStorage.GetMatchingCount(order.player, BuildStageOutputValue(stage));
            order.Status = string.Format(Localization.Get("HyperdimensionalNetworkCraftingChainStepProgress"),
                order.StageIndex + 1, order.Stages.Count, GetItemName(stage));
            NetworkLog.Debug("Crafting-chain vanilla step queued. player=" + order.player.entityId + "; step=" + (order.StageIndex + 1)
                + "; recipe=" + stage.SourceRecipe.GetName() + "; batches=" + stage.Node.Batches + "; workstations="
                + stage.WorkstationTasks.Count + "; source=" + (stage.Workstation != null ? "native-tile-entity-queue" : "private-order-escrow"));
        }
        catch (Exception ex)
        {
            FailOrder(order, "HyperdimensionalNetworkCraftingChainPlanFailed", "queue-stage-exception-" + ex.GetType().Name);
            NetworkLog.Error("Crafting-chain queue stage failed. player=" + order.player.entityId + "; step=" + (order.StageIndex + 1) + "; exception=" + ex);
        }
    }

    private static RecipeQueueItem CloneQueueItem(RecipeQueueItem item)
    {
        if (item == null) return new RecipeQueueItem();
        return new RecipeQueueItem
        {
            Recipe = item.Recipe,
            Multiplier = item.Multiplier,
            CraftingTimeLeft = item.CraftingTimeLeft,
            OneItemCraftTime = item.OneItemCraftTime,
            IsCrafting = item.IsCrafting,
            RepairItem = item.RepairItem?.Clone(),
            AmountToRepair = item.AmountToRepair,
            Quality = item.Quality,
            StartingEntityId = item.StartingEntityId
        };
    }

    private static bool TryConsumeForgeInputs(Stage stage, out List<ItemStack> removed)
    {
        removed = new List<ItemStack>();
        TileEntityWorkstation workstation = stage?.Workstation;
        if (workstation == null || !TryBuildStageRequirements(stage, out List<ItemStack> requirements)) return false;
        ItemStack[] current = workstation.Input;
        ItemStack[] updated = current.Select(stack => stack == null ? ItemStack.Empty.Clone() : stack.Clone()).ToArray();
        foreach (ItemStack requirement in requirements)
        {
            int remaining = requirement.count;
            for (int index = 0; index < updated.Length && remaining > 0; index++)
            {
                ItemStack stack = updated[index];
                if (stack == null || stack.IsEmpty() || !NetworkItemMatcher.Matches(stack.itemValue, requirement.itemValue)) continue;
                int take = Math.Min(remaining, stack.count);
                removed.Add(new ItemStack(stack.itemValue.Clone(), take));
                stack.count -= take;
                remaining -= take;
                if (stack.count <= 0) updated[index] = ItemStack.Empty.Clone();
            }
            if (remaining > 0)
            {
                removed.Clear();
                return false;
            }
        }
        workstation.Input = updated;
        NetworkLog.Debug("Crafting-chain forge inputs reserved from the selected loaded forge. recipe=" + stage.SourceRecipe.GetName()
            + "; variants=" + removed.Count + "; batches=" + stage.Node.Batches);
        return true;
    }

    private static void RestoreUnstartedStageInputs(Order order, Stage stage)
    {
        if (stage?.ReservedInputs == null || stage.ReservedInputs.Count == 0) return;
        if (stage.UsesForgeMaterials)
        {
            List<ItemStack> overflow = RestoreForgeInputs(stage, stage.ReservedInputs);
            AddEscrowItems(order, overflow);
        }
        else
        {
            AddEscrowItems(order, stage.ReservedInputs);
            AddNetworkEscrowItems(order, stage.ReservedNetworkInputs);
        }
        stage.ReservedInputs.Clear();
        stage.ReservedNetworkInputs.Clear();
    }

    private static List<ItemStack> RestoreForgeInputs(Stage stage, IEnumerable<ItemStack> items)
    {
        List<ItemStack> overflow = new List<ItemStack>();
        TileEntityWorkstation workstation = stage?.Workstation;
        if (workstation == null)
        {
            overflow.AddRange((items ?? Enumerable.Empty<ItemStack>()).Where(item => item != null && !item.IsEmpty()).Select(item => item.Clone()));
            return overflow;
        }
        TileEntityWorkstation live = GameManager.Instance?.World?.GetTileEntity(workstation.ToWorldPos()) as TileEntityWorkstation;
        if (!ReferenceEquals(live, workstation))
        {
            overflow.AddRange((items ?? Enumerable.Empty<ItemStack>()).Where(item => item != null && !item.IsEmpty()).Select(item => item.Clone()));
            NetworkLog.Debug("Crafting-chain forge input returned to private escrow because its loaded workstation changed. position="
                + workstation.ToWorldPos() + "; items=" + overflow.Count);
            return overflow;
        }
        ItemStack[] current = workstation.Input;
        ItemStack[] updated = current.Select(stack => stack == null ? ItemStack.Empty.Clone() : stack.Clone()).ToArray();
        foreach (ItemStack item in items ?? Enumerable.Empty<ItemStack>())
        {
            if (item == null || item.IsEmpty()) continue;
            int remaining = item.count;
            int maxCount = Math.Max(1, item.itemValue.ItemClass?.MaxCount ?? item.count);
            for (int index = 0; index < updated.Length && remaining > 0; index++)
            {
                ItemStack stack = updated[index];
                if (stack == null || stack.IsEmpty() || !stack.CanStackWith(item) || stack.count >= maxCount) continue;
                int add = Math.Min(remaining, maxCount - stack.count);
                stack.count += add;
                remaining -= add;
            }
            for (int index = 0; index < updated.Length && remaining > 0; index++)
            {
                if (updated[index] != null && !updated[index].IsEmpty()) continue;
                int add = Math.Min(remaining, maxCount);
                updated[index] = new ItemStack(item.itemValue.Clone(), add);
                remaining -= add;
            }
            if (remaining > 0) overflow.Add(new ItemStack(item.itemValue.Clone(), remaining));
        }
        workstation.Input = updated;
        return overflow;
    }

    private static bool TryPrepareStage(XUi xui, EntityPlayerLocal player, CraftingChainPlanInput input,
        CraftingChainPlanNode node, out Stage stage, out string error)
    {
        stage = null;
        error = string.Empty;
        if (node == null || node.ItemId < 0 || node.ItemId >= input.Items.Count)
        {
            error = "HyperdimensionalNetworkCraftingChainPlanFailed";
            return false;
        }
        CraftingChainItemInfo item = input.Items[node.ItemId];
        Recipe recipe = (CraftingManager.recipes ?? new List<Recipe>()).FirstOrDefault(candidate => candidate != null
            && CraftingChainPlanner.BuildRecipeKey(candidate, item.Quality) == node.RecipeKey && candidate.IsUnlocked(player));
        if (recipe == null)
        {
            error = "HyperdimensionalNetworkCraftingChainRecipeUnavailable";
            return false;
        }

        XUiC_CraftingWindowGroup group;
        XUiC_WorkstationWindowGroup workstationGroup = null;
        TileEntityWorkstation workstation = null;
        List<WorkstationTask> workstationTasks = new List<WorkstationTask>();
        if (string.IsNullOrEmpty(recipe.craftingArea))
        {
            group = xui.FindWindowGroupByName("crafting") as XUiC_CraftingWindowGroup;
        }
        else
        {
            group = null;
            if (!input.WorkstationsByArea.TryGetValue(recipe.craftingArea, out List<CraftingChainWorkstationPosition> candidates)
                || candidates.Count == 0)
            {
                error = "HyperdimensionalNetworkCraftingChainWorkstationUnavailable";
                return false;
            }
            List<TileEntityWorkstation> available = candidates.Select(CraftingChainWorkstationService.Resolve)
                .Where(candidate => candidate != null && !candidate.IsUserAccessing()
                    && CraftingChainWorkstationService.IsQueueIdle(candidate)
                    && (LockManager.Instance == null || !LockManager.Instance.IsLockedByLocalPlayer(candidate)))
                .Distinct().ToList();
            Recipe candidateQueueRecipe = BuildQueueRecipe(xui, player, recipe, item.Quality);
            if (candidateQueueRecipe == null)
            {
                error = "HyperdimensionalNetworkCraftingChainRecipeUnavailable";
                return false;
            }
            int maxTasks = string.Equals(recipe.craftingArea, "forge", StringComparison.OrdinalIgnoreCase)
                ? Math.Min(1, available.Count)
                : Math.Min(Math.Min(available.Count, Math.Max(1, node.Batches)), CraftingChainWorkstationService.MaxTrackedWorkstations);
            for (int taskCount = maxTasks; taskCount > 0 && workstationTasks.Count == 0; taskCount--)
            {
                List<TileEntityWorkstation> selected = available.Take(taskCount).ToList();
                int baseBatches = node.Batches / taskCount;
                int remainder = node.Batches % taskCount;
                bool allRunnable = true;
                List<WorkstationTask> proposed = new List<WorkstationTask>();
                for (int index = 0; index < selected.Count; index++)
                {
                    int assignedBatches = baseBatches + (index < remainder ? 1 : 0);
                    TileEntityWorkstation candidate = selected[index];
                    ItemValue candidateOutput = item.Quality > 0
                        ? new ItemValue(item.Type, item.Quality, item.Quality) : new ItemValue(item.Type);
                    int candidateOutputCount = (int)Math.Min(int.MaxValue, (long)candidateQueueRecipe.count * assignedBatches);
                    if (!CraftingChainWorkstationService.CanRun(candidate, recipe, candidateOutput, candidateOutputCount,
                        candidateQueueRecipe.craftingTime, assignedBatches, out string workstationReason))
                    {
                        NetworkLog.Debug("Crafting-chain workstation preflight rejected a distributed candidate. area=" + node.CraftingArea
                            + "; position=" + candidate.ToWorldPos() + "; batches=" + assignedBatches + "; reason=" + workstationReason);
                        allRunnable = false;
                        break;
                    }
                    proposed.Add(new WorkstationTask { Workstation = candidate, Batches = assignedBatches });
                }
                if (allRunnable) workstationTasks = proposed;
            }
            if (workstationTasks.Count == 0)
            {
                error = "HyperdimensionalNetworkCraftingChainWorkstationUnavailable";
                return false;
            }
            workstation = workstationTasks[0].Workstation;
        }
        XUiC_CraftingQueue queue = group?.GetChildByType<XUiC_CraftingQueue>();
        if (string.IsNullOrEmpty(recipe.craftingArea) && (group == null || queue == null))
        {
            error = "HyperdimensionalNetworkCraftingChainWorkstationUnavailable";
            return false;
        }

        Recipe queueRecipe = BuildQueueRecipe(xui, player, recipe, item.Quality);
        if (queueRecipe == null)
        {
            error = "HyperdimensionalNetworkCraftingChainRecipeUnavailable";
            return false;
        }
        XUiC_WorkstationOutputGrid outputGrid = group?.GetChildByType<XUiC_WorkstationOutputGrid>();
        bool usesForge = string.Equals(recipe.craftingArea, "forge", StringComparison.OrdinalIgnoreCase);
        int expected = (int)Math.Min(int.MaxValue, (long)queueRecipe.count * node.Batches);
        stage = new Stage
        {
            Node = node,
            SourceRecipe = recipe,
            QueueRecipe = queueRecipe,
            OutputItemType = item.Type,
            OutputQuality = item.Quality,
            ExpectedOutput = expected,
            Group = group,
            WorkstationGroup = workstationGroup,
            Workstation = workstation,
            WorkstationQueueIndex = workstation?.Queue?.Length - 1 ?? -1,
            Queue = queue,
            OutputGrid = outputGrid,
            UsesForgeMaterials = usesForge
        };
        stage.WorkstationTasks.AddRange(workstationTasks);
        return true;
    }

    private static Recipe BuildQueueRecipe(XUi xui, EntityPlayerLocal player, Recipe source, int outputQuality)
    {
        List<ItemStack> ingredients = new List<ItemStack>();
        int tier = source.GetCraftingTier(player);
        foreach (ItemStack ingredient in source.ingredients)
        {
            if (ingredient == null || ingredient.IsEmpty()) continue;
            int count = CraftingChainPlanner.GetIngredientCount(xui, player, source, ingredient, tier);
            if (count > 0) ingredients.Add(new ItemStack(ingredient.itemValue.Clone(), count));
        }
        try
        {
            return new Recipe
            {
                itemValueType = source.itemValueType,
                count = Math.Max(1, XUiM_Recipes.GetRecipeCraftOutputCount(xui, source)),
                craftingArea = source.craftingArea,
                craftExpGain = source.craftExpGain,
                craftingTime = XUiM_Recipes.GetRecipeCraftTime(xui, source),
                craftingToolType = source.craftingToolType,
                craftingTier = outputQuality,
                materialBasedRecipe = source.materialBasedRecipe,
                wildcardForgeCategory = source.wildcardForgeCategory,
                IsScrap = source.IsScrap,
                isQuest = source.isQuest,
                isChallenge = source.isChallenge,
                IsTrackable = source.IsTrackable,
                UseIngredientModifier = false,
                tags = source.tags,
                ingredients = ingredients
            };
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Crafting-chain recipe could not be converted to a vanilla queue entry. recipe=" + source.GetName() + "; exception=" + ex);
            return null;
        }
    }

    private static ItemValue BuildStageOutputValue(Stage stage)
        => stage == null ? ItemValue.None : stage.OutputQuality > 0
            ? new ItemValue(stage.OutputItemType, stage.OutputQuality, stage.OutputQuality)
            : new ItemValue(stage.OutputItemType);

    private static bool QueueIsIdle(XUiC_CraftingQueue queue)
        => queue != null && queue.GetRecipesToCraft().All(stack => stack == null || !stack.HasRecipe());

    private static bool TransitOutputToEscrow(Order order, Stage stage, ItemValue output, int count,
        out bool waitingInNetwork)
    {
        waitingInNetwork = false;
        if (count <= 0) return true;
        if (NetworkRemoteClient.IsRemoteClient)
        {
            // Remote workstation output is still synchronized by the vanilla tile entity.
            // Keep existing orders usable until a server-authoritative transfer RPC is added.
            AddEscrowItem(order, new ItemStack(output.Clone(), count));
            NetworkLog.Debug("Crafting-chain remote output collected with legacy escrow path. player=" + order.player.entityId
                + "; recipe=" + stage.SourceRecipe.GetName() + "; count=" + count);
            return true;
        }
        StorageAddition transfer = new StorageAddition { Value = output.Clone(), Count = count };
        if (!NetworkStorage.AddBatch(order.player, new[] { transfer })) return false;
        if (!NetworkStorage.RemoveBatch(order.player, new[] { transfer }))
        {
            waitingInNetwork = true;
            NetworkLog.Error("Crafting-chain output entered network but could not enter escrow. player="
                + order.player.entityId + "; item=" + output.type + "; count=" + count);
            return true;
        }
        AddEscrowItem(order, new ItemStack(output.Clone(), count));
        return true;
    }

    private static void TryCollectNetworkOutput(Order order, Stage stage)
    {
        if (NetworkRemoteClient.IsRemoteClient || stage.CollectedOutput >= stage.ExpectedOutput) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < stage.NextNetworkCheck) return;
        stage.NextNetworkCheck = now + 0.5f;
        ItemValue output = BuildStageOutputValue(stage);
        long current = NetworkStorage.GetMatchingCount(order.player, output);
        long newlyDeposited = Math.Max(0L, current - stage.NetworkBaseline);
        int take = (int)Math.Min(Math.Min(newlyDeposited, int.MaxValue), stage.ExpectedOutput - stage.CollectedOutput);
        bool withdrawalFailed = false;
        if (take > 0)
        {
            StorageAddition withdrawal = new StorageAddition { Value = output.Clone(), Count = take };
            if (NetworkStorage.RemoveBatch(order.player, new[] { withdrawal }))
            {
                AddEscrowItem(order, new ItemStack(output.Clone(), take));
                stage.CollectedOutput += take;
                current -= take;
                NetworkLog.Debug("Crafting-chain accepted matching network deposit. player=" + order.player.entityId
                    + "; recipe=" + stage.SourceRecipe.GetName() + "; accepted=" + take
                    + "; remaining=" + (stage.ExpectedOutput - stage.CollectedOutput));
            }
            else withdrawalFailed = true;
        }
        if (!withdrawalFailed) stage.NetworkBaseline = current;
    }

    private static bool CollectOutputsToEscrow(Order order, Stage stage, int expectedCount)
    {
        int remaining = Math.Max(0, Math.Min(expectedCount - stage.CollectedOutput,
            stage.ExpectedOutput - stage.CollectedOutput));
        if (remaining == 0) return true;
        ItemValue output = stage.OutputQuality > 0
            ? new ItemValue(stage.OutputItemType, stage.OutputQuality, stage.OutputQuality)
            : new ItemValue(stage.OutputItemType);
        int collected = 0;
        if (stage.Workstation != null)
        {
            ItemStack[] slots = stage.Workstation.Output ?? Array.Empty<ItemStack>();
            ItemStack[] updated = slots.Select(stack => stack == null ? ItemStack.Empty.Clone() : stack.Clone()).ToArray();
            for (int index = 0; index < updated.Length && remaining > 0; index++)
            {
                ItemStack stack = updated[index];
                if (stack == null || stack.IsEmpty() || !NetworkItemMatcher.Matches(stack.itemValue, output)) continue;
                ItemStack baseline = index < stage.WorkstationOutputBaseline.Count ? stage.WorkstationOutputBaseline[index] : ItemStack.Empty;
                int baselineCount = baseline != null && !baseline.IsEmpty()
                    && NetworkStorage.NormalizeKey(baseline.itemValue).Equals(NetworkStorage.NormalizeKey(stack.itemValue))
                    ? baseline.count : 0;
                if (baseline != null && !baseline.IsEmpty()
                    && !NetworkStorage.NormalizeKey(baseline.itemValue).Equals(NetworkStorage.NormalizeKey(stack.itemValue))) continue;
                int ownedCount = Math.Max(0, stack.count - baselineCount);
                int take = Math.Min(remaining, ownedCount);
                if (take <= 0) continue;
                stack.count -= take;
                collected += take;
                remaining -= take;
                if (stack.count <= 0) updated[index] = ItemStack.Empty.Clone();
            }
            if (collected > 0)
            {
                if (!TransitOutputToEscrow(order, stage, output, collected, out bool waitingInNetwork)) return false;
                stage.Workstation.Output = updated;
                if (waitingInNetwork) return false;
            }
        }
        else if (stage.OutputGrid != null)
        {
            ItemStack[] slots = stage.OutputGrid.GetSlots();
            ItemStack[] updated = slots.Select(stack => stack == null ? ItemStack.Empty.Clone() : stack.Clone()).ToArray();
            for (int index = 0; index < updated.Length && remaining > 0; index++)
            {
                ItemStack stack = updated[index];
                if (stack == null || stack.IsEmpty() || !NetworkItemMatcher.Matches(stack.itemValue, output)) continue;
                int take = Math.Min(remaining, stack.count);
                stack.count -= take;
                collected += take;
                remaining -= take;
                if (stack.count <= 0) updated[index] = ItemStack.Empty.Clone();
            }
            if (collected > 0)
            {
                if (!TransitOutputToEscrow(order, stage, output, collected, out bool waitingInNetwork)) return false;
                stage.OutputGrid.SetSlots(updated);
                stage.OutputGrid.UpdateData(updated);
                stage.OutputGrid.IsDirty = true;
                if (waitingInNetwork) return false;
            }
        }
        else
        {
            collected = RemovePhysical(order.player, output, remaining);
            remaining -= collected;
            bool waitingInNetwork = false;
            if (collected > 0 && !TransitOutputToEscrow(order, stage, output, collected, out waitingInNetwork))
            {
                order.xui.PlayerInventory.AddItemNoPartial(new ItemStack(output.Clone(), collected), _playCollectSound: false);
                return false;
            }
            if (collected > 0 && waitingInNetwork) return false;
        }
        if (collected > 0)
        {
            stage.CollectedOutput += collected;
            NetworkLog.Debug("Crafting-chain output moved through network into order escrow. player=" + order.player.entityId
                + "; recipe=" + stage.SourceRecipe.GetName() + "; collected=" + collected + "; remaining=" + remaining);
        }
        if (remaining > 0)
        {
            NetworkLog.Error("Crafting-chain could not retrieve all completed output. player=" + order.player.entityId
                + "; recipe=" + stage.SourceRecipe.GetName() + "; missing=" + remaining + "; collected=" + stage.CollectedOutput
                + "; expected=" + expectedCount);
            return false;
        }
        return true;
    }

    private static bool CollectWorkstationTaskOutput(Order order, Stage stage, WorkstationTask task, int expectedCount)
    {
        if (order == null || stage == null || task?.Workstation == null) return false;
        int remaining = Math.Max(0, Math.Min(expectedCount - task.CollectedOutput,
            stage.ExpectedOutput - stage.CollectedOutput));
        if (remaining == 0) return true;
        ItemValue output = BuildStageOutputValue(stage);
        ItemStack[] slots = task.Workstation.Output ?? Array.Empty<ItemStack>();
        ItemStack[] updated = slots.Select(stack => stack == null ? ItemStack.Empty.Clone() : stack.Clone()).ToArray();
        int collected = 0;
        for (int index = 0; index < updated.Length && remaining > 0; index++)
        {
            ItemStack stack = updated[index];
            if (stack == null || stack.IsEmpty() || !NetworkItemMatcher.Matches(stack.itemValue, output)) continue;
            ItemStack baseline = index < task.OutputBaseline.Count ? task.OutputBaseline[index] : ItemStack.Empty;
            bool sameVariant = baseline != null && !baseline.IsEmpty()
                && NetworkStorage.NormalizeKey(baseline.itemValue).Equals(NetworkStorage.NormalizeKey(stack.itemValue));
            if (baseline != null && !baseline.IsEmpty() && !sameVariant) continue;
            int ownedCount = Math.Max(0, stack.count - (sameVariant ? baseline.count : 0));
            int take = Math.Min(remaining, ownedCount);
            if (take <= 0) continue;
            stack.count -= take;
            collected += take;
            remaining -= take;
            if (stack.count <= 0) updated[index] = ItemStack.Empty.Clone();
        }
        if (collected > 0)
        {
            if (!TransitOutputToEscrow(order, stage, output, collected, out bool waitingInNetwork)) return false;
            task.Workstation.Output = updated;
            if (waitingInNetwork) return false;
            task.CollectedOutput += collected;
            stage.CollectedOutput += collected;
            NetworkLog.Debug("Crafting-chain workstation output moved through network into order escrow. player=" + order.player.entityId
                + "; recipe=" + stage.SourceRecipe.GetName() + "; position=" + task.Workstation.ToWorldPos()
                + "; collected=" + collected + "; remaining=" + remaining);
        }
        if (remaining > 0)
        {
            NetworkLog.Error("Crafting-chain could not retrieve all completed workstation output. player=" + order.player.entityId
                + "; recipe=" + stage.SourceRecipe.GetName() + "; position=" + task.Workstation.ToWorldPos()
                + "; missing=" + remaining + "; collected=" + task.CollectedOutput + "; expected=" + expectedCount);
            return false;
        }
        return true;
    }

    private static List<CraftingChainPlanNode> FlattenPostOrder(IEnumerable<CraftingChainPlanNode> roots)
    {
        List<CraftingChainPlanNode> result = new List<CraftingChainPlanNode>();
        Action<CraftingChainPlanNode> visit = null;
        visit = node =>
        {
            if (node == null) return;
            foreach (CraftingChainPlanNode child in node.Children) visit(child);
            if (node.Batches > 0) result.Add(node);
        };
        foreach (CraftingChainPlanNode root in roots ?? Array.Empty<CraftingChainPlanNode>()) visit(root);
        return result;
    }

    private static string GetItemName(Stage stage)
    {
        ItemClass itemClass = ItemClass.GetForId(stage.OutputItemType);
        return itemClass?.GetLocalizedItemName() ?? stage.OutputItemType.ToString();
    }

    private static ItemValue BuildOutputValue(CraftingChainPlanInput input, int itemId)
    {
        if (input == null || itemId < 0 || itemId >= input.Items.Count) return ItemValue.None;
        if (input.MainThreadItemValues != null && input.MainThreadItemValues.TryGetValue(itemId, out ItemValue value))
            return value.Clone();
        CraftingChainItemInfo item = input.Items[itemId];
        return item.Quality > 0
            ? new ItemValue(item.Type, item.Quality, item.Quality)
            : new ItemValue(item.Type);
    }

    private static ItemValue GetItemValue(CraftingChainPlanInput input, int itemId)
    {
        if (input == null || itemId < 0 || itemId >= input.Items.Count) return ItemValue.None;
        if (input.MainThreadItemValues != null && input.MainThreadItemValues.TryGetValue(itemId, out ItemValue value))
            return value.Clone();
        CraftingChainItemInfo item = input.Items[itemId];
        return item.Quality > 0 ? new ItemValue(item.Type, item.Quality, item.Quality) : new ItemValue(item.Type);
    }

    private static int RemovePhysical(EntityPlayer player, ItemValue requirement, int requested)
    {
        if (!(bool)player || requirement == null || requested <= 0) return 0;
        int remaining = requested;
        int removed = 0;
        foreach (ItemStackGrid grid in new[] { player.inventory?.ItemGrid, player.bag?.ItemGrid })
        {
            if (grid == null) continue;
            for (int index = 0; index < grid.Length && remaining > 0; index++)
            {
                ItemStack stack = grid[index];
                if (stack == null || stack.IsEmpty() || !NetworkItemMatcher.Matches(stack.itemValue, requirement)) continue;
                int take = Math.Min(remaining, stack.count);
                int changed = grid.ChangeCount(index, -take);
                if (changed >= 0) continue;
                int actual = Math.Min(take, -changed);
                remaining -= actual;
                removed += actual;
            }
        }
        return removed;
    }

    private static void CompleteOrder(Order order)
    {
        if (order == null || !Orders.ContainsKey(order.player.entityId) || order.ReturnPending) return;
        if (!DeliverRootOutput(order)) return;
        order.TerminalStatus = Localization.Get("HyperdimensionalNetworkCraftingChainCompleted");
        TryReturnEscrow(order);
    }

    private static void CancelOrder(Order order, string reason)
    {
        if (order == null || order.ReturnPending || order.ReturnInFlight) return;
        StopCurrentStage(order, reason);
        order.CancelRequested = false;
        order.TerminalStatus = Localization.Get("HyperdimensionalNetworkCraftingChainCancelled");
        NetworkLog.Debug("Crafting-chain order cancelled. player=" + order.player.entityId + "; root=" + order.RootRecipeKey
            + "; reason=" + reason + "; completedSteps=" + order.StageIndex);
        TryReturnEscrow(order);
    }

    private static void FailOrder(Order order, string statusKey, string reason)
    {
        if (order == null || order.ReturnPending || order.ReturnInFlight) return;
        order.TerminalStatus = Localization.Get(statusKey);
        NetworkLog.Error("Crafting-chain order failed safely. player=" + order.player.entityId + "; root=" + order.RootRecipeKey
            + "; step=" + (order.StageIndex + 1) + "; reason=" + reason);
        StopCurrentStage(order, reason);
        order.CancelRequested = false;
        TryReturnEscrow(order);
    }

    private static void StopCurrentStage(Order order, string reason, Stage onlyStage = null)
    {
        foreach (Stage stage in order.Stages.Where(candidate => candidate.IsActive || (!candidate.IsCompleted && candidate.QueueSubmitted)
            || candidate.ReservedInputs.Count > 0).Where(candidate => onlyStage == null || ReferenceEquals(candidate, onlyStage)).ToList())
        {
            if (stage.ReservedInputs.Count == 0 && !stage.QueueSubmitted) continue;
            try
            {
            int remainingBatches = stage.Node.Batches;
            if (stage.WorkstationTasks.Count > 0)
            {
                remainingBatches = 0;
                foreach (WorkstationTask workstationTask in stage.WorkstationTasks)
                {
                    if (!workstationTask.QueueSubmitted)
                    {
                        remainingBatches += workstationTask.Batches;
                        continue;
                    }
                    TileEntityWorkstation live = GameManager.Instance?.World?.GetTileEntity(workstationTask.Workstation.ToWorldPos()) as TileEntityWorkstation;
                    if (!ReferenceEquals(live, workstationTask.Workstation))
                    {
                        NetworkLog.Error("Crafting-chain cannot stop a workstation task after its chunk unloaded. player=" + order.player.entityId
                            + "; position=" + workstationTask.Workstation.ToWorldPos() + "; recipe=" + stage.SourceRecipe.GetName()
                            + "; reason=" + reason + "; remainingBatchesNotRefunded=" + workstationTask.Batches);
                        continue;
                    }
                    RecipeQueueItem[] workstationQueue = workstationTask.Workstation.Queue ?? Array.Empty<RecipeQueueItem>();
                    RecipeQueueItem task = workstationTask.QueueIndex >= 0 && workstationTask.QueueIndex < workstationQueue.Length
                        ? workstationQueue[workstationTask.QueueIndex] : null;
                    if (task?.Recipe != null)
                    {
                        if (!ReferenceEquals(task.Recipe, stage.QueueRecipe) || task.StartingEntityId != order.player.entityId)
                        {
                            NetworkLog.Error("Crafting-chain refused to clear a workstation queue entry it cannot identify. player="
                                + order.player.entityId + "; position=" + workstationTask.Workstation.ToWorldPos()
                                + "; recipe=" + stage.SourceRecipe.GetName() + "; inputsNotRefunded=" + workstationTask.Batches);
                            continue;
                        }
                        int queuedRemainingBatches = Math.Max(0, Math.Min(workstationTask.Batches, (int)task.Multiplier));
                        int completedBatches = workstationTask.Batches - queuedRemainingBatches;
                        int completedOutput = (int)Math.Min(int.MaxValue, (long)completedBatches * stage.QueueRecipe.count);
                        if (completedOutput > workstationTask.CollectedOutput)
                            CollectWorkstationTaskOutput(order, stage, workstationTask, completedOutput);
                        RecipeQueueItem[] clearedQueue = workstationQueue.Select(CloneQueueItem).ToArray();
                        clearedQueue[workstationTask.QueueIndex] = new RecipeQueueItem();
                        workstationTask.Workstation.Queue = clearedQueue;
                        remainingBatches += queuedRemainingBatches;
                        NetworkLog.Debug("Crafting-chain removed its registered native workstation task. player=" + order.player.entityId
                            + "; position=" + workstationTask.Workstation.ToWorldPos() + "; completedBatches=" + completedBatches
                            + "; remainingBatches=" + queuedRemainingBatches + "; reason=" + reason);
                    }
                    else
                    {
                        int expectedTaskOutput = (int)Math.Min(int.MaxValue, (long)workstationTask.Batches * stage.QueueRecipe.count);
                        if (!CollectWorkstationTaskOutput(order, stage, workstationTask, expectedTaskOutput))
                            NetworkLog.Error("Crafting-chain output vanished with an unidentifiable workstation task; its inputs will not be refunded. player="
                                + order.player.entityId + "; position=" + workstationTask.Workstation.ToWorldPos()
                                + "; recipe=" + stage.SourceRecipe.GetName() + "; expectedOutput=" + expectedTaskOutput);
                    }
                }
            }
            else if (stage.QueueSubmitted && stage.QueueStack?.HasRecipe() == true && ReferenceEquals(stage.QueueStack.GetRecipe(), stage.QueueRecipe))
            {
                int queuedRemainingBatches = Math.Max(0, stage.QueueStack.GetRecipeCount());
                int completedBatches = Math.Max(0, stage.Node.Batches - queuedRemainingBatches);
                int completedOutput = (int)Math.Min(int.MaxValue, (long)completedBatches * stage.QueueRecipe.count);
                if (completedOutput > stage.CollectedOutput
                    && !CollectOutputsToEscrow(order, stage, completedOutput))
                {
                    // The output has left the queue but cannot be identified. Treat those
                    // completed batches as spent instead of refunding their ingredients.
                    remainingBatches = queuedRemainingBatches;
                }
                remainingBatches = Math.Min(stage.Node.Batches, queuedRemainingBatches);
                stage.QueueStack.ClearRecipe();
                stage.QueueStack.IsCrafting = false;
                stage.QueueStack.IsDirty = true;
                stage.Queue.IsDirty = true;
                if (stage.WorkstationGroup?.WorkstationData != null) stage.WorkstationGroup.syncTEfromUI();
                NetworkLog.Debug("Crafting-chain removed its registered vanilla queue task. player=" + order.player.entityId
                    + "; recipe=" + stage.SourceRecipe.GetName() + "; completedBatches=" + completedBatches + "; reason=" + reason);
            }
            else if (stage.QueueSubmitted)
            {
                bool recovered = CollectOutputsToEscrow(order, stage, stage.ExpectedOutput);
                remainingBatches = 0;
                if (!recovered)
                    NetworkLog.Error("Crafting-chain could not identify a missing player queue task; inputs were not refunded to avoid duplication. player="
                        + order.player.entityId + "; recipe=" + stage.SourceRecipe.GetName() + "; expectedOutput=" + stage.ExpectedOutput);
            }

            List<ItemStack> networkRefundable;
            List<ItemStack> refundable = GetReservedInputsForBatches(stage, remainingBatches, out networkRefundable);
            if (stage.UsesForgeMaterials)
                AddEscrowItems(order, RestoreForgeInputs(stage, refundable));
            else
            {
                AddEscrowItems(order, refundable);
                AddNetworkEscrowItems(order, networkRefundable);
            }
            stage.ReservedInputs.Clear();
            stage.ReservedNetworkInputs.Clear();
            stage.IsActive = false;
            }
            catch (Exception ex)
            {
                NetworkLog.Error("Crafting-chain could not safely stop its registered queue task. player=" + order.player.entityId
                    + "; recipe=" + stage.SourceRecipe.GetName() + "; reason=" + reason + "; exception=" + ex);
            }
        }
    }

    private static bool DeliverRootOutput(Order order)
    {
        if (order.DeliverableRootOutput <= 0) return true;
        if (UnityEngine.Time.realtimeSinceStartup < order.NextOutputAttempt) return false;
        long available = order.EscrowItems.Where(item => item != null && !item.IsEmpty()
            && NetworkItemMatcher.Matches(item.itemValue, order.RootOutputValue)).Sum(item => (long)item.count);
        if (available < order.DeliverableRootOutput)
        {
            FailOrder(order, "HyperdimensionalNetworkCraftingChainOutputLost", "planned-root-output-not-in-private-escrow-" + available
                + "-of-" + order.DeliverableRootOutput);
            return false;
        }
        ItemStack output = new ItemStack(order.RootOutputValue.Clone(), order.DeliverableRootOutput);
        if (!order.xui.PlayerInventory.AddItemNoPartial(output, _playCollectSound: false))
        {
            order.Status = Localization.Get("HyperdimensionalNetworkCraftingChainOutputSpace");
            order.NextOutputAttempt = UnityEngine.Time.realtimeSinceStartup + 0.5f;
            return false;
        }
        ConsumeEscrowItems(order, order.RootOutputValue, order.DeliverableRootOutput);
        NetworkLog.Debug("Crafting-chain target output delivered to its owner. player=" + order.player.entityId
            + "; item=" + order.RootOutputValue.type + "; count=" + order.DeliverableRootOutput);
        order.DeliverableRootOutput = 0;
        return true;
    }

    private static void ConsumeEscrowItems(Order order, ItemValue requirement, int count)
    {
        int remaining = count;
        foreach (ItemStack item in order.EscrowItems.Where(item => item != null && !item.IsEmpty()
            && NetworkItemMatcher.Matches(item.itemValue, requirement)).ToList())
        {
            if (remaining <= 0) break;
            int networkCount = GetExactCount(order.NetworkEscrowItems, item.itemValue);
            int physicalCount = Math.Max(0, item.count - networkCount);
            int takePhysical = Math.Min(remaining, physicalCount);
            TakeExactFromList(order.EscrowItems, item.itemValue, takePhysical, null);
            remaining -= takePhysical;
        }
        foreach (ItemStack item in order.NetworkEscrowItems.Where(item => item != null && !item.IsEmpty()
            && NetworkItemMatcher.Matches(item.itemValue, requirement)).ToList())
        {
            if (remaining <= 0) break;
            int take = Math.Min(remaining, item.count);
            int takenNetwork = TakeExactFromList(order.NetworkEscrowItems, item.itemValue, take, null);
            int takenTotal = TakeExactFromList(order.EscrowItems, item.itemValue, takenNetwork, null);
            remaining -= Math.Min(takenNetwork, takenTotal);
        }
    }

    private static void TryReturnEscrow(Order order)
    {
        if (order == null || order.ReturnInFlight) return;
        if (order.EscrowItems.Count == 0)
        {
            FinishOrder(order);
            return;
        }
        if (!NetworkRemoteClient.IsRemoteClient)
        {
            List<ItemStack> items = order.EscrowItems.Where(item => item != null && !item.IsEmpty())
                .Select(item => item.Clone()).ToList();
            List<StorageAddition> additions = items.Select(item => new StorageAddition { Value = item.itemValue.Clone(), Count = item.count }).ToList();
            if (!NetworkStorage.AddBatch(order.player, additions))
            {
                order.Status = Localization.Get("HyperdimensionalNetworkCraftingChainReturnFailed");
                order.ReturnPending = true;
                order.NextReturnAttempt = UnityEngine.Time.realtimeSinceStartup + 5f;
                NetworkLog.Error("Crafting-chain escrow could not be returned to the local network. player=" + order.player.entityId + "; variants=" + items.Count);
                return;
            }
            order.EscrowItems.Clear();
            FinishOrder(order);
            return;
        }
        if (order.NetworkEscrowItems.Any(item => item != null && !item.IsEmpty()))
        {
            TryReturnNetworkEscrow(order);
            return;
        }

        TryReturnPlayerEscrow(order);
    }

    private static void TryReturnNetworkEscrow(Order order)
    {
        if (order.ReservationRequestId <= 0)
        {
            NetworkLog.Error("Crafting-chain network escrow has no matching consumption authorization; returning it to the owner instead. player="
                + order.player.entityId + "; items=" + order.NetworkEscrowItems.Count);
            order.NetworkEscrowItems.Clear();
            TryReturnPlayerEscrow(order);
            return;
        }
        if (order.NetworkReturnChunkItems == null || order.NetworkReturnChunkItems.Count == 0)
            order.NetworkReturnChunkItems = order.NetworkEscrowItems.Where(item => item != null && !item.IsEmpty())
                .Take(64).Select(item => item.Clone()).ToList();
        if (order.NetworkReturnChunkItems.Count == 0)
        {
            order.NetworkEscrowItems.Clear();
            TryReturnPlayerEscrow(order);
            return;
        }

        order.ReturnPending = true;
        order.ReturnInFlight = true;
        order.Status = Localization.Get("HyperdimensionalNetworkCraftingChainReturning");
        order.NextReturnAttempt = UnityEngine.Time.realtimeSinceStartup + 5f;
        bool sent = NetworkRemoteClient.ReturnCraftingChainAuthorization(order.player, order.ReservationRequestId,
            order.NetworkReturnChunkItems, order.NetworkReturnRequestId, out long requestId, result =>
        {
            order.ReturnInFlight = false;
            if (!result.Success)
            {
                order.ReturnPending = true;
                order.Status = Localization.Get("HyperdimensionalNetworkCraftingChainReturnFailed");
                order.NextReturnAttempt = UnityEngine.Time.realtimeSinceStartup + 5f;
                NetworkLog.Error("Crafting-chain authorized network refund failed. player=" + order.player.entityId
                    + "; originalRequestId=" + order.ReservationRequestId + "; network=" + order.NetworkId
                    + "; error=" + result.Error);
                if (string.Equals(result.Error, "Timeout", StringComparison.Ordinal)) return;

                // An explicit rejection guarantees the original refund did not commit. The
                // originating network may have changed, or its authorization may be stale;
                // preserve the items for the owner instead of redirecting them to another network.
                List<ItemStack> fallback = order.NetworkReturnChunkItems ?? new List<ItemStack>();
                foreach (ItemStack item in fallback) RemoveNetworkEscrowItems(order, item.itemValue, item.count);
                order.NetworkReturnChunkItems = null;
                order.NetworkReturnRequestId = 0;
                order.ReturnPending = false;
                TryReturnPlayerEscrow(order);
                return;
            }

            foreach (ItemStack item in order.NetworkReturnChunkItems ?? new List<ItemStack>())
                RemoveNetworkEscrowItems(order, item.itemValue, item.count);
            order.NetworkReturnChunkItems = null;
            order.NetworkReturnRequestId = 0;
            order.ReturnPending = false;
            TryReturnEscrow(order);
        });
        if (requestId > 0) order.NetworkReturnRequestId = requestId;
        if (!sent)
        {
            order.ReturnInFlight = false;
            order.ReturnPending = true;
            order.Status = Localization.Get("HyperdimensionalNetworkCraftingChainReturnFailed");
            order.NextReturnAttempt = UnityEngine.Time.realtimeSinceStartup + 2f;
            NetworkLog.Debug("Crafting-chain authorized refund waits for another network mutation. player=" + order.player.entityId
                + "; requestId=" + order.NetworkReturnRequestId);
        }
    }

    private static void TryReturnPlayerEscrow(Order order)
    {
        if (order == null || order.ReturnInFlight) return;
        ItemStack item = order.EscrowItems.FirstOrDefault(stack => stack != null && !stack.IsEmpty());
        if (item == null)
        {
            order.ReturnPending = false;
            FinishOrder(order);
            return;
        }
        int maxCount = Math.Max(1, item.itemValue.ItemClass?.MaxCount ?? item.count);
        int count = Math.Min(item.count, maxCount);
        ItemStack returned = new ItemStack(item.itemValue.Clone(), count);
        if (!order.xui.PlayerInventory.AddItemNoPartial(returned, _playCollectSound: false))
        {
            order.ReturnPending = true;
            order.Status = Localization.Get("HyperdimensionalNetworkCraftingChainReturnInventorySpace");
            order.NextReturnAttempt = UnityEngine.Time.realtimeSinceStartup + 0.5f;
            return;
        }
        ConsumeEscrowItems(order, item.itemValue, count);
        NetworkLog.Debug("Crafting-chain personal escrow returned to its owner. player=" + order.player.entityId
            + "; item=" + item.itemValue.type + "; count=" + count);
        if (order.EscrowItems.Any(stack => stack != null && !stack.IsEmpty()))
        {
            order.ReturnPending = true;
            order.NextReturnAttempt = UnityEngine.Time.realtimeSinceStartup;
        }
        else
        {
            order.ReturnPending = false;
            FinishOrder(order);
        }
    }

    private static void RemoveNetworkEscrowItems(Order order, ItemValue value, int count)
    {
        int remaining = count;
        foreach (ItemStack item in order.NetworkEscrowItems.Where(item => item != null && !item.IsEmpty()
            && NetworkItemMatcher.Matches(item.itemValue, value)).ToList())
        {
            if (remaining <= 0) break;
            int take = Math.Min(remaining, item.count);
            int taken = TakeExactFromList(order.NetworkEscrowItems, item.itemValue, take, null);
            TakeExactFromList(order.EscrowItems, item.itemValue, taken, null);
            remaining -= taken;
        }
    }

    private static void FinishOrder(Order order)
    {
        if (order == null) return;
        order.Status = string.IsNullOrEmpty(order.TerminalStatus)
            ? Localization.Get("HyperdimensionalNetworkCraftingChainCompleted") : order.TerminalStatus;
        LastStatuses[order.player.entityId] = order.Status;
        LastElapsedSeconds[order.player.entityId] = Math.Max(0f, UnityEngine.Time.realtimeSinceStartup - order.StartedAt);
        DetachTrackingHandler(order);
        Orders.Remove(order.player.entityId);
        if (order.Status == Localization.Get("HyperdimensionalNetworkCraftingChainCompleted"))
        {
            try
            {
                GameManager.ShowTooltip(order.player, order.Status, string.Empty, "ui_challenge_complete");
                NetworkLog.Debug("Crafting-chain completion toast queued. player=" + order.player.entityId);
            }
            catch (Exception exception)
            {
                NetworkLog.Error("Crafting-chain completion toast failed. player=" + order.player.entityId
                    + "; exception=" + exception);
            }
        }
        NetworkLog.Debug("Crafting-chain order closed. player=" + order.player.entityId + "; root=" + order.RootRecipeKey
            + "; steps=" + order.Stages.Count + "; terminal=" + order.Status);
    }
}
