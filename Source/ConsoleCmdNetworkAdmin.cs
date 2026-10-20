using System;
using System.Collections.Generic;
using System.Linq;

namespace HyperdimensionalNetwork;

public sealed class ConsoleCmdNetworkAdmin : ConsoleCmdAbstract
{
    public override string[] getCommands() => new[] { "hdn-network-admin" };
    public override string getDescription() => "Server administrator maintenance for Hyperdimensional Network.";
    public override string getHelp() =>
        "Usage: hdn-network-admin <help|move-items <sourceNetworkId> <targetNetworkId>|clear <networkId> --confirm|cleanup-quest-item <networkId> <itemName> <count> --confirm|player-info <PlayerId>|list-players [offset] [count]|set-admin <PlayerId>|trader-sessions|trader-force-close <sessionId>>";

    public override void Execute(List<string> parameters, CommandSenderInfo senderInfo)
    {
        if (!IsServerAdministrator(senderInfo))
        {
            Write("This command is restricted to server administrators.");
            return;
        }
        if (parameters == null || parameters.Count == 0 || Eq(parameters[0], "help"))
        {
            Write(getHelp());
            return;
        }

        switch (parameters[0].ToLowerInvariant())
        {
            case "move-items": MoveItems(parameters); return;
            case "clear": ClearNetwork(parameters); return;
            case "cleanup-quest-item": CleanupQuestItem(parameters); return;
            case "player-info": PlayerInfo(parameters); return;
            case "list-players": ListPlayers(parameters); return;
            case "set-admin": SetAdministrator(parameters); return;
            case "trader-sessions": ListTraderSessions(parameters); return;
            case "trader-force-close": ForceCloseTraderSession(parameters); return;
            default:
                Write("Unknown maintenance command. " + getHelp());
                return;
        }
    }

    private static void MoveItems(List<string> parameters)
    {
        if (parameters.Count != 3) { Write("Usage: hdn-network-admin move-items <sourceNetworkId> <targetNetworkId>"); return; }
        string sourceId = parameters[1];
        string targetId = parameters[2];
        if (string.Equals(sourceId, targetId, StringComparison.OrdinalIgnoreCase)) { Write("Source and target networks must differ."); return; }
        if (!PlayerNetworkDirectory.TryGetNetwork(sourceId, out PlayerNetworkRecord sourceNetwork)) { Write("Source network was not found."); return; }
        if (!PlayerNetworkDirectory.TryGetNetwork(targetId, out PlayerNetworkRecord targetNetwork)) { Write("Target network was not found."); return; }
        sourceId = sourceNetwork.NetworkId;
        targetId = targetNetwork.NetworkId;
        if (HasActiveNetworkWork(sourceId) || HasActiveNetworkWork(targetId))
        { Write("Close aggregate trader sessions and wait for automatic trader rules to finish before moving inventory."); return; }
        if (!NetworkMembershipService.TryAcquireNetworkMaintenance(new[] { sourceId, targetId }, out IDisposable lease))
        { Write("One of these networks is currently changing membership or undergoing maintenance."); return; }
        using (lease)
        {
            if (!NetworkStorage.TryTransferContents(sourceId, targetId, out List<StorageAddition> moved, out string error))
            {
                NetworkLog.Info("Server inventory migration failed. source=" + sourceId + "; target=" + targetId + "; error=" + error);
                Write("Inventory migration failed: " + error);
                return;
            }
            NetworkLog.Info("Server inventory migration completed. source=" + sourceId + "; target=" + targetId
                + "; variants=" + (moved?.Count ?? 0));
            Write("Moved " + (moved?.Count ?? 0) + " item variants from " + sourceId + " to " + targetId + ". The source network and members were retained.");
        }
    }

