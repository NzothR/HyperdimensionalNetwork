using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HyperdimensionalNetwork.Patches;

[HarmonyPatch]
internal static class VanillaTraderCurrencyPatch
{
    [ThreadStatic] private static bool _resumingPurchase;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(XUiM_PlayerInventory), nameof(XUiM_PlayerInventory.CanSwapItems))]
    private static void AllowNetworkCurrency(XUiM_PlayerInventory __instance, ItemStack _removedStack,
        ItemStack _addedStack, int _slotNumber, ref bool __result)
    {
        if (__result || _slotNumber != -1 || !IsCurrency(_removedStack) || _addedStack == null
            || _addedStack.IsEmpty() || !(bool)__instance.localPlayer) return;
        int physical = PhysicalCurrency(__instance, _removedStack.itemValue);
        long network = NetworkStorage.GetMatchingCount(__instance.localPlayer, _removedStack.itemValue);
        if ((long)physical + network < _removedStack.count) return;
        // The original swap test can use a freed coin slot. When coins come from the network,
        // require enough space for the purchased stack without relying on that physical slot.
        if (__instance.CountAvailableSpaceForItem(_addedStack.itemValue) < _addedStack.count) return;
        __result = true;
        NetworkLog.Debug("Vanilla trader swap accepted with network currency. player=" + __instance.localPlayer.entityId
            + "; price=" + _removedStack.count + "; physical=" + physical + "; network=" + network);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ItemActionEntryPurchase), nameof(ItemActionEntryPurchase.OnActivated))]
    private static bool ReserveRemoteNetworkCurrency(ItemActionEntryPurchase __instance)
    {
        if (_resumingPurchase || !NetworkRemoteClient.IsRemoteClient) return true;
        XUiC_TraderItemEntry entry = __instance.ItemController as XUiC_TraderItemEntry;
        XUi xui = entry?.xui;
        EntityPlayerLocal player = xui?.playerUI?.entityPlayer;
        ItemStack item = entry?.Item;
        if (!(bool)player || item == null || item.IsEmpty() || xui.Trader?.TraderData == null) return true;
        int count = entry.InfoWindow?.BuySellCounter?.Count ?? 0;
        if (count <= 0) return true;
        int price = XUiM_Trader.GetBuyPrice(xui, item.itemValue, count,
            ItemClass.GetForId(item.itemValue.type), entry.SlotIndex);
        ItemValue coin = ItemClass.GetItem(TraderInfo.CurrencyItem);
        int physical = PhysicalCurrency(xui.PlayerInventory, coin);
        if (price <= physical) return true;
        if (NetworkRemoteClient.IsMutationPending)
        {
            NetworkLog.Debug("Vanilla trader purchase deferred while a network mutation is pending. player=" + player.entityId);
            return false;
        }
        ItemValue selected = item.itemValue.Clone();
        int traderId = xui.Trader.Trader is EntityTrader trader ? trader.entityId : -1;
        NetworkLog.Debug("Vanilla trader network currency reservation requested. player=" + player.entityId
            + "; trader=" + traderId + "; item=" + selected.type + "; count=" + count + "; price=" + price
            + "; physical=" + physical);
        bool sent = NetworkRemoteClient.RequestConsumption(player,
            new List<ItemStack> { new ItemStack(coin, price) }, NetworkConsumptionSource.PlayerInventory,
            Vector3i.zero, result =>
            {
                if (entry.Item == null || entry.Item.IsEmpty() || !entry.Item.itemValue.Equals(selected)
                    || entry.InfoWindow?.BuySellCounter?.Count != count || xui.Trader?.TraderData == null
                    || (traderId >= 0 && (xui.Trader.Trader as EntityTrader)?.entityId != traderId))
                {
                    NetworkLog.Debug("Vanilla trader purchase changed before reserved currency could be used. player=" + player.entityId
                        + "; requestId=" + result.RequestId);
                    return; // The authorization scope returns unused coins to the network.
                }
                try
                {
                    _resumingPurchase = true;
                    __instance.OnActivated();
                    NetworkLog.Debug("Vanilla trader purchase resumed with reserved network currency. player=" + player.entityId
                        + "; requestId=" + result.RequestId);
                }
                catch (Exception ex)
                {
                    NetworkLog.Error("Vanilla trader purchase failed after currency reservation. player=" + player.entityId
                        + "; requestId=" + result.RequestId + "; exception=" + ex);
                }
                finally { _resumingPurchase = false; }
            }, error =>
            {
                NetworkLog.Debug("Vanilla trader network currency reservation failed. player=" + player.entityId
                    + "; item=" + selected.type + "; price=" + price + "; error=" + error);
                GameManager.ShowTooltip(player, Localization.Get("ttNoMoney"));
            });
        if (!sent) NetworkLog.Debug("Vanilla trader network currency reservation could not be queued. player=" + player.entityId);
        return false;
    }

    private static bool IsCurrency(ItemStack stack)
        => stack != null && !stack.IsEmpty()
            && stack.itemValue.type == ItemClass.GetItem(TraderInfo.CurrencyItem).type;

    private static int PhysicalCurrency(XUiM_PlayerInventory inventory, ItemValue currency)
    {
        int count = 0;
        foreach (ItemStack stack in inventory.GetAllItemStacks())
            if (stack != null && !stack.IsEmpty() && NetworkItemMatcher.Matches(stack.itemValue, currency))
                count = NetworkItemMatcher.AddClamped(count, stack.count);
        return count;
    }
}
