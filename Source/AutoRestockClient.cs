using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace HyperdimensionalNetwork;

internal static class AutoRestockClient
{
    private sealed class PlayerState
    {
        public EntityPlayerLocal Player;
        public ItemStackGrid ToolbeltGrid;
        public ItemStackGrid BackpackGrid;
        public Action<int> ToolbeltChangedHandler;
        public Action<int> BackpackChangedHandler;
        public ItemStack[] ToolbeltSnapshot;
        public ItemStack[] BackpackSnapshot;
        public bool HasSnapshot;
        public int SuppressDepth;
        public bool SettingsLoaded;
        public bool SettingsPending;
        public bool AutoRestockEnabled;
        public RestockTargetMode TargetMode;
        public bool IsAdmin;
        public int CooldownMs = 1000;
        public float NextSettingsRequestAt;
        public float NextAutomaticRestockAt;
        public readonly HashSet<int> PendingToolbeltSlots = new HashSet<int>();
        public readonly HashSet<int> PendingBackpackSlots = new HashSet<int>();
        public readonly HashSet<int> AuthorizedToolbeltSlots = new HashSet<int>();
        public readonly HashSet<int> AuthorizedBackpackSlots = new HashSet<int>();
    }

    private sealed class SuppressScope : IDisposable
    {
        private PlayerState _state;
        public SuppressScope(PlayerState state) { _state = state; }
        public void Dispose()
        {
            PlayerState state = _state;
            _state = null;
            if (state == null) return;
            state.SuppressDepth = Math.Max(0, state.SuppressDepth - 1);
        }
    }

    private static readonly Dictionary<int, PlayerState> Players = new Dictionary<int, PlayerState>();
    private static long _localRequestId;

    public static void Initialize(XUiM_PlayerInventory inventory)
    {
        EntityPlayerLocal player = inventory?.localPlayer;
        if (!(bool)player) return;
        PlayerState state = GetState(player);
        BindGrid(state, toolbelt: true, player.inventory.ItemGrid);
        BindGrid(state, toolbelt: false, player.bag.ItemGrid);
        state.HasSnapshot = true;
        if (NetworkRemoteClient.IsRemoteClient) RequestSettingsIfNeeded(state, force: true);
        else ApplySettings(state, RestockSettingsService.Get(player));
        NetworkLog.Debug("Automatic restock slot observers initialized. player=" + player.entityId + "; toolbeltSlots=" + (state.ToolbeltSnapshot?.Length ?? 0)
            + "; backpackSlots=" + (state.BackpackSnapshot?.Length ?? 0));
    }

    public static void Update()
    {
        EntityPlayerLocal player = NetworkRemoteClient.GetLocalPlayer();
        if (!(bool)player) return;
        PlayerState state = GetState(player);
        if (!ReferenceEquals(state.ToolbeltGrid, player.inventory.ItemGrid) || !ReferenceEquals(state.BackpackGrid, player.bag.ItemGrid))
        {
            BindGrid(state, toolbelt: true, player.inventory.ItemGrid);
            BindGrid(state, toolbelt: false, player.bag.ItemGrid);
            state.HasSnapshot = true;
            NetworkLog.Debug("Automatic restock observers rebound after inventory grids changed. player=" + player.entityId);
        }
        if (!state.HasSnapshot)
        {
            Capture(state);
            state.HasSnapshot = true;
            NetworkLog.Debug("Automatic restock baseline captured after player inventory became available. player=" + player.entityId);
        }
        if (NetworkRemoteClient.IsRemoteClient) RequestSettingsIfNeeded(state, force: false);
        else if (!state.SettingsLoaded) ApplySettings(state, RestockSettingsService.Get(player));

        ProcessAutomaticRestock(state);
    }

    public static IDisposable SuppressInventoryEvents(EntityPlayerLocal player)
    {
        if (!(bool)player) return new SuppressScope(null);
        PlayerState state = GetState(player);
        state.SuppressDepth++;
        return new SuppressScope(state);
    }

    public static void RefreshBaseline(EntityPlayerLocal player)
    {
        if (!(bool)player) return;
        PlayerState state = GetState(player);
        BindGrid(state, toolbelt: true, player.inventory.ItemGrid);
        BindGrid(state, toolbelt: false, player.bag.ItemGrid);
        Capture(state);
        state.HasSnapshot = true;
    }

