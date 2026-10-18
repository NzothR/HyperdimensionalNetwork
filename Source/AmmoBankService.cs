using System;
using HarmonyLib;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal sealed class AmmoReloadContext
{
    public EntityPlayerLocal Player;
    public ItemValue Weapon;
    public ItemValue Ammo;
    public int ToolbeltSlot;
    public int MagazineSize;
    public int InitialMeta;
    public byte SelectedAmmoIndex;
    public bool AmmoIsPerMagazine;
}

internal static class AmmoBankService
{
    public static bool IsEnabled(EntityPlayer player)
    {
        if (player is EntityPlayerLocal localPlayer && NetworkRemoteClient.IsRemoteClient)
            return AutoRestockClient.IsAmmoBankEnabled(localPlayer);
        string error = string.Empty;
        if (!(bool)player || !NetworkFeatureSettingsService.TryGet(player, out NetworkFeatureSettings settings, out error))
        {
            if (!string.IsNullOrEmpty(error)) NetworkLog.Debug("Ammo bank availability check failed closed. player=" + player?.entityId + "; error=" + error);
            return false;
        }
        return settings.AmmoBankEnabled;
    }

    public static bool TryGetReloadAmmo(ItemActionRanged action, ItemValue weapon, EntityAlive holder, out ItemValue ammo)
    {
        ammo = null;
        if (action == null || weapon == null || holder == null || action is ItemActionTextureBlock
            || action.MagazineItemNames == null || weapon.SelectedAmmoTypeIndex >= action.MagazineItemNames.Length) return false;
        ammo = ItemClass.GetItem(action.MagazineItemNames[weapon.SelectedAmmoTypeIndex]);
        return ammo != null && ammo.type > 0;
    }

