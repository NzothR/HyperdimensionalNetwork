using System.Collections.Generic;

namespace HyperdimensionalNetwork;

public sealed class ConsoleCmdTraderMarket : ConsoleCmdAbstract
{
    public override int DefaultPermissionLevel => 0;

    public override string[] getCommands() => new[] { "hdn-trader-market" };
    public override string getDescription() => "Lists or force-closes Hyperdimensional Network aggregate trader market sessions.";
    public override string getHelp() => "Usage: hdn-trader-market [list|close <sessionId>]";

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        if (!IsServerAdministrator(_senderInfo))
        {
            SingletonMonoBehaviour<SdtdConsole>.Instance.Output("This command is restricted to server administrators.");
            return;
        }
        if (_params.Count == 0 || string.Equals(_params[0], "list", System.StringComparison.OrdinalIgnoreCase))
        {
            List<string> sessions = TraderMarketplaceService.GetSessionSummaries();
            if (sessions.Count == 0) SingletonMonoBehaviour<SdtdConsole>.Instance.Output("No active aggregate trader market sessions.");
            else foreach (string session in sessions) SingletonMonoBehaviour<SdtdConsole>.Instance.Output(session);
            return;
        }
        if (_params.Count == 2 && string.Equals(_params[0], "close", System.StringComparison.OrdinalIgnoreCase))
        {
            bool closed = TraderMarketplaceService.ForceClose(_params[1]);
            SingletonMonoBehaviour<SdtdConsole>.Instance.Output(closed ? "Trader market session closed." : "Trader market session was not found.");
            return;
        }
        SingletonMonoBehaviour<SdtdConsole>.Instance.Output(GetHelp());
    }

    private static bool IsServerAdministrator(CommandSenderInfo sender)
    {
        if (sender.IsLocalGame) return true;
        ClientInfo client = sender.RemoteClientInfo;
        return client != null && GameManager.Instance?.adminTools?.Users?.GetUserPermissionLevel(client) == 0;
    }
}