    public static void OnInventorySnapshotApplied(EntityPlayerLocal player, bool backpackChanged, bool toolbeltChanged)
    {
        if (!(bool)player) return;
        PlayerState state = GetState(player);
        if (!ReferenceEquals(state.ToolbeltGrid, player.inventory.ItemGrid) || !ReferenceEquals(state.BackpackGrid, player.bag.ItemGrid))
        {
            BindGrid(state, toolbelt: true, player.inventory.ItemGrid);
            BindGrid(state, toolbelt: false, player.bag.ItemGrid);
            state.HasSnapshot = true;
            NetworkLog.Debug("Automatic restock ignored a snapshot while rebinding inventory grids. player=" + player.entityId);
            return;
        }
        if (!state.HasSnapshot)
        {
            Capture(state);
            state.HasSnapshot = true;
            NetworkLog.Debug("Automatic restock ignored the initial synchronized inventory snapshot. player=" + player.entityId);
            return;
        }

        if (toolbeltChanged)
            for (int i = 0; i < state.ToolbeltGrid.Length; i++) OnSlotChanged(state, toolbelt: true, i);
        if (backpackChanged)
            for (int i = 0; i < state.BackpackGrid.Length; i++) OnSlotChanged(state, toolbelt: false, i);
    }

    public static void GetSettings(EntityPlayerLocal player, Action<RestockSettingsSnapshot, string> callback)
    {
        if (!(bool)player) { callback?.Invoke(null, "PlayerUnavailable"); return; }
        if (NetworkRemoteClient.IsRemoteClient)
        {
            PlayerState state = GetState(player);
            bool sent = NetworkRemoteClient.RequestRestockSettings(player, result =>
            {
                state.SettingsPending = false;
                if (!result.Success || !result.RestockSettingsLoaded)
                {
                    callback?.Invoke(null, result.Error ?? "SettingsUnavailable");
                    return;
                }
                ApplySettings(state, result);
                callback?.Invoke(ToSnapshot(state), string.Empty);
            });
            state.SettingsPending = sent;
            if (!sent) callback?.Invoke(null, "RequestUnavailable");
            return;
        }
        if (!RestockSettingsService.TryGet(player, out RestockSettingsSnapshot snapshot, out string error))
        {
            callback?.Invoke(null, error);
            return;
        }
        PlayerState localState = GetState(player);
        ApplySettings(localState, snapshot);
        callback?.Invoke(snapshot, string.Empty);
    }

    public static bool SetSetting(EntityPlayerLocal player, RestockSettingKey key, int value, Action<RestockSettingsSnapshot, string> callback)
    {
        if (!(bool)player || RestockSettingCatalog.Get(key) == null) return false;
        if (NetworkRemoteClient.IsRemoteClient)
        {
            return NetworkRemoteClient.SetRestockSetting(player, key, value, result =>
            {
                PlayerState state = GetState(player);
                if (!result.Success || !result.RestockSettingsLoaded)
                {
                    callback?.Invoke(null, result.Error ?? "SettingUpdateFailed");
                    return;
                }
                ApplySettings(state, result);
                callback?.Invoke(ToSnapshot(state), string.Empty);
            });
        }

        if (!RestockSettingsService.TrySet(player, key, value, out string error, out RestockSettingsSnapshot snapshot))
        {
            callback?.Invoke(null, error);
            return false;
        }
        PlayerState localState = GetState(player);
        ApplySettings(localState, snapshot);
        callback?.Invoke(snapshot, string.Empty);
        return true;
    }

    public static bool RequestManual(EntityPlayerLocal player, Action<NetworkRemoteResult> callback)
    {
        if (!(bool)player) return false;
        if (NetworkRemoteClient.IsRemoteClient)
        {
            return NetworkRemoteClient.RequestRestock(player, automatic: false, null, null, result =>
            {
                ApplyRemoteResult(player, result);
                callback?.Invoke(result);
            });
        }
        NetworkRemoteRequest request = NewLocalRequest(player, NetworkRequestOperation.ManualRestock);
        NetworkRemoteResult localResult = RestockTransactionService.Execute(GameManager.Instance?.World, player, null, request);
        if (localResult.Success && localResult.Amount > 0) RefreshInventoryViews(player);
        callback?.Invoke(localResult);
        return localResult.Success;
    }