    private static void ClearNetwork(List<string> parameters)
    {
        if (parameters.Count != 3 || !Eq(parameters[2], "--confirm"))
        { Write("Usage: hdn-network-admin clear <networkId> --confirm (clears the network inventory permanently)"); return; }
        string networkId = parameters[1];
        if (!PlayerNetworkDirectory.TryGetNetwork(networkId, out PlayerNetworkRecord network)) { Write("Network was not found."); return; }
        networkId = network.NetworkId;
        if (HasActiveNetworkWork(networkId))
        { Write("Close aggregate trader sessions and wait for automatic trader rules to finish before clearing inventory."); return; }
        if (!NetworkMembershipService.TryAcquireNetworkMaintenance(new[] { networkId }, out IDisposable lease))
        { Write("The network is currently changing membership or undergoing maintenance."); return; }
        using (lease)
        {
            if (!NetworkStorage.TryClearNetworkInventory(networkId, out int clearedVariants, out string error))
            {
                NetworkLog.Info("Server inventory clear failed. network=" + networkId + "; error=" + error);
                Write("Network inventory clear failed: " + error);
                return;
            }
            NetworkLog.Info("Server inventory cleared. network=" + networkId + "; variants=" + clearedVariants);
            Write("Cleared " + clearedVariants + " item variants. Network ID and membership are unchanged.");
        }
    }

    private static void CleanupQuestItem(List<string> parameters)
    {
        if (parameters.Count != 5 || !Eq(parameters[4], "--confirm")
            || !int.TryParse(parameters[3], out int count) || count < 1 || count > 64)
        { Write("Usage: hdn-network-admin cleanup-quest-item <networkId> <itemName> <count 1..64> --confirm"); return; }
        if (!PlayerNetworkDirectory.TryGetNetwork(parameters[1], out PlayerNetworkRecord network))
        { Write("Network was not found."); return; }
        ItemValue item = ItemClass.GetItem(parameters[2]);
        if (item?.ItemClass?.IsQuestItem != true || NetworkItemPolicy.CanEnterOrDestroy(item))
        { Write("The item must be a non-droppable quest item."); return; }
        EntityPlayer actor = GameManager.Instance?.World?.Players?.list?.FirstOrDefault(player => (bool)player
            && string.Equals(PlayerNetworkDirectory.GetNetworkId(player), network.NetworkId, StringComparison.OrdinalIgnoreCase));
        if (!(bool)actor) { Write("A member of this network must be online."); return; }
        int removed = QuestNetworkItemService.RemoveMatching(actor, item, count);
        NetworkLog.Debug("Administrator cleaned legacy quest item. network=" + network.NetworkId + "; item="
            + item.type + "; requested=" + count + "; removed=" + removed);
        Write(removed > 0 ? "Removed " + removed + " legacy quest item(s) from the network."
            : "No matching quest item was removed; check the item name and network stock.");
    }

    private static void PlayerInfo(List<string> parameters)
    {
        if (parameters.Count != 2) { Write("Usage: hdn-network-admin player-info <PlayerId>"); return; }
        string playerId = parameters[1];
        string networkId = PlayerNetworkDirectory.GetNetworkIdForPlayerId(playerId);
        if (string.IsNullOrEmpty(networkId) || !PlayerNetworkDirectory.TryGetNetwork(networkId, out PlayerNetworkRecord network))
        { Write("Player is not present in the current network directory."); return; }
        EntityPlayer online = FindOnlinePlayer(playerId);
        string role = string.Equals(network.AdminPlayerId, playerId, StringComparison.Ordinal) ? "administrator" : "member";
        Write(PlayerDisplayName(playerId, online) + " | " + playerId + " | network=" + networkId + " | " + role
            + " | " + ((bool)online ? "online" : "offline"));
    }

    private static void ListPlayers(List<string> parameters)
    {
        if (parameters.Count > 3) { Write("Usage: hdn-network-admin list-players [offset] [count]"); return; }
        if (!TryParseNonnegative(parameters, 1, 0, out int offset) || !TryParseNonnegative(parameters, 2, 50, out int count)
            || count < 1 || count > 200)
        { Write("Offset must be nonnegative; count must be between 1 and 200."); return; }

        List<(string PlayerId, string NetworkId, string AdminId)> all = PlayerNetworkDirectory.GetNetworksSnapshot()
            .SelectMany(network => network.Members.Select(playerId =>
                (PlayerId: playerId, NetworkId: network.NetworkId, AdminId: network.AdminPlayerId)))
            .OrderBy(item => item.NetworkId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PlayerId, StringComparer.Ordinal)
            .ToList();
        if (all.Count == 0) { Write("No players are present in the current network directory."); return; }
        int start = Math.Min(offset, all.Count);
        int end = Math.Min(all.Count, start + count);
        Write("Known network players " + (start + 1) + "-" + end + " of " + all.Count + ":");
        for (int i = start; i < end; i++)
        {
            var item = all[i];
            EntityPlayer online = FindOnlinePlayer(item.PlayerId);
            string role = string.Equals(item.AdminId, item.PlayerId, StringComparison.Ordinal) ? "administrator" : "member";
            Write(PlayerDisplayName(item.PlayerId, online) + " | " + item.PlayerId + " | network=" + item.NetworkId
                + " | " + role + " | " + ((bool)online ? "online" : "offline"));
        }
        if (end < all.Count) Write("More results: hdn-network-admin list-players " + end + " " + count);
    }

