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
        public bool HasUpdateBaseline;
        public int SuppressDepth;
        public bool SettingsLoaded;
        public bool SettingsPending;
        public bool AutoRestockEnabled;
        public bool ReplenishmentEnabled = true;
        public bool AmmoBankEnabled = true;
        public bool TraderEnabled = true;
        public bool CraftingChainEnabled = true;
        public bool AutoDepositEnabled = true;
        public bool TrashEnabled = true;
        public bool BookReadingEnabled = true;
        public bool RetainSearchOnClose = true;
        public bool ManualSaleEnabled;
        public bool PersonalAutoDeposit = true;
        public bool PersonalAutoTrash = true;
        public TrashScope PersonalTrashScope = TrashScope.Backpack;
        public bool PersonalAutoRead = true;
        public ReadBookPolicy ReadBookPolicy = ReadBookPolicy.Sell;
        public RestockTargetMode TargetMode;
        public bool IsAdmin;
        public int CooldownMs = 1000;
        public float NextSettingsRequestAt;
        public float NextAutomaticRestockAt;
        public bool AutomaticRequestPending;
        public bool PendingAutomaticScan;
        public bool AuthorizedAutomaticScan;
        public bool WaitingForMutationLogged;
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
        if (NetworkMembershipClient.IsPreparing) return;
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

        // Some vanilla inventory paths change a stack without raising SlotChanged.
        // Compare the small player grids once per update so those changes can still trigger restock.
        if (!state.HasUpdateBaseline)
        {
            Capture(state);
            state.HasUpdateBaseline = true;
        }
        else
        {
            ScanGridForChanges(state, toolbelt: true);
            ScanGridForChanges(state, toolbelt: false);
        }

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

    internal static void OnNetworkIdentityChanged(EntityPlayerLocal player)
    {
        if (!(bool)player) return;
        PlayerState state = GetState(player);
        state.SettingsLoaded = false;
        state.SettingsPending = false;
        state.NextSettingsRequestAt = 0f;
        state.AutomaticRequestPending = false;
        state.PendingAutomaticScan = false;
        state.AuthorizedAutomaticScan = false;
        RefreshBaseline(player);
        if (NetworkRemoteClient.IsRemoteClient) RequestSettingsIfNeeded(state, force: true);
        else ApplySettings(state, RestockSettingsService.Get(player));
        NetworkLog.Debug("Automatic restock settings and inventory baselines reset after network identity changed. player=" + player.entityId);
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
        if (state.SuppressDepth > 0 || !HasStackCountChanged(previous, current)) return;
        NetworkLog.Debug("Player inventory slot changed. player=" + state.Player.entityId + "; toolbelt=" + toolbelt + "; slot=" + index
            + "; countBefore=" + GetStackCount(previous) + "; countAfter=" + GetStackCount(current)
            + "; autoEnabled=" + state.AutoRestockEnabled + "; remote=" + NetworkRemoteClient.IsRemoteClient);

        if (!state.SettingsLoaded)
        {
            if (NetworkRemoteClient.IsRemoteClient) RequestSettingsIfNeeded(state, force: false);
            NetworkLog.Debug("Automatic restock slot event ignored until player settings are loaded. player=" + state.Player.entityId + "; toolbelt=" + toolbelt + "; slot=" + index);
            return;
        }
        if (!state.AutoRestockEnabled || !state.ReplenishmentEnabled) return;

        if (NetworkRemoteClient.IsRemoteClient)
        {
            if (NetworkRemoteClient.IsAuthorizedConsumption(state.Player))
                state.AuthorizedAutomaticScan = true;
            else
            {
                state.PendingAutomaticScan = true;
            }
            NetworkLog.Debug("Automatic restock scan queued after an inventory count change. player=" + state.Player.entityId
                + "; changedToolbelt=" + toolbelt + "; changedSlot=" + index
                + "; authorized=" + NetworkRemoteClient.IsAuthorizedConsumption(state.Player));
            return;
        }

        state.PendingAutomaticScan = true;
        NetworkLog.Debug("Local automatic restock scan queued after an inventory count change. player=" + state.Player.entityId
            + "; changedToolbelt=" + toolbelt + "; changedSlot=" + index);
    }

    private static void ProcessAutomaticRestock(PlayerState state)
    {
        if (!state.SettingsLoaded || !state.AutoRestockEnabled || !state.ReplenishmentEnabled)
        {
            ClearPending(state);
            return;
        }
        if (state.AutomaticRequestPending) return;

        if (!state.PendingAutomaticScan && !state.AuthorizedAutomaticScan) return;
        if (NetworkRemoteClient.IsRemoteClient && state.AuthorizedAutomaticScan && NetworkRemoteClient.IsAuthorizedConsumption(state.Player)) return;

        if (NetworkRemoteClient.IsRemoteClient && NetworkRemoteClient.IsMutationPending)
        {
            if (!state.WaitingForMutationLogged)
            {
                NetworkLog.Debug("Automatic restock scan waits for the pending inventory transaction to finish. player=" + state.Player.entityId
                    + "; authorized=" + state.AuthorizedAutomaticScan);
                state.WaitingForMutationLogged = true;
            }
            return;
        }
        state.WaitingForMutationLogged = false;

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < state.NextAutomaticRestockAt)
        {
            ClearPending(state);
            NetworkLog.Debug("Automatic restock targets dropped during cooldown. player=" + state.Player.entityId + "; remainingMs="
                + Math.Max(0, (int)((state.NextAutomaticRestockAt - now) * 1000f)));
            return;
        }

        GetConfiguredTargetSlots(state, out List<int> toolbelt, out List<int> backpack);
        ClearPending(state);
        if (toolbelt.Count == 0 && backpack.Count == 0) return;
        NetworkLog.Debug("Automatic restock scanning configured target slots after inventory activity. player=" + state.Player.entityId
            + "; mode=" + state.TargetMode + "; toolbeltSlots=" + toolbelt.Count + "; lockedBackpackSlots=" + backpack.Count);

        if (NetworkRemoteClient.IsRemoteClient)
        {
            state.AutomaticRequestPending = true;
            bool sent = NetworkRemoteClient.RequestRestock(state.Player, automatic: true, toolbelt, backpack,
                result =>
                {
                    state.AutomaticRequestPending = false;
                    if (result == null || !result.Success)
                    {
                        NetworkLog.Debug("Remote automatic restock did not complete. player=" + state.Player.entityId + "; requestId=" + (result?.RequestId ?? 0)
                            + "; error=" + (result?.Error ?? "NoResult"));
                        return;
                    }
                    if (result.Amount > 0)
                        state.NextAutomaticRestockAt = UnityEngine.Time.realtimeSinceStartup + Math.Max(0, state.CooldownMs) / 1000f;
                    ApplyRemoteResult(state.Player, result);
                });
            if (sent)
            {
                NetworkLog.Debug("Automatic restock request sent. player=" + state.Player.entityId + "; toolbeltSlots=" + toolbelt.Count + "; backpackSlots=" + backpack.Count);
            }
            else
            {
                state.AutomaticRequestPending = false;
                NetworkLog.Debug("Automatic restock targets dropped because the request could not be sent. player=" + state.Player.entityId);
            }
            return;
        }

        NetworkRemoteRequest request = NewLocalRequest(state.Player, NetworkRequestOperation.AutomaticRestock);
        request.RestockToolbeltSlots = toolbelt;
        request.RestockBackpackSlots = backpack;
        NetworkRemoteResult localResult = RestockTransactionService.Execute(GameManager.Instance?.World, state.Player, null, request);
        if (!localResult.Success) NetworkLog.Debug("Local automatic restock did not complete. player=" + state.Player.entityId + "; error=" + localResult.Error);
        else
        {
            if (localResult.Amount > 0)
            {
                state.NextAutomaticRestockAt = UnityEngine.Time.realtimeSinceStartup + Math.Max(0, state.CooldownMs) / 1000f;
                RefreshInventoryViews(state.Player);
            }
            NetworkLog.Debug("Local automatic restock completed. player=" + state.Player.entityId + "; amount=" + localResult.Amount);
        }
    }

    private static void ClearPending(PlayerState state)
    {
        state.PendingAutomaticScan = false;
        state.AuthorizedAutomaticScan = false;
        state.WaitingForMutationLogged = false;
    }

    private static void GetConfiguredTargetSlots(PlayerState state, out List<int> toolbelt, out List<int> backpack)
    {
        toolbelt = state.TargetMode == RestockTargetMode.LockedBackpackOnly
            ? new List<int>()
            : Enumerable.Range(0, state.ToolbeltGrid?.Length ?? 0).ToList();
        backpack = new List<int>();
        if (state.TargetMode == RestockTargetMode.ToolbeltOnly) return;

        PackedBoolArray lockedSlots = state.Player?.bag?.LockedSlots;
        int slotCount = state.BackpackGrid?.Length ?? 0;
        for (int index = 0; index < slotCount; index++)
            if (lockedSlots != null && index < lockedSlots.Length && lockedSlots[index]) backpack.Add(index);
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

    private static void ScanGridForChanges(PlayerState state, bool toolbelt)
    {
        ItemStackGrid grid = toolbelt ? state.ToolbeltGrid : state.BackpackGrid;
        ItemStack[] snapshot = toolbelt ? state.ToolbeltSnapshot : state.BackpackSnapshot;
        if (grid == null || snapshot == null || snapshot.Length != grid.Length) return;
        for (int index = 0; index < grid.Length; index++)
        {
            ItemStack current = grid[index];
            if (!HasStackChanged(snapshot[index], current)) continue;
            NetworkLog.Debug("Automatic restock detected an inventory change without a slot event. player=" + state.Player.entityId
                + "; toolbelt=" + toolbelt + "; slot=" + index + "; before=" + (snapshot[index]?.count ?? 0)
                + "; after=" + (current?.count ?? 0) + "; item=" + (current?.itemValue?.type ?? 0));
            OnSlotChanged(state, toolbelt, index);
        }
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

    private static bool HasStackCountChanged(ItemStack before, ItemStack after) => GetStackCount(before) != GetStackCount(after);

    private static int GetStackCount(ItemStack stack) => stack == null || stack.IsEmpty() ? 0 : Math.Max(0, stack.count);

    private static void RequestSettingsIfNeeded(PlayerState state, bool force)
    {
        if (!NetworkRemoteClient.IsRemoteClient || state.SettingsPending) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (!force && now < state.NextSettingsRequestAt) return;
        state.NextSettingsRequestAt = now + (state.SettingsLoaded ? 5f : 2f);
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
        state.ReplenishmentEnabled = result.ReplenishmentEnabled;
        state.AmmoBankEnabled = result.AmmoBankEnabled;
        state.TraderEnabled = result.TraderEnabled;
        state.CraftingChainEnabled = result.CraftingChainEnabled;
        state.AutoDepositEnabled = result.AutoDepositEnabled;
        state.TrashEnabled = result.TrashEnabled;
        state.BookReadingEnabled = result.BookReadingEnabled;
        state.RetainSearchOnClose = result.RetainSearchOnClose;
        state.ManualSaleEnabled = result.ManualSaleEnabled;
        state.PersonalAutoDeposit = result.PersonalAutoDeposit;
        state.PersonalAutoTrash = result.PersonalAutoTrash;
        state.PersonalTrashScope = result.PersonalTrashScope;
        state.PersonalAutoRead = result.PersonalAutoRead;
        state.ReadBookPolicy = result.ReadBookPolicy;
        state.TargetMode = result.RestockTargetMode;
        state.IsAdmin = result.IsNetworkAdministrator;
        state.CooldownMs = Math.Max(0, Math.Min(60000, result.AutoRestockCooldownMs));
        state.SettingsLoaded = true;
    }

    private static void ApplySettings(PlayerState state, RestockSettingsSnapshot settings)
    {
        state.AutoRestockEnabled = settings.AutoRestockEnabled;
        state.ReplenishmentEnabled = settings.ReplenishmentEnabled;
        state.AmmoBankEnabled = settings.AmmoBankEnabled;
        state.TraderEnabled = settings.TraderEnabled;
        state.CraftingChainEnabled = settings.CraftingChainEnabled;
        state.AutoDepositEnabled = settings.AutoDepositEnabled;
        state.TrashEnabled = settings.TrashEnabled;
        state.BookReadingEnabled = settings.BookReadingEnabled;
        state.RetainSearchOnClose = settings.RetainSearchOnClose;
        state.ManualSaleEnabled = settings.ManualSaleEnabled;
        state.PersonalAutoDeposit = settings.PersonalAutoDeposit;
        state.PersonalAutoTrash = settings.PersonalAutoTrash;
        state.PersonalTrashScope = settings.PersonalTrashScope;
        state.PersonalAutoRead = settings.PersonalAutoRead;
        state.ReadBookPolicy = settings.ReadBookPolicy;
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
            ReplenishmentEnabled = state.ReplenishmentEnabled,
            AmmoBankEnabled = state.AmmoBankEnabled,
            TraderEnabled = state.TraderEnabled,
            CraftingChainEnabled = state.CraftingChainEnabled,
            AutoDepositEnabled = state.AutoDepositEnabled,
            TrashEnabled = state.TrashEnabled,
            BookReadingEnabled = state.BookReadingEnabled,
            RetainSearchOnClose = state.RetainSearchOnClose,
            ManualSaleEnabled = state.ManualSaleEnabled,
            PersonalAutoDeposit = state.PersonalAutoDeposit,
            PersonalAutoTrash = state.PersonalAutoTrash,
            PersonalTrashScope = state.PersonalTrashScope,
            PersonalAutoRead = state.PersonalAutoRead,
            ReadBookPolicy = state.ReadBookPolicy,
            TargetMode = state.TargetMode,
            IsNetworkAdministrator = state.IsAdmin,
            AutoRestockCooldownMs = state.CooldownMs
        };
    }

    public static bool IsAmmoBankEnabled(EntityPlayerLocal player)
    {
        if (!(bool)player) return false;
        if (!NetworkRemoteClient.IsRemoteClient) return RestockSettingsService.Get(player).AmmoBankEnabled;
        PlayerState state = GetState(player);
        return state.SettingsLoaded && state.AmmoBankEnabled;
    }

    public static bool IsFeatureEnabled(EntityPlayerLocal player, NetworkFeatureSetting key)
    {
        if (!(bool)player) return false;
        if (!NetworkRemoteClient.IsRemoteClient) return NetworkFeatureSettingsService.IsEnabled(player, key);
        PlayerState state = GetState(player);
        if (!state.SettingsLoaded) return false;
        return key switch
        {
            NetworkFeatureSetting.TraderEnabled => state.TraderEnabled,
            NetworkFeatureSetting.CraftingChainEnabled => state.CraftingChainEnabled,
            NetworkFeatureSetting.AutoDepositEnabled => state.AutoDepositEnabled,
            NetworkFeatureSetting.TrashEnabled => state.TrashEnabled,
            NetworkFeatureSetting.BookReadingEnabled => state.BookReadingEnabled,
            NetworkFeatureSetting.ReplenishmentEnabled => state.ReplenishmentEnabled,
            NetworkFeatureSetting.AmmoBankEnabled => state.AmmoBankEnabled,
            _ => false
        };
    }

    public static bool ShouldRetainSearchOnClose(EntityPlayerLocal player)
    {
        if (!(bool)player) return true;
        if (!NetworkRemoteClient.IsRemoteClient) return RestockSettingsService.GetRetainSearchOnClose(player);
        PlayerState state = GetState(player);
        return !state.SettingsLoaded || state.RetainSearchOnClose;
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