    private static void BindGrid(PlayerState state, bool toolbelt, ItemStackGrid grid)
    {
        ItemStackGrid oldGrid = toolbelt ? state.ToolbeltGrid : state.BackpackGrid;
        Action<int> oldHandler = toolbelt ? state.ToolbeltChangedHandler : state.BackpackChangedHandler;
        if (ReferenceEquals(oldGrid, grid)) return;
        if (oldGrid != null && oldHandler != null) oldGrid.SlotChanged -= oldHandler;

        Action<int> handler = grid == null ? null : new Action<int>(index => OnSlotChanged(state, toolbelt, index));
        if (grid != null && handler != null) grid.SlotChanged += handler;
        ItemStack[] snapshot = CaptureGrid(grid);
        if (toolbelt)
        {
            state.ToolbeltGrid = grid;
            state.ToolbeltChangedHandler = handler;
            state.ToolbeltSnapshot = snapshot;
        }
        else
        {
            state.BackpackGrid = grid;
            state.BackpackChangedHandler = handler;
            state.BackpackSnapshot = snapshot;
        }
    }

    private static void OnSlotChanged(PlayerState state, bool toolbelt, int index)
    {
        if (state == null || !(bool)state.Player) return;
        ItemStackGrid grid = toolbelt ? state.ToolbeltGrid : state.BackpackGrid;
        ItemStack[] snapshot = toolbelt ? state.ToolbeltSnapshot : state.BackpackSnapshot;
        if (grid == null || index < 0 || index >= grid.Length) return;
        if (snapshot == null || snapshot.Length != grid.Length)
        {
            BindGrid(state, toolbelt, grid);
            state.HasSnapshot = true;
            NetworkLog.Debug("Automatic restock ignored a slot event while rebuilding its inventory baseline. player=" + state.Player.entityId + "; toolbelt=" + toolbelt);
            return;
        }

        ItemStack current = CloneStack(grid[index]);
        ItemStack previous = snapshot[index];
        snapshot[index] = current;
        state.HasSnapshot = true;
        if (state.SuppressDepth > 0 || !HasStackChanged(previous, current)) return;
        NetworkLog.Debug("Player inventory slot changed. player=" + state.Player.entityId + "; toolbelt=" + toolbelt + "; slot=" + index
            + "; autoEnabled=" + state.AutoRestockEnabled + "; remote=" + NetworkRemoteClient.IsRemoteClient);

        if (!state.SettingsLoaded)
        {
            if (NetworkRemoteClient.IsRemoteClient) RequestSettingsIfNeeded(state, force: false);
            NetworkLog.Debug("Automatic restock slot event ignored until player settings are loaded. player=" + state.Player.entityId + "; toolbelt=" + toolbelt + "; slot=" + index);
            return;
        }
        if (!state.AutoRestockEnabled) return;

        if (NetworkRemoteClient.IsRemoteClient)
        {
            HashSet<int> targets;
            if (NetworkRemoteClient.IsAuthorizedConsumption(state.Player))
                targets = toolbelt ? state.AuthorizedToolbeltSlots : state.AuthorizedBackpackSlots;
            else
            {
                if (NetworkRemoteClient.IsMutationPending)
                {
                    NetworkLog.Debug("Automatic restock slot event ignored during another inventory transaction. player=" + state.Player.entityId + "; toolbelt=" + toolbelt + "; slot=" + index);
                    return;
                }
                targets = toolbelt ? state.PendingToolbeltSlots : state.PendingBackpackSlots;
            }
            targets.Add(index);
            NetworkLog.Debug("Automatic restock target slot recorded. player=" + state.Player.entityId + "; toolbelt=" + toolbelt + "; slot=" + index
                + "; authorized=" + NetworkRemoteClient.IsAuthorizedConsumption(state.Player));
            return;
        }

        (toolbelt ? state.PendingToolbeltSlots : state.PendingBackpackSlots).Add(index);
        NetworkLog.Debug("Local automatic restock target slot recorded. player=" + state.Player.entityId + "; toolbelt=" + toolbelt + "; slot=" + index);
    }

