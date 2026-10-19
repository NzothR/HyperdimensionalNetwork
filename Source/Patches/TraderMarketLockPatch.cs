using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(EntityTrader), "OnLockRequestServer")]
internal static class TraderMarketLockPatch
{
    private static bool Prefix(EntityTrader __instance, int _lockingPlayerID, ushort _channel, ref bool __result)
    {
        if (_channel != 1 || !TraderMarketplaceService.IsLeasedByOther(__instance, _lockingPlayerID)) return true;
        __result = false;
        NetworkLog.Debug("Vanilla trader trade lock refused because an aggregate market session owns the trader. trader="
            + __instance.entityId + "; requestingPlayer=" + _lockingPlayerID);
        return false;
    }
}