    public static NetworkRemoteResult ExecuteRemoteReload(EntityPlayer player, NetworkRemoteRequest request)
    {
        if (request?.Value == null || request.Count <= 0 || request.Slot < 0)
            return Failure(request, "InvalidAmmoRequest");
        if (!IsEnabled(player)) return Failure(request, "AmmoBankDisabled");
        if (player.inventory == null || player.inventory.holdingItemIdx != request.Slot)
            return Failure(request, "WeaponChanged");

        ItemValue weapon = player.inventory.holdingItemItemValue;
        ItemActionRanged action = player.inventory.GetHoldingGun() as ItemActionRanged;
        if (!TryGetReloadAmmo(action, weapon, player, out ItemValue expectedAmmo)
            || expectedAmmo.type != request.Value.type)
            return Failure(request, "AmmoTypeChanged");

        int magazineSize = GetMagazineSize(action, weapon, player);
        if (magazineSize <= 0) return Failure(request, "InvalidMagazineSize");
        int remainingCapacity = Math.Max(0, magazineSize - weapon.Meta);
        int maximumRequested = action.AmmoIsPerMagazine ? (remainingCapacity > 0 ? 1 : 0) : remainingCapacity;
        if (maximumRequested <= 0) return Failure(request, "WeaponAlreadyLoaded");
        if (request.Count > maximumRequested) return Failure(request, "InvalidAmmoAmount");

        int removed = NetworkStorage.Remove(player, expectedAmmo, request.Count);
        if (removed <= 0)
        {
            NetworkLog.Debug("Ammo bank reload found no consumable network ammo. player=" + player.entityId + "; requestId=" + request.RequestId + "; ammo=" + expectedAmmo.type);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = 0 };
        }
        NetworkLog.Debug("Ammo bank supplied network ammo. player=" + player.entityId + "; requestId=" + request.RequestId
            + "; ammo=" + expectedAmmo.type + "; units=" + removed + "; perMagazine=" + action.AmmoIsPerMagazine);
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Value = expectedAmmo.Clone(), Amount = removed };
    }

    public static int TrySupplyLocal(AmmoReloadContext context, int amount)
    {
        if (context?.Player == null || amount <= 0 || !IsEnabled(context.Player)) return 0;
        int removed = NetworkStorage.Remove(context.Player, context.Ammo, amount);
        if (removed <= 0) return 0;
        int rounds = context.AmmoIsPerMagazine ? context.MagazineSize : removed;
        if (!ApplyRounds(context.Player, context.Weapon, context.ToolbeltSlot, context.SelectedAmmoIndex,
            context.MagazineSize, rounds))
        {
            int refunded = NetworkStorage.AddStack(context.Player, new ItemStack(context.Ammo.Clone(), removed)) ? removed : 0;
            NetworkLog.Error("Ammo bank could not apply locally supplied ammo; attempted refund. player=" + context.Player.entityId + "; removed=" + removed + "; refunded=" + refunded);
            return 0;
        }
        NetworkLog.Debug("Ammo bank completed local reload from network. player=" + context.Player.entityId + "; ammo=" + context.Ammo.type
            + "; units=" + removed + "; rounds=" + rounds + "; slot=" + context.ToolbeltSlot);
        return removed;
    }

    public static bool ApplyRemoteSupply(AmmoReloadContext context, int suppliedUnits, long requestId)
    {
        if (context == null || suppliedUnits <= 0) return true;
        int rounds = context.AmmoIsPerMagazine ? context.MagazineSize : suppliedUnits;
        if (ApplyRounds(context.Player, context.Weapon, context.ToolbeltSlot, context.SelectedAmmoIndex,
            context.MagazineSize, rounds))
        {
            NetworkLog.Debug("Ammo bank applied remote reload response. player=" + context.Player.entityId + "; requestId=" + requestId
                + "; ammo=" + context.Ammo.type + "; units=" + suppliedUnits + "; rounds=" + rounds);
            return true;
        }

        NetworkLog.Debug("Ammo bank weapon changed before the remote reload response; returning unused ammo. player=" + context.Player.entityId
            + "; requestId=" + requestId + "; units=" + suppliedUnits);
        if (NetworkRemoteClient.RequestAmmoBankRefund(context.Player, context.Ammo, suppliedUnits, requestId, result =>
        {
            if (!result.Success) NetworkLog.Error("Ammo bank could not refund unused remote ammo. player=" + context.Player.entityId + "; requestId=" + requestId + "; error=" + result.Error);
        })) return false;
        NetworkLog.Error("Ammo bank could not queue a refund for unused remote ammo. player=" + context.Player.entityId + "; requestId=" + requestId);
        return false;
    }

    public static bool ApplyRounds(EntityPlayerLocal player, ItemValue expectedWeapon, int slot, byte ammoIndex, int magazineSize, int rounds)
    {
        if (!(bool)player || expectedWeapon == null || slot < 0 || slot >= player.inventory.ItemGrid.Length || rounds <= 0) return false;
        ItemStack stack = player.inventory.ItemGrid[slot];
        if (stack == null || stack.IsEmpty() || !SameWeapon(stack.itemValue, expectedWeapon, ammoIndex)) return false;

        int newMeta = Math.Min(magazineSize, stack.itemValue.Meta + rounds);
        if (newMeta <= stack.itemValue.Meta) return false;
        stack.itemValue.Meta = newMeta;
        player.inventory.ItemGrid.SetItem(slot, stack);
        player.inventory.CallOnToolbeltChangedInternal();
        AutoRestockClient.RefreshInventoryViews(player);
        RefreshAmmoHud(player);
        return true;
    }

    public static void RefreshAmmoHud(EntityPlayerLocal player)
    {
        if (!(bool)player) return;
        XUiC_InGameHUD hud = player.playerUI?.xui?.GetChildByType<XUiC_InGameHUD>();
        if (hud?.statBarList == null) return;
        foreach (XUiController controller in hud.statBarList)
            if (controller is XUiC_HUDStatBar statBar) statBar.updateActiveItemAmmo();
    }

    private static bool SameWeapon(ItemValue actual, ItemValue expected, byte ammoIndex)
    {
        if (actual == null || expected == null || actual.type != expected.type || actual.Quality != expected.Quality
            || actual.SelectedAmmoTypeIndex != ammoIndex) return false;
        ItemValue normalized = expected.Clone();
        normalized.Meta = actual.Meta;
        return normalized.Equals(actual);
    }

    public static int GetMagazineSize(ItemActionRanged action, ItemValue weapon, EntityAlive holder)
    {
        return action == null || weapon == null ? 0 : Math.Max(0, (int)EffectManager.GetValue(PassiveEffects.MagazineSize, weapon, action.BulletsPerMagazine, holder));
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
    {
        return new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
    }
}

[HarmonyPatch(typeof(ItemActionRanged), nameof(ItemActionRanged.CanReload))]
internal static class AmmoBankCanReloadPatch
{
    [HarmonyPostfix]
    private static void Postfix(ItemActionRanged __instance, ItemActionData _actionData, ref bool __result)
    {
        if (__result || !(_actionData is ItemActionRanged.ItemActionDataRanged rangedData)) return;
        EntityPlayerLocal player = rangedData.invData?.holdingEntity as EntityPlayerLocal;
        if (!(bool)player || !AmmoBankService.IsEnabled(player) || !ItemActionRanged.NotReloading(rangedData)
            || player.CancellingInventoryActions) return;
        ItemValue weapon = rangedData.invData.itemValue;
        int magazineSize = AmmoBankService.GetMagazineSize(__instance, weapon, player);
        if (magazineSize <= 0 || (weapon.Meta >= magazineSize && !__instance.isJammed(weapon))) return;
        if (!AmmoBankService.TryGetReloadAmmo(__instance, weapon, player, out ItemValue ammo)) return;
        if (NetworkStorage.GetMatchingCount(player, ammo) > 0)
        {
            __result = true;
            NetworkLog.Debug("Reload allowed by ammo bank stock. player=" + player.entityId + "; weapon=" + weapon.type + "; ammo=" + ammo.type);
        }
    }
}

