using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal enum NetworkRequestOperation : byte
{
    QueryPage = 1,
    DepositCursor = 2,
    DepositBackpackSlot = 3,
    DepositToolbeltSlot = 4,
    DepositBackpackAll = 5,
    DepositLootAll = 6,
    WithdrawCursor = 7,
    WithdrawBackpack = 8,
    ReturnWithdrawal = 9,
    QueryMatching = 10,
    ConsumeRequirements = 11,
    VehicleRefuel = 12,
    VehicleRepair = 13,
    ReturnAuthorization = 14,
    QueryRestockSettings = 15,
    SetRestockSetting = 16,
    ManualRestock = 17,
    AutomaticRestock = 18,
    AmmoBankReload = 19,
    AmmoBankRefund = 20,
    TraderVoidSell = 21,
    TraderOpenMarket = 22,
    TraderQueryMarket = 23,
    TraderCloseMarket = 24,
    TraderBuyItem = 25,
    TraderQueryRules = 26,
    TraderSaveRule = 27,
    TraderDeleteRule = 28,
    TraderMoveRule = 29,
    TraderRunRules = 30,
    MembershipQuery = 31,
    MembershipInvite = 32,
    MembershipAcceptInvite = 33,
    MembershipDeclineInvite = 34,
    MembershipLeave = 35,
    MembershipKick = 36,
    MembershipTransferAdministrator = 37,
    MembershipAcknowledgePrepare = 38,
    DepositEntityBagAll = 39,
    AutomationProcessSlot = 40,
    TrashOpen = 41,
    TrashPut = 42,
    TrashTake = 43,
    TrashClose = 44,
    BookConsumeNetwork = 45,
    BookScrapNetwork = 46
}

internal enum NetworkMembershipNotice : byte { None = 0, Invite = 1, Prepare = 2, Completed = 3, Changed = 4, InviteInvalidated = 5 }

internal sealed class NetworkMemberInfo
{
    public string PlayerId = string.Empty;
    public string DisplayName = string.Empty;
    public bool IsOnline;
    public bool IsAdministrator;
    public bool Eligible;
    public string EligibilityError = string.Empty;
}

internal sealed class TraderOffer
{
    public ItemValue Value;
    public int Count;
    public int BundleSize;
    public int BundlePrice;
    public int TraderEntityId;
    public int InventoryIndex;
}

internal sealed class TrashSlotEntry
{
    public int Slot;
    public ItemValue Value;
    public int Count;
}

internal enum NetworkConsumptionSource : byte
{
    PlayerInventory = 0,
    WorkstationInput = 1,
    NetworkPreferredPlayerInventory = 2,
    CraftingChainReservation = 3
}

internal sealed class NetworkRemoteResult
{
    public bool Success;
    public string Error = string.Empty;
    public long RequestId;
    public long Revision;
    public string NetworkId = string.Empty;
    public int TotalEntries;
    public int Page;
    public int PageSize;
    public int Amount;
    public int UpdatedSourceCount;
    public ItemValue Value;
    public List<StoredEntry> Entries = new List<StoredEntry>();
    public List<TrashSlotEntry> TrashSlots = new List<TrashSlotEntry>();
    public byte[] BagData;
    public byte[] InventoryData;
    public byte[] EntityBagData;
    public bool RestockSettingsLoaded;
    public bool AutoRestockEnabled;
    public RestockTargetMode RestockTargetMode;
    public bool IsNetworkAdministrator;
    public int AutoRestockCooldownMs;
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
    public string TraderSessionId = string.Empty;
    public List<TraderOffer> TraderOffers = new List<TraderOffer>();
    public List<TraderAutomationRule> TraderRules = new List<TraderAutomationRule>();
    public bool TraderRunProgressAvailable;
    public bool TraderRunActive;
    public int TraderRunCurrentRule;
    public int TraderRunTotalRules;
    public string TraderRunMessage = string.Empty;
    public string TraderRunRuleId = string.Empty;
    public NetworkMembershipNotice MembershipNotice;
    public long MembershipRevision;
    public string MembershipOperationId = string.Empty;
    public string MembershipTargetPlayerId = string.Empty;
    public string MembershipSourceNetworkId = string.Empty;
    public string MembershipTargetNetworkId = string.Empty;
    public string MembershipInviterName = string.Empty;
    public bool MembershipWillMerge;
    public int MembershipOnlineCount;
    public List<NetworkMemberInfo> NetworkMembers = new List<NetworkMemberInfo>();
    public List<NetworkMemberInfo> MembershipCandidates = new List<NetworkMemberInfo>();
}

internal sealed class NetworkRemoteRequest
{
    public int EntityId;
    public long RequestId;
    public long OriginalRequestId;
    public string ExpectedNetworkId = string.Empty;
    public NetworkRequestOperation Operation;
    public XUiC_ItemStack.StackLocationTypes SourceLocation;
    public string Category = string.Empty;
    public string Search = string.Empty;
    public int Page;
    public int PageSize;
    public ItemValue Value;
    public int Count;
    public int Slot;
    public Vector3i Position;
    public int TargetEntityId;
    public NetworkConsumptionSource ConsumptionSource;
    public List<ItemStack> Requirements = new List<ItemStack>();
    public RestockSettingKey RestockSetting;
    public int RestockSettingValue;
    public bool TraderNetworkSource;
    public bool TraderPersonalPayout;
    public bool TraderBuyToBackpack;
    public bool TraderAutomation;
    public TraderAutomationRule TraderRule;
    public string TraderRuleId = string.Empty;
    public int TraderRuleMove;
    public string MembershipTargetPlayerId = string.Empty;
    public string MembershipOperationId = string.Empty;
    public int MembershipCandidatePage;
    public string TraderSessionId = string.Empty;
    public int TraderEntityId;
    public int TraderInventoryIndex;
    public List<int> RestockToolbeltSlots = new List<int>();
    public List<int> RestockBackpackSlots = new List<int>();
}

