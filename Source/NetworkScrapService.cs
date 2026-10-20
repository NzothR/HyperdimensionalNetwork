using System;
using System.Linq;
using Audio;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class NetworkScrapService
{
    private static readonly FastTags<TagGroup.Global> Scrap100Tag = FastTags<TagGroup.Global>.Parse("scrap100");

    internal static void Activate(XUiC_HyperdimensionalSlot slot, ItemActionEntryScrap action)
    {
        EntityPlayerLocal player = slot?.xui?.playerUI?.entityPlayer;
        ItemStack source = slot?.ItemStack?.Clone();
        if (!(bool)player || source == null || source.IsEmpty()) return;

        if (HasAttachment(source.itemValue))
        {
            action.OnDisabledActivate();
            return;
        }

        if (!TryCreateScrapRecipe(player, source, out Recipe recipe, out int consumed))
        {
            NetworkLog.Debug("Network scrap rejected because vanilla could not create a valid scrap recipe. player="
                + player.entityId + "; item=" + source.itemValue.type + "; count=" + source.count);
            return;
        }

        XUiC_CraftingWindowGroup group = FindCraftingWindow(slot.xui);
        XUiC_CraftingQueue queue = group?.GetChildByType<XUiC_CraftingQueue>();
        if (queue == null || !HasQueueSpace(queue))
        {
            GameManager.ShowTooltip(player, Localization.Get("xuiCraftQueueFull"));
            Manager.PlayInsidePlayerHead("ui_denied");
            NetworkLog.Debug("Network scrap rejected because the vanilla crafting queue is full or unavailable. player="
                + player.entityId + "; item=" + source.itemValue.type + "; count=" + consumed);
            return;
        }

        NetworkLog.Debug("Network scrap debit requested. player=" + player.entityId + "; item=" + source.itemValue.type
            + "; sourceCount=" + source.count + "; consumed=" + consumed + "; output=" + recipe.itemValueType
            + "; outputCount=" + recipe.count + "; remote=" + NetworkRemoteClient.IsRemoteClient);

        if (NetworkRemoteClient.IsRemoteClient)
        {
            if (!NetworkRemoteClient.ScrapNetworkItem(player, source.itemValue, source.count, consumed, result =>
            {
                if (!result.Success)
                {
                    NetworkLog.Debug("Network scrap debit rejected by server. player=" + player.entityId + "; item="
                        + source.itemValue.type + "; count=" + consumed + "; error=" + result.Error);
                    slot.xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
                    return;
                }
                QueueOrRefund(player, queue, recipe, source.itemValue, consumed, result.RequestId,
                    result.UpdatedSourceCount, NetworkRemoteClient.IsRemoteClient, slot.xui);
            }))
            {
                NetworkLog.Debug("Network scrap debit was not queued. player=" + player.entityId + "; item=" + source.itemValue.type);
            }
            return;
        }

        NetworkRemoteResult debit = Execute(player, new NetworkRemoteRequest
        {
            EntityId = player.entityId,
            RequestId = DateTime.UtcNow.Ticks,
            Operation = NetworkRequestOperation.ScrapNetworkItem,
            Value = source.itemValue.Clone(),
            Slot = source.count,
            Count = consumed
        });
        if (!debit.Success)
        {
            NetworkLog.Debug("Local network scrap debit failed. player=" + player.entityId + "; item=" + source.itemValue.type
                + "; count=" + consumed + "; error=" + debit.Error);
            return;
        }
        QueueOrRefund(player, queue, recipe, source.itemValue, consumed, debit.RequestId,
            debit.UpdatedSourceCount, false, slot.xui);
    }

    internal static NetworkRemoteResult Execute(EntityPlayer player, NetworkRemoteRequest request)
    {
        if (!(bool)player || request?.Value == null || request.Count <= 0 || request.Slot < request.Count)
            return Failure(request, "InvalidScrapRequest");
        long available = NetworkStorage.GetCount(player, request.Value);
        if (available < request.Count || available < request.Slot)
            return Failure(request, "ScrapItemUnavailable");
        if (!TryCreateScrapRecipe(player, new ItemStack(request.Value.Clone(), request.Slot),
            out Recipe verifiedRecipe, out int verifiedCount) || verifiedCount != request.Count)
            return Failure(request, "ScrapRecipeChanged");

        int removed = NetworkStorage.Remove(player, request.Value, request.Count);
        if (removed != request.Count)
        {
            if (removed > 0 && !NetworkStorage.AddStack(player, new ItemStack(request.Value.Clone(), removed)))
                NetworkLog.Error("Network scrap debit rollback failed after a partial removal. player=" + player.entityId
                    + "; item=" + request.Value.type + "; requested=" + request.Count + "; removed=" + removed);
            return Failure(request, "ScrapItemUnavailable");
        }
        NetworkLog.Debug("Network scrap debit committed. player=" + player.entityId + "; requestId=" + request.RequestId
            + "; item=" + request.Value.type + "; count=" + removed);
        return new NetworkRemoteResult
        {
            RequestId = request.RequestId,
            Success = true,
            Value = request.Value.Clone(),
            Amount = removed,
            UpdatedSourceCount = verifiedRecipe.count
        };
    }

    private static void QueueOrRefund(EntityPlayerLocal player, XUiC_CraftingQueue queue, Recipe recipe,
        ItemValue source, int consumed, long debitRequestId, int authorizedOutputCount, bool remote, XUi xui)
    {
        bool queued = false;
        try
        {
            queued = authorizedOutputCount == recipe.count && queue != null && HasQueueSpace(queue)
                && queue.AddRecipeToCraft(recipe, 1);
            if (authorizedOutputCount != recipe.count)
                NetworkLog.Error("Network scrap recipe output differed from server validation; queue submission blocked. player="
                    + player.entityId + "; item=" + source.type + "; clientOutput=" + recipe.count
                    + "; serverOutput=" + authorizedOutputCount);
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Network scrap queue insertion threw after source debit. player=" + player.entityId
                + "; item=" + source.type + "; count=" + consumed + "; exception=" + ex);
        }

        if (queued)
        {
            NetworkLog.Debug("Network scrap entered vanilla queue after debit. player=" + player.entityId + "; requestId="
                + debitRequestId + "; item=" + source.type + "; consumed=" + consumed + "; output=" + recipe.itemValueType
                + "; outputCount=" + recipe.count);
            xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
            return;
        }

        NetworkLog.Error("Network scrap could not enter vanilla queue after source debit; refunding source. player="
            + player.entityId + "; requestId=" + debitRequestId + "; item=" + source.type + "; count=" + consumed);
        if (!remote)
        {
            if (!NetworkStorage.AddStack(player, new ItemStack(source.Clone(), consumed)))
                NetworkLog.Error("Local network scrap source refund failed. player=" + player.entityId + "; item="
                    + source.type + "; count=" + consumed);
            xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
            return;
        }

        if (!NetworkRemoteClient.ReturnNetworkScrap(player, debitRequestId, source, consumed, result =>
        {
            if (!result.Success)
                NetworkLog.Error("Remote network scrap source refund failed. player=" + player.entityId + "; originalRequestId="
                    + debitRequestId + "; item=" + source.type + "; count=" + consumed + "; error=" + result.Error);
            else NetworkLog.Debug("Remote network scrap source refund confirmed. player=" + player.entityId
                + "; originalRequestId=" + debitRequestId + "; item=" + source.type + "; count=" + result.Amount);
            xui.GetChildByType<XUiC_HyperdimensionalGrid>()?.Reload();
        }))
            NetworkLog.Error("Remote network scrap source refund could not be queued. player=" + player.entityId
                + "; originalRequestId=" + debitRequestId + "; item=" + source.type + "; count=" + consumed);
    }

    private static XUiC_CraftingWindowGroup FindCraftingWindow(XUi xui)
    {
        XUiController window = xui?.FindWindowGroupByName("workstation_workbench");
        if (window == null || !window.WindowGroup.isShowing) window = xui?.FindWindowGroupByName("crafting");
        return window?.GetChildByType<XUiC_CraftingWindowGroup>();
    }

    private static bool HasQueueSpace(XUiC_CraftingQueue queue)
        => queue.GetRecipesToCraft().Any(item => item != null && !item.HasRecipe());

    internal static bool HasAttachment(ItemValue value)
    {
        if (value.ModificationCount <= 0) return false;
        for (int i = 0; i < value.ModificationCount; i++)
        {
            ItemValue modification = value.GetModification(i);
            if (modification != null && !modification.IsEmpty()
                && modification.ItemClass is ItemClassModifier modifier
                && modifier.Type == ItemClassModifier.ModifierTypes.Attachment) return true;
        }
        return false;
    }

    internal static bool TryCreateScrapRecipe(EntityPlayer player, ItemStack source, out Recipe recipe, out int consumed)
    {
        recipe = null;
        consumed = 0;
        Recipe scrapRecipe = CraftingManager.GetScrapableRecipe(source.itemValue, source.count);
        ItemClass output = scrapRecipe == null ? null : ItemClass.GetForId(scrapRecipe.itemValueType);
        ItemClass input = source.itemValue.ItemClass;
        if (output == null || input == null || input.GetWeight() <= 0 || output.GetWeight() <= 0) return false;

        long inputWeight = (long)input.GetWeight() * source.count;
        int outputUnits = (int)Math.Min(int.MaxValue, inputWeight / output.GetWeight());
        if (outputUnits <= 0) return false;
        consumed = (int)((outputUnits * (long)output.GetWeight()) / input.GetWeight() + 0.5f);
        if (consumed <= 0 || consumed > source.count) return false;

        float multiplier = XUiM_Recipes.DisableSmelter && input.HasAnyTags(Scrap100Tag) ? 1f : 0.75f;
        outputUnits = Math.Max(1, (int)(outputUnits * multiplier * XUiM_Recipes.ScrappingOutputModifier));
        recipe = new Recipe
        {
            count = outputUnits,
            craftExpGain = scrapRecipe.craftExpGain,
            itemValueType = output.Id,
            craftingTime = input.ScrapTimeOverride > 0f
                ? input.ScrapTimeOverride
                : (bool)player
                    ? EffectManager.GetValue(PassiveEffects.ScrappingTime, null, output.CraftComponentTime * outputUnits, player)
                    : output.CraftComponentTime * outputUnits
        };
        recipe.ingredients.Add(new ItemStack(source.itemValue.Clone(), consumed));
        recipe.IsScrap = true;
        return true;
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
}
