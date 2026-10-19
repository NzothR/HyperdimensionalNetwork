using System;

namespace HyperdimensionalNetwork;

internal static class NetworkMembershipClient
{
    private static string _preparingOperationId = string.Empty;
    private static bool _prepareAcknowledgementSent;
    private static bool _cancelRequested;
    private static bool _inviteDialogOpen;
    private static string _activeInviteNetworkId = string.Empty;
    private static long _localRequestId;

    internal static bool IsPreparing => !string.IsNullOrEmpty(_preparingOperationId);

    internal static void OnWorldShuttingDown()
    {
        _preparingOperationId = string.Empty;
        _prepareAcknowledgementSent = false;
        _cancelRequested = false;
        _inviteDialogOpen = false;
        _activeInviteNetworkId = string.Empty;
        NetworkRemoteClient.ResetMembershipIdentityForWorldShutdown();
    }

    internal static void HandleNotice(int entityId, NetworkRemoteResult result)
    {
        if (result == null) return;
        switch (result.MembershipNotice)
        {
            case NetworkMembershipNotice.Invite:
                ShowInvite(result);
                break;
            case NetworkMembershipNotice.InviteInvalidated:
                CloseInviteDialog(result.MembershipSourceNetworkId);
                EntityPlayerLocal invitedPlayer = NetworkRemoteClient.GetLocalPlayer();
                if ((bool)invitedPlayer)
                    GameManager.ShowTooltip(invitedPlayer, Localization.Get(result.Error == "InviteExpired"
                        ? "HyperdimensionalNetworkInviteExpired" : "HyperdimensionalNetworkInviteUnavailable"));
                NetworkLog.Debug("Network invite invalidation received. entity=" + entityId + "; network=" + result.MembershipSourceNetworkId
                    + "; error=" + result.Error);
                break;
            case NetworkMembershipNotice.Prepare:
                _preparingOperationId = result.MembershipOperationId ?? string.Empty;
                _prepareAcknowledgementSent = false;
                _cancelRequested = false;
                CloseMembershipSensitiveWindows(entityId);
                EntityPlayerLocal preparingPlayer = NetworkRemoteClient.GetLocalPlayer();
                if (CraftingChainOrderService.IsActive(preparingPlayer))
                {
                    _cancelRequested = CraftingChainOrderService.RequestCancel(preparingPlayer)
                        || CraftingChainOrderService.TryGetStatus(preparingPlayer, out _, out _, out _, out bool alreadyCancelling) && alreadyCancelling;
                    NetworkLog.Debug("Crafting-chain cancellation requested before membership change. player=" + entityId
                        + "; operation=" + _preparingOperationId + "; accepted=" + _cancelRequested);
                }
                NetworkLog.Debug("Membership preparation received. entity=" + entityId + "; operation=" + _preparingOperationId);
                break;
            case NetworkMembershipNotice.Completed:
                if (string.Equals(result.Error, "MembershipPreparePending", StringComparison.Ordinal)) return;
                if (!string.IsNullOrEmpty(_preparingOperationId)
                    && string.Equals(_preparingOperationId, result.MembershipOperationId, StringComparison.Ordinal))
                {
                    _preparingOperationId = string.Empty;
                    _prepareAcknowledgementSent = false;
                    _cancelRequested = false;
                }
                ShowTransitionResult(result);
                XUiC_NetworkManagementWindow.RefreshOpen(LocalPlayerUI.GetUIForPrimaryPlayer());
                break;
            case NetworkMembershipNotice.Changed:
                EntityPlayerLocal player = NetworkRemoteClient.GetLocalPlayer();
                if ((bool)player && string.Equals(PlayerNetworkDirectory.GetStablePlayerId(player), result.MembershipTargetPlayerId, StringComparison.Ordinal))
                {
                    AutoRestockClient.OnNetworkIdentityChanged(player);
                    TraderTransactionService.LoadPersonalPayoutMode(player);
                    CloseMembershipSensitiveWindows(entityId);
                }
                XUiC_NetworkManagementWindow.RefreshOpen(LocalPlayerUI.GetUIForPrimaryPlayer());
                NetworkLog.Debug("Membership cache invalidation received. entity=" + entityId + "; source="
                    + result.MembershipSourceNetworkId + "; target=" + result.MembershipTargetNetworkId);
                break;
        }
    }

