using System;
using System.Collections.Generic;

namespace HyperdimensionalNetwork;

public sealed class XUiC_NetworkManagementWindow : XUiController
{
    private const int Rows = 8;
    private readonly XUiController[] _memberNames = new XUiController[Rows];
    private readonly XUiController[] _memberLines = new XUiController[Rows];
    private readonly XUiController[] _memberKick = new XUiController[Rows];
    private readonly XUiController[] _memberKickLabels = new XUiController[Rows];
    private readonly XUiController[] _memberTransfer = new XUiController[Rows];
    private readonly XUiController[] _memberTransferLabels = new XUiController[Rows];
    private readonly XUiController[] _candidateNames = new XUiController[Rows];
    private readonly XUiController[] _candidateLines = new XUiController[Rows];
    private readonly XUiController[] _candidateInvite = new XUiController[Rows];
    private readonly XUiController[] _candidateInviteLabels = new XUiController[Rows];
    private XUiController _networkSummary, _memberPageLabel, _candidatePageLabel, _memberScrollArea, _candidateScrollArea, _status, _leaveButton, _leaveButtonLabel;
    private int _memberOffset, _candidateOffset, _memberCount, _candidateCount;
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
            _memberLines[i] = GetChildById("memberRowLine" + i);
            _memberKick[i] = GetChildById("btnManagementKick" + i);
            _memberKickLabels[i] = GetChildById("managementKickLabel" + i);
            _memberTransfer[i] = GetChildById("btnManagementTransfer" + i);
            _memberTransferLabels[i] = GetChildById("managementTransferLabel" + i);
            _candidateNames[i] = GetChildById("managementCandidateName" + i);
            _candidateLines[i] = GetChildById("candidateRowLine" + i);
            _candidateInvite[i] = GetChildById("btnManagementInvite" + i);
            _candidateInviteLabels[i] = GetChildById("managementInviteLabel" + i);
            if (_memberKick[i] != null) _memberKick[i].OnScroll += OnMemberScroll;
            if (_memberTransfer[i] != null) _memberTransfer[i].OnScroll += OnMemberScroll;
            if (_candidateInvite[i] != null) _candidateInvite[i].OnScroll += OnCandidateScroll;
            Bind("btnManagementKick" + i, (sender, button) => KickMember(row));
            Bind("btnManagementTransfer" + i, (sender, button) => TransferAdministrator(row));
            Bind("btnManagementInvite" + i, (sender, button) => InviteCandidate(row));
        }
        _networkSummary = GetChildById("managementNetworkSummary");
        _memberPageLabel = GetChildById("managementMemberPage");
        _candidatePageLabel = GetChildById("managementCandidatePage");
        _memberScrollArea = GetChildById("managementMemberList");
        _candidateScrollArea = GetChildById("managementCandidateList");
        if (_memberScrollArea != null) _memberScrollArea.OnScroll += OnMemberScroll;
        if (_candidateScrollArea != null) _candidateScrollArea.OnScroll += OnCandidateScroll;
        _status = GetChildById("managementStatus");
        _leaveButton = GetChildById("btnManagementLeave");
        _leaveButtonLabel = GetChildById("managementLeaveLabel");
        Bind("btnManagementClose", (sender, button) => xui?.playerUI?.windowManager?.Close("hyperdimensionalNetworkManagement"));
        Bind("btnManagementLeave", (sender, button) => ConfirmLeave());
        RenderRows();
        NetworkLog.Debug("Network membership window initialized. rows=" + Rows);
    }

    public override void OnOpen()
    {
        base.OnOpen();
        _memberOffset = 0;
        _candidateOffset = 0;
        _requestPending = false;
        _members.Clear();
        _candidates.Clear();
        _memberCount = 0;
        _candidateCount = 0;
        _isAdministrator = false;
        _networkId = string.Empty;
        RenderRows();
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
        int requestedMemberOffset = _memberOffset;
        int requestedCandidateOffset = _candidateOffset;
        bool sent = NetworkMembershipClient.SendRequest(player, NetworkRequestOperation.MembershipQuery, string.Empty, string.Empty, result =>
        {
            _requestPending = false;
            if (!result.Success)
            {
                SetLabel(_status, Localization.Get("HyperdimensionalNetworkManagementUnavailable") + ": " + NetworkMembershipClient.GetErrorMessage(result.Error));
                RenderRows();
                if (_memberOffset != requestedMemberOffset || _candidateOffset != requestedCandidateOffset) Refresh();
                return;
            }
            _networkId = result.NetworkId ?? string.Empty;
            _isAdministrator = result.IsNetworkAdministrator;
            _memberCount = result.TotalEntries;
            _candidateCount = result.Amount;
            _memberOffset = Math.Min(_memberOffset, Math.Max(0, _memberCount - Rows));
            _candidateOffset = Math.Min(_candidateOffset, Math.Max(0, _candidateCount - Rows));
            if (_memberOffset == requestedMemberOffset) _memberOffset = result.Page;
            _members = result.NetworkMembers ?? new List<NetworkMemberInfo>();
            _candidates = result.MembershipCandidates ?? new List<NetworkMemberInfo>();
            string memberSummary = string.Format(Localization.Get("HyperdimensionalNetworkManagementMemberCount"), result.MembershipOnlineCount, result.TotalEntries);
            SetLabel(_networkSummary, Localization.Get("HyperdimensionalNetworkManagementNetwork") + " " + _networkId
                + "  •  " + Localization.Get(_isAdministrator ? "HyperdimensionalNetworkManagementAdministrator" : "HyperdimensionalNetworkManagementMember")
                + "  •  " + memberSummary);
            SetLabel(_status, Localization.Get("HyperdimensionalNetworkManagementReady"));
            SetVisible(_leaveButton, !_isAdministrator);
            SetVisible(_leaveButtonLabel, !_isAdministrator);
            RenderRows();
            NetworkLog.Debug("Membership roster loaded. network=" + _networkId + "; members=" + _memberCount + "; onlineCandidates=" + _candidateCount
                + "; memberOffset=" + requestedMemberOffset + "; candidateOffset=" + requestedCandidateOffset);
            if (_memberOffset != requestedMemberOffset || _candidateOffset != requestedCandidateOffset) Refresh();
        }, requestedMemberOffset, requestedCandidateOffset);
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
        bool canLeave = !_isAdministrator && !string.IsNullOrEmpty(_networkId);
        SetVisible(_leaveButton, canLeave);
        SetVisible(_leaveButtonLabel, canLeave);
        for (int i = 0; i < Rows; i++)
        {
            NetworkMemberInfo member = i < _members.Count ? _members[i] : null;
            bool hasMember = member != null;
            bool canKick = hasMember && _isAdministrator && !member.IsAdministrator
                && !string.Equals(selfId, member.PlayerId, StringComparison.Ordinal);
            bool canTransfer = hasMember && _isAdministrator && !member.IsAdministrator;
            SetVisible(_memberLines[i], hasMember);
            SetVisible(_memberNames[i], hasMember);
            SetVisible(_memberKick[i], canKick);
            SetVisible(_memberKickLabels[i], canKick);
            SetVisible(_memberTransfer[i], canTransfer);
            SetVisible(_memberTransferLabels[i], canTransfer);
            if (member != null) SetLabel(_memberNames[i], member.DisplayName + (member.IsAdministrator ? "  ★" : string.Empty)
                + (member.IsOnline ? string.Empty : "  (" + Localization.Get("HyperdimensionalNetworkManagementOffline") + ")"));

            NetworkMemberInfo candidate = i < _candidates.Count ? _candidates[i] : null;
            bool hasCandidate = candidate != null;
            SetVisible(_candidateLines[i], hasCandidate);
            SetVisible(_candidateNames[i], hasCandidate);
            SetVisible(_candidateInvite[i], hasCandidate);
            SetVisible(_candidateInviteLabels[i], hasCandidate);
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
        SetLabel(_memberPageLabel, RangeLabel(_memberOffset, _memberCount));
        SetLabel(_candidatePageLabel, RangeLabel(_candidateOffset, _candidateCount));
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

    private void OnMemberScroll(XUiController sender, float delta)
    {
        if (delta == 0f || _memberCount <= Rows) return;
        ChangeMemberOffset(delta > 0f ? -1 : 1);
    }

    private void OnCandidateScroll(XUiController sender, float delta)
    {
        if (delta == 0f || _candidateCount <= Rows) return;
        ChangeCandidateOffset(delta > 0f ? -1 : 1);
    }

    private void ChangeMemberOffset(int delta)
    {
        int offset = Math.Max(0, Math.Min(Math.Max(0, _memberCount - Rows), _memberOffset + delta));
        if (offset == _memberOffset) return;
        _memberOffset = offset;
        RequestPageRefresh();
    }

    private void ChangeCandidateOffset(int delta)
    {
        int offset = Math.Max(0, Math.Min(Math.Max(0, _candidateCount - Rows), _candidateOffset + delta));
        if (offset == _candidateOffset) return;
        _candidateOffset = offset;
        RequestPageRefresh();
    }

    private void RequestPageRefresh()
    {
        NetworkLog.Debug("Membership list wheel scroll. memberOffset=" + _memberOffset + "; candidateOffset=" + _candidateOffset
            + "; pending=" + _requestPending);
        if (!_requestPending) Refresh();
    }

    private void Bind(string id, XUiEvent_OnPressEventHandler handler)
    {
        XUiController control = GetChildById(id);
        if (control != null) control.OnPress += handler;
    }

    private static string RangeLabel(int offset, int count)
    {
        if (count <= 0) return "0 / 0";
        if (count <= Rows) return count + " / " + count;
        int first = Math.Min(offset, count - 1) + 1;
        int last = Math.Min(offset + Rows, count);
        return first + "–" + last + " / " + count;
    }
    private static string EligibilityKey(string error) => error == "AdministratorHasMembers"
        ? "HyperdimensionalNetworkManagementSharedAdministrator"
        : error == "NetworkFull" ? "HyperdimensionalNetworkManagementNetworkFull"
        : error == "MembershipBusy" ? "HyperdimensionalNetworkManagementMemberBusy"
        : "HyperdimensionalNetworkManagementInvitePending";

    private static void SetVisible(XUiController controller, bool visible)
    {
        if (controller?.ViewComponent != null && controller.ViewComponent.IsVisible != visible)
            controller.ViewComponent.IsVisible = visible;
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