internal static class NetworkRemoteClient
{
    private sealed class PendingRequest
    {
        public Action<NetworkRemoteResult> Callback;
        public int EntityId;
        public NetworkRemoteRequest Request;
        public float StartedAt;
        public int RetryCount;
    }

    private static readonly object Sync = new object();
    private static readonly Dictionary<long, PendingRequest> Pending = new Dictionary<long, PendingRequest>();
    private static readonly Dictionary<int, string> NetworkIds = new Dictionary<int, string>();
    private static readonly Dictionary<ItemValue, long> MatchingEntries = new Dictionary<ItemValue, long>();
    private static readonly Dictionary<int, float> MatchingTypeFetchedAt = new Dictionary<int, float>();
    private static readonly HashSet<int> MatchingTypesQueued = new HashSet<int>();
    private static readonly List<StoredEntry> AuthorizedEntries = new List<StoredEntry>();
    private static long _nextRequestId = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & 0x3FFFFFFFFFFFFFFF;
    private static long _activeMutationRequestId;
    private static float _nextTimeoutSweep;
    private static float _nextMatchingFlush;
    private static long _knownRevision = -1;
    private static long _knownMembershipRevision;
    private static int _authorizedEntityId = -1;
    private static int _authorizedDepth;
    private static long _authorizedRequestId;
    private static int _craftingChainCaptureEntityId = -1;
    private static List<ItemStack> _craftingChainCapturedItems;

    private sealed class CraftingChainCaptureScope : IDisposable
    {
        private readonly int _entityId;
        private readonly List<ItemStack> _target;
        private bool _disposed;

