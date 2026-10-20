using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace HyperdimensionalNetwork;

internal static class QuestNetworkItemService
{
    private static World scannedWorld;
    private static int scannedPlayerId = -1;

    internal static void ReconcileLegacyCompleted(World world)
    {
        if (world == null || PlayerNetworkDirectory.IsRemoteClient()) return;
        EntityPlayerLocal player = NetworkRemoteClient.GetLocalPlayer();
        if (!(bool)player || !player.Spawned || player.QuestJournal?.quests == null
            || string.IsNullOrEmpty(PlayerNetworkDirectory.GetNetworkId(player))) return;
        if (ReferenceEquals(scannedWorld, world) && scannedPlayerId == player.entityId) return;
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        if (!PlayerNetworkDirectory.TryGetNetwork(networkId, out PlayerNetworkRecord network)
            || network.Members.Count != 1) return;
        scannedWorld = world;
        scannedPlayerId = player.entityId;

        List<StoredEntry> legacy = NetworkStorage.GetEntries(player)
            .Where(entry => entry.Value?.ItemClass?.IsQuestItem == true
                && !NetworkItemPolicy.CanEnterOrDestroy(entry.Value)).ToList();
        foreach (IGrouping<int, StoredEntry> group in legacy.GroupBy(entry => entry.Value.type))
        {
            ItemValue value = group.First().Value;
            bool active = player.QuestJournal.quests.Any(quest => (quest.CurrentState is Quest.QuestState.InProgress
                or Quest.QuestState.ReadyForTurnIn) && quest.Objectives.OfType<ObjectiveFetch>()
                    .Any(objective => objective.ID == value.ItemClass.Name));
            if (active) continue;
            int completedDemand = player.QuestJournal.quests.Where(quest => quest.CurrentState == Quest.QuestState.Completed)
                .SelectMany(quest => quest.Objectives.OfType<ObjectiveFetch>())
                .Where(objective => objective is not ObjectiveFetchKeep && objective.ID == value.ItemClass.Name)
                .Sum(RequiredCount);
            int count = (int)Math.Min(group.Sum(entry => entry.Count), completedDemand);
            if (count <= 0) continue;
            int removed = RemoveMatching(player, value, count);
            NetworkLog.Debug("Legacy completed quest supply reconciled from network. player=" + player.entityId
                + "; item=" + value.type + "; requested=" + count + "; removed=" + removed);
        }
    }

    internal static void OnWorldShuttingDown()
    {
        scannedWorld = null;
        scannedPlayerId = -1;
    }

    internal static NetworkRemoteResult ExecuteTurnIn(EntityPlayer player, NetworkRemoteRequest request)
    {
        if (!(bool)player || request?.Value?.ItemClass == null || request.Count <= 0
            || NetworkItemPolicy.CanEnterOrDestroy(request.Value) || string.IsNullOrEmpty(request.Category))
            return Failure(request, "InvalidQuestTurnInItem");
        Quest quest = player.QuestJournal?.quests?.FirstOrDefault(candidate => candidate.ID == request.Category
            && candidate.QuestCode == request.Slot
            && candidate.CurrentState is Quest.QuestState.InProgress or Quest.QuestState.ReadyForTurnIn or Quest.QuestState.Completed);
        if (quest == null || !quest.Objectives.OfType<ObjectiveFetch>().Any(objective => objective is not ObjectiveFetchKeep
            && objective.ID == request.Value.ItemClass.Name && RequiredCount(objective) >= request.Count))
            return Failure(request, "QuestTurnInMismatch");

        int removed = RemoveMatching(player, request.Value, request.Count);
        if (removed != request.Count) return Failure(request, "QuestNetworkItemUnavailable");
        NetworkLog.Debug("Quest turn-in consumed a legacy network item. player=" + player.entityId + "; quest="
            + quest.ID + "; code=" + quest.QuestCode + "; item=" + request.Value.type + "; count=" + removed);
        return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, Amount = removed };
    }

    internal static int RemoveMatching(EntityPlayer player, ItemValue value, int requested)
    {
        if (!(bool)player || value == null || requested <= 0) return 0;
        List<StoredEntry> entries = NetworkStorage.GetEntries(player).Where(entry => entry.Value.type == value.type).ToList();
        List<StorageAddition> removals = new List<StorageAddition>();
        int remaining = requested;
        foreach (StoredEntry entry in entries)
        {
            if (remaining <= 0) break;
            int take = (int)Math.Min(remaining, entry.Count);
            removals.Add(new StorageAddition { Value = entry.Value.Clone(), Count = take });
            remaining -= take;
        }
        if (remaining != 0 || !NetworkStorage.RemoveBatch(player, removals)) return 0;
        return requested;
    }

    private static int RequiredCount(ObjectiveFetch objective)
        => int.TryParse(objective?.Value, out int count) ? Math.Max(0, count) : Math.Max(0, objective?.itemCount ?? 0);

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
}

[HarmonyPatch(typeof(ObjectiveFetch), nameof(ObjectiveFetch.RemoveObjectives))]
internal static class QuestNetworkItemTurnInPatch
{
    private sealed class State
    {
        internal EntityPlayerLocal Player;
        internal Quest Quest;
        internal ItemValue Item;
        internal int Requested;
        internal int BeforePhysical;
    }

    [HarmonyPrefix]
    private static void Prefix(ObjectiveFetch __instance, out State __state)
    {
        __state = null;
        Quest quest = __instance?.OwnerQuest;
        EntityPlayerLocal player = quest?.OwnerJournal?.OwnerPlayer;
        if (!(bool)player || quest.CurrentState != Quest.QuestState.Completed
            || __instance is ObjectiveFetchKeep || __instance.itemCount <= 0) return;
        ItemValue item = ItemClass.GetItem(__instance.ID);
        if (item == null || NetworkItemPolicy.CanEnterOrDestroy(item)) return;
        __state = new State
        {
            Player = player,
            Quest = quest,
            Item = item,
            Requested = __instance.itemCount,
            BeforePhysical = player.bag.GetItemCount(item) + player.inventory.GetItemCount(item)
        };
    }

    [HarmonyPostfix]
    private static void Postfix(State __state)
    {
        if (__state == null) return;
        int afterPhysical = __state.Player.bag.GetItemCount(__state.Item) + __state.Player.inventory.GetItemCount(__state.Item);
        int physicalConsumed = Math.Max(0, __state.BeforePhysical - afterPhysical);
        int remaining = Math.Max(0, __state.Requested - physicalConsumed);
        if (remaining <= 0) return;
        if (NetworkRemoteClient.IsRemoteClient)
        {
            bool queued = NetworkRemoteClient.RequestQuestTurnIn(__state.Player, __state.Quest, __state.Item, remaining, result =>
            {
                if (!result.Success) NetworkLog.Error("Quest legacy network item turn-in failed. quest=" + __state.Quest.ID
                    + "; item=" + __state.Item.type + "; count=" + remaining + "; error=" + result.Error);
            });
            if (!queued) NetworkLog.Error("Quest legacy network item turn-in could not be queued. quest=" + __state.Quest.ID
                + "; item=" + __state.Item.type + "; count=" + remaining);
        }
        else
        {
            int removed = QuestNetworkItemService.RemoveMatching(__state.Player, __state.Item, remaining);
            NetworkLog.Debug("Quest turn-in reconciled legacy network item. quest=" + __state.Quest.ID
                + "; item=" + __state.Item.type + "; requested=" + remaining + "; removed=" + removed);
        }
    }
}
