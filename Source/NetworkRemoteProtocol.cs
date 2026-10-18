using System;
using System.Collections.Generic;
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
    ReturnWithdrawal = 9
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
    public byte[] BagData;
    public byte[] InventoryData;
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
    private static long _nextRequestId = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & 0x3FFFFFFFFFFFFFFF;
    private static long _activeMutationRequestId;
    private static float _nextTimeoutSweep;

    public static bool IsRemoteClient => PlayerNetworkDirectory.IsRemoteClient();
    public static bool IsMutationPending { get { lock (Sync) return _activeMutationRequestId != 0; } }

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
        lock (Sync)
        {
            if (!string.IsNullOrEmpty(result.NetworkId))
                NetworkIds[entityId] = result.NetworkId;
            if (!Pending.TryGetValue(result.RequestId, out pending))
            {
                NetworkLog.Debug("Ignored unmatched network response. requestId=" + result.RequestId + "; network=" + result.NetworkId);
                return;
            }
            if (pending.EntityId != entityId)
            {
                NetworkLog.Error("Ignored network response routed to the wrong player entity. requestId=" + result.RequestId + "; expectedEntity=" + pending.EntityId + "; receivedEntity=" + entityId);
                return;
            }
            Pending.Remove(result.RequestId);
            if (_activeMutationRequestId == result.RequestId) _activeMutationRequestId = 0;
        }
        try { pending.Callback?.Invoke(result); }
        catch (Exception ex) { NetworkLog.Error("Network response handler failed. requestId=" + result.RequestId + "; exception=" + ex); }
    }

    public static void Update()
    {
        float now = Time.realtimeSinceStartup;
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
            bool mutation = request.Operation != NetworkRequestOperation.QueryPage;
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