    internal static void Update()
    {
        if (!IsPreparing || _prepareAcknowledgementSent) return;
        EntityPlayerLocal player = NetworkRemoteClient.GetLocalPlayer();
        if (!(bool)player) return;
        if (CraftingChainOrderService.IsActive(player))
        {
            if (!_cancelRequested)
            {
                _cancelRequested = CraftingChainOrderService.RequestCancel(player)
                    || CraftingChainOrderService.TryGetStatus(player, out _, out _, out _, out bool alreadyCancelling) && alreadyCancelling;
                NetworkLog.Debug("Membership preparation retried crafting-chain cancellation. player=" + player.entityId
                    + "; operation=" + _preparingOperationId + "; accepted=" + _cancelRequested);
            }
            return;
        }
        if (NetworkRemoteClient.IsRemoteClient && NetworkRemoteClient.PendingRequestCount > 0) return;
        string operationId = _preparingOperationId;
        if (SendRequest(player, NetworkRequestOperation.MembershipAcknowledgePrepare, string.Empty, _preparingOperationId, result =>
        {
            if (!result.Success)
            {
                NetworkLog.Error("Membership preparation acknowledgement failed. operation=" + operationId + "; error=" + result.Error);
                _preparingOperationId = string.Empty;
                _prepareAcknowledgementSent = false;
                _cancelRequested = false;
                ShowTransitionResult(result);
            }
            else NetworkLog.Debug("Membership preparation acknowledged and committed. operation=" + result.MembershipOperationId);
        }))
        {
            _prepareAcknowledgementSent = true;
            NetworkLog.Debug("Membership preparation acknowledged after outstanding work settled. player=" + player.entityId + "; operation=" + _preparingOperationId);
        }
    }

    internal static bool SendRequest(EntityPlayerLocal player, NetworkRequestOperation operation, string targetPlayerId,
        string operationId, Action<NetworkRemoteResult> callback, int memberPage = 0, int candidatePage = 0)
    {
        if (!(bool)player || !NetworkMembershipService.IsMembershipOperation(operation)) return false;
        if (NetworkRemoteClient.IsRemoteClient)
            return NetworkRemoteClient.RequestMembership(player, operation, targetPlayerId, operationId, memberPage, candidatePage, callback);
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (!(bool)connection || !connection.IsServer) return false;
        NetworkRemoteRequest request = new NetworkRemoteRequest
        {
            EntityId = player.entityId,
            RequestId = ++_localRequestId,
            ExpectedNetworkId = PlayerNetworkDirectory.GetNetworkId(player) ?? string.Empty,
            Operation = operation,
            MembershipTargetPlayerId = targetPlayerId ?? string.Empty,
            MembershipOperationId = operationId ?? string.Empty,
            Page = Math.Max(0, memberPage),
            MembershipCandidatePage = Math.Max(0, candidatePage)
        };
        NetworkRemoteResult result = NetworkMembershipService.ProcessRequest(GameManager.Instance?.World, player, request);
        result.NetworkId = PlayerNetworkDirectory.GetNetworkId(player) ?? string.Empty;
        result.Revision = NetworkStorage.GetRevision(player);
        result.MembershipRevision = PlayerNetworkDirectory.GetMembershipRevision();
        if (result.MembershipNotice != NetworkMembershipNotice.None) HandleNotice(player.entityId, result);
        callback?.Invoke(result);
        return true;
    }

    private static void ShowInvite(NetworkRemoteResult invite)
    {
        LocalPlayerUI playerUI = LocalPlayerUI.GetUIForPrimaryPlayer();
        EntityPlayerLocal player = playerUI?.entityPlayer;
        if (!(bool)player || playerUI.xui == null) return;
        string text = Localization.Get("HyperdimensionalNetworkInviteFrom") + " " + invite.MembershipInviterName + "\n\n"
            + Localization.Get(invite.MembershipWillMerge
                ? "HyperdimensionalNetworkInviteMergeText" : "HyperdimensionalNetworkInviteSwitchText");
        try
        {
            XUiC_MessageBoxWindowGroup.ShowCustom(playerUI.xui,
                Localization.Get("HyperdimensionalNetworkInviteTitle"), text, string.Empty,
                dialog =>
                {
                    dialog.buttons[0].DefaultConfirm(invite.MembershipWillMerge
                        ? "HyperdimensionalNetworkInviteMergeAccept" : "HyperdimensionalNetworkInviteAccept", () =>
                    {
                        _inviteDialogOpen = false;
                        _activeInviteNetworkId = string.Empty;
                        SendRequest(player, NetworkRequestOperation.MembershipAcceptInvite, string.Empty, invite.MembershipOperationId,
                            ShowTransitionResult);
                    });
                    dialog.buttons[2].DefaultCancel("HyperdimensionalNetworkInviteDecline", () =>
                    {
                        _inviteDialogOpen = false;
                        _activeInviteNetworkId = string.Empty;
                        SendRequest(player, NetworkRequestOperation.MembershipDeclineInvite, string.Empty, invite.MembershipOperationId, null);
                    });
                }, _openMainMenuOnClose: false, _modal: true, _buttonOnOutsideClick: 2, _buttonOnExternalClose: 2);
            _inviteDialogOpen = true;
            _activeInviteNetworkId = invite.MembershipSourceNetworkId ?? string.Empty;
            NetworkLog.Debug("Native network invite dialog opened. entity=" + player.entityId + "; network=" + invite.MembershipSourceNetworkId);
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Native network invite dialog failed. entity=" + player.entityId + "; exception=" + ex);
        }
    }