    private static void ProcessAutomaticRestock(PlayerState state)
    {
        if (!state.SettingsLoaded || !state.AutoRestockEnabled)
        {
            ClearPending(state);
            return;
        }

        bool hasAuthorizedTargets = state.AuthorizedToolbeltSlots.Count > 0 || state.AuthorizedBackpackSlots.Count > 0;
        if (NetworkRemoteClient.IsRemoteClient && hasAuthorizedTargets && NetworkRemoteClient.IsAuthorizedConsumption(state.Player)) return;
        if (!HasPendingTargets(state)) return;

        if (NetworkRemoteClient.IsRemoteClient && NetworkRemoteClient.IsMutationPending)
        {
            if (hasAuthorizedTargets)
            {
                NetworkLog.Debug("Automatic restock waits for the authorized material transaction refund to finish. player=" + state.Player.entityId);
                return;
            }
            state.PendingToolbeltSlots.Clear();
            state.PendingBackpackSlots.Clear();
            NetworkLog.Debug("Automatic restock targets dropped because another inventory transaction is pending. player=" + state.Player.entityId);
            return;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < state.NextAutomaticRestockAt)
        {
            ClearPending(state);
            NetworkLog.Debug("Automatic restock targets dropped during cooldown. player=" + state.Player.entityId + "; remainingMs="
                + Math.Max(0, (int)((state.NextAutomaticRestockAt - now) * 1000f)));
            return;
        }

        List<int> toolbelt = state.PendingToolbeltSlots.Concat(state.AuthorizedToolbeltSlots).OrderBy(index => index).ToList();
        List<int> backpack = state.PendingBackpackSlots.Concat(state.AuthorizedBackpackSlots).OrderBy(index => index).ToList();
        ClearPending(state);
        if (toolbelt.Count == 0 && backpack.Count == 0) return;

        if (NetworkRemoteClient.IsRemoteClient)
        {
            bool sent = NetworkRemoteClient.RequestRestock(state.Player, automatic: true, toolbelt, backpack,
                result =>
                {
                    if (!result.Success)
                    {
                        NetworkLog.Debug("Remote automatic restock did not complete. player=" + state.Player.entityId + "; requestId=" + result.RequestId + "; error=" + result.Error);
                        return;
                    }
                    ApplyRemoteResult(state.Player, result);
                });
            if (sent)
            {
                state.NextAutomaticRestockAt = now + Math.Max(0, state.CooldownMs) / 1000f;
                NetworkLog.Debug("Automatic restock request sent. player=" + state.Player.entityId + "; toolbeltSlots=" + toolbelt.Count + "; backpackSlots=" + backpack.Count);
            }
            else NetworkLog.Debug("Automatic restock targets dropped because the request could not be sent. player=" + state.Player.entityId);
            return;
        }

        state.NextAutomaticRestockAt = now + Math.Max(0, state.CooldownMs) / 1000f;
        NetworkRemoteRequest request = NewLocalRequest(state.Player, NetworkRequestOperation.AutomaticRestock);
        request.RestockToolbeltSlots = toolbelt;
        request.RestockBackpackSlots = backpack;
        NetworkRemoteResult localResult = RestockTransactionService.Execute(GameManager.Instance?.World, state.Player, null, request);
        if (!localResult.Success) NetworkLog.Debug("Local automatic restock did not complete. player=" + state.Player.entityId + "; error=" + localResult.Error);
        else
        {
            if (localResult.Amount > 0) RefreshInventoryViews(state.Player);
            NetworkLog.Debug("Local automatic restock completed. player=" + state.Player.entityId + "; amount=" + localResult.Amount);
        }
    }

    private static bool HasPendingTargets(PlayerState state) => state.PendingToolbeltSlots.Count > 0 || state.PendingBackpackSlots.Count > 0
        || state.AuthorizedToolbeltSlots.Count > 0 || state.AuthorizedBackpackSlots.Count > 0;

    private static void ClearPending(PlayerState state)
    {
        state.PendingToolbeltSlots.Clear();
        state.PendingBackpackSlots.Clear();
        state.AuthorizedToolbeltSlots.Clear();
        state.AuthorizedBackpackSlots.Clear();
    }

    private static PlayerState GetState(EntityPlayerLocal player)
    {
        if (Players.TryGetValue(player.entityId, out PlayerState state) && ReferenceEquals(state.Player, player)) return state;
        state = new PlayerState { Player = player };
        Players[player.entityId] = state;
        return state;
    }

    private static void Capture(PlayerState state)
    {
        if (!(bool)state?.Player) return;
        state.ToolbeltSnapshot = CaptureGrid(state.Player.inventory.ItemGrid);
        state.BackpackSnapshot = CaptureGrid(state.Player.bag.ItemGrid);
    }

    private static ItemStack[] CaptureGrid(ItemStackGrid grid)
    {
        ItemStack[] snapshot = new ItemStack[grid?.Length ?? 0];
        for (int i = 0; i < snapshot.Length; i++) snapshot[i] = CloneStack(grid[i]);
        return snapshot;
    }

