using System;
using System.Collections.Generic;
using System.Linq;

namespace HyperdimensionalNetwork;

public sealed class ConsoleCmdNetworkMembers : ConsoleCmdAbstract
{
    private const string HelpText =
        "Usage: hdn-network <help|info|members|invite <PlayerId|online name>|accept <InviteId>|decline <InviteId>|invite-status|leave|kick <PlayerId|online name>>";

    public override bool IsExecuteOnClient => true;
    public override int DefaultPermissionLevel => 1000;
    public override string[] getCommands() => new[] { "hdn-network", "sn" };
    public override string getDescription() => "Manage your Hyperdimensional Network membership.";
    public override string getHelp() => HelpText;

    public override void Execute(List<string> parameters, CommandSenderInfo senderInfo)
    {
        EntityPlayerLocal player = NetworkRemoteClient.GetLocalPlayer();
        if (!(bool)player)
        {
            Write(parameters == null || parameters.Count == 0 || Eq(parameters[0], "help")
                ? getHelp() : "Run this command from a player client while a world is loaded.");
            return;
        }
        if (parameters == null || parameters.Count == 0 || Eq(parameters[0], "help"))
        {
            ShowHelp(player);
            return;
        }

        string action = parameters[0].ToLowerInvariant();
        switch (action)
        {
            case "info":
                if (!HasOnly(parameters, 1)) { Write("Usage: hdn-network info"); return; }
                QueryInfo(player);
                return;
            case "help":
                if (!HasOnly(parameters, 1)) { Write(getHelp()); return; }
                ShowHelp(player);
                return;
            case "members":
                if (!HasOnly(parameters, 1)) { Write("Usage: hdn-network members"); return; }
                QueryMembers(player);
                return;
            case "invite-status":
                if (!HasOnly(parameters, 1)) { Write("Usage: hdn-network invite-status"); return; }
                QueryInviteStatus(player);
                return;
            case "invite":
                if (parameters.Count != 2) { Write("Usage: hdn-network invite <PlayerId|online name>"); return; }
                Invite(player, parameters[1]);
                return;
            case "accept":
            case "decline":
                if (parameters.Count != 2) { Write("Usage: hdn-network " + action + " <InviteId>; use invite-status to find the ID."); return; }
                SendMembershipRequest(player,
                    action == "accept" ? NetworkRequestOperation.MembershipAcceptInvite : NetworkRequestOperation.MembershipDeclineInvite,
                    string.Empty, parameters[1], action == "accept" ? "Invite accepted; membership transition is preparing." : "Invite declined.");
                return;
            case "leave":
                if (!HasOnly(parameters, 1)) { Write("Usage: hdn-network leave"); return; }
                SendMembershipRequest(player, NetworkRequestOperation.MembershipLeave, string.Empty, string.Empty,
                    "Leave request submitted; the new private network is being prepared.");
                return;
            case "kick":
                if (parameters.Count != 2) { Write("Usage: hdn-network kick <PlayerId|online name>"); return; }
                if (!TryResolvePlayerId(parameters[1], out string targetId, out string resolutionError)) { Write(resolutionError); return; }
                SendMembershipRequest(player, NetworkRequestOperation.MembershipKick, targetId, string.Empty,
                    "Kick request submitted for " + targetId + ".");
                return;
            default:
                Write("Unknown network command. " + getHelp());
                return;
        }
    }

    private static void Invite(EntityPlayerLocal player, string target)
    {
        if (!TryResolvePlayerId(target, out string targetId, out string error)) { Write(error); return; }
        SendMembershipRequest(player, NetworkRequestOperation.MembershipInvite, targetId, string.Empty,
            "Invitation sent to " + targetId + ". They have five minutes to respond.");
    }

    private static void QueryInfo(EntityPlayerLocal player)
    {
        Dispatch(player, NetworkRequestOperation.MembershipQuery, string.Empty, string.Empty, (result, emit) =>
        {
            if (!RequireSuccess(result, emit)) return;
            string role = result.IsNetworkAdministrator ? "network administrator" : "member";
            emit("Network " + result.NetworkId + " | " + role + " | online " + result.MembershipOnlineCount + "/" + result.TotalEntries);
        });
    }

    private static void ShowHelp(EntityPlayerLocal player)
    {
        Dispatch(player, NetworkRequestOperation.MembershipQuery, string.Empty, string.Empty, (result, emit) =>
        {
            if (!RequireSuccess(result, emit)) { emit(HelpText); return; }
            string role = result.IsNetworkAdministrator ? "network administrator" : "member";
            string commands = "help, info, members, invite <PlayerId|online name>, invite-status, accept <InviteId>, decline <InviteId>";
            commands += result.IsNetworkAdministrator ? ", kick <PlayerId|online name>" : ", leave";
            emit("Network role: " + role + ". Commands: " + commands + ".");
            emit("Use invite-status to obtain the current InviteId. Commands run in the F1 console without a leading slash.");
        });
    }

    private static void QueryInviteStatus(EntityPlayerLocal player)
    {
        Dispatch(player, NetworkRequestOperation.MembershipQuery, string.Empty, string.Empty, (result, emit) =>
        {
            if (!RequireSuccess(result, emit)) return;
            if (string.IsNullOrEmpty(result.MembershipOperationId))
            {
                emit("You have no pending network invitation.");
                return;
            }
            emit("Pending invitation from " + result.MembershipInviterName + " to network "
                + result.MembershipTargetNetworkId + ". InviteId=" + result.MembershipOperationId
                + (result.MembershipWillMerge ? " (accepting merges your solo network inventory)." : ".")
                + " Use: hdn-network accept " + result.MembershipOperationId);
        });
    }

