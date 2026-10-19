using System;

namespace HyperdimensionalNetwork;

internal static class TraderRuleClient
{
    private static long _requestSequence = DateTime.UtcNow.Ticks;

    public static bool Query(EntityPlayerLocal player, Action<NetworkRemoteResult> callback)
    {
        if (!(bool)player) return false;
        if (NetworkRemoteClient.IsRemoteClient) return NetworkRemoteClient.QueryTraderRules(player, callback);
        callback?.Invoke(TraderAutomationService.QueryRules(player, NewRequest(player, NetworkRequestOperation.TraderQueryRules)));
        return true;
    }

    public static bool Save(EntityPlayerLocal player, TraderAutomationRule rule, Action<NetworkRemoteResult> callback)
        => Mutate(player, NetworkRequestOperation.TraderSaveRule, rule, rule?.Id, 0, callback);

    public static bool Delete(EntityPlayerLocal player, string id, Action<NetworkRemoteResult> callback)
        => Mutate(player, NetworkRequestOperation.TraderDeleteRule, null, id, 0, callback);

    public static bool Move(EntityPlayerLocal player, string id, int offset, Action<NetworkRemoteResult> callback)
        => Mutate(player, NetworkRequestOperation.TraderMoveRule, null, id, offset, callback);

    public static bool RunAll(EntityPlayerLocal player, Action<NetworkRemoteResult> callback)
        => Mutate(player, NetworkRequestOperation.TraderRunRules, null, string.Empty, 0, callback);

    private static bool Mutate(EntityPlayerLocal player, NetworkRequestOperation operation, TraderAutomationRule rule, string id, int move,
        Action<NetworkRemoteResult> callback)
    {
        if (!(bool)player) return false;
        if (NetworkRemoteClient.IsRemoteClient) return NetworkRemoteClient.MutateTraderRules(player, operation, rule, id, move, callback);
        NetworkRemoteRequest request = NewRequest(player, operation);
        request.TraderRule = rule?.Clone();
        request.TraderRuleId = id ?? string.Empty;
        request.TraderRuleMove = move;
        callback?.Invoke(TraderAutomationService.ExecuteRuleRequest(GameManager.Instance?.World, player, request));
        return true;
    }

    private static NetworkRemoteRequest NewRequest(EntityPlayerLocal player, NetworkRequestOperation operation)
        => new NetworkRemoteRequest
        {
            EntityId = player.entityId,
            RequestId = System.Threading.Interlocked.Increment(ref _requestSequence),
            ExpectedNetworkId = PlayerNetworkDirectory.GetNetworkId(player) ?? string.Empty,
            Operation = operation
        };
}