    private static ItemStack CloneStack(ItemStack stack) => stack == null || stack.IsEmpty() ? ItemStack.Empty.Clone() : stack.Clone();

    private static bool HasStackChanged(ItemStack before, ItemStack after)
    {
        bool beforeEmpty = before == null || before.IsEmpty();
        bool afterEmpty = after == null || after.IsEmpty();
        return beforeEmpty != afterEmpty || !beforeEmpty && !afterEmpty
            && (before.count != after.count || !before.itemValue.Equals(after.itemValue));
    }

    private static void RequestSettingsIfNeeded(PlayerState state, bool force)
    {
        if (!NetworkRemoteClient.IsRemoteClient || state.SettingsPending || state.SettingsLoaded && !force) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (!force && now < state.NextSettingsRequestAt) return;
        state.NextSettingsRequestAt = now + 2f;
        bool sent = NetworkRemoteClient.RequestRestockSettings(state.Player, result =>
        {
            state.SettingsPending = false;
            if (result.Success && result.RestockSettingsLoaded) ApplySettings(state, result);
            else NetworkLog.Debug("Could not load remote restock settings. player=" + state.Player.entityId + "; error=" + result.Error);
        });
        state.SettingsPending = sent;
    }

    private static void ApplySettings(PlayerState state, NetworkRemoteResult result)
    {
        state.AutoRestockEnabled = result.AutoRestockEnabled;
        state.TargetMode = result.RestockTargetMode;
        state.IsAdmin = result.IsNetworkAdministrator;
        state.CooldownMs = Math.Max(0, Math.Min(60000, result.AutoRestockCooldownMs));
        state.SettingsLoaded = true;
    }

    private static void ApplySettings(PlayerState state, RestockSettingsSnapshot settings)
    {
        state.AutoRestockEnabled = settings.AutoRestockEnabled;
        state.TargetMode = settings.TargetMode;
        state.IsAdmin = settings.IsNetworkAdministrator;
        state.CooldownMs = Math.Max(0, Math.Min(60000, settings.AutoRestockCooldownMs));
        state.SettingsLoaded = true;
    }

    private static RestockSettingsSnapshot ToSnapshot(PlayerState state)
    {
        return new RestockSettingsSnapshot
        {
            AutoRestockEnabled = state.AutoRestockEnabled,
            TargetMode = state.TargetMode,
            IsNetworkAdministrator = state.IsAdmin,
            AutoRestockCooldownMs = state.CooldownMs
        };
    }

    private static void ApplyRemoteResult(EntityPlayerLocal player, NetworkRemoteResult result)
    {
        if (result == null || !result.Success) return;
        if (result.BagData != null || result.InventoryData != null)
        {
            if (!NetworkInventorySync.Apply(player, result, suppressRestockEvents: true))
                NetworkLog.Error("Server confirmed restock but its inventory snapshot could not be applied. player=" + player.entityId + "; requestId=" + result.RequestId);
            RefreshInventoryViews(player);
            NetworkLog.Debug("Server restock snapshot applied. player=" + player.entityId + "; requestId=" + result.RequestId + "; amount=" + result.Amount);
        }
    }

    public static void RefreshInventoryViews(EntityPlayerLocal player)
    {
        if (!(bool)player) return;
        player.playerUI?.xui?.GetChildByType<XUiC_Backpack>()?.RefreshBackpackSlots();
        player.playerUI?.xui?.GetChildByType<XUiC_Toolbelt>()?.PlayerInventory_OnToolbeltItemsChanged();
        player.playerUI?.xui?.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
        NetworkLog.Debug("Player inventory views refreshed after restock. player=" + player.entityId);
    }

    private static NetworkRemoteRequest NewLocalRequest(EntityPlayerLocal player, NetworkRequestOperation operation)
    {
        return new NetworkRemoteRequest
        {
            EntityId = player.entityId,
            RequestId = System.Threading.Interlocked.Increment(ref _localRequestId),
            Operation = operation
        };
    }
}

[HarmonyPatch]
internal static class PlayerInventoryRestockPatch
{
    [HarmonyTargetMethod]
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Constructor(typeof(XUiM_PlayerInventory), new[] { typeof(XUi), typeof(EntityPlayerLocal) });
    }

    [HarmonyPostfix]
    private static void ConstructorPostfix(XUiM_PlayerInventory __instance) => AutoRestockClient.Initialize(__instance);
}
