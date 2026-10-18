using System.Reflection;
using HarmonyLib;

namespace HyperdimensionalNetwork;

public sealed class HyperdimensionalNetworkModApi : IModApi
{
    public void InitMod(Mod mod)
    {
        NetworkLog.Info("Mod initialization started. Version=0.1.0; storage=journal-v1.");
        new Harmony("NANA.HyperdimensionalNetwork").PatchAll(Assembly.GetExecutingAssembly());
        ModEvents.GameShutdown.RegisterHandler(OnShutdown);
        NetworkLog.Info("Harmony patches registered.");
    }

    private static void OnShutdown(ref ModEvents.SGameShutdownData data) => NetworkStorage.FlushAll();
}