    private static void CloseInviteDialog(string networkId)
    {
        if (!_inviteDialogOpen || !string.Equals(_activeInviteNetworkId, networkId ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return;
        _inviteDialogOpen = false;
        _activeInviteNetworkId = string.Empty;
        LocalPlayerUI playerUI = LocalPlayerUI.GetUIForPrimaryPlayer();
        string messageBoxId = XUiC_MessageBoxWindowGroup.id;
        if ((bool)playerUI && !string.IsNullOrEmpty(messageBoxId)) playerUI.windowManager?.Close(messageBoxId);
        NetworkLog.Debug("Expired network invitation dialog closed. network=" + (networkId ?? string.Empty));
    }

    private static void ShowTransitionResult(NetworkRemoteResult result)
    {
        EntityPlayerLocal player = NetworkRemoteClient.GetLocalPlayer();
        if (!(bool)player || result == null) return;
        if (string.Equals(result.Error, "MembershipPreparePending", StringComparison.Ordinal))
        {
            GameManager.ShowTooltip(player, Localization.Get("HyperdimensionalNetworkMembershipPreparing"));
            return;
        }
        string key = result.Success ? "HyperdimensionalNetworkMembershipComplete" : "HyperdimensionalNetworkMembershipFailed";
        string message = Localization.Get(key);
        if (!result.Success && !string.IsNullOrEmpty(result.Error)) message += ": " + GetErrorMessage(result.Error);
        GameManager.ShowTooltip(player, message);
        NetworkLog.Debug("Membership operation result shown. success=" + result.Success + "; error=" + result.Error
            + "; source=" + result.MembershipSourceNetworkId + "; target=" + result.MembershipTargetNetworkId);
    }

    internal static string GetErrorMessage(string error)
    {
        string key = error switch
        {
            "InvitePending" => "HyperdimensionalNetworkErrorInvitePending",
            "InviteUnavailable" => "HyperdimensionalNetworkErrorInviteUnavailable",
            "InviteExpired" => "HyperdimensionalNetworkErrorInviteExpired",
            "AdministratorHasMembers" => "HyperdimensionalNetworkErrorAdministratorHasMembers",
            "AdministratorCannotLeave" => "HyperdimensionalNetworkErrorAdministratorCannotLeave",
            "AlreadyNetworkMember" => "HyperdimensionalNetworkErrorAlreadyNetworkMember",
            "CannotKickSelf" => "HyperdimensionalNetworkErrorCannotKickSelf",
            "MemberUnavailable" => "HyperdimensionalNetworkErrorMemberUnavailable",
            "MembershipBusy" => "HyperdimensionalNetworkErrorMembershipBusy",
            "MembershipChanged" => "HyperdimensionalNetworkErrorMembershipChanged",
            "MembershipPrepareTimeout" => "HyperdimensionalNetworkErrorPrepareTimeout",
            "NetworkDirectorySaveFailed" => "HyperdimensionalNetworkErrorSaveFailed",
            "NetworkMemberLimit" => "HyperdimensionalNetworkErrorNetworkFull",
            "NetworkUnavailable" => "HyperdimensionalNetworkErrorNetworkUnavailable",
            "PermissionDenied" => "HyperdimensionalNetworkErrorPermissionDenied",
            "PlayerOffline" => "HyperdimensionalNetworkErrorPlayerOffline",
            "PlayerIdentityUnavailable" => "HyperdimensionalNetworkErrorIdentityUnavailable",
            "NetworkStorageTransferWriteFailed" => "HyperdimensionalNetworkErrorTransferFailed",
            "NetworkMergeRollbackFailed" => "HyperdimensionalNetworkErrorRollbackFailed",
            "MembershipTransitionFailed" => "HyperdimensionalNetworkErrorTransitionFailed",
            _ => string.Empty
        };
        return string.IsNullOrEmpty(key) ? (error ?? string.Empty) : Localization.Get(key);
    }

    private static void CloseMembershipSensitiveWindows(int entityId)
    {
        LocalPlayerUI playerUI = LocalPlayerUI.GetUIForPrimaryPlayer();
        if (!(bool)playerUI) return;
        GUIWindowManager windows = playerUI.windowManager;
        if (windows == null) return;
        windows.Close("hyperdimensionalNetworkManagement");
        windows.Close("hyperdimensionalNetwork");
        windows.Close("hyperdimensionalCraftingChain");
        windows.Close("hyperdimensionalTraderMarket");
        windows.Close("hyperdimensionalTraderRules");
        NetworkLog.Debug("Membership preparation closed network feature windows. entity=" + entityId);
    }
}
