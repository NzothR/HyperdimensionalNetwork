using System;
using System.Reflection;
using HarmonyLib;

namespace HyperdimensionalNetwork;

// KFCommonUtilityLib bypasses ItemActionRanged.CompleteReload and inserts ammo in animation events.
// Keep this optional integration separate from the vanilla ammo-bank patch.
internal static class KfAnimationReloadAdapter
{
    private static FieldInfo playerField;
    private static FieldInfo actionDataField;
    private static FieldInfo actionRangedField;

    internal static void Install(Harmony harmony)
    {
        Type type = AccessTools.TypeByName("AnimationReloadEvents");
        if (type == null)
        {
            NetworkLog.Debug("KF animation reload adapter inactive: AnimationReloadEvents was not found.");
            return;
        }
        playerField = AccessTools.Field(type, "player");
        actionDataField = AccessTools.Field(type, "actionData");
        actionRangedField = AccessTools.Field(type, "actionRanged");
        MethodInfo regular = AccessTools.Method(type, "OnReloadAmmo");
        MethodInfo partial = AccessTools.Method(type, "OnPartialReloadEnd");
        if (playerField == null || actionDataField == null || actionRangedField == null || regular == null || partial == null)
        {
            NetworkLog.Debug("KF animation reload adapter inactive: required fields or methods were not found.");
            return;
        }
        try
        {
            HarmonyMethod postfix = new HarmonyMethod(typeof(KfAnimationReloadAdapter), nameof(OnReloadAmmoPostfix));
            harmony.Patch(regular, postfix: postfix);
            harmony.Patch(partial, postfix: new HarmonyMethod(typeof(KfAnimationReloadAdapter), nameof(OnPartialReloadEndPostfix)));
            NetworkLog.Debug("KF animation reload adapter installed for regular and partial reload events. assembly=" + type.Assembly.GetName().Name);
        }
        catch (Exception ex)
        {
            NetworkLog.Error("KF animation reload adapter installation failed. " + ex);
        }
    }

    private static void OnReloadAmmoPostfix(object __instance) => Supply(__instance, false);
    private static void OnPartialReloadEndPostfix(object __instance) => Supply(__instance, true);

    private static void Supply(object instance, bool partial)
    {
        try
        {
            if (playerField?.GetValue(instance) is not EntityPlayerLocal player || !(bool)player
                || actionDataField.GetValue(instance) is not ItemActionRanged.ItemActionDataRanged data
                || actionRangedField.GetValue(instance) is not ItemActionRanged action
                || !data.isReloading || data.isReloadCancelled || data.isWeaponReloadCancelled
                || !AmmoBankService.IsEnabled(player)) return;

            ItemValue weapon = data.invData?.itemValue;
            int slot = data.invData?.slotIdx ?? -1;
            if (weapon == null || slot < 0 || player.inventory.holdingItemIdx != slot
                || !AmmoBankService.TryGetReloadAmmo(action, weapon, player, out ItemValue ammo)) return;
            int magazineSize = AmmoBankService.GetMagazineSize(action, weapon, player);
            int capacity = Math.Max(0, magazineSize - weapon.Meta);
            if (capacity == 0) return;
            if (partial && action.AmmoIsPerMagazine) return;
            int needed = action.AmmoIsPerMagazine ? 1 : partial ? 1 : capacity;
            if (needed <= 0) return;

            AmmoReloadContext context = new AmmoReloadContext
            {
                Player = player,
                Weapon = weapon.Clone(),
                Ammo = ammo.Clone(),
                ToolbeltSlot = slot,
                MagazineSize = partial ? weapon.Meta + needed : magazineSize,
                InitialMeta = weapon.Meta,
                SelectedAmmoIndex = weapon.SelectedAmmoTypeIndex,
                AmmoIsPerMagazine = action.AmmoIsPerMagazine
            };
            if (NetworkRemoteClient.IsRemoteClient)
            {
                bool sent = NetworkRemoteClient.RequestAmmoBankReload(player, ammo, needed, slot, result =>
                {
                    if (!result.Success)
                    {
                        NetworkLog.Debug("KF reload network request rejected. player=" + player.entityId + "; requestId=" + result.RequestId + "; error=" + result.Error);
                        return;
                    }
                    AmmoBankService.ApplyRemoteSupply(context, result.Amount, result.RequestId);
                });
                NetworkLog.Debug("KF animation reload event reached ammo bank. player=" + player.entityId + "; weapon=" + weapon.type
                    + "; ammo=" + ammo.type + "; partial=" + partial + "; requested=" + needed + "; remoteQueued=" + sent);
            }
            else
            {
                int supplied = AmmoBankService.TrySupplyLocal(context, needed);
                NetworkLog.Debug("KF animation reload event reached ammo bank. player=" + player.entityId + "; weapon=" + weapon.type
                    + "; ammo=" + ammo.type + "; partial=" + partial + "; requested=" + needed + "; supplied=" + supplied);
            }
        }
        catch (Exception ex)
        {
            NetworkLog.Error("KF animation reload adapter failed. partial=" + partial + "; " + ex);
        }
    }

}
