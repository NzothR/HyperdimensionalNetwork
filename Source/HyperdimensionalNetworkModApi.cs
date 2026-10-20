using System.Reflection;
using HarmonyLib;

namespace HyperdimensionalNetwork;

public sealed class HyperdimensionalNetworkModApi : IModApi
{
    public void InitMod(Mod mod)
    {
        NetworkLog.Info("Mod initialization started. Version=0.1.0; storage=journal-v1.");
        Harmony harmony = new Harmony("NANA.HyperdimensionalNetwork");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        KfAnimationReloadAdapter.Install(harmony);
        ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
        ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
        ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
        ModEvents.GameShutdown.RegisterHandler(OnShutdown);
        NetworkLog.Info("Harmony patches registered.");
    }

    private static void OnGameStartDone(ref ModEvents.SGameStartDoneData data)
        => CraftingChainOrderService.OnGameStarted();

    private static void OnGameUpdate(ref ModEvents.SGameUpdateData data)
    {
        using var profile = NetworkProfiler.Measure("GameUpdate.total");
        World world = GameManager.Instance?.World;
        using (NetworkProfiler.Measure("GameUpdate.membership")) NetworkMembershipService.Update(world);
        using (NetworkProfiler.Measure("GameUpdate.traderSessions")) TraderMarketplaceService.SweepDisconnected(world);
        using (NetworkProfiler.Measure("GameUpdate.traderRules")) TraderAutomationService.Update(world);
        using (NetworkProfiler.Measure("GameUpdate.scrapQueue")) AutoScrapQueueService.Update(world);
        QuestNetworkItemService.ReconcileLegacyCompleted(world);
    }

    private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData data)
    {
        CraftingChainOrderService.OnWorldShuttingDown();
        NetworkMembershipService.OnWorldShuttingDown();
        NetworkMembershipClient.OnWorldShuttingDown();
        TraderMarketplaceService.OnWorldShuttingDown();
        AutoScrapQueueService.OnWorldShuttingDown();
        QuestNetworkItemService.OnWorldShuttingDown();
    }

    private static void OnShutdown(ref ModEvents.SGameShutdownData data)
    {
        CraftingChainOrderService.OnWorldShuttingDown();
        NetworkMembershipService.OnWorldShuttingDown();
        NetworkMembershipClient.OnWorldShuttingDown();
        TraderMarketplaceService.OnWorldShuttingDown();
        AutoScrapQueueService.OnWorldShuttingDown();
        QuestNetworkItemService.OnWorldShuttingDown();
        NetworkStorage.FlushAll();
    }
}
