using System.Runtime.CompilerServices;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

internal static class RecipeRefreshPatch
{
    private sealed class RevisionState { public long Value = -1; }
    private static readonly ConditionalWeakTable<XUiController, RevisionState> Revisions = new ConditionalWeakTable<XUiController, RevisionState>();

    [HarmonyPatch(typeof(XUiC_RecipeList), nameof(XUiC_RecipeList.Update)), HarmonyPrefix]
    private static void RecipeList(XUiC_RecipeList __instance)
    {
        if (!Changed(__instance)) return;
        __instance.IsDirty = true;
        __instance.resortRecipes = true;
        __instance.pageChanged = true;
    }

    [HarmonyPatch(typeof(XUiC_RecipeCraftCount), nameof(XUiC_RecipeCraftCount.Update)), HarmonyPrefix]
    private static void CraftCount(XUiC_RecipeCraftCount __instance)
    {
        if (Changed(__instance)) __instance.IsDirty = true;
    }

    private static bool Changed(XUiController controller)
    {
        if (controller?.WindowGroup?.isShowing != true) return false;
        EntityPlayer player = controller.xui?.playerUI?.entityPlayer;
        if (!(bool)player) return false;
        RevisionState state = Revisions.GetOrCreateValue(controller);
        long revision = NetworkStorage.GetRevision(player);
        if (revision == state.Value) return false;
        state.Value = revision;
        NetworkLog.Debug("Recipe UI refreshed after network revision. controller=" + controller.GetType().Name + "; revision=" + revision);
        return true;
    }
}