    private static void QueryMembers(EntityPlayerLocal player)
    {
        List<NetworkMemberInfo> members = new List<NetworkMemberInfo>();
        string networkId = null;
        int expectedTotal = -1;
        int offset = 0;
        bool executing = true;
        Action<string> emit = message => Emit(message, executing);

        void RequestNextPage()
        {
            bool sent = NetworkMembershipClient.SendRequest(player, NetworkRequestOperation.MembershipQuery,
                string.Empty, string.Empty, result =>
                {
                    if (!result.Success)
                    {
                        emit("Could not read members: " + ErrorText(result.Error));
                        return;
                    }
                    if (networkId != null && !string.Equals(networkId, result.NetworkId, StringComparison.OrdinalIgnoreCase))
                    {
                        emit("The network changed while the member list was loading; run the command again.");
                        return;
                    }
                    if (expectedTotal >= 0 && expectedTotal != result.TotalEntries)
                    {
                        emit("The member list changed while it was loading; run the command again.");
                        return;
                    }
                    networkId = result.NetworkId;
                    expectedTotal = result.TotalEntries;
                    members.AddRange(result.NetworkMembers ?? new List<NetworkMemberInfo>());
                    int nextOffset = result.Page + Math.Max(1, result.PageSize);
                    if (nextOffset < result.TotalEntries && nextOffset > offset)
                    {
                        offset = nextOffset;
                        RequestNextPage();
                        return;
                    }

                    List<string> lines = new List<string> { "Network " + networkId + " members (" + members.Count + "):" };
                    foreach (NetworkMemberInfo member in members)
                        lines.Add((member.IsAdministrator ? "[admin] " : "[member] ") + member.DisplayName
                            + (member.IsOnline ? " (online)" : " (offline)") + " | " + member.PlayerId);
                    EmitMany(lines, executing);
                }, offset, 0);
            if (!sent) emit("Could not send member query. Make sure you are connected to a server.");
        }

        RequestNextPage();
        executing = false;
    }

    private static void SendMembershipRequest(EntityPlayerLocal player, NetworkRequestOperation operation,
        string targetId, string operationId, string successMessage)
        => Dispatch(player, operation, targetId, operationId, (result, emit) =>
        {
            if (!RequireSuccess(result, emit)) return;
            emit(string.Equals(result.Error, "MembershipPreparePending", StringComparison.Ordinal)
                ? "Membership transition is preparing; wait for the completion notification."
                : successMessage);
        });

    private static void Dispatch(EntityPlayerLocal player, NetworkRequestOperation operation, string targetId,
        string operationId, Action<NetworkRemoteResult, Action<string>> callback)
    {
        bool executing = true;
        Action<string> emit = message => Emit(message, executing);
        bool sent = NetworkMembershipClient.SendRequest(player, operation, targetId, operationId,
            result => callback(result, emit));
        executing = false;
        if (!sent) emit("Could not send the request. Make sure you are connected to a server.");
    }

    private static bool RequireSuccess(NetworkRemoteResult result, Action<string> emit)
    {
        if (result != null && result.Success) return true;
        emit("Network request failed: " + ErrorText(result?.Error));
        return false;
    }

    private static bool TryResolvePlayerId(string value, out string playerId, out string error)
    {
        playerId = string.Empty;
        error = string.Empty;
        World world = GameManager.Instance?.World;
        List<EntityPlayer> players = world?.Players?.list?.Where(candidate => (bool)candidate).ToList()
            ?? new List<EntityPlayer>();
        EntityPlayer byId = players.FirstOrDefault(candidate =>
            string.Equals(PlayerNetworkDirectory.GetStablePlayerId(candidate), value, StringComparison.Ordinal));
        if ((bool)byId)
        {
            playerId = PlayerNetworkDirectory.GetStablePlayerId(byId);
            return true;
        }

        EntityPlayer[] byName = players.Where(candidate => string.Equals(candidate.PlayerDisplayName, value,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byName.Length == 1)
        {
            playerId = PlayerNetworkDirectory.GetStablePlayerId(byName[0]);
            return !string.IsNullOrEmpty(playerId);
        }
        if (byName.Length > 1)
        {
            error = "More than one online player has that display name; use a PlayerId instead: "
                + string.Join(", ", byName.Select(candidate => PlayerNetworkDirectory.GetStablePlayerId(candidate)));
            return false;
        }

        // Stable IDs are passed through to the authoritative service, which verifies that the target is online.
        playerId = value?.Trim() ?? string.Empty;
        if (playerId.Length == 0) { error = "PlayerId cannot be empty."; return false; }
        return true;
    }

    private static string ErrorText(string error)
    {
        string localized = NetworkMembershipClient.GetErrorMessage(error);
        return string.IsNullOrEmpty(localized) || string.Equals(localized, error, StringComparison.Ordinal) ? error : localized;
    }

    private static void Emit(string message, bool synchronous)
    {
        string line = "[HyperdimensionalNetwork] " + (message ?? string.Empty);
        if (synchronous) SingletonMonoBehaviour<SdtdConsole>.Instance?.Output(line);
        else GUIWindowConsole.AddLines(new List<string> { line });
    }

    private static void EmitMany(List<string> messages, bool synchronous)
    {
        if (messages == null || messages.Count == 0) return;
        List<string> lines = messages.Select(message => "[HyperdimensionalNetwork] " + message).ToList();
        if (synchronous) foreach (string line in lines) SingletonMonoBehaviour<SdtdConsole>.Instance?.Output(line);
        else GUIWindowConsole.AddLines(lines);
    }

    private static void Write(string message) => SingletonMonoBehaviour<SdtdConsole>.Instance?.Output("[HyperdimensionalNetwork] " + message);
    private static bool HasOnly(List<string> parameters, int count) => parameters != null && parameters.Count == count;
    private static bool Eq(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
