using System;
using System.Collections.Generic;

namespace HyperdimensionalNetwork;

public sealed class ConsoleCmdNetworkProfile : ConsoleCmdAbstract
{
    public override string[] getCommands() => new[] { "hdn-profile" };
    public override string getDescription() => "Profile Hyperdimensional Network performance in this game process.";
    public override string getHelp() => "Usage: hdn-profile <start [seconds]|status|stop|report [top]>; seconds=1..600 (default 30), top=1..40 (default 15).";

    public override void Execute(List<string> parameters, CommandSenderInfo senderInfo)
    {
        string output;
        if (parameters == null || parameters.Count == 0)
            output = getHelp();
        else
        {
            switch (parameters[0].ToLowerInvariant())
            {
                case "start":
                    output = parameters.Count <= 2 && TryNumber(parameters, 1, 30, 1, 600, out int seconds)
                        ? NetworkProfiler.Start(seconds) : getHelp();
                    break;
                case "status":
                    output = parameters.Count == 1 ? NetworkProfiler.Status() : getHelp();
                    break;
                case "stop":
                    output = parameters.Count == 1 ? NetworkProfiler.Stop() : getHelp();
                    break;
                case "report":
                    output = parameters.Count <= 2 && TryNumber(parameters, 1, 15, 1, 40, out int top)
                        ? NetworkProfiler.Report(top) : getHelp();
                    break;
                default:
                    output = getHelp();
                    break;
            }
        }
        SingletonMonoBehaviour<SdtdConsole>.Instance?.Output("[HyperdimensionalNetwork] " + output);
    }

    private static bool TryNumber(List<string> args, int index, int fallback, int min, int max, out int value)
    {
        value = fallback;
        return args.Count <= index || (int.TryParse(args[index], out value) && value >= min && value <= max);
    }
}
