using System;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(ItemActionEat), "ExecuteAction")]
internal static class StressTestPatch
{
    public static bool Prefix(ItemActionData _actionData, bool _bReleased)
    {
        ItemClass itemClass = _actionData?.invData?.item;
        if (!NetworkStressTest.IsTestItem(itemClass)) return true;
        if (_bReleased)
        {
            EntityPlayerLocal player = _actionData?.invData?.holdingEntity as EntityPlayerLocal;
            try { NetworkStressTest.Run(player, NetworkStressTest.IsExtreme(itemClass)); }
            catch (Exception ex) { NetworkLog.Error("Stress test failed. " + ex); }
        }
        return false;
    }
}
