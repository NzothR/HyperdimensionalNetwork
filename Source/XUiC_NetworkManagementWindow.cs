using System;
using System.Collections.Generic;

namespace HyperdimensionalNetwork;

public sealed class XUiC_NetworkManagementWindow : XUiController
{
    private const int Rows = 8;
    private readonly XUiController[] _memberNames = new XUiController[Rows];
    private readonly XUiController[] _memberKick = new XUiController[Rows];
    private readonly XUiController[] _memberTransfer = new XUiController[Rows];
    private readonly XUiController[] _candidateNames = new XUiController[Rows];
    private readonly XUiController[] _candidateInvite = new XUiController[Rows];
    private XUiController _networkSummary, _memberPageLabel, _candidatePageLabel, _status, _leaveButton;
    private int _memberPage, _candidatePage, _memberCount, _candidateCount;
    private bool _isAdministrator, _requestPending;
    private string _networkId = string.Empty;
    private List<NetworkMemberInfo> _members = new List<NetworkMemberInfo>();
    private List<NetworkMemberInfo> _candidates = new List<NetworkMemberInfo>();

    public override void Init()
    {
        base.Init();
        for (int i = 0; i < Rows; i++)
        {
            int row = i;
            _memberNames[i] = GetChildById("managementMemberName" + i);
            _memberKick[i] = GetChildById("btnManagementKick" + i);
            _memberTransfer[i] = GetChildById("btnManagementTransfer" + i);
            _candidateNames[i] = GetChildById("managementCandidateName" + i);
            _candidateInvite[i] = GetChildById("btnManagementInvite" + i);
            Bind("btnManagementKick" + i, (sender, button) => KickMember(row));
            Bind("btnManagementTransfer" + i, (sender, button) => TransferAdministrator(row));
            Bind("btnManagementInvite" + i, (sender, button) => InviteCandidate(row));
        }
        _networkSummary = GetChildById("managementNetworkSummary");
        _memberPageLabel = GetChildById("managementMemberPage");
        _candidatePageLabel = GetChildById("managementCandidatePage");
        _status = GetChildById("managementStatus");
        _leaveButton = GetChildById("btnManagementLeave");
        Bind("btnManagementClose", (sender, button) => xui?.playerUI?.windowManager?.Close("hyperdimensionalNetworkManagement"));
        Bind("btnManagementMemberPrevious", (sender, button) => ChangeMemberPage(-1));
        Bind("btnManagementMemberNext", (sender, button) => ChangeMemberPage(1));
        Bind("btnManagementCandidatePrevious", (sender, button) => ChangeCandidatePage(-1));
        Bind("btnManagementCandidateNext", (sender, button) => ChangeCandidatePage(1));
        Bind("btnManagementLeave", (sender, button) => ConfirmLeave());
        NetworkLog.Debug("Network membership window initialized. rows=" + Rows);
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _memberPage = 0;
        _candidatePage = 0;
        _requestPending = false;
        Refresh();
        NetworkLog.Debug("Network membership window opened.");
    }

    public override void OnClose()
    {
        _requestPending = false;
        base.OnClose();
    }

    internal static void RefreshOpen(LocalPlayerUI playerUI)
    {
        if (!(bool)playerUI || playerUI.xui == null || !playerUI.windowManager.IsWindowOpen("hyperdimensionalNetworkManagement")) return;
        XUiC_NetworkManagementWindow window = playerUI.xui.FindWindowGroupByName("hyperdimensionalNetworkManagement")?
            .GetChildByType<XUiC_NetworkManagementWindow>();
        window?.Refresh();
    }

