using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch(typeof(XUiC_WindowSelector))]
internal static class SelectorPatch
{
    private static readonly FieldInfo ButtonsByName = AccessTools.Field(typeof(XUiC_WindowSelector), "buttonsByName");
    private static readonly FieldInfo Buttons = AccessTools.Field(typeof(XUiC_WindowSelector), "buttons");
    private static readonly HashSet<XUiController> Bound = new HashSet<XUiController>();

    [HarmonyPostfix, HarmonyPatch("Init")]
    public static void InitPostfix(XUiC_WindowSelector __instance) => Setup(__instance);

    [HarmonyPostfix, HarmonyPatch("OnOpen")]
    public static void OnOpenPostfix(XUiC_WindowSelector __instance) => Setup(__instance);

    [HarmonyPrefix, HarmonyPatch("HandleOnPress")]
    public static bool PressPrefix(XUiC_WindowSelector __instance, XUiController _sender, int _mouseButton)
    {
        if (!string.Equals(_sender?.ViewComponent?.ID, "hyperdimensionalNetwork", StringComparison.OrdinalIgnoreCase)) return true;
        return !(__instance?.xui?.playerUI?.windowManager?.IsWindowOpen("hyperdimensionalNetwork") ?? false);
    }

    private static void Setup(XUiC_WindowSelector selector)
    {
        XUiController button = selector?.GetChildById("hyperdimensionalNetwork");
        if (button?.ViewComponent == null) return;
        button.ViewComponent.IsVisible = true;
        button.ViewComponent.ToolTip = "打开超维网络";
        XUiC_ButtonSelectable selectable = button as XUiC_ButtonSelectable;
        if (selectable == null) { NetworkLog.Error("Navigation button controller is not selectable."); return; }
        if (ButtonsByName?.GetValue(selector) is Dictionary<string, XUiC_ButtonSelectable> byName) byName["hyperdimensionalNetwork"] = selectable;
        if (Buttons?.GetValue(selector) is XUiC_ButtonSelectable[] existing && Array.IndexOf(existing, selectable) < 0)
        {
            XUiC_ButtonSelectable[] next = new XUiC_ButtonSelectable[existing.Length + 1];
            Array.Copy(existing, next, existing.Length);
            next[next.Length - 1] = selectable;
            Buttons.SetValue(selector, next);
        }
        else if (Buttons?.GetValue(selector) == null) Buttons?.SetValue(selector, new[] { selectable });
        if (Bound.Add(button)) button.OnPress += selector.HandleOnPress;
    }
}
