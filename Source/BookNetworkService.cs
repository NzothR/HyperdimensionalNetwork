using System;

namespace HyperdimensionalNetwork;

internal static class BookNetworkService
{
    internal static NetworkRemoteResult Execute(EntityPlayer player, NetworkRemoteRequest request)
    {
        if (!(bool)player || request?.Value == null || request.Count <= 0 || request.Count > 100
            || !BookAutomationClient.IsBook(request.Value))
            return Failure(request, "InvalidBook");
        if (!NetworkFeatureSettingsService.IsEnabled(player, NetworkFeatureSetting.BookReadingEnabled))
            return Failure(request, "BookReadingDisabled");
        if (NetworkStorage.GetCount(player, request.Value) < request.Count) return Failure(request, "BookUnavailable");
        if (request.Operation == NetworkRequestOperation.BookConsumeNetwork)
        {
            if (NetworkStorage.Remove(player, request.Value, request.Count) != request.Count) return Failure(request, "BookUnavailable");
            NetworkLog.Debug("Network books consumed for manual reading. player=" + player.entityId + "; item=" + request.Value.type
                + "; count=" + request.Count);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Value = request.Value.Clone(), Amount = request.Count };
        }
        if (request.Operation != NetworkRequestOperation.BookScrapNetwork) return Failure(request, "UnsupportedOperation");
        if (!BookAutomationClient.TryGetBookState(player, request.Value, out bool unread) || unread)
            return Failure(request, "BookNotRead");
        if (!TryCalculateVanillaScrap(player, request.Value, out ItemValue output, out int outputCount))
            return Failure(request, "BookNotScrappable");
        if (outputCount > int.MaxValue / request.Count) return Failure(request, "ScrapOutputTooLarge");
        outputCount *= request.Count;
        if (NetworkStorage.Remove(player, request.Value, request.Count) != request.Count) return Failure(request, "BookUnavailable");
        if (!NetworkStorage.AddStack(player, new ItemStack(output.Clone(), outputCount)))
        {
            if (!NetworkStorage.AddStack(player, new ItemStack(request.Value.Clone(), request.Count)))
                NetworkLog.Error("Manual network book scrap could not restore its source after failed output credit. player=" + player.entityId
                    + "; item=" + request.Value.type);
            return Failure(request, "ScrapOutputUnavailable");
        }
        NetworkLog.Debug("Manual network book scrapped using vanilla recipe yield. player=" + player.entityId
            + "; item=" + request.Value.type + "; output=" + output.type + "; count=" + outputCount);
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Value = output, Amount = outputCount };
    }

    private static bool TryCalculateVanillaScrap(EntityPlayer player, ItemValue value, out ItemValue output, out int count)
    {
        output = null; count = 0;
        Recipe recipe = CraftingManager.GetScrapableRecipe(value, 1);
        ItemClass source = value.ItemClass;
        ItemClass result = recipe == null ? null : ItemClass.GetForId(recipe.itemValueType);
        if (source == null || result == null || source.GetWeight() <= 0 || result.GetWeight() <= 0) return false;
        int baseCount = source.GetWeight() / result.GetWeight();
        if (baseCount <= 0) return false;
        FastTags<TagGroup.Global> scrap100 = FastTags<TagGroup.Global>.Parse("scrap100");
        float multiplier = XUiM_Recipes.DisableSmelter && source.HasAnyTags(scrap100) ? 1f : 0.75f;
        count = Math.Max(1, (int)(baseCount * multiplier * XUiM_Recipes.ScrappingOutputModifier));
        output = new ItemValue(recipe.itemValueType);
        return output != null && !output.IsEmpty() && count > 0;
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
}