        public CraftingChainCaptureScope(int entityId, List<ItemStack> target)
        {
            _entityId = entityId;
            _target = target;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Sync)
            {
                if (_craftingChainCaptureEntityId == _entityId && ReferenceEquals(_craftingChainCapturedItems, _target))
                {
                    _craftingChainCaptureEntityId = -1;
                    _craftingChainCapturedItems = null;
                }
            }
        }
    }

    private sealed class AuthorizedScope : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Sync)
            {
                if (_authorizedDepth > 0) _authorizedDepth--;
                if (_authorizedDepth == 0)
                {
                    List<StorageAddition> unused = AuthorizedEntries.Where(entry => entry.Count > 0)
                        .Select(entry => new StorageAddition { Value = entry.Value.Clone(), Count = entry.Count }).ToList();
                    int entityId = _authorizedEntityId;
                    long requestId = _authorizedRequestId;
                    _authorizedEntityId = -1;
                    _authorizedRequestId = 0;
                    AuthorizedEntries.Clear();
                    if (unused.Count > 0) SendAuthorizationRefund(entityId, requestId, unused);
                }
            }
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new EmptyScope();
        public void Dispose() { }
    }

    public static bool IsRemoteClient => PlayerNetworkDirectory.IsRemoteClient();
    public static bool IsMutationPending { get { lock (Sync) return _activeMutationRequestId != 0; } }
    public static long Revision { get { lock (Sync) return Math.Max(0, _knownRevision); } }

    public static bool AreMatchingTypesReady(IEnumerable<int> types)
    {
        if (types == null) return true;
        float now = Time.realtimeSinceStartup;
        lock (Sync)
        {
            foreach (int type in types.Where(type => type > 0).Distinct())
                if (!MatchingTypeFetchedAt.TryGetValue(type, out float fetchedAt) || now - fetchedAt >= 2f)
                    return false;
            return true;
        }
    }

    public static long GetMatchingCount(EntityPlayer player, ItemValue requirement, bool includeModded = false)
    {
        if (!(bool)player || requirement == null) return 0;
        lock (Sync)
        {
            QueueMatchingType(requirement.type);
            long total = 0;
            foreach (StoredEntry entry in GetAuthorizedEntries(player.entityId))
                if (NetworkItemMatcher.Matches(entry.Value, requirement, includeModded)) total = SafeAdd(total, entry.Count);
            foreach (KeyValuePair<ItemValue, long> entry in MatchingEntries)
                if (NetworkItemMatcher.Matches(entry.Key, requirement, includeModded)) total = SafeAdd(total, entry.Value);
            return total;
        }
    }

    public static long GetExactCount(EntityPlayer player, ItemValue value)
    {
        if (!(bool)player || value == null) return 0;
        lock (Sync)
        {
            QueueMatchingType(value.type);
            ItemValue key = NetworkStorage.NormalizeKey(value);
            long count = MatchingEntries.TryGetValue(key, out long available) ? available : 0;
            foreach (StoredEntry entry in GetAuthorizedEntries(player.entityId))
                if (NetworkStorage.NormalizeKey(entry.Value).Equals(key)) count = SafeAdd(count, entry.Count);
            return count;
        }
    }

    public static List<StoredEntry> GetEntriesForRequirements(EntityPlayer player, IEnumerable<ItemStack> requirements)
    {
        List<StoredEntry> result = new List<StoredEntry>();
        if (!(bool)player || requirements == null) return result;
        HashSet<int> types = new HashSet<int>();
        foreach (ItemStack requirement in requirements)
            if (requirement?.itemValue != null && requirement.itemValue.type > 0) types.Add(requirement.itemValue.type);
        lock (Sync)
        {
            foreach (int type in types) QueueMatchingType(type);
            foreach (KeyValuePair<ItemValue, long> entry in MatchingEntries)
                if (types.Contains(entry.Key.type) && entry.Value > 0)
                    result.Add(new StoredEntry { Value = entry.Key.Clone(), Count = entry.Value });
            foreach (StoredEntry entry in GetAuthorizedEntries(player.entityId))
                if (types.Contains(entry.Value.type))
                    result.Add(new StoredEntry { Value = entry.Value.Clone(), Count = entry.Count });
        }
        return result;
    }

    public static List<StoredEntry> GetAuthorizedEntriesForRequirements(EntityPlayer player, IEnumerable<ItemStack> requirements)
    {
        List<StoredEntry> result = new List<StoredEntry>();
        if (!(bool)player || requirements == null) return result;
        HashSet<int> types = new HashSet<int>();
        foreach (ItemStack requirement in requirements)
            if (requirement?.itemValue != null && requirement.itemValue.type > 0) types.Add(requirement.itemValue.type);
        lock (Sync)
        {
            foreach (StoredEntry entry in GetAuthorizedEntries(player.entityId))
                if (types.Contains(entry.Value.type))
                    result.Add(new StoredEntry { Value = entry.Value.Clone(), Count = entry.Count });
        }
        return result;
    }

    public static bool RequestConsumption(EntityPlayerLocal player, IList<ItemStack> requirements, NetworkConsumptionSource source,
        Vector3i workstationPosition, Action onSuccess, Action<string> onFailure = null)
    {
        return RequestConsumption(player, requirements, source, workstationPosition, _ => onSuccess?.Invoke(), onFailure);
    }

    public static bool RequestConsumption(EntityPlayerLocal player, IList<ItemStack> requirements, NetworkConsumptionSource source,
        Vector3i workstationPosition, Action<NetworkRemoteResult> onSuccess, Action<string> onFailure = null,
        ItemValue requiredNetworkValue = null, int requiredNetworkCount = 0)
    {
        if (!IsRemoteClient || !(bool)player || requirements == null || requirements.Count == 0) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.ConsumeRequirements);
        request.ConsumptionSource = source;
        request.Position = workstationPosition;
        request.Requirements = CloneRequirements(requirements);
        if (source == NetworkConsumptionSource.CraftingChainReservation && requiredNetworkCount > 0)
        {
            request.Value = requiredNetworkValue?.Clone();
            request.Count = requiredNetworkCount;
        }
        return Send(player, request, true, true, false, result =>
        {
            if (!result.Success)
            {
                NetworkLog.Debug("Remote material transaction rejected. operation=" + request.Operation + "; requestId=" + result.RequestId + "; error=" + result.Error);
                onFailure?.Invoke(result.Error);
                return;
            }
            using (EnterAuthorizedConsumption(player.entityId, result.RequestId, result.Entries)) onSuccess?.Invoke(result);
        });
    }

    internal static IDisposable CaptureCraftingChainNetworkConsumption(EntityPlayer player, List<ItemStack> capturedItems)
    {
        if (!(bool)player || capturedItems == null) return EmptyScope.Instance;
        lock (Sync)
        {
            if (_craftingChainCaptureEntityId != -1)
            {
                NetworkLog.Error("Nested crafting-chain network consumption capture was rejected. activeEntity="
                    + _craftingChainCaptureEntityId + "; requestedEntity=" + player.entityId);
                return EmptyScope.Instance;
            }
            _craftingChainCaptureEntityId = player.entityId;
            _craftingChainCapturedItems = capturedItems;
            return new CraftingChainCaptureScope(player.entityId, capturedItems);
        }
    }

    internal static void RecordCraftingChainNetworkConsumption(EntityPlayer player, IEnumerable<StorageAddition> items)
    {
        if (!(bool)player || items == null) return;
        lock (Sync)
        {
            if (_craftingChainCaptureEntityId != player.entityId || _craftingChainCapturedItems == null) return;
            foreach (StorageAddition addition in items)
            {
                if (addition?.Value == null || addition.Count <= 0 || addition.Count > int.MaxValue) continue;
                ItemValue key = NetworkStorage.NormalizeKey(addition.Value);
                ItemStack existing = _craftingChainCapturedItems.FirstOrDefault(stack =>
                    NetworkStorage.NormalizeKey(stack.itemValue).Equals(key));
                if (existing == null) _craftingChainCapturedItems.Add(new ItemStack(key, (int)addition.Count));
                else existing.count = NetworkItemMatcher.AddClamped(existing.count, (int)addition.Count);
            }
        }
    }

    public static bool IsAuthorizedConsumption(EntityPlayer player)
    {
        lock (Sync) return _authorizedDepth > 0 && player != null && player.entityId == _authorizedEntityId;
    }

    public static bool ConsumeAuthorized(EntityPlayer player, IEnumerable<StorageAddition> used)
    {
        if (used == null) return true;
        List<StorageAddition> pending = used.Where(item => item?.Value != null && item.Count > 0).ToList();
        if (pending.Count == 0) return true;
        lock (Sync)
        {
            if (_authorizedDepth <= 0 || player == null || player.entityId != _authorizedEntityId) return false;
            Dictionary<int, long> remainingByIndex = new Dictionary<int, long>();
            List<KeyValuePair<int, long>> plan = new List<KeyValuePair<int, long>>();
            foreach (StorageAddition item in pending)
            {
                long remaining = item.Count;
                for (int i = 0; i < AuthorizedEntries.Count && remaining > 0; i++)
                {
                    StoredEntry available = AuthorizedEntries[i];
                    long alreadyPlanned = remainingByIndex.TryGetValue(i, out long reserved) ? reserved : 0;
                    long usable = available.Count - alreadyPlanned;
                    if (usable <= 0 || !NetworkStorage.NormalizeKey(available.Value).Equals(NetworkStorage.NormalizeKey(item.Value))) continue;
                    long take = Math.Min(remaining, usable);
                    remainingByIndex[i] = alreadyPlanned + take;
                    plan.Add(new KeyValuePair<int, long>(i, take));
                    remaining -= take;
                }
                if (remaining > 0) return false;
            }
            foreach (KeyValuePair<int, long> take in plan) AuthorizedEntries[take.Key].Count -= take.Value;
            return true;
        }
    }

    public static void RestoreAuthorized(EntityPlayer player, IEnumerable<StorageAddition> restored)
    {
        if (restored == null) return;
        lock (Sync)
        {
            if (_authorizedDepth <= 0 || player == null || player.entityId != _authorizedEntityId) return;
            foreach (StorageAddition item in restored) RestoreAuthorizedUnsafe(item);
        }
    }

    public static bool RequestVehicleOperation(EntityPlayerLocal player, NetworkRequestOperation operation, int vehicleEntityId, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || (operation != NetworkRequestOperation.VehicleRefuel && operation != NetworkRequestOperation.VehicleRepair)) return false;
        NetworkRemoteRequest request = NewRequest(player, operation);
        request.Slot = vehicleEntityId;
        return Send(player, request, true, true, false, result =>
        {
            if (result.Success) NetworkInventorySync.Apply(player, result);
            callback?.Invoke(result);
        });
    }

    public static bool RequestRestockSettings(EntityPlayerLocal player, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player) return false;
        return Send(player, NewRequest(player, NetworkRequestOperation.QueryRestockSettings), false, false, false, callback);
    }

    internal static bool RequestMembership(EntityPlayerLocal player, NetworkRequestOperation operation, string targetPlayerId,
        string operationId, int memberPage, int candidatePage, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || !NetworkMembershipService.IsMembershipOperation(operation)) return false;
        NetworkRemoteRequest request = NewRequest(player, operation);
        request.MembershipTargetPlayerId = targetPlayerId ?? string.Empty;
        request.MembershipOperationId = operationId ?? string.Empty;
        request.Page = Math.Max(0, memberPage);
        request.MembershipCandidatePage = Math.Max(0, candidatePage);
        return Send(player, request, false, false, false, callback);
    }

    internal static int PendingRequestCount { get { lock (Sync) return Pending.Count; } }

    internal static void ResetMembershipIdentityForWorldShutdown()
    {
        lock (Sync)
        {
            Pending.Clear();
            NetworkIds.Clear();
            MatchingEntries.Clear();
            MatchingTypeFetchedAt.Clear();
            MatchingTypesQueued.Clear();
            _activeMutationRequestId = 0;
            _knownRevision = -1;
            _knownMembershipRevision = 0;
        }
        NetworkLog.Debug("Remote membership identity and pending request caches cleared for world shutdown.");
    }

    public static bool OpenTraderMarket(EntityPlayerLocal player, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player) return false;
        return Send(player, NewRequest(player, NetworkRequestOperation.TraderOpenMarket), false, false, false, callback);
    }

    public static bool QueryTraderMarket(EntityPlayerLocal player, string sessionId, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || string.IsNullOrEmpty(sessionId)) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.TraderQueryMarket);
        request.TraderSessionId = sessionId;
        return Send(player, request, false, false, false, callback);
    }

    public static bool CloseTraderMarket(EntityPlayerLocal player, string sessionId, Action<NetworkRemoteResult> callback = null)
    {
        if (!IsRemoteClient || !(bool)player || string.IsNullOrEmpty(sessionId)) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.TraderCloseMarket);
        request.TraderSessionId = sessionId;
        return Send(player, request, false, false, false, callback);
    }

    public static bool BuyTraderItem(EntityPlayerLocal player, string sessionId, TraderOffer offer, int count, bool toBackpack, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || string.IsNullOrEmpty(sessionId) || offer?.Value == null || count <= 0) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.TraderBuyItem);
        request.TraderSessionId = sessionId;
        request.TraderEntityId = offer.TraderEntityId;
        request.TraderInventoryIndex = offer.InventoryIndex;
        request.Value = offer.Value.Clone();
        request.Count = count;
        request.TraderBuyToBackpack = toBackpack;
        return Send(player, request, true, true, false, callback);
    }

    public static bool QueryTraderRules(EntityPlayerLocal player, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player) return false;
        return Send(player, NewRequest(player, NetworkRequestOperation.TraderQueryRules), false, false, false, callback);
    }

    public static bool MutateTraderRules(EntityPlayerLocal player, NetworkRequestOperation operation, TraderAutomationRule rule,
        string ruleId, int move, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || (byte)operation < (byte)NetworkRequestOperation.TraderSaveRule || (byte)operation > (byte)NetworkRequestOperation.TraderRunRules) return false;
        NetworkRemoteRequest request = NewRequest(player, operation);
        request.TraderRule = rule?.Clone();
        request.TraderRuleId = ruleId ?? string.Empty;
        request.TraderRuleMove = move;
        return Send(player, request, false, false, false, callback);
    }

    public static bool SetRestockSetting(EntityPlayerLocal player, RestockSettingKey setting, int value, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || RestockSettingCatalog.Get(setting) == null) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.SetRestockSetting);
        request.RestockSetting = setting;
        request.RestockSettingValue = value;
        return Send(player, request, false, false, false, callback);
    }

    public static bool RequestAmmoBankReload(EntityPlayerLocal player, ItemValue ammo, int count, int toolbeltSlot, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || ammo == null || ammo.type <= 0 || count <= 0 || toolbeltSlot < 0) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.AmmoBankReload);
        request.Value = ammo.Clone();
        request.Count = count;
        request.Slot = toolbeltSlot;
        return Send(player, request, false, false, false, callback);
    }

    public static bool RequestAmmoBankRefund(EntityPlayerLocal player, ItemValue ammo, int count, long originalRequestId, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || ammo == null || count <= 0 || originalRequestId <= 0) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.AmmoBankRefund);
        request.Value = ammo.Clone();
        request.Count = count;
        request.OriginalRequestId = originalRequestId;
        return Send(player, request, false, false, false, callback);
    }

    public static bool RequestTraderVoidSell(EntityPlayerLocal player, ItemValue value, int count, bool networkSource, bool personalPayout,
        XUiC_ItemStack.StackLocationTypes sourceLocation, int slot, Action<NetworkRemoteResult> callback, bool automation = false)
    {
        if (!IsRemoteClient || !(bool)player || value == null || value.type <= 0 || count <= 0 || slot < 0) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.TraderVoidSell);
        request.Value = value.Clone();
        request.Count = count;
        request.TraderNetworkSource = networkSource;
        request.TraderPersonalPayout = personalPayout;
        request.TraderAutomation = automation;
        request.SourceLocation = sourceLocation;
        request.Slot = slot;
        return Send(player, request, personalPayout || sourceLocation == XUiC_ItemStack.StackLocationTypes.ToolBelt && !networkSource,
            personalPayout || sourceLocation == XUiC_ItemStack.StackLocationTypes.Backpack && !networkSource, false, callback);
    }

    public static bool ReturnCraftingChainAuthorization(EntityPlayerLocal player, long originalRequestId,
        IEnumerable<ItemStack> items, long retryRequestId, out long requestId, Action<NetworkRemoteResult> callback)
    {
        requestId = 0;
        if (!IsRemoteClient || !(bool)player || originalRequestId <= 0 || items == null) return false;
        List<ItemStack> requirements = CloneRequirements(items);
        if (requirements.Count == 0 || requirements.Count > 64) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.ReturnAuthorization);
        request.OriginalRequestId = originalRequestId;
        if (retryRequestId > 0) request.RequestId = retryRequestId;
        request.Requirements = requirements;
        bool sent = Send(player, request, false, false, false, callback);
        if (sent || retryRequestId > 0) requestId = request.RequestId;
        return sent;
    }

    public static string GetCurrentNetworkId(EntityPlayer player)
    {
        if (!(bool)player) return string.Empty;
        lock (Sync) return NetworkIds.TryGetValue(player.entityId, out string networkId) ? networkId : string.Empty;
    }

    public static bool RequestRestock(EntityPlayerLocal player, bool automatic, IEnumerable<int> toolbeltSlots,
        IEnumerable<int> backpackSlots, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player) return false;
        NetworkRemoteRequest request = NewRequest(player, automatic ? NetworkRequestOperation.AutomaticRestock : NetworkRequestOperation.ManualRestock);
        request.RestockToolbeltSlots = (toolbeltSlots ?? Enumerable.Empty<int>()).Where(slot => slot >= 0).Distinct().Take(128).ToList();
        request.RestockBackpackSlots = (backpackSlots ?? Enumerable.Empty<int>()).Where(slot => slot >= 0).Distinct().Take(128).ToList();
        return Send(player, request, true, true, false, callback);
    }

    public static bool RequestPage(EntityPlayer player, string category, string search, int page, int pageSize, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(player is EntityPlayerLocal localPlayer)) return false;
        NetworkRemoteRequest request = NewRequest(localPlayer, NetworkRequestOperation.QueryPage);
        request.Category = LimitUtf8(category, 256);
        request.Search = LimitUtf8(search, 256);
        request.Page = Math.Max(0, page);
        request.PageSize = Math.Max(1, Math.Min(128, pageSize));
        return Send(localPlayer, request, false, false, false, callback);
    }

    public static bool DepositCursor(EntityPlayer player, ItemValue value, int count, XUiC_ItemStack.StackLocationTypes sourceLocation, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(player is EntityPlayerLocal localPlayer) || value == null || count <= 0) return false;
        NetworkRemoteRequest request = NewRequest(localPlayer, NetworkRequestOperation.DepositCursor);
        request.Value = value.Clone();
        request.Count = count;
        request.SourceLocation = sourceLocation;
        bool syncBag = sourceLocation == XUiC_ItemStack.StackLocationTypes.Backpack;
        bool syncToolbelt = sourceLocation == XUiC_ItemStack.StackLocationTypes.ToolBelt;
        return Send(localPlayer, request, syncToolbelt, syncBag, true, callback);
    }

    public static bool DepositPlayerSlot(EntityPlayer player, bool toolbelt, int slot, ItemValue value, int count, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(player is EntityPlayerLocal localPlayer) || value == null || count <= 0) return false;
        NetworkRemoteRequest request = NewRequest(localPlayer, toolbelt ? NetworkRequestOperation.DepositToolbeltSlot : NetworkRequestOperation.DepositBackpackSlot);
        request.Value = value.Clone();
        request.Count = count;
        request.Slot = slot;
        return Send(localPlayer, request, toolbelt, !toolbelt, false, callback);
    }

    internal static bool ProcessAutomationSlot(EntityPlayerLocal player, bool toolbelt, int slot, ItemValue value, int count,
        Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || value == null || count <= 0 || slot < 0) return false;
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.AutomationProcessSlot);
        request.Value = value.Clone(); request.Count = count; request.Slot = slot;
        request.SourceLocation = toolbelt ? XUiC_ItemStack.StackLocationTypes.ToolBelt : XUiC_ItemStack.StackLocationTypes.Backpack;
        return Send(player, request, toolbelt, !toolbelt, false, callback);
    }

    internal static bool RequestTrash(EntityPlayerLocal player, NetworkRequestOperation operation, int slot, ItemValue value,
        int count, XUiC_ItemStack.StackLocationTypes sourceLocation, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || operation is not (NetworkRequestOperation.TrashOpen or NetworkRequestOperation.TrashPut
            or NetworkRequestOperation.TrashTake or NetworkRequestOperation.TrashClose)) return false;
        NetworkRemoteRequest request = NewRequest(player, operation);
        request.Slot = slot; request.Value = value?.Clone(); request.Count = count; request.SourceLocation = sourceLocation;
        return Send(player, request, false, false, operation is NetworkRequestOperation.TrashPut or NetworkRequestOperation.TrashTake, callback);
    }

    internal static bool RequestBookNetwork(EntityPlayerLocal player, NetworkRequestOperation operation, ItemValue value,
        Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(bool)player || value == null || operation is not (NetworkRequestOperation.BookConsumeNetwork
            or NetworkRequestOperation.BookScrapNetwork)) return false;
        NetworkRemoteRequest request = NewRequest(player, operation);
        request.Value = value.Clone(); request.Count = 1;
        return Send(player, request, false, false, false, callback);
    }

    public static bool DepositBackpackAll(EntityPlayer player, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(player is EntityPlayerLocal localPlayer)) return false;
        return Send(localPlayer, NewRequest(localPlayer, NetworkRequestOperation.DepositBackpackAll), false, true, false, callback);
    }

    public static bool DepositLootAll(EntityPlayer player, Vector3i position, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(player is EntityPlayerLocal localPlayer)) return false;
        NetworkRemoteRequest request = NewRequest(localPlayer, NetworkRequestOperation.DepositLootAll);
        request.Position = position;
        return Send(localPlayer, request, false, false, false, callback);
    }

    public static bool DepositEntityBagAll(EntityPlayer player, int entityId, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(player is EntityPlayerLocal localPlayer) || entityId <= 0) return false;
        NetworkRemoteRequest request = NewRequest(localPlayer, NetworkRequestOperation.DepositEntityBagAll);
        request.TargetEntityId = entityId;
        return Send(localPlayer, request, false, false, false, callback);
    }

    public static bool Withdraw(EntityPlayer player, ItemValue value, int count, bool toBackpack, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(player is EntityPlayerLocal localPlayer) || value == null || count <= 0) return false;
        NetworkRemoteRequest request = NewRequest(localPlayer, toBackpack ? NetworkRequestOperation.WithdrawBackpack : NetworkRequestOperation.WithdrawCursor);
        request.Value = value.Clone();
        request.Count = count;
        return Send(localPlayer, request, false, toBackpack, !toBackpack, callback);
    }

    public static bool ReturnWithdrawal(EntityPlayer player, long originalRequestId, ItemValue value, int count, Action<NetworkRemoteResult> callback)
    {
        if (!IsRemoteClient || !(player is EntityPlayerLocal localPlayer) || value == null || count <= 0 || originalRequestId <= 0) return false;
        NetworkRemoteRequest request = NewRequest(localPlayer, NetworkRequestOperation.ReturnWithdrawal);
        request.OriginalRequestId = originalRequestId;
        request.Value = value.Clone();
        request.Count = count;
        return Send(localPlayer, request, false, false, false, callback);
    }

    public static void HandleResponse(int entityId, NetworkRemoteResult result)
    {
        if (result == null) return;
        PendingRequest pending;
        bool responseCurrent;
        bool revisionChanged = false;
        bool membershipStateCurrent;
        lock (Sync)
        {
            if (!Pending.TryGetValue(result.RequestId, out pending)) pending = null;
            else if (pending.EntityId != entityId)
            {
                NetworkLog.Error("Ignored network response routed to the wrong player entity. requestId=" + result.RequestId + "; expectedEntity=" + pending.EntityId + "; receivedEntity=" + entityId);
                return;
            }
            else
            {
                Pending.Remove(result.RequestId);
                if (_activeMutationRequestId == result.RequestId) _activeMutationRequestId = 0;
            }

            membershipStateCurrent = result.MembershipRevision <= 0 || result.MembershipRevision >= _knownMembershipRevision;
            if (result.MembershipRevision > _knownMembershipRevision) _knownMembershipRevision = result.MembershipRevision;
            if (membershipStateCurrent && !string.IsNullOrEmpty(result.NetworkId))
            {
                if (!NetworkIds.TryGetValue(entityId, out string currentNetworkId)
                    || string.Equals(currentNetworkId, result.NetworkId, StringComparison.OrdinalIgnoreCase))
                    NetworkIds[entityId] = result.NetworkId;
                else
                {
                    NetworkIds[entityId] = result.NetworkId;
                    NetworkLog.Debug("Network identity cache advanced by membership revision. entity=" + entityId + "; previous="
                        + currentNetworkId + "; current=" + result.NetworkId + "; membershipRevision=" + result.MembershipRevision);
                }
            }
            bool membershipChanged = result.MembershipNotice == NetworkMembershipNotice.Changed && membershipStateCurrent;
            if (membershipChanged)
            {
                MatchingEntries.Clear();
                MatchingTypeFetchedAt.Clear();
                _knownRevision = result.Revision;
                revisionChanged = true;
            }
            else if (membershipStateCurrent && result.Revision > _knownRevision)
            {
                _knownRevision = result.Revision;
                MatchingEntries.Clear();
                MatchingTypeFetchedAt.Clear();
                revisionChanged = true;
            }
            responseCurrent = membershipStateCurrent && result.Revision >= _knownRevision;
        }
        if (result.MembershipNotice != NetworkMembershipNotice.None && membershipStateCurrent)
            NetworkMembershipClient.HandleNotice(entityId, result);
        if (pending == null)
        {
            if (result.RequestId != 0) NetworkLog.Debug("Ignored unmatched network response. requestId=" + result.RequestId + "; network=" + result.NetworkId);
            if (revisionChanged) AmmoBankService.RefreshAmmoHud(GetLocalPlayer());
            return;
        }
        if (pending.Request.Operation == NetworkRequestOperation.QueryMatching && responseCurrent && result.Success)
        {
            lock (Sync)
            {
                HashSet<int> types = new HashSet<int>(pending.Request.Requirements.Select(item => item.itemValue.type));
                foreach (ItemValue old in MatchingEntries.Keys.Where(value => types.Contains(value.type)).ToList()) MatchingEntries.Remove(old);
                foreach (StoredEntry entry in result.Entries)
                    if (entry?.Value != null && entry.Count > 0 && types.Contains(entry.Value.type))
                        MatchingEntries[NetworkStorage.NormalizeKey(entry.Value)] = entry.Count;
                foreach (int type in types) MatchingTypeFetchedAt[type] = Time.realtimeSinceStartup;
            }
            AmmoBankService.RefreshAmmoHud(GetLocalPlayer());
        }
        else if (revisionChanged) AmmoBankService.RefreshAmmoHud(GetLocalPlayer());
        try { pending.Callback?.Invoke(result); }
        catch (Exception ex) { NetworkLog.Error("Network response handler failed. requestId=" + result.RequestId + "; exception=" + ex); }
    }

    public static void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (now >= _nextMatchingFlush)
        {
            _nextMatchingFlush = now + 0.2f;
            List<int> types = null;
            lock (Sync)
            {
                List<int> queuedTypes = MatchingTypesQueued.Take(64).ToList();
                foreach (int type in queuedTypes)
                {
                    MatchingTypesQueued.Remove(type);
                    if (types == null) types = new List<int>();
                    types.Add(type);
                }
            }
            if (types != null)
            {
                NetworkLog.Debug("Flushing queued matching-item query batch. count=" + types.Count);
                EntityPlayerLocal player = GetLocalPlayer();
                if ((bool)player)
                {
                    NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.QueryMatching);
                    request.Requirements = types.Select(type => new ItemStack(new ItemValue(type), 1)).ToList();
                    Send(player, request, false, false, false, null);
                }
                else lock (Sync) foreach (int type in types) MatchingTypesQueued.Add(type);
            }
        }
        if (now < _nextTimeoutSweep) return;
        _nextTimeoutSweep = now + 1f;
        List<PendingRequest> timedOut = null;
        List<long> requestIds = null;
        List<PendingRequest> retry = null;
        lock (Sync)
        {
            foreach (KeyValuePair<long, PendingRequest> entry in Pending)
            {
                if (now - entry.Value.StartedAt < 30f) continue;
                if (entry.Value.RetryCount < 2)
                {
                    entry.Value.RetryCount++;
                    entry.Value.StartedAt = now;
                    if (retry == null) retry = new List<PendingRequest>();
                    retry.Add(entry.Value);
                }
                else
                {
                    if (timedOut == null) { timedOut = new List<PendingRequest>(); requestIds = new List<long>(); }
                    timedOut.Add(entry.Value);
                    requestIds.Add(entry.Key);
                }
            }
            if (requestIds != null)
                foreach (long requestId in requestIds)
                {
                    Pending.Remove(requestId);
                    if (_activeMutationRequestId == requestId) _activeMutationRequestId = 0;
                }
        }
        if (retry != null)
        {
            ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            foreach (PendingRequest request in retry)
            {
                try
                {
                    if ((bool)connection && connection.IsClient && !connection.IsServer)
                        connection.SendToServer(NetPackageManager.GetPackage<NetPackageHyperdimensionalRequest>().Setup(request.Request));
                    NetworkLog.Debug("Retrying idempotent network request. operation=" + request.Request.Operation + "; requestId=" + request.Request.RequestId + "; attempt=" + request.RetryCount);
                }
                catch (Exception ex) { NetworkLog.Error("Could not retry network request. operation=" + request.Request.Operation + "; requestId=" + request.Request.RequestId + "; exception=" + ex); }
            }
        }
        if (timedOut == null) return;
        for (int i = 0; i < timedOut.Count; i++)
        {
            NetworkLog.Error("Network request timed out. requestId=" + requestIds[i]);
            try { timedOut[i].Callback?.Invoke(new NetworkRemoteResult { RequestId = requestIds[i], Success = false, Error = "Timeout" }); }
            catch (Exception ex) { NetworkLog.Error("Timeout response handler failed. requestId=" + requestIds[i] + "; exception=" + ex); }
        }
    }

    private static NetworkRemoteRequest NewRequest(EntityPlayerLocal player, NetworkRequestOperation operation)
    {
        string networkId = string.Empty;
        lock (Sync) NetworkIds.TryGetValue(player.entityId, out networkId);
        return new NetworkRemoteRequest
        {
            EntityId = player.entityId,
            RequestId = Interlocked.Increment(ref _nextRequestId),
            ExpectedNetworkId = networkId ?? string.Empty,
            Operation = operation
        };
    }

    internal static EntityPlayerLocal GetLocalPlayer()
    {
        return GameManager.Instance?.World?.GetPrimaryPlayer() as EntityPlayerLocal;
    }

    private static void QueueMatchingType(int type)
    {
        if (type <= 0) return;
        float now = Time.realtimeSinceStartup;
        if (MatchingTypeFetchedAt.TryGetValue(type, out float fetchedAt) && now - fetchedAt < 2f) return;
        bool alreadyPending = Pending.Values.Any(pending => pending.Request.Operation == NetworkRequestOperation.QueryMatching
            && pending.Request.Requirements.Any(item => item.itemValue.type == type));
        if (!alreadyPending) MatchingTypesQueued.Add(type);
    }

    private static List<StoredEntry> GetAuthorizedEntries(int entityId)
    {
        if (_authorizedDepth == 0 || _authorizedEntityId != entityId) return new List<StoredEntry>();
        return AuthorizedEntries.Where(entry => entry.Count > 0)
            .Select(entry => new StoredEntry { Value = entry.Value.Clone(), Count = entry.Count }).ToList();
    }

    private static IDisposable EnterAuthorizedConsumption(int entityId, long requestId, IEnumerable<StoredEntry> entries)
    {
        lock (Sync)
        {
            if (_authorizedDepth == 0)
            {
                _authorizedEntityId = entityId;
                _authorizedRequestId = requestId;
                AuthorizedEntries.Clear();
                if (entries != null)
                    AuthorizedEntries.AddRange(entries.Where(entry => entry?.Value != null && entry.Count > 0)
                        .Select(entry => new StoredEntry { Value = entry.Value.Clone(), Count = entry.Count }));
            }
            else if (_authorizedEntityId != entityId)
            {
                NetworkLog.Error("Could not nest remote material authorization for a different player. active=" + _authorizedEntityId + "; requested=" + entityId);
                return EmptyScope.Instance;
            }
            _authorizedDepth++;
            return new AuthorizedScope();
        }
    }

    private static void RestoreAuthorizedUnsafe(StorageAddition item)
    {
        if (item?.Value == null || item.Count <= 0) return;
        ItemValue key = NetworkStorage.NormalizeKey(item.Value);
        StoredEntry existing = AuthorizedEntries.FirstOrDefault(entry => NetworkStorage.NormalizeKey(entry.Value).Equals(key));
        if (existing == null) AuthorizedEntries.Add(new StoredEntry { Value = key, Count = item.Count });
        else existing.Count = SafeAdd(existing.Count, item.Count);
    }

    private static void SendAuthorizationRefund(int entityId, long originalRequestId, List<StorageAddition> unused)
    {
        EntityPlayerLocal player = GetLocalPlayer();
        if (!(bool)player || player.entityId != entityId || originalRequestId <= 0)
        {
            NetworkLog.Error("Unused network material authorization could not be returned because the local player was unavailable. entity=" + entityId + "; originalRequestId=" + originalRequestId);
            return;
        }
        List<ItemStack> requirements = new List<ItemStack>();
        foreach (StorageAddition item in unused)
        {
            if (item.Count > int.MaxValue)
            {
                NetworkLog.Error("Unused network authorization exceeded the refund protocol count limit. originalRequestId=" + originalRequestId + "; item=" + item.Value.type + "; count=" + item.Count);
                return;
            }
            requirements.Add(new ItemStack(item.Value.Clone(), (int)item.Count));
        }
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.ReturnAuthorization);
        request.OriginalRequestId = originalRequestId;
        request.Requirements = requirements;
        bool sent = Send(player, request, false, false, false, result =>
        {
            if (!result.Success) NetworkLog.Error("Unused network authorization refund was rejected. originalRequestId=" + originalRequestId + "; error=" + result.Error);
        });
        if (!sent) NetworkLog.Error("Unused network authorization refund could not be sent. originalRequestId=" + originalRequestId);
    }

    private static List<ItemStack> CloneRequirements(IEnumerable<ItemStack> requirements)
    {
        return requirements.Where(item => item?.itemValue != null && !item.IsEmpty())
            .Select(item => new ItemStack(item.itemValue.Clone(), item.count)).ToList();
    }

    private static long SafeAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static string LimitUtf8(string value, int maximumBytes)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        StringBuilder result = new StringBuilder(value.Length);
        int bytes = 0;
        for (int i = 0; i < value.Length; i++)
        {
            string character;
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                character = new string(new[] { value[i++], value[i] });
            else character = value[i].ToString();
            int characterBytes = Encoding.UTF8.GetByteCount(character);
            if (bytes + characterBytes > maximumBytes) break;
            result.Append(character);
            bytes += characterBytes;
        }
        return result.ToString();
    }

    private static bool Send(EntityPlayerLocal player, NetworkRemoteRequest request, bool syncToolbelt, bool syncBag, bool syncCursor, Action<NetworkRemoteResult> callback)
    {
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (!(bool)connection || !connection.IsClient || connection.IsServer) return false;
        lock (Sync)
        {
            if (NetworkMembershipClient.IsPreparing && request.Operation != NetworkRequestOperation.ReturnAuthorization
                && request.Operation != NetworkRequestOperation.ReturnWithdrawal && request.Operation != NetworkRequestOperation.AmmoBankRefund
                && request.Operation != NetworkRequestOperation.MembershipAcknowledgePrepare)
                return false;
            bool mutation = request.Operation != NetworkRequestOperation.QueryPage
                && request.Operation != NetworkRequestOperation.QueryMatching
                && request.Operation != NetworkRequestOperation.QueryRestockSettings
                && request.Operation != NetworkRequestOperation.TraderQueryMarket
                && request.Operation != NetworkRequestOperation.TraderCloseMarket
                && request.Operation != NetworkRequestOperation.TraderQueryRules
                && request.Operation != NetworkRequestOperation.MembershipQuery;
            if (mutation && _activeMutationRequestId != 0)
            {
                NetworkLog.Debug("Remote mutation not queued because another inventory transaction is awaiting confirmation. operation=" + request.Operation + "; activeRequestId=" + _activeMutationRequestId);
                return false;
            }
            if (Pending.Count >= 256)
            {
                NetworkLog.Error("Network request queue is full; request rejected. operation=" + request.Operation);
                return false;
            }
            Pending[request.RequestId] = new PendingRequest
            {
                Callback = callback,
                EntityId = request.EntityId,
                Request = request,
                StartedAt = Time.realtimeSinceStartup
            };
            if (mutation) _activeMutationRequestId = request.RequestId;
        }

        try
        {
            if (syncToolbelt || syncBag || syncCursor)
            {
                connection.SendToServer(NetPackageManager.GetPackage<NetPackagePlayerInventory>()
                    .Setup(player, syncToolbelt, syncBag, false, syncCursor));
            }
            connection.SendToServer(NetPackageManager.GetPackage<NetPackageHyperdimensionalRequest>().Setup(request));
            NetworkLog.Debug("Network request sent. operation=" + request.Operation + "; requestId=" + request.RequestId + "; entity=" + request.EntityId + "; expectedNetwork=" + request.ExpectedNetworkId);
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Network request send raised an exception; keeping the request pending for idempotent retry. operation=" + request.Operation + "; requestId=" + request.RequestId + "; exception=" + ex);
        }
        return true;
    }
}
