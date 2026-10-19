using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HyperdimensionalNetwork;

internal static class InventoryAutomationService
{
    internal static NetworkRemoteResult ExecuteSlot(EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        if (!(bool)player || playerData == null || request?.Value == null || request.Count <= 0 || request.Slot < 0)
            return Failure(request, "InvalidAutomationSource");
        bool toolbelt = request.SourceLocation == XUiC_ItemStack.StackLocationTypes.ToolBelt;
        if (!toolbelt && request.SourceLocation != XUiC_ItemStack.StackLocationTypes.Backpack)
            return Failure(request, "InvalidAutomationSource");
        MemoryStream original = toolbelt ? playerData.inventoryData : playerData.bagData;
        if (original == null) return Failure(request, "PlayerInventoryUnavailable");
        bool locked = !toolbelt && IsLocked(original, request.Slot);
        RestockSettingsSnapshot settings = RestockSettingsService.Get(player);
        List<TraderAutomationRule> personal = PersonalAutomationRuleService.GetRules(player);
        List<TraderAutomationRule> network = TraderAutomationService.GetRules(player);
        TraderAutomationRule sale = NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.TraderEnabled)
            ? network.FirstOrDefault(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoSell
                && TraderAutomationService.Matches(rule, request.Value) && TraderTransactionService.CanSell(request.Value)) : null;
        if (sale != null && !locked)
        {
            NetworkRemoteRequest saleRequest = new NetworkRemoteRequest
            {
                EntityId = player.entityId, RequestId = request.RequestId, Operation = NetworkRequestOperation.TraderVoidSell,
                Value = request.Value.Clone(), Count = BoundedCount(request.Count, sale.MaximumCount),
                SourceLocation = request.SourceLocation, Slot = request.Slot, TraderAutomation = true
            };
            NetworkRemoteResult sold = TraderTransactionService.ExecuteVoidSell(player, playerData, saleRequest);
            if (sold.Success)
            {
                NetworkLog.Debug("Inventory automation sold before trash/deposit. player=" + player.entityId + "; slot=" + request.Slot
                    + "; toolbelt=" + toolbelt + "; rule=" + sale.Id + "; count=" + saleRequest.Count);
                return sold;
            }
            NetworkLog.Debug("Inventory automation sale unavailable; trying trash/deposit. player=" + player.entityId
                + "; rule=" + sale.Id + "; error=" + sold.Error);
        }

        if (settings.PersonalAutoTrash && settings.TrashEnabled && ScopeIncludes(settings.PersonalTrashScope, toolbelt, locked))
        {
            TraderAutomationRule trash = personal.FirstOrDefault(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoTrash
                && TraderAutomationService.Matches(rule, request.Value));
            if (trash != null)
            {
                int count = BoundedCount(request.Count, trash.MaximumCount);
                MemoryStream candidate;
                bool updated = toolbelt
                    ? NetworkRpcServer.TryUpdateToolbelt(original, request.Slot, request.Value, count, out _, out candidate)
                    : NetworkRpcServer.TryUpdateBag(original, request.Slot, request.Value, count, out _, out candidate, allowLocked: true);
                if (!updated || candidate == null) return Failure(request, "AutomationSourceChanged");
                try
                {
                    NetworkRpcServer.ApplyPlayerInventorySnapshot(player, candidate, toolbelt);
                    if (toolbelt) playerData.inventoryData = candidate; else playerData.bagData = candidate;
                    playerData.bModifiedSinceLastSave = true;
                    NetworkLog.Debug("Personal trash rule destroyed inventory items. player=" + player.entityId + "; rule=" + trash.Id
                        + "; item=" + request.Value.type + "; count=" + count + "; toolbelt=" + toolbelt + "; locked=" + locked);
                    return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = count,
                        BagData = toolbelt ? null : candidate.ToArray(), InventoryData = toolbelt ? candidate.ToArray() : null };
                }
                catch (Exception ex)
                {
                    NetworkLog.Error("Personal trash inventory update failed. player=" + player.entityId + "; exception=" + ex);
                    try { NetworkRpcServer.ApplyPlayerInventorySnapshot(player, original, toolbelt); } catch (Exception restore)
                    { NetworkLog.Error("Personal trash could not restore original inventory. player=" + player.entityId + "; exception=" + restore); }
                    return Failure(request, "AutomationInventoryUpdateFailed");
                }
            }
        }

        if (!toolbelt && !locked && settings.PersonalAutoDeposit && settings.AutoDepositEnabled)
        {
            TraderAutomationRule deposit = personal.FirstOrDefault(rule => rule.Enabled && rule.Kind == TraderAutomationRuleKind.AutoDeposit
                && TraderAutomationService.Matches(rule, request.Value));
            if (deposit != null)
            {
                request.Count = BoundedCount(request.Count, deposit.MaximumCount);
                NetworkRemoteResult deposited = NetworkRpcServer.DepositPlayerSlot(player, playerData, request, toolbelt: false);
                NetworkLog.Debug("Personal deposit rule processed inventory slot. player=" + player.entityId + "; rule=" + deposit.Id
                    + "; item=" + request.Value.type + "; count=" + request.Count + "; success=" + deposited.Success + "; error=" + deposited.Error);
                return deposited;
            }
        }
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true };
    }

    private static int BoundedCount(int sourceCount, int maximum) => maximum <= 0 ? sourceCount : Math.Min(sourceCount, maximum);

    private static bool IsLocked(MemoryStream bagData, int slot)
    {
        try
        {
            Bag bag = NetworkRpcServer.ReadBag(bagData);
            return bag != null && slot >= 0 && slot < bag.SlotCount && bag.LockedSlots != null
                && slot < bag.LockedSlots.Length && bag.LockedSlots[slot];
        }
        catch (Exception ex) { NetworkLog.Error("Could not inspect backpack lock state for automation. slot=" + slot + "; exception=" + ex); return false; }
    }

    internal static bool ScopeIncludes(TrashScope scope, bool toolbelt, bool locked) => scope switch
    {
        TrashScope.EntireInventory => true,
        TrashScope.Backpack => !toolbelt,
        TrashScope.Toolbelt => toolbelt,
        TrashScope.LockedSlots => !toolbelt && locked,
        _ => false
    };

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
}
