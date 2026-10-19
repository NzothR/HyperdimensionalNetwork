using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class NetworkMembershipService
{
    private enum TransitionKind { Join, Merge, Leave, Kick }

    private sealed class Invitation
    {
        public string InviterPlayerId;
        public string InviterName;
        public string TargetNetworkId;
        public float ExpiresAt;
    }

    private sealed class Transition
    {
        public string Id;
        public TransitionKind Kind;
        public string ActorPlayerId;
        public string TargetPlayerId;
        public string SourceNetworkId;
        public string TargetNetworkId;
        public int TargetEntityId;
        public float ExpiresAt;
        public World World;
    }

    private const float InvitationLifetimeSeconds = 300f;
    private const float PrepareLifetimeSeconds = 45f;
    private const int RowsPerPage = 8;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, Invitation> Invitations = new Dictionary<string, Invitation>(StringComparer.Ordinal);
    private static readonly Dictionary<string, Transition> Transitions = new Dictionary<string, Transition>(StringComparer.Ordinal);
    private static readonly Dictionary<string, Transition> PlayerTransitions = new Dictionary<string, Transition>(StringComparer.Ordinal);
    private static readonly HashSet<string> BlockedNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    [ThreadStatic] private static Dictionary<string, int> InternalNetworkScopes;
    private static float _nextSweep;

    private sealed class InternalScope : IDisposable
    {
        private string _networkId;
        public InternalScope(string networkId) { _networkId = networkId; }
        public void Dispose()
        {
            string networkId = _networkId;
            _networkId = null;
            if (string.IsNullOrEmpty(networkId) || InternalNetworkScopes == null || !InternalNetworkScopes.TryGetValue(networkId, out int count)) return;
            if (count <= 1) InternalNetworkScopes.Remove(networkId);
            else InternalNetworkScopes[networkId] = count - 1;
        }
    }

    internal static IDisposable EnterInternalNetworkScope(string networkId)
    {
        if (string.IsNullOrWhiteSpace(networkId)) return new InternalScope(null);
        InternalNetworkScopes = InternalNetworkScopes ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        InternalNetworkScopes[networkId] = InternalNetworkScopes.TryGetValue(networkId, out int count) ? count + 1 : 1;
        return new InternalScope(networkId);
    }

    internal static bool IsStorageBlocked(EntityPlayer player, string networkId)
    {
        if (string.IsNullOrWhiteSpace(networkId)) return false;
        if (InternalNetworkScopes != null && InternalNetworkScopes.TryGetValue(networkId, out int scopes) && scopes > 0) return false;
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        lock (Sync)
            return (!string.IsNullOrEmpty(playerId) && PlayerTransitions.ContainsKey(playerId)) || BlockedNetworks.Contains(networkId);
    }

    internal static bool IsPlayerBlocked(EntityPlayer player)
    {
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(playerId)) return false;
        lock (Sync) return PlayerTransitions.ContainsKey(playerId);
    }

    internal static bool IsNetworkFrozen(string networkId)
    {
        if (string.IsNullOrWhiteSpace(networkId)) return false;
        lock (Sync) return BlockedNetworks.Contains(networkId);
    }

    internal static bool ShouldRejectRequest(EntityPlayer player, string networkId, NetworkRequestOperation operation)
    {
        if (operation == NetworkRequestOperation.MembershipAcknowledgePrepare) return false;
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        bool blocked;
        bool isTransitionOwner;
        lock (Sync)
        {
            isTransitionOwner = !string.IsNullOrEmpty(playerId) && PlayerTransitions.ContainsKey(playerId);
            blocked = isTransitionOwner || (!string.IsNullOrEmpty(networkId) && BlockedNetworks.Contains(networkId));
        }
        if (!blocked) return false;
        bool refund = operation == NetworkRequestOperation.ReturnAuthorization
            || operation == NetworkRequestOperation.ReturnWithdrawal || operation == NetworkRequestOperation.AmmoBankRefund;
        return !(isTransitionOwner && refund);
    }

    internal static bool IsMembershipOperation(NetworkRequestOperation operation)
        => (byte)operation >= (byte)NetworkRequestOperation.MembershipQuery
            && (byte)operation <= (byte)NetworkRequestOperation.MembershipAcknowledgePrepare;

    internal static void Update(World world)
    {
        if (Time.realtimeSinceStartup < _nextSweep) return;
        _nextSweep = Time.realtimeSinceStartup + 1f;
        List<KeyValuePair<string, Invitation>> invitations;
        List<Transition> expiredTransitions;
        lock (Sync)
        {
            invitations = Invitations.ToList();
            expiredTransitions = Transitions.Values.Where(transition => transition.ExpiresAt <= Time.realtimeSinceStartup).ToList();
            foreach (Transition transition in expiredTransitions) ReleaseTransitionUnsafe(transition);
        }
        foreach (KeyValuePair<string, Invitation> entry in invitations)
        {
            bool recipientPreparing;
            lock (Sync)
                recipientPreparing = PlayerTransitions.TryGetValue(entry.Key, out Transition pendingTransition)
                    && (pendingTransition.Kind == TransitionKind.Join || pendingTransition.Kind == TransitionKind.Merge);
            if (recipientPreparing) continue;
            string invalidation = string.Empty;
            bool recipientOnline = (bool)FindOnlinePlayer(world, entry.Key);
            if (entry.Value.ExpiresAt <= Time.realtimeSinceStartup) invalidation = "InviteExpired";
            else if (!recipientOnline) invalidation = "";
            else if (!PlayerNetworkDirectory.TryGetNetwork(entry.Value.TargetNetworkId, out PlayerNetworkRecord targetNetwork)
                || !targetNetwork.Members.Contains(entry.Value.InviterPlayerId)
                || !string.Equals(PlayerNetworkDirectory.GetNetworkIdForPlayerId(entry.Value.InviterPlayerId), entry.Value.TargetNetworkId, StringComparison.OrdinalIgnoreCase))
                invalidation = "InviteUnavailable";
            if (invalidation == "" && recipientOnline) continue;
            bool removed;
            lock (Sync)
                removed = Invitations.TryGetValue(entry.Key, out Invitation current) && ReferenceEquals(current, entry.Value)
                    && Invitations.Remove(entry.Key);
            if (!removed || !recipientOnline || string.IsNullOrEmpty(invalidation)) continue;
            SendInviteInvalidated(world, entry.Key, entry.Value, invalidation);
        }
        foreach (Transition transition in expiredTransitions)
        {
            if (transition.Kind == TransitionKind.Join || transition.Kind == TransitionKind.Merge)
            {
                Invitation expiredInvite = null;
                lock (Sync)
                    if (Invitations.TryGetValue(transition.TargetPlayerId, out Invitation current)
                        && string.Equals(current.TargetNetworkId, transition.TargetNetworkId, StringComparison.OrdinalIgnoreCase))
                    { expiredInvite = current; Invitations.Remove(transition.TargetPlayerId); }
                if (expiredInvite != null) SendInviteInvalidated(world, transition.TargetPlayerId, expiredInvite, "MembershipPrepareTimeout");
            }
            NetworkLog.Error("Network membership transition expired before the affected client prepared. operation=" + transition.Id + "; kind=" + transition.Kind
                + "; target=" + PlayerNetworkDirectory.Summarize(transition.TargetPlayerId));
            NetworkRemoteResult expired = new NetworkRemoteResult
            {
                MembershipNotice = NetworkMembershipNotice.Completed,
                MembershipOperationId = transition.Id,
                MembershipTargetPlayerId = transition.TargetPlayerId,
                Success = false,
                Error = "MembershipPrepareTimeout"
            };
            SendToPlayer(world, transition.ActorPlayerId, CloneNotice(expired));
            if (!string.Equals(transition.ActorPlayerId, transition.TargetPlayerId, StringComparison.Ordinal))
                SendToPlayer(world, transition.TargetPlayerId, CloneNotice(expired));
        }
    }

    internal static void OnWorldShuttingDown()
    {
        lock (Sync)
        {
            Invitations.Clear();
            Transitions.Clear();
            PlayerTransitions.Clear();
            BlockedNetworks.Clear();
        }
        NetworkLog.Debug("In-memory invitations and pending membership transitions cleared for world shutdown.");
    }

    internal static NetworkRemoteResult ProcessRequest(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        switch (request.Operation)
        {
            case NetworkRequestOperation.MembershipQuery: return Query(world, player, request);
            case NetworkRequestOperation.MembershipInvite: return Invite(world, player, request);
            case NetworkRequestOperation.MembershipAcceptInvite: return AcceptInvite(world, player, request);
            case NetworkRequestOperation.MembershipDeclineInvite: return DeclineInvite(player, request);
            case NetworkRequestOperation.MembershipLeave: return Leave(world, player, request);
            case NetworkRequestOperation.MembershipKick: return Kick(world, player, request);
            case NetworkRequestOperation.MembershipTransferAdministrator: return TransferAdministrator(world, player, request);
            case NetworkRequestOperation.MembershipAcknowledgePrepare: return AcknowledgePrepare(world, player, request);
            default: return Failure(request, "UnsupportedMembershipOperation");
        }
    }

    internal static NetworkRemoteResult Query(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(networkId) || !PlayerNetworkDirectory.TryGetNetwork(networkId, out PlayerNetworkRecord network))
            return Failure(request, "NetworkUnavailable");
        List<NetworkMemberInfo> allMembers = network.Members
            .Select(id => MakeMember(world, network, id, false, string.Empty))
            .OrderByDescending(member => member.IsAdministrator).ThenBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        List<NetworkMemberInfo> allCandidates = GetOnlineCandidates(world, network);
        int onlineMemberCount = allMembers.Count(member => member.IsOnline);
        int memberOffset = Math.Min(Math.Max(0, request.Page), Math.Max(0, allMembers.Count - RowsPerPage));
        int candidateOffset = Math.Min(Math.Max(0, request.MembershipCandidatePage), Math.Max(0, allCandidates.Count - RowsPerPage));
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            NetworkId = networkId,
            IsNetworkAdministrator = PlayerNetworkDirectory.IsNetworkAdministrator(player),
            TotalEntries = allMembers.Count,
            Page = memberOffset,
            PageSize = RowsPerPage,
            MembershipOnlineCount = onlineMemberCount,
            NetworkMembers = allMembers.Skip(memberOffset).Take(RowsPerPage).ToList(),
            MembershipCandidates = allCandidates.Skip(candidateOffset).Take(RowsPerPage).ToList(),
            Amount = allCandidates.Count,
            MembershipSourceNetworkId = networkId
        };
    }

    private static List<NetworkMemberInfo> GetOnlineCandidates(World world, PlayerNetworkRecord network)
    {
        if (world?.Players?.list == null) return new List<NetworkMemberInfo>();
        List<NetworkMemberInfo> candidates = new List<NetworkMemberInfo>();
        foreach (EntityPlayer candidate in world.Players.list.Where(candidate => (bool)candidate))
        {
            string playerId = PlayerNetworkDirectory.GetStablePlayerId(candidate);
            if (string.IsNullOrEmpty(playerId) || network.Members.Contains(playerId)) continue;
            if (candidates.Any(entry => string.Equals(entry.PlayerId, playerId, StringComparison.Ordinal))) continue;
            string candidateNetworkId = PlayerNetworkDirectory.GetNetworkIdForPlayerId(playerId);
            PlayerNetworkRecord candidateNetwork = null;
            if (!string.IsNullOrEmpty(candidateNetworkId)) PlayerNetworkDirectory.TryGetNetwork(candidateNetworkId, out candidateNetwork);
            string eligibilityError = string.Empty;
            lock (Sync)
            {
                if (Invitations.ContainsKey(playerId)) eligibilityError = "InvitePending";
                else if (PlayerTransitions.ContainsKey(playerId)) eligibilityError = "MembershipBusy";
            }
            if (string.IsNullOrEmpty(eligibilityError) && candidateNetwork != null
                && string.Equals(candidateNetwork.AdminPlayerId, playerId, StringComparison.Ordinal)
                && candidateNetwork.Members.Count > 1)
                eligibilityError = "AdministratorHasMembers";
            if (string.IsNullOrEmpty(eligibilityError) && network.Members.Count >= 4096)
                eligibilityError = "NetworkFull";
            candidates.Add(new NetworkMemberInfo
            {
                PlayerId = playerId,
                DisplayName = SafeName(candidate),
                IsOnline = true,
                IsAdministrator = candidateNetwork != null && string.Equals(candidateNetwork.AdminPlayerId, playerId, StringComparison.Ordinal),
                Eligible = string.IsNullOrEmpty(eligibilityError),
                EligibilityError = eligibilityError
            });
        }
        return candidates.OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static NetworkMemberInfo MakeMember(World world, PlayerNetworkRecord network, string playerId, bool isOnline, string error)
    {
        EntityPlayer online = world?.Players?.list?.FirstOrDefault(player => (bool)player
            && string.Equals(PlayerNetworkDirectory.GetStablePlayerId(player), playerId, StringComparison.Ordinal));
        return new NetworkMemberInfo
        {
            PlayerId = playerId,
            DisplayName = (bool)online ? SafeName(online) : SafeOfflineName(playerId),
            IsOnline = (bool)online || isOnline,
            IsAdministrator = string.Equals(network.AdminPlayerId, playerId, StringComparison.Ordinal),
            Eligible = true,
            EligibilityError = error ?? string.Empty
        };
    }

    private static string SafeName(EntityPlayer player)
    {
        try { return string.IsNullOrWhiteSpace(player?.PlayerDisplayName) ? "Player" : LimitName(player.PlayerDisplayName); }
        catch { return "Player"; }
    }

    private static string SafeOfflineName(string playerId)
    {
        try
        {
            var players = GameManager.Instance?.persistentPlayers?.Players;
            if (players != null)
                foreach (var entry in players)
                    if (string.Equals(entry.Key?.CombinedString, playerId, StringComparison.Ordinal))
                        return LimitName(entry.Value?.PlayerName?.DisplayName ?? string.Empty);
        }
        catch (Exception ex) { NetworkLog.Debug("Offline member display-name lookup failed. exception=" + ex.Message); }
        return "Player " + (playerId?.Length <= 8 ? playerId : playerId?.Substring(Math.Max(0, playerId.Length - 8)));
    }

    private static string LimitName(string name) => string.IsNullOrWhiteSpace(name) ? "Player" : (name.Length <= 48 ? name : name.Substring(0, 48));

    private static NetworkRemoteResult Invite(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        string actorId = PlayerNetworkDirectory.GetStablePlayerId(player);
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(actorId) || string.IsNullOrEmpty(networkId) || string.IsNullOrEmpty(request.MembershipTargetPlayerId)) return Failure(request, "InvalidInviteTarget");
        if (!PlayerNetworkDirectory.TryGetNetwork(networkId, out PlayerNetworkRecord network) || !network.Members.Contains(actorId)) return Failure(request, "PermissionDenied");
        if (network.Members.Count >= 4096) return Failure(request, "NetworkMemberLimit");
        EntityPlayer target = FindOnlinePlayer(world, request.MembershipTargetPlayerId);
        if (!(bool)target) return Failure(request, "PlayerOffline");
        string targetNetworkId = PlayerNetworkDirectory.GetNetworkId(target);
        if (string.IsNullOrEmpty(targetNetworkId) || !PlayerNetworkDirectory.TryGetNetwork(targetNetworkId, out PlayerNetworkRecord targetNetwork))
            return Failure(request, "NetworkUnavailable");
        if (string.Equals(targetNetworkId, networkId, StringComparison.OrdinalIgnoreCase)) return Failure(request, "AlreadyNetworkMember");
        if (string.Equals(targetNetwork.AdminPlayerId, request.MembershipTargetPlayerId, StringComparison.Ordinal)
            && targetNetwork.Members.Count > 1) return Failure(request, "AdministratorHasMembers");
        lock (Sync)
        {
            if (Invitations.ContainsKey(request.MembershipTargetPlayerId)) return Failure(request, "InvitePending");
            if (PlayerTransitions.ContainsKey(request.MembershipTargetPlayerId)) return Failure(request, "MembershipBusy");
            Invitations[request.MembershipTargetPlayerId] = new Invitation
            {
                InviterPlayerId = actorId,
                InviterName = SafeName(player),
                TargetNetworkId = networkId,
                ExpiresAt = Time.realtimeSinceStartup + InvitationLifetimeSeconds
            };
        }
        NetworkLog.Debug("Network invite created. inviter=" + PlayerNetworkDirectory.Summarize(actorId) + "; target="
            + PlayerNetworkDirectory.Summarize(request.MembershipTargetPlayerId) + "; network=" + networkId);
        NetworkRpcServer.SendUnsolicited(target.entityId, new NetworkRemoteResult
        {
            Success = true,
            MembershipNotice = NetworkMembershipNotice.Invite,
            MembershipTargetPlayerId = request.MembershipTargetPlayerId,
            MembershipSourceNetworkId = networkId,
            MembershipInviterName = SafeName(player),
            MembershipWillMerge = string.Equals(targetNetwork.AdminPlayerId, request.MembershipTargetPlayerId, StringComparison.Ordinal)
                && targetNetwork.Members.Count == 1
        });
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, MembershipTargetPlayerId = request.MembershipTargetPlayerId };
    }

    private static NetworkRemoteResult AcceptInvite(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        Invitation invitation;
        lock (Sync)
        {
            if (string.IsNullOrEmpty(playerId) || !Invitations.TryGetValue(playerId, out invitation)) return Failure(request, "InviteUnavailable");
            if (invitation.ExpiresAt <= Time.realtimeSinceStartup) { Invitations.Remove(playerId); return Failure(request, "InviteExpired"); }
        }
        string sourceNetworkId = PlayerNetworkDirectory.GetNetworkId(player);
        lock (Sync)
            if (BlockedNetworks.Contains(invitation.TargetNetworkId) || PlayerTransitions.ContainsKey(invitation.InviterPlayerId))
            {
                Invitations.Remove(playerId);
                return Failure(request, "MembershipBusy");
            }
        if (string.IsNullOrEmpty(sourceNetworkId) || !PlayerNetworkDirectory.TryGetNetwork(sourceNetworkId, out PlayerNetworkRecord source)
            || !PlayerNetworkDirectory.TryGetNetwork(invitation.TargetNetworkId, out PlayerNetworkRecord invitedNetwork)
            || !invitedNetwork.Members.Contains(invitation.InviterPlayerId))
        {
            if (!string.IsNullOrEmpty(playerId)) lock (Sync) Invitations.Remove(playerId);
            return Failure(request, "MembershipChanged");
        }
        bool administrator = string.Equals(source.AdminPlayerId, playerId, StringComparison.Ordinal);
        if (administrator && source.Members.Count != 1)
        {
            lock (Sync) Invitations.Remove(playerId);
            return Failure(request, "AdministratorHasMembers");
        }
        NetworkRemoteResult started = StartTransition(world, player, playerId, administrator ? TransitionKind.Merge : TransitionKind.Join,
            sourceNetworkId, invitation.TargetNetworkId, request.RequestId);
        if (!started.Success)
            lock (Sync) Invitations.Remove(playerId);
        return started;
    }

    private static NetworkRemoteResult DeclineInvite(EntityPlayer player, NetworkRemoteRequest request)
    {
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        bool removed;
        lock (Sync) removed = !string.IsNullOrEmpty(playerId) && Invitations.Remove(playerId);
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = removed, Error = removed ? string.Empty : "InviteUnavailable" };
    }

    private static NetworkRemoteResult Leave(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        string sourceNetworkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(sourceNetworkId) || PlayerNetworkDirectory.IsNetworkAdministrator(player)) return Failure(request, "AdministratorCannotLeave");
        return StartTransition(world, player, playerId, TransitionKind.Leave, sourceNetworkId, string.Empty, request.RequestId);
    }

    private static NetworkRemoteResult Kick(World world, EntityPlayer actor, NetworkRemoteRequest request)
    {
        string actorId = PlayerNetworkDirectory.GetStablePlayerId(actor);
        string networkId = PlayerNetworkDirectory.GetNetworkId(actor);
        string targetId = request.MembershipTargetPlayerId;
        if (string.IsNullOrEmpty(actorId) || string.IsNullOrEmpty(networkId) || !PlayerNetworkDirectory.IsNetworkAdministrator(actor)) return Failure(request, "PermissionDenied");
        if (string.IsNullOrEmpty(targetId) || string.Equals(actorId, targetId, StringComparison.Ordinal)) return Failure(request, "CannotKickSelf");
        if (!PlayerNetworkDirectory.TryGetNetwork(networkId, out PlayerNetworkRecord record) || !record.Members.Contains(targetId)
            || string.Equals(record.AdminPlayerId, targetId, StringComparison.Ordinal)) return Failure(request, "MemberUnavailable");
        EntityPlayer target = FindOnlinePlayer(world, targetId);
        if (!(bool)target)
        {
            lock (Sync)
                if (PlayerTransitions.ContainsKey(targetId)) return Failure(request, "MembershipBusy");
            if (!PlayerNetworkDirectory.TryMoveMemberToNewNetwork(targetId, networkId, actorId, true, out string offlineNewNetwork, out string offlineError))
                return Failure(request, offlineError);
            lock (Sync) Invitations.Remove(targetId);
            BroadcastChanged(world, networkId, offlineNewNetwork, targetId);
            NetworkLog.Debug("Offline member was removed into a new private network. actor=" + PlayerNetworkDirectory.Summarize(actorId)
                + "; target=" + PlayerNetworkDirectory.Summarize(targetId) + "; source=" + networkId + "; new=" + offlineNewNetwork);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, MembershipTargetNetworkId = offlineNewNetwork };
        }
        return StartTransition(world, target, actorId, TransitionKind.Kick, networkId, string.Empty, request.RequestId, targetId);
    }

    private static NetworkRemoteResult TransferAdministrator(World world, EntityPlayer actor, NetworkRemoteRequest request)
    {
        string actorId = PlayerNetworkDirectory.GetStablePlayerId(actor);
        string networkId = PlayerNetworkDirectory.GetNetworkId(actor);
        string targetId = request.MembershipTargetPlayerId;
        if (!PlayerNetworkDirectory.TryTransferAdministrator(actorId, targetId, networkId, out string error)) return Failure(request, error);
        BroadcastChanged(world, networkId, networkId, targetId);
        SendToPlayer(world, actorId, new NetworkRemoteResult
        {
            Success = true,
            NetworkId = networkId,
            Revision = NetworkStorage.GetRevision(actor),
            MembershipNotice = NetworkMembershipNotice.Changed,
            MembershipTargetPlayerId = actorId,
            MembershipSourceNetworkId = networkId,
            MembershipTargetNetworkId = networkId
        });
        NetworkLog.Debug("Network administrator transferred. network=" + networkId + "; from=" + PlayerNetworkDirectory.Summarize(actorId)
            + "; to=" + PlayerNetworkDirectory.Summarize(targetId));
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true };
    }

    private static NetworkRemoteResult StartTransition(World world, EntityPlayer target, string actorId, TransitionKind kind,
        string sourceNetworkId, string targetNetworkId, long requestId, string targetIdOverride = null)
    {
        string targetId = targetIdOverride ?? PlayerNetworkDirectory.GetStablePlayerId(target);
        if (string.IsNullOrEmpty(targetId)) return new NetworkRemoteResult { RequestId = requestId, Success = false, Error = "PlayerIdentityUnavailable" };
        Transition transition = new Transition
        {
            Id = Guid.NewGuid().ToString("N"), Kind = kind, ActorPlayerId = actorId, TargetPlayerId = targetId,
            SourceNetworkId = sourceNetworkId, TargetNetworkId = targetNetworkId, TargetEntityId = target.entityId,
            ExpiresAt = Time.realtimeSinceStartup + PrepareLifetimeSeconds, World = world
        };
        lock (Sync)
        {
            if (PlayerTransitions.ContainsKey(targetId)) return new NetworkRemoteResult { RequestId = requestId, Success = false, Error = "MembershipBusy" };
            if (kind == TransitionKind.Merge && BlockedNetworks.Contains(sourceNetworkId)) return new NetworkRemoteResult { RequestId = requestId, Success = false, Error = "MembershipBusy" };
            Transitions.Add(transition.Id, transition);
            PlayerTransitions.Add(targetId, transition);
            if (kind == TransitionKind.Merge) BlockedNetworks.Add(sourceNetworkId);
        }
        NetworkLog.Debug("Membership preparation requested. operation=" + transition.Id + "; kind=" + kind + "; player="
            + PlayerNetworkDirectory.Summarize(targetId) + "; source=" + sourceNetworkId + "; target=" + targetNetworkId);
        NetworkRpcServer.SendUnsolicited(target.entityId, new NetworkRemoteResult
        {
            Success = true,
            MembershipNotice = NetworkMembershipNotice.Prepare,
            MembershipOperationId = transition.Id,
            MembershipTargetPlayerId = targetId,
            MembershipSourceNetworkId = sourceNetworkId,
            MembershipTargetNetworkId = targetNetworkId
        });
        return new NetworkRemoteResult
        {
            RequestId = requestId,
            Success = true,
            Error = "MembershipPreparePending",
            MembershipNotice = NetworkMembershipNotice.Completed,
            MembershipOperationId = transition.Id,
            MembershipTargetPlayerId = targetId,
            MembershipSourceNetworkId = sourceNetworkId,
            MembershipTargetNetworkId = targetNetworkId
        };
    }

    private static NetworkRemoteResult AcknowledgePrepare(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        Transition transition;
        lock (Sync)
        {
            if (string.IsNullOrEmpty(playerId) || !Transitions.TryGetValue(request.MembershipOperationId ?? string.Empty, out transition)
                || !string.Equals(transition.TargetPlayerId, playerId, StringComparison.Ordinal)) return Failure(request, "MembershipPrepareUnavailable");
            if (transition.ExpiresAt <= Time.realtimeSinceStartup)
            {
                ReleaseTransitionUnsafe(transition);
                return Failure(request, "MembershipPrepareTimeout");
            }
        }
        return CommitTransition(world, player, transition, request.RequestId);
    }

    private static NetworkRemoteResult CommitTransition(World world, EntityPlayer targetPlayer, Transition transition, long requestId)
    {
        string error = string.Empty;
        string newNetworkId = string.Empty;
        bool success = false;
        bool targetNetworkFrozen = false;
        string oldNetworkId = transition.SourceNetworkId;
        try
        {
            bool identityMatches = ReferenceEquals(world, transition.World)
                && string.Equals(PlayerNetworkDirectory.GetNetworkId(targetPlayer), transition.SourceNetworkId, StringComparison.OrdinalIgnoreCase);
            if (identityMatches) TraderMarketplaceService.CloseForMembershipChange(targetPlayer.entityId);
            if (!identityMatches)
                error = "MembershipChanged";
            else if (transition.Kind == TransitionKind.Join)
                success = PlayerNetworkDirectory.TryDirectSwitch(transition.TargetPlayerId, transition.SourceNetworkId, transition.TargetNetworkId, out error);
            else if (transition.Kind == TransitionKind.Merge)
            {
                lock (Sync)
                {
                    targetNetworkFrozen = BlockedNetworks.Add(transition.TargetNetworkId);
                    if (!targetNetworkFrozen) error = "MembershipBusy";
                }
                if (targetNetworkFrozen)
                {
                    using (EnterInternalNetworkScope(transition.SourceNetworkId))
                    using (EnterInternalNetworkScope(transition.TargetNetworkId))
                    {
                        if (!NetworkStorage.TryTransferContents(transition.SourceNetworkId, transition.TargetNetworkId, out List<StorageAddition> moved, out error))
                            success = false;
                        else
                        {
                            TraderAutomationService.CancelNetworkRuntime(transition.SourceNetworkId, "network-merge");
                            if (PlayerNetworkDirectory.TryMergeMember(transition.TargetPlayerId, transition.SourceNetworkId, transition.TargetNetworkId, out error))
                            {
                                success = true;
                                FinalizeMergedNetwork(world, targetPlayer, transition, moved);
                            }
                            else if (moved.Count > 0 && !NetworkStorage.TryTransferBatch(transition.TargetNetworkId, transition.SourceNetworkId, moved, out string rollbackError))
                            {
                                NetworkLog.Error("Could not compensate inventory after the network merge directory commit failed. operation=" + transition.Id + "; error=" + rollbackError);
                                error = "NetworkMergeRollbackFailed";
                            }
                        }
                    }
                }
            }
            else if (transition.Kind == TransitionKind.Leave)
                success = PlayerNetworkDirectory.TryMoveMemberToNewNetwork(transition.TargetPlayerId, transition.SourceNetworkId,
                    transition.TargetPlayerId, false, out newNetworkId, out error);
            else if (transition.Kind == TransitionKind.Kick)
                success = PlayerNetworkDirectory.TryMoveMemberToNewNetwork(transition.TargetPlayerId, transition.SourceNetworkId,
                    transition.ActorPlayerId, true, out newNetworkId, out error);

            if (success)
            {
                if (transition.Kind != TransitionKind.Merge)
                    newNetworkId = PlayerNetworkDirectory.GetNetworkId(targetPlayer);
                NetworkLog.Debug("Membership transition committed. operation=" + transition.Id + "; kind=" + transition.Kind
                    + "; player=" + PlayerNetworkDirectory.Summarize(transition.TargetPlayerId) + "; old=" + oldNetworkId + "; new=" + newNetworkId);
            }
        }
        catch (Exception ex)
        {
            error = "MembershipTransitionFailed";
            NetworkLog.Error("Membership transition failed. operation=" + transition.Id + "; kind=" + transition.Kind + "; exception=" + ex);
        }
        finally
        {
            lock (Sync)
            {
                if (targetNetworkFrozen) BlockedNetworks.Remove(transition.TargetNetworkId);
                ReleaseTransitionUnsafe(transition);
                if (success || transition.Kind == TransitionKind.Join || transition.Kind == TransitionKind.Merge)
                    Invitations.Remove(transition.TargetPlayerId);
            }
        }
        if (success)
        {
            BroadcastChanged(world, oldNetworkId, newNetworkId, transition.TargetPlayerId);
        }
        NetworkRemoteResult completed = new NetworkRemoteResult
        {
            RequestId = requestId,
            Success = success,
            Error = success ? string.Empty : (error ?? "MembershipTransitionFailed"),
            MembershipNotice = NetworkMembershipNotice.Completed,
            MembershipOperationId = transition.Id,
            MembershipTargetPlayerId = transition.TargetPlayerId,
            MembershipSourceNetworkId = oldNetworkId,
            MembershipTargetNetworkId = success ? newNetworkId : transition.TargetNetworkId,
            NetworkId = PlayerNetworkDirectory.GetNetworkId(targetPlayer) ?? string.Empty,
            Revision = NetworkStorage.GetRevision(targetPlayer)
        };
        if (!string.Equals(transition.ActorPlayerId, transition.TargetPlayerId, StringComparison.Ordinal))
            SendToPlayer(world, transition.ActorPlayerId, CloneNotice(completed));
        return completed;
    }

    private static void FinalizeMergedNetwork(World world, EntityPlayer player, Transition transition, List<StorageAddition> moved)
    {
        if (!NetworkStorage.RemoveEmptyNetworkStorage(transition.SourceNetworkId, out string storageError))
            NetworkLog.Error("Merged network storage cleanup was deferred. source=" + transition.SourceNetworkId + "; error=" + storageError);
        if (!NetworkFeatureSettingsService.RemoveNetwork(transition.SourceNetworkId, out string settingsError))
            NetworkLog.Error("Merged network feature settings cleanup was deferred. source=" + transition.SourceNetworkId + "; error=" + settingsError);
        if (!TraderAutomationService.RemoveNetworkData(transition.SourceNetworkId, out string rulesError))
            NetworkLog.Error("Merged network trader-rule cleanup was deferred. source=" + transition.SourceNetworkId + "; error=" + rulesError);
        if (moved != null && moved.Count > 0)
        {
            using (EnterInternalNetworkScope(transition.TargetNetworkId))
                TraderAutomationService.OnNetworkDeposit(player, moved);
        }
    }

    private static void BroadcastChanged(World world, string sourceNetworkId, string targetNetworkId, string targetPlayerId)
    {
        InvalidateInvitationsForMembershipChange(world, sourceNetworkId, targetPlayerId);
        if (world?.Players?.list == null) return;
        HashSet<int> sent = new HashSet<int>();
        foreach (EntityPlayer player in world.Players.list.Where(player => (bool)player))
        {
            string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
            string networkId = PlayerNetworkDirectory.GetNetworkId(player);
            if (!sent.Add(player.entityId)) continue;
            if (!string.Equals(playerId, targetPlayerId, StringComparison.Ordinal)
                && !string.Equals(networkId, sourceNetworkId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(networkId, targetNetworkId, StringComparison.OrdinalIgnoreCase)) continue;
            NetworkRpcServer.SendUnsolicited(player.entityId, new NetworkRemoteResult
            {
                Success = true,
                NetworkId = networkId ?? string.Empty,
                Revision = NetworkStorage.GetRevision(player),
                MembershipNotice = NetworkMembershipNotice.Changed,
                MembershipTargetPlayerId = targetPlayerId,
                MembershipSourceNetworkId = sourceNetworkId ?? string.Empty,
                MembershipTargetNetworkId = targetNetworkId ?? string.Empty
            });
        }
    }

    private static void InvalidateInvitationsForMembershipChange(World world, string sourceNetworkId, string changedPlayerId)
    {
        bool sourceRetired = !string.IsNullOrEmpty(sourceNetworkId) && !PlayerNetworkDirectory.TryGetNetwork(sourceNetworkId, out _);
        List<KeyValuePair<string, Invitation>> invalidated;
        lock (Sync)
        {
            invalidated = Invitations.Where(pair => string.Equals(pair.Value.TargetNetworkId, sourceNetworkId, StringComparison.OrdinalIgnoreCase)
                && (sourceRetired || string.Equals(pair.Value.InviterPlayerId, changedPlayerId, StringComparison.Ordinal)))
                .ToList();
            foreach (KeyValuePair<string, Invitation> entry in invalidated)
                if (Invitations.TryGetValue(entry.Key, out Invitation current) && ReferenceEquals(current, entry.Value)) Invitations.Remove(entry.Key);
        }
        foreach (KeyValuePair<string, Invitation> entry in invalidated)
            SendInviteInvalidated(world, entry.Key, entry.Value, "InviteUnavailable");
    }

    private static void SendInviteInvalidated(World world, string recipientId, Invitation invitation, string error)
    {
        SendToPlayer(world, recipientId, new NetworkRemoteResult
        {
            Success = false,
            Error = error ?? "InviteUnavailable",
            MembershipNotice = NetworkMembershipNotice.InviteInvalidated,
            MembershipTargetPlayerId = recipientId ?? string.Empty,
            MembershipSourceNetworkId = invitation?.TargetNetworkId ?? string.Empty,
            MembershipInviterName = invitation?.InviterName ?? string.Empty
        });
        NetworkLog.Debug("Network invitation invalidated. recipient=" + PlayerNetworkDirectory.Summarize(recipientId)
            + "; inviter=" + PlayerNetworkDirectory.Summarize(invitation?.InviterPlayerId) + "; reason=" + error);
    }

    private static void SendToPlayer(World world, string playerId, NetworkRemoteResult notice)
    {
        EntityPlayer player = FindOnlinePlayer(world, playerId);
        if ((bool)player) NetworkRpcServer.SendUnsolicited(player.entityId, notice);
    }

    private static EntityPlayer FindOnlinePlayer(World world, string playerId)
        => world?.Players?.list?.FirstOrDefault(player => (bool)player
            && string.Equals(PlayerNetworkDirectory.GetStablePlayerId(player), playerId, StringComparison.Ordinal));

    private static void ReleaseTransitionUnsafe(Transition transition)
    {
        if (transition == null) return;
        Transitions.Remove(transition.Id);
        if (PlayerTransitions.TryGetValue(transition.TargetPlayerId, out Transition current) && ReferenceEquals(current, transition))
            PlayerTransitions.Remove(transition.TargetPlayerId);
        if (transition.Kind == TransitionKind.Merge) BlockedNetworks.Remove(transition.SourceNetworkId);
    }

    private static NetworkRemoteResult CloneNotice(NetworkRemoteResult source)
        => new NetworkRemoteResult
        {
            Success = source.Success, Error = source.Error, MembershipNotice = source.MembershipNotice,
            MembershipOperationId = source.MembershipOperationId, MembershipTargetPlayerId = source.MembershipTargetPlayerId,
            MembershipSourceNetworkId = source.MembershipSourceNetworkId, MembershipTargetNetworkId = source.MembershipTargetNetworkId,
            NetworkId = source.NetworkId, Revision = source.Revision
        };

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error ?? "MembershipFailed" };
}
