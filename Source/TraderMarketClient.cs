using System;
using System.Threading;

namespace HyperdimensionalNetwork;

internal static class TraderMarketClient
{
    private static long _requestSequence = DateTime.UtcNow.Ticks;

    public static bool Open(EntityPlayerLocal player, Action<NetworkRemoteResult> callback)
    {
        if (!(bool)player) return false;
        if (NetworkRemoteClient.IsRemoteClient) return NetworkRemoteClient.OpenTraderMarket(player, callback);
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.TraderOpenMarket);
        callback?.Invoke(TraderMarketplaceService.Open(GameManager.Instance?.World, player, request));
        return true;
    }

    public static bool Query(EntityPlayerLocal player, string sessionId, Action<NetworkRemoteResult> callback)
    {
        if (!(bool)player || string.IsNullOrEmpty(sessionId)) return false;
        if (NetworkRemoteClient.IsRemoteClient) return NetworkRemoteClient.QueryTraderMarket(player, sessionId, callback);
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.TraderQueryMarket);
        request.TraderSessionId = sessionId;
        callback?.Invoke(TraderMarketplaceService.Query(GameManager.Instance?.World, player, request));
        return true;
    }

    public static bool Close(EntityPlayerLocal player, string sessionId, Action<NetworkRemoteResult> callback = null)
    {
        if (!(bool)player || string.IsNullOrEmpty(sessionId)) return false;
        if (NetworkRemoteClient.IsRemoteClient) return NetworkRemoteClient.CloseTraderMarket(player, sessionId, callback);
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.TraderCloseMarket);
        request.TraderSessionId = sessionId;
        NetworkRemoteResult result = TraderMarketplaceService.Close(player, request);
        callback?.Invoke(result);
        NetworkLog.Debug("Local trader market close completed. player=" + player.entityId + "; session=" + sessionId + "; success=" + result.Success + "; error=" + result.Error);
        return true;
    }

    public static bool Buy(EntityPlayerLocal player, string sessionId, TraderOffer offer, int count, bool toBackpack, Action<NetworkRemoteResult> callback)
    {
        if (!(bool)player || string.IsNullOrEmpty(sessionId) || offer?.Value == null || count <= 0) return false;
        if (NetworkRemoteClient.IsRemoteClient) return NetworkRemoteClient.BuyTraderItem(player, sessionId, offer, count, toBackpack, callback);
        NetworkRemoteRequest request = NewRequest(player, NetworkRequestOperation.TraderBuyItem);
        request.TraderSessionId = sessionId;
        request.TraderEntityId = offer.TraderEntityId;
        request.TraderInventoryIndex = offer.InventoryIndex;
        request.Value = offer.Value.Clone();
        request.Count = count;
        request.TraderBuyToBackpack = toBackpack;
        callback?.Invoke(TraderMarketplaceService.Buy(GameManager.Instance?.World, player, null, request));
        return true;
    }

    private static NetworkRemoteRequest NewRequest(EntityPlayerLocal player, NetworkRequestOperation operation)
        => new NetworkRemoteRequest
        {
            EntityId = player.entityId,
            RequestId = Interlocked.Increment(ref _requestSequence),
            ExpectedNetworkId = PlayerNetworkDirectory.GetNetworkId(player) ?? string.Empty,
            Operation = operation
        };
}
