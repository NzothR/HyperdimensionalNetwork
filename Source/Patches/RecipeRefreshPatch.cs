using System.Runtime.CompilerServices;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

internal static class RecipeRefreshState
{
    private sealed class RevisionState { public long Value = -1; }
    private static readonly ConditionalWeakTable<XUiController, RevisionState> Revisions = new ConditionalWeakTable<XUiController, RevisionState>();

    public static bool Changed(XUiController controller)
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

[HarmonyPatch(typeof(XUiC_RecipeList), nameof(XUiC_RecipeList.Update))]
internal static class RecipeListRefreshPatch
{
    [HarmonyPrefix]
    private static void Prefix(XUiC_RecipeList __instance)
    {
        if (!RecipeRefreshState.Changed(__instance)) return;
        __instance.IsDirty = true;
        __instance.resortRecipes = true;
        __instance.pageChanged = true;
    }
}

[HarmonyPatch(typeof(XUiC_RecipeCraftCount), nameof(XUiC_RecipeCraftCount.Update))]
internal static class RecipeCraftCountRefreshPatch
{
    [HarmonyPrefix]
    private static void Prefix(XUiC_RecipeCraftCount __instance)
    {
        if (RecipeRefreshState.Changed(__instance)) __instance.IsDirty = true;
    }
}
