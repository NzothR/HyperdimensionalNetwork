using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

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
        bool open = __instance?.xui?.playerUI?.windowManager?.IsWindowOpen("hyperdimensionalNetwork") ?? false;
        NetworkLog.Debug("Navigation entry clicked. mouse=" + _mouseButton + "; openBeforeClick=" + open + "; selector=" + (__instance?.ViewComponent?.ID ?? "<none>"));
        return !open;
    }

    private static void Setup(XUiC_WindowSelector selector)
    {
        XUiController button = selector?.GetChildById("hyperdimensionalNetwork");
        if (button?.ViewComponent == null) return;
        button.ViewComponent.IsVisible = true;
        button.ViewComponent.ToolTip = "打开超维网络";
        ApplyNetworkEntryIcon(selector, button);
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
        NetworkLog.Debug("Navigation entry ready. registered=" + (ButtonsByName?.GetValue(selector) is Dictionary<string, XUiC_ButtonSelectable> entries && entries.ContainsKey("hyperdimensionalNetwork")) + "; x=" + (button.ViewComponent.UiTransform != null ? button.ViewComponent.UiTransform.localPosition.x.ToString("F1") : "<no transform>"));
    }

    private static void ApplyNetworkEntryIcon(XUiC_WindowSelector selector, XUiController button)
    {
        const string spriteName = "hdn_network";
        if (button?.ViewComponent is XUiV_Button viewButton)
        {
            viewButton.UIAtlas = "SymbolAtlas";
            viewButton.DefaultSpriteName = spriteName;
            viewButton.HoverSpriteName = spriteName;
            viewButton.SelectedSpriteName = spriteName;
            viewButton.DisabledSpriteName = spriteName;
        }
        else if (button?.ViewComponent is XUiV_Sprite viewSprite)
        {
            viewSprite.UIAtlas = "SymbolAtlas";
            viewSprite.SpriteName = spriteName;
        }

        // `iconbutton` can retain its vanilla sprite after the XUi XML has already
        // initialized the selector. Reapply the atlas pair to its rendered widget.
        UISprite rendered = button?.ViewComponent?.UiTransform?.GetComponent<UISprite>();
        var atlas = selector?.xui?.GetAtlasByName("SymbolAtlas", spriteName);
        if (rendered != null)
        {
            // Do not clear the widget's atlas if XUi has not finished registering
            // the custom symbol atlas during the selector's first initialization.
            if (atlas != null) rendered.atlas = atlas;
            rendered.spriteName = spriteName;
        }
        NetworkLog.Debug("Navigation entry icon bound. sprite=" + spriteName + "; view="
            + (button?.ViewComponent?.GetType().Name ?? "<none>") + "; atlasLoaded="
            + (atlas != null));
    }
}
