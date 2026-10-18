using System;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(TileEntity), nameof(TileEntity.OnLockRequestServer))]
internal static class CraftingChainWorkstationLockRangePatch
{
    [HarmonyPrefix]
    private static bool Prefix(TileEntity __instance, int _lockingPlayerID, ref bool __result)
    {
        if (!(__instance is TileEntityWorkstation workstation)) return true;
        EntityPlayer player = GameManager.Instance?.World?.GetEntity(_lockingPlayerID) as EntityPlayer;
        if (!(bool)player)
        {
            __result = false;
            NetworkLog.Debug("Vanilla workstation lock request rejected because its player entity is unavailable. player="
                + _lockingPlayerID + "; position=" + workstation.ToWorldPos());
            return false;
        }
        if (!CraftingChainWorkstationService.IsWithinRange(player, workstation) || workstation.IsUserAccessing())
        {
            __result = false;
            NetworkLog.Debug("Vanilla workstation lock request rejected by chain-compatible range/access guard. player="
                + _lockingPlayerID + "; position=" + workstation.ToWorldPos() + "; distance="
                + (workstation.ToWorldCenterPos() - player.position).magnitude.ToString("F1")
                + "; userAccessing=" + workstation.IsUserAccessing());
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(LockManager), "LockRequestLocal", new[] { typeof(int), typeof(ushort) })]
internal static class CraftingChainLockReservationPatch
{
    [HarmonyPrefix]
    private static bool Prefix(LockManager __instance, int _count)
    {
        int playerId = GameManager.Instance?.World?.GetPrimaryPlayerId() ?? -1;
        if (playerId < 0 || CraftingChainOrderService.ShouldAllowVanillaLockRequest(playerId,
            __instance?.requestTargets, _count)) return true;

        if (__instance?.requestTargets != null)
            Array.Clear(__instance.requestTargets, 0, __instance.requestTargets.Length);
        NetworkLog.Debug("Vanilla lock request rejected because it conflicts with an active crafting-chain workstation. player="
            + playerId + "; targetCount=" + _count);
        return false;
    }
}

[HarmonyPatch(typeof(TileEntityWorkstation), nameof(TileEntityWorkstation.OnLockResponseLocal))]
internal static class CraftingChainWorkstationLockResponsePatch
{
    [HarmonyPrefix]
    private static bool Prefix(TileEntityWorkstation __instance, bool _success, ref bool __result)
    {
        if (!CraftingChainOrderService.IsWorkstationLockPending(__instance)) return true;
        CraftingChainOrderService.HandleWorkstationLockResponse(__instance, _success);
        __result = true;
        return false;
    }
}