[HarmonyPatch(typeof(ItemActionRanged), nameof(ItemActionRanged.CompleteReload))]
internal static class AmmoBankCompleteReloadPatch
{
    [HarmonyPostfix]
    private static void Postfix(ItemActionRanged __instance, ItemActionRanged.ItemActionDataRanged _adr)
    {
        if (_adr?.invData?.holdingEntity is not EntityPlayerLocal player || !(bool)player
            || !AmmoBankService.IsEnabled(player)) return;
        ItemValue weapon = _adr.invData.itemValue;
        if (!AmmoBankService.TryGetReloadAmmo(__instance, weapon, player, out ItemValue ammo)) return;
        int magazineSize = AmmoBankService.GetMagazineSize(__instance, weapon, player);
        if (magazineSize <= 0) return;
        int needed = __instance.AmmoIsPerMagazine
            ? (weapon.Meta < magazineSize ? 1 : 0)
            : Math.Max(0, magazineSize - weapon.Meta);
        if (needed <= 0) return;

        AmmoReloadContext context = new AmmoReloadContext
        {
            Player = player,
            Weapon = weapon.Clone(),
            Ammo = ammo.Clone(),
            ToolbeltSlot = _adr.invData.slotIdx,
            MagazineSize = magazineSize,
            InitialMeta = weapon.Meta,
            SelectedAmmoIndex = weapon.SelectedAmmoTypeIndex,
            AmmoIsPerMagazine = __instance.AmmoIsPerMagazine
        };
        if (NetworkRemoteClient.IsRemoteClient)
        {
            bool sent = NetworkRemoteClient.RequestAmmoBankReload(player, ammo, needed, context.ToolbeltSlot, result =>
            {
                if (!result.Success)
                {
                    NetworkLog.Debug("Remote ammo bank reload rejected. player=" + player.entityId + "; requestId=" + result.RequestId + "; error=" + result.Error);
                    return;
                }
                AmmoBankService.ApplyRemoteSupply(context, result.Amount, result.RequestId);
            });
            if (!sent) NetworkLog.Debug("Remote ammo bank reload was not queued. player=" + player.entityId + "; weapon=" + weapon.type);
            return;
        }

        AmmoBankService.TrySupplyLocal(context, needed);
    }
}

[HarmonyPatch(typeof(AnimatorRangedReloadState), nameof(AnimatorRangedReloadState.GetAmmoCount))]
internal static class AmmoBankLauncherCountPatch
{
    [HarmonyPostfix]
    private static void Postfix(EntityAlive ea, ItemValue ammo, int modifiedMagazineSize, ref int __result)
    {
        EntityPlayerLocal player = ea as EntityPlayerLocal;
        if (!(bool)player || ammo == null || !AmmoBankService.IsEnabled(player)) return;
        long network = NetworkStorage.GetMatchingCount(player, ammo);
        if (network <= 0) return;
        __result = Math.Min(modifiedMagazineSize, __result + (int)Math.Min(network, int.MaxValue));
    }
}

[HarmonyPatch(typeof(Animator3PRangedReloadState), nameof(Animator3PRangedReloadState.GetAmmoCount))]
internal static class AmmoBankThirdPersonLauncherCountPatch
{
    [HarmonyPostfix]
    private static void Postfix(EntityAlive ea, ItemValue ammo, int modifiedMagazineSize, ref int __result)
    {
        EntityPlayerLocal player = ea as EntityPlayerLocal;
        if (!(bool)player || ammo == null || !AmmoBankService.IsEnabled(player)) return;
        long network = NetworkStorage.GetMatchingCount(player, ammo);
        if (network <= 0) return;
        __result = Math.Min(modifiedMagazineSize, __result + (int)Math.Min(network, int.MaxValue));
    }
}

[HarmonyPatch(typeof(XUiC_HUDStatBar), "updateActiveItemAmmo")]
internal static class AmmoBankHudCountPatch
{
    [HarmonyPostfix]
    private static void Postfix(XUiC_HUDStatBar __instance)
    {
        EntityPlayerLocal player = __instance?.localPlayer;
        ItemValue ammo = __instance?.activeAmmoItemValue;
        if (!(bool)player || ammo == null || ammo.type <= 0 || !AmmoBankService.IsEnabled(player)) return;
        long network = NetworkStorage.GetMatchingCount(player, ammo);
        if (network <= 0) return;
        __instance.currentAmmoCount = (int)Math.Min(int.MaxValue, (long)__instance.currentAmmoCount + network);
    }
}