    private static void SetAdministrator(List<string> parameters)
    {
        if (parameters.Count != 2) { Write("Usage: hdn-network-admin set-admin <PlayerId>"); return; }
        if (!NetworkMembershipService.SetAdministratorFromServer(GameManager.Instance?.World, parameters[1],
            out string networkId, out string previousAdmin, out string error))
        {
            NetworkLog.Info("Server administrator reassignment failed. target=" + PlayerNetworkDirectory.Summarize(parameters[1]) + "; error=" + error);
            Write("Could not change network administrator: " + error);
            return;
        }
        if (string.Equals(previousAdmin, parameters[1], StringComparison.Ordinal))
            Write(parameters[1] + " is already the administrator of network " + networkId + ".");
        else
            Write("Network " + networkId + " administrator changed from " + previousAdmin + " to " + parameters[1] + ".");
    }

    private static void ListTraderSessions(List<string> parameters)
    {
        if (parameters.Count != 1) { Write("Usage: hdn-network-admin trader-sessions"); return; }
        List<string> sessions = TraderMarketplaceService.GetSessionSummaries();
        if (sessions.Count == 0) { Write("No active aggregate trader market sessions."); return; }
        foreach (string session in sessions) Write(session);
    }

    private static void ForceCloseTraderSession(List<string> parameters)
    {
        if (parameters.Count != 2) { Write("Usage: hdn-network-admin trader-force-close <sessionId>"); return; }
        bool closed = TraderMarketplaceService.ForceClose(parameters[1]);
        NetworkLog.Info("Server trader session force-close requested. session=" + parameters[1] + "; closed=" + closed);
        Write(closed ? "Trader market session closed and its locks released." : "Trader market session was not found.");
    }

    private static bool IsServerAdministrator(CommandSenderInfo sender)
    {
        if (sender.IsLocalGame) return true;
        if (sender.RemoteClientInfo != null)
            return GameManager.Instance?.adminTools?.Users?.GetUserPermissionLevel(sender.RemoteClientInfo) == 0;
        // Remote console connections are authenticated by the game's console server and command permission table.
        return sender.NetworkConnection != null;
    }

    private static bool HasActiveNetworkWork(string networkId)
        => TraderMarketplaceService.HasSessionForNetwork(networkId)
            || TraderAutomationService.IsNetworkExecutionActive(networkId);

    private static EntityPlayer FindOnlinePlayer(string playerId)
        => GameManager.Instance?.World?.Players?.list?.FirstOrDefault(player => (bool)player
            && string.Equals(PlayerNetworkDirectory.GetStablePlayerId(player), playerId, StringComparison.Ordinal));

    private static string PlayerDisplayName(string playerId, EntityPlayer online)
    {
        if ((bool)online) return online.PlayerDisplayName;
        try
        {
            var players = GameManager.Instance?.persistentPlayers?.Players;
            if (players != null)
                foreach (var entry in players)
                    if (string.Equals(entry.Key?.CombinedString, playerId, StringComparison.Ordinal))
                        return string.IsNullOrWhiteSpace(entry.Value?.PlayerName?.DisplayName) ? playerId : entry.Value.PlayerName.DisplayName;
        }
        catch (Exception ex) { NetworkLog.Debug("Offline player name lookup failed. exception=" + ex.Message); }
        return playerId;
    }

    private static bool TryParseNonnegative(List<string> parameters, int index, int defaultValue, out int value)
    {
        if (parameters.Count <= index) { value = defaultValue; return true; }
        return int.TryParse(parameters[index], out value) && value >= 0;
    }

    private static void Write(string message) => SingletonMonoBehaviour<SdtdConsole>.Instance?.Output("[HyperdimensionalNetwork] " + message);
    private static bool Eq(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