    private void Refresh()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _requestPending = true;
        SetLabel(_status, Localization.Get("HyperdimensionalNetworkManagementLoading"));
        bool sent = NetworkMembershipClient.SendRequest(player, NetworkRequestOperation.MembershipQuery, string.Empty, string.Empty, result =>
        {
            _requestPending = false;
            if (!result.Success)
            {
                SetLabel(_status, Localization.Get("HyperdimensionalNetworkManagementUnavailable") + ": " + NetworkMembershipClient.GetErrorMessage(result.Error));
                RenderRows();
                return;
            }
            _networkId = result.NetworkId ?? string.Empty;
            _isAdministrator = result.IsNetworkAdministrator;
            _memberCount = result.TotalEntries;
            _candidateCount = result.Amount;
            _members = result.NetworkMembers ?? new List<NetworkMemberInfo>();
            _candidates = result.MembershipCandidates ?? new List<NetworkMemberInfo>();
            string memberSummary = string.Format(Localization.Get("HyperdimensionalNetworkManagementMemberCount"), result.MembershipOnlineCount, result.TotalEntries);
            SetLabel(_networkSummary, Localization.Get("HyperdimensionalNetworkManagementNetwork") + " " + _networkId
                + "  •  " + Localization.Get(_isAdministrator ? "HyperdimensionalNetworkManagementAdministrator" : "HyperdimensionalNetworkManagementMember")
                + "  •  " + memberSummary);
            SetLabel(_status, Localization.Get("HyperdimensionalNetworkManagementReady"));
            SetLabel(_memberPageLabel, (PageNumber(_memberPage, _memberCount)) + " / " + PageCount(_memberCount));
            SetLabel(_candidatePageLabel, (PageNumber(_candidatePage, _candidateCount)) + " / " + PageCount(_candidateCount));
            SetVisible(_leaveButton, !_isAdministrator);
            RenderRows();
            NetworkLog.Debug("Membership roster loaded. network=" + _networkId + "; members=" + _memberCount + "; onlineCandidates=" + _candidateCount
                + "; memberPage=" + _memberPage + "; candidatePage=" + _candidatePage);
        });
        if (!sent)
        {
            _requestPending = false;
            SetLabel(_status, Localization.Get("HyperdimensionalNetworkManagementUnavailable"));
        }
    }

    private void RenderRows()
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        string selfId = PlayerNetworkDirectory.GetStablePlayerId(player);
        for (int i = 0; i < Rows; i++)
        {
            NetworkMemberInfo member = i < _members.Count ? _members[i] : null;
            SetVisible(_memberNames[i], member != null);
            SetVisible(_memberKick[i], member != null && _isAdministrator && !member.IsAdministrator
                && !string.Equals(selfId, member.PlayerId, StringComparison.Ordinal));
            SetVisible(_memberTransfer[i], member != null && _isAdministrator && !member.IsAdministrator);
            if (member != null) SetLabel(_memberNames[i], member.DisplayName + (member.IsAdministrator ? "  ★" : string.Empty)
                + (member.IsOnline ? string.Empty : "  (" + Localization.Get("HyperdimensionalNetworkManagementOffline") + ")"));

            NetworkMemberInfo candidate = i < _candidates.Count ? _candidates[i] : null;
            SetVisible(_candidateNames[i], candidate != null);
            SetVisible(_candidateInvite[i], candidate != null);
            if (candidate != null)
            {
                string suffix = candidate.Eligible ? string.Empty : "  ·  " + Localization.Get(EligibilityKey(candidate.EligibilityError));
                SetLabel(_candidateNames[i], candidate.DisplayName + suffix);
                if (_candidateInvite[i]?.ViewComponent is XUiV_Button button)
                {
                    button.Enabled = !_requestPending && candidate.Eligible;
                    _candidateInvite[i].IsDirty = true;
                }
            }
        }
        SetLabel(_memberPageLabel, PageNumber(_memberPage, _memberCount) + " / " + PageCount(_memberCount));
        SetLabel(_candidatePageLabel, PageNumber(_candidatePage, _candidateCount) + " / " + PageCount(_candidateCount));
    }

    private void InviteCandidate(int row)
    {
        if (_requestPending || row < 0 || row >= _candidates.Count) return;
        NetworkMemberInfo candidate = _candidates[row];
        if (!candidate.Eligible) return;
        SendAction(NetworkRequestOperation.MembershipInvite, candidate.PlayerId, string.Empty);
    }

    private void KickMember(int row)
    {
        if (_requestPending || row < 0 || row >= _members.Count) return;
        NetworkMemberInfo member = _members[row];
        Confirm(Localization.Get("HyperdimensionalNetworkManagementKickTitle"),
            Localization.Get("HyperdimensionalNetworkManagementKickConfirm") + " " + member.DisplayName,
            () => SendAction(NetworkRequestOperation.MembershipKick, member.PlayerId, string.Empty));
    }

    private void TransferAdministrator(int row)
    {
        if (_requestPending || row < 0 || row >= _members.Count) return;
        NetworkMemberInfo member = _members[row];
        Confirm(Localization.Get("HyperdimensionalNetworkManagementTransferTitle"),
            Localization.Get("HyperdimensionalNetworkManagementTransferConfirm") + " " + member.DisplayName,
            () => SendAction(NetworkRequestOperation.MembershipTransferAdministrator, member.PlayerId, string.Empty));
    }

    private void ConfirmLeave()
    {
        Confirm(Localization.Get("HyperdimensionalNetworkManagementLeaveTitle"),
            Localization.Get("HyperdimensionalNetworkManagementLeaveConfirm"),
            () => SendAction(NetworkRequestOperation.MembershipLeave, string.Empty, string.Empty));
    }

    private void Confirm(string title, string message, Action confirmed)
    {
        try
        {
            XUiC_MessageBoxWindowGroup.ShowConfirmCancel(xui, title, message, _onConfirm: confirmed,
                _onCancel: null, _openMainMenuOnClose: false, _modal: true, _cancelOnOutsideClick: true);
        }
        catch (Exception ex) { NetworkLog.Error("Membership confirmation dialog failed. exception=" + ex); }
    }

    private void SendAction(NetworkRequestOperation operation, string targetPlayerId, string operationId)
    {
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        if (!(bool)player) return;
        _requestPending = true;
        SetLabel(_status, Localization.Get("HyperdimensionalNetworkMembershipWorking"));
        bool sent = NetworkMembershipClient.SendRequest(player, operation, targetPlayerId, operationId, result =>
        {
            _requestPending = false;
            string statusKey = result.Success ? "HyperdimensionalNetworkMembershipComplete" : "HyperdimensionalNetworkMembershipFailed";
            SetLabel(_status, Localization.Get(statusKey) + (string.IsNullOrEmpty(result.Error) ? string.Empty : ": " + NetworkMembershipClient.GetErrorMessage(result.Error)));
            if (result.Success && string.Equals(result.Error, "MembershipPreparePending", StringComparison.Ordinal))
                SetLabel(_status, Localization.Get("HyperdimensionalNetworkMembershipPreparing"));
            Refresh();
            NetworkLog.Debug("Membership UI action completed. operation=" + operation + "; target=" + targetPlayerId
                + "; success=" + result.Success + "; error=" + result.Error);
        });
        if (!sent)
        {
            _requestPending = false;
            SetLabel(_status, Localization.Get("HyperdimensionalNetworkManagementBusy"));
        }
        RenderRows();
    }

    private void ChangeMemberPage(int delta)
    {
        int pageCount = Math.Max(1, PageCount(_memberCount));
        _memberPage = Math.Max(0, Math.Min(pageCount - 1, _memberPage + delta));
        Refresh();
    }

    private void ChangeCandidatePage(int delta)
    {
        int pageCount = Math.Max(1, PageCount(_candidateCount));
        _candidatePage = Math.Max(0, Math.Min(pageCount - 1, _candidatePage + delta));
        Refresh();
    }

    private void Bind(string id, XUiEvent_OnPressEventHandler handler)
    {
        XUiController control = GetChildById(id);
        if (control != null) control.OnPress += handler;
    }

    private static int PageCount(int count) => Math.Max(1, (count + Rows - 1) / Rows);
    private static int PageNumber(int page, int count) => Math.Min(Math.Max(1, page + 1), PageCount(count));
    private static string EligibilityKey(string error) => error == "AdministratorHasMembers"
        ? "HyperdimensionalNetworkManagementSharedAdministrator"
        : error == "NetworkFull" ? "HyperdimensionalNetworkManagementNetworkFull"
        : error == "MembershipBusy" ? "HyperdimensionalNetworkManagementMemberBusy"
        : "HyperdimensionalNetworkManagementInvitePending";

    private static void SetVisible(XUiController controller, bool visible)
    {
        if (controller?.ViewComponent != null) controller.ViewComponent.IsVisible = visible;
    }

    private static void SetLabel(XUiController controller, string text)
    {
        if (controller?.ViewComponent is XUiV_Label label)
        {
            label.Text = text ?? string.Empty;
            controller.IsDirty = true;
        }
    }
}
