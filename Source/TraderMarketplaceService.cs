using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SandboxOptions;
using UnityEngine;

namespace HyperdimensionalNetwork;

internal static class TraderMarketplaceService
{
    private sealed class Session
    {
        public int PlayerEntityId;
        public string PlayerId;
        public string NetworkId;
        public string Token;
        public World World;
        public readonly List<TraderArea> Areas = new List<TraderArea>();
        public readonly List<ChunkManager.ChunkObserver> Observers = new List<ChunkManager.ChunkObserver>();
        public readonly Dictionary<int, EntityTrader> Traders = new Dictionary<int, EntityTrader>();
    }

    private static readonly object Sync = new object();
    private static readonly Dictionary<int, Session> Sessions = new Dictionary<int, Session>();
    private static readonly Dictionary<int, string> TraderLeases = new Dictionary<int, string>();
    private static float _nextDisconnectSweep;
    private const int MaximumAreasPerSession = 64;
    private const int MaximumOffers = 2048;

    public static NetworkRemoteResult Open(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        if (!(bool)player || world == null) return Failure(request, "TraderMarketUnavailable");
        string networkId = PlayerNetworkDirectory.GetNetworkId(player);
        string playerId = PlayerNetworkDirectory.GetStablePlayerId(player);
        if (string.IsNullOrEmpty(networkId) || string.IsNullOrEmpty(playerId)) return Failure(request, "NetworkUnavailable");
        lock (Sync)
        {
            ReleaseUnsafe(player.entityId);
            Session session = new Session
            {
                PlayerEntityId = player.entityId,
                PlayerId = playerId,
                NetworkId = networkId,
                Token = Guid.NewGuid().ToString("N"),
                World = world
            };
            List<TraderArea> areas = world.TraderAreas;
            if (areas != null)
            {
                DiscoverNearbyTrader(player, areas);
                NetworkLog.Debug("Trader discovery scan. player=" + player.entityId + "; journalPOIs="
                    + (player.QuestJournal?.TraderPOIs?.Count ?? 0) + "; worldAreas=" + areas.Count);
                GameManager manager = GameManager.Instance;
                for (int i = 0; i < areas.Count && session.Areas.Count < MaximumAreasPerSession; i++)
                {
                    TraderArea area = areas[i];
                    if (area == null) continue;
                    if (!IsDiscoveredTraderArea(player, area))
                    {
                        NetworkLog.Debug("Trader area skipped because it is not in the player's discovered trader journal. player=" + player.entityId + "; area=" + area.Position);
                        continue;
                    }
                    session.Areas.Add(area);
                    if (manager == null) continue;
                    Vector3 center = Vector3IntToVector(area.Position + area.PrefabSize / 2);
                    int view = CalculateViewDim(area, center);
                    try
                    {
                        ChunkManager.ChunkObserver observer = manager.AddChunkObserver(center, false, view, -1);
                        if (observer != null) session.Observers.Add(observer);
                    }
                    catch (Exception ex)
                    {
                        NetworkLog.Error("Trader area observer could not be acquired. player=" + player.entityId + "; area=" + area.Position + "; exception=" + ex);
                    }
                }
            }
            Sessions[player.entityId] = session;
            CollectAvailableTradersUnsafe(session);
            List<TraderOffer> offers = BuildOffers(session, player, request.TraderAutomation);
            NetworkLog.Debug("Trader market opened. player=" + player.entityId + "; network=" + networkId + "; session=" + session.Token
                + "; areas=" + session.Areas.Count + "; leasedTraders=" + session.Traders.Count + "; offers=" + offers.Count);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, TraderSessionId = session.Token, TraderOffers = offers };
        }
    }

    public static NetworkRemoteResult Query(World world, EntityPlayer player, NetworkRemoteRequest request)
    {
        lock (Sync)
        {
            if (!TryGetSession(world, player, request, out Session session, out string error)) return Failure(request, error);
            CollectAvailableTradersUnsafe(session);
            List<TraderOffer> offers = BuildOffers(session, player, request.TraderAutomation);
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true, TraderSessionId = session.Token, TraderOffers = offers };
        }
    }

    public static NetworkRemoteResult Close(EntityPlayer player, NetworkRemoteRequest request)
    {
        lock (Sync)
        {
            if (!Sessions.TryGetValue(player.entityId, out Session session))
                return new NetworkRemoteResult { RequestId = request.RequestId, Success = true };
            if (!string.Equals(session.Token, request.TraderSessionId, StringComparison.Ordinal)) return Failure(request, "TraderSessionMismatch");
            ReleaseUnsafe(player.entityId);
            NetworkLog.Debug("Trader market closed. player=" + player.entityId + "; session=" + request.TraderSessionId + "; reason=client-close");
            return new NetworkRemoteResult { RequestId = request.RequestId, Success = true };
        }
    }

    public static NetworkRemoteResult Buy(World world, EntityPlayer player, PlayerDataFile playerData, NetworkRemoteRequest request)
    {
        lock (Sync)
        {
            if (!TryGetSession(world, player, request, out Session session, out string error)) return Failure(request, error);
            CollectAvailableTradersUnsafe(session);
            if (!session.Traders.TryGetValue(request.TraderEntityId, out EntityTrader trader)
                || trader == null || trader.IsDead() || trader.IsDespawned || trader.IsMarkedForUnload()
                || trader.TraderData == null || request.TraderInventoryIndex < 0
                || request.TraderInventoryIndex >= trader.TraderData.PrimaryInventory.Count)
                return Failure(request, "TraderOfferExpired");

            TraderData data = trader.TraderData;
            TraderData.Entry entry = data.PrimaryInventory[request.TraderInventoryIndex];
            ItemStack stock = entry?.Item;
            if (stock == null || stock.IsEmpty() || request.Value == null || request.Count <= 0
                || !NetworkStorage.NormalizeKey(stock.itemValue).Equals(NetworkStorage.NormalizeKey(request.Value))
                || request.Count > stock.count)
                return Failure(request, "TraderStockChanged");
            ItemClass itemClass = stock.itemValue.ItemClass;
            int bundleSize = GetBundleSize(stock.itemValue, itemClass);
            if (request.Count % bundleSize != 0) return Failure(request, "InvalidBundleCount");
            int price;
            EntityPlayer pricePlayer = request.TraderAutomation
                ? TraderAutomationService.ResolveOnlineAdministrator(world, session.NetworkId) : player;
            try { price = GetBuyPrice(pricePlayer, data, entry, stock.itemValue, request.Count, request.TraderInventoryIndex); }
            catch (Exception ex)
            {
                NetworkLog.Error("Trader purchase price calculation failed. player=" + player.entityId + "; trader=" + trader.entityId + "; item=" + stock.itemValue.type + "; exception=" + ex);
                return Failure(request, "TraderPriceUnavailable");
            }
            if (price <= 0) return Failure(request, "TraderPriceUnavailable");

            ItemValue currency = ItemClass.GetItem(TraderInfo.CurrencyItem, false);
            if (currency == null || currency.IsEmpty()) return Failure(request, "TraderCurrencyUnavailable");
            if (request.TraderNetworkSource)
            {
                long networkFundsOnly = NetworkStorage.GetCount(player, currency);
                if (networkFundsOnly < price) return Failure(request, "InsufficientTraderFunds");
                int networkDebitOnly = NetworkStorage.Remove(player, currency, price);
                if (networkDebitOnly != price)
                {
                    if (networkDebitOnly > 0 && !NetworkStorage.AddStack(player, new ItemStack(currency.Clone(), networkDebitOnly)))
                        NetworkLog.Error("Automatic trader purchase could not restore a partial network currency debit. player=" + player.entityId + "; amount=" + networkDebitOnly);
                    NetworkLog.Error("Automatic trader purchase payment failed before product delivery. player=" + player.entityId + "; trader=" + trader.entityId);
                    return Failure(request, "TraderPaymentCommitFailed");
                }
                if (!NetworkStorage.AddStack(player, new ItemStack(stock.itemValue.Clone(), request.Count)))
                {
                    if (!NetworkStorage.AddStack(player, new ItemStack(currency.Clone(), networkDebitOnly)))
                        NetworkLog.Error("Automatic trader purchase could not restore debited network currency after product delivery failed. player=" + player.entityId + "; amount=" + networkDebitOnly);
                    return Failure(request, "NetworkCapacityUnavailable");
                }
                stock.count -= request.Count;
                if (stock.count <= 0) data.PrimaryInventory.RemoveAt(request.TraderInventoryIndex);
                data.AvailableMoney = (int)Math.Min(int.MaxValue, (long)data.AvailableMoney + price);
                data.SetModified(trader);
                NetworkLog.Debug("Network-funded trader purchase committed. player=" + player.entityId + "; network=" + session.NetworkId + "; trader=" + trader.entityId
                    + "; item=" + stock.itemValue.type + "; count=" + request.Count + "; price=" + price);
                return new NetworkRemoteResult
                {
                    RequestId = request.RequestId, Success = true, Amount = request.Count, Value = stock.itemValue.Clone(),
                    TraderSessionId = session.Token, TraderOffers = BuildOffers(session, player, request.TraderAutomation)
                };
            }
            if (!TryGetInventory(player, playerData, out Bag bag, out ItemStackGrid toolbelt, out byte selectedSlot,
                    out MemoryStream originalBag, out MemoryStream originalToolbelt))
                return Failure(request, "PlayerInventoryUnavailable");
            long personalMoney = CountCurrency(bag, toolbelt, currency);
            long networkMoney = NetworkStorage.GetCount(player, currency);
            if (personalMoney < price && networkMoney < price - personalMoney) return Failure(request, "InsufficientTraderFunds");
            int fromPersonal = (int)Math.Min((long)price, personalMoney);
            int fromNetwork = price - fromPersonal;
            if (fromPersonal > 0 && !RemoveCurrency(bag, toolbelt, currency, fromPersonal)) return Failure(request, "TraderCurrencyChanged");
            bool productToBackpack = false;
            if (request.TraderBuyToBackpack)
            {
                Bag candidateBag = NetworkRpcServer.ReadBag(NetworkRpcServer.WriteBag(bag));
                if (candidateBag != null && candidateBag.AddItem(new ItemStack(stock.itemValue.Clone(), request.Count)))
                { bag = candidateBag; productToBackpack = true; }
            }
            if (request.TraderBuyToBackpack && !productToBackpack)
                NetworkLog.Debug("Trader purchase destination fell back to the network because the backpack could not fit the full product. player=" + player.entityId + "; item=" + stock.itemValue.type + "; count=" + request.Count);
            MemoryStream updatedBag = NetworkRpcServer.WriteBag(bag);
            MemoryStream updatedToolbelt = WriteToolbelt(toolbelt, selectedSlot);
            MemoryStream oldBagData = playerData?.bagData;
            MemoryStream oldToolbeltData = playerData?.inventoryData;

            bool playerInventoryChanged = fromPersonal > 0 || productToBackpack;
            bool playerDebited = !playerInventoryChanged || ApplyPlayerInventory(player, playerData, updatedBag, updatedToolbelt);
            int networkDebited = !playerDebited || fromNetwork == 0 ? 0 : NetworkStorage.Remove(player, currency, fromNetwork);
            bool productDelivered = playerDebited && networkDebited == fromNetwork
                && (productToBackpack || NetworkStorage.AddStack(player, new ItemStack(stock.itemValue.Clone(), request.Count)));
            if (!playerDebited || networkDebited != fromNetwork || !productDelivered)
            {
                if (playerInventoryChanged) RestorePlayerInventory(player, playerData, originalBag, originalToolbelt, oldBagData, oldToolbeltData);
                if (networkDebited > 0 && !NetworkStorage.AddStack(player, new ItemStack(currency.Clone(), networkDebited)))
                    NetworkLog.Error("Trader purchase could not restore partially debited network currency. player=" + player.entityId + "; amount=" + networkDebited);
                NetworkLog.Error("Trader purchase transaction did not commit; player and network currency rollback was attempted. player=" + player.entityId
                    + "; trader=" + trader.entityId + "; productDelivered=" + productDelivered + "; personalDebit=" + playerDebited + "; networkDebit=" + networkDebited);
                return Failure(request, "TraderPaymentCommitFailed");
            }

            stock.count -= request.Count;
            if (stock.count <= 0) data.PrimaryInventory.RemoveAt(request.TraderInventoryIndex);
            data.AvailableMoney = (int)Math.Min(int.MaxValue, (long)data.AvailableMoney + price);
            data.SetModified(trader);
            NetworkLog.Debug("Real trader purchase committed. player=" + player.entityId + "; network=" + session.NetworkId + "; trader=" + trader.entityId
                + "; item=" + stock.itemValue.type + "; count=" + request.Count + "; price=" + price + "; personalPayment=" + fromPersonal + "; networkPayment=" + fromNetwork);
            return new NetworkRemoteResult
            {
                RequestId = request.RequestId,
                Success = true,
                Amount = request.Count,
                Value = stock.itemValue.Clone(),
                BagData = playerInventoryChanged ? updatedBag.ToArray() : null,
                InventoryData = playerInventoryChanged ? updatedToolbelt.ToArray() : null,
                TraderSessionId = session.Token,
                TraderOffers = BuildOffers(session, player, request.TraderAutomation)
            };
        }
    }

    public static bool IsLeasedByOther(EntityTrader trader, int playerEntityId)
    {
        if (trader == null) return false;
        lock (Sync)
            return TraderLeases.TryGetValue(trader.entityId, out string token)
                && (!Sessions.TryGetValue(playerEntityId, out Session session) || !string.Equals(token, session.Token, StringComparison.Ordinal));
    }

    public static bool HasSession(int playerEntityId)
    {
        lock (Sync) return Sessions.ContainsKey(playerEntityId);
    }

    public static void OnWorldShuttingDown()
    {
        lock (Sync)
        {
            foreach (int playerId in Sessions.Keys.ToArray()) ReleaseUnsafe(playerId);
            Sessions.Clear();
            TraderLeases.Clear();
        }
    }

    public static void SweepDisconnected(World world)
    {
        if (Time.realtimeSinceStartup < _nextDisconnectSweep) return;
        _nextDisconnectSweep = Time.realtimeSinceStartup + 2f;
        if (world == null) return;
        lock (Sync)
        {
            foreach (Session session in Sessions.Values.ToArray())
            {
                if (!ReferenceEquals(session.World, world) || !(bool)world.GetEntity(session.PlayerEntityId))
                {
                    NetworkLog.Debug("Trader market session released after player/world became unavailable. player=" + session.PlayerEntityId + "; session=" + session.Token);
                    ReleaseUnsafe(session.PlayerEntityId);
                }
            }
        }
    }

    public static List<string> GetSessionSummaries()
    {
        lock (Sync)
            return Sessions.Values.OrderBy(session => session.PlayerEntityId)
                .Select(session => "session=" + session.Token + " player=" + session.PlayerEntityId + " network=" + session.NetworkId
                    + " traders=" + string.Join(",", session.Traders.Keys.OrderBy(id => id)))
                .ToList();
    }

    public static bool ForceClose(string token)
    {
        lock (Sync)
        {
            Session session = Sessions.Values.FirstOrDefault(item => string.Equals(item.Token, token, StringComparison.Ordinal));
            if (session == null) return false;
            int playerId = session.PlayerEntityId;
            ReleaseUnsafe(playerId);
            NetworkLog.Debug("Trader market session force-closed by server administrator. player=" + playerId + "; session=" + token);
            return true;
        }
    }

    private static bool TryGetSession(World world, EntityPlayer player, NetworkRemoteRequest request, out Session session, out string error)
    {
        session = null;
        error = "TraderSessionUnavailable";
        if (!(bool)player || world == null || !Sessions.TryGetValue(player.entityId, out session)) return false;
        if (!ReferenceEquals(session.World, world) || !string.Equals(session.Token, request.TraderSessionId, StringComparison.Ordinal)
            || !string.Equals(session.NetworkId, PlayerNetworkDirectory.GetNetworkId(player), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(session.PlayerId, PlayerNetworkDirectory.GetStablePlayerId(player), StringComparison.Ordinal))
        {
            error = "TraderSessionMismatch";
            session = null;
            return false;
        }
        return true;
    }

    private static void CollectAvailableTradersUnsafe(Session session)
    {
        foreach (TraderArea area in session.Areas)
        {
            EntityTrader trader = area.GetTrader();
            if (!IsValid(trader) || trader.TraderInfo == null || trader.TraderData == null) continue;
            if (session.Traders.ContainsKey(trader.entityId)) continue;
            if (TraderLeases.TryGetValue(trader.entityId, out string owner) && !string.Equals(owner, session.Token, StringComparison.Ordinal)) continue;
            try
            {
                if (LockManager.Instance != null && LockManager.Instance.IsLockedServer(trader, 1)) continue;
            }
            catch (Exception ex) { NetworkLog.Debug("Could not inspect vanilla trader lock state. trader=" + trader.entityId + "; exception=" + ex.Message); }
            TraderLeases[trader.entityId] = session.Token;
            session.Traders[trader.entityId] = trader;
        }
    }

    private static List<TraderOffer> BuildOffers(Session session, EntityPlayer player, bool automation = false)
    {
        List<TraderOffer> offers = new List<TraderOffer>();
        EntityPlayer pricePlayer = automation ? TraderAutomationService.ResolveOnlineAdministrator(session.World, session.NetworkId) : player;
        foreach (EntityTrader trader in session.Traders.Values.OrderBy(value => value.entityId))
        {
            if (!IsValid(trader) || trader.TraderData?.PrimaryInventory == null) continue;
            TraderData traderData = trader.TraderData;
            if (traderData.CurrentTime <= 0f && traderData.TraderInfo != null
                && !traderData.TraderInfo.PlayerOwned && !traderData.TraderInfo.Rentable
                && GameManager.Instance?.traderManager?.TraderInventoryRequested(traderData, player.entityId) == true)
            {
                traderData.SetModified(trader);
                NetworkLog.Debug("Expired trader stock was refreshed for aggregate market. trader=" + trader.entityId + "; player=" + player.entityId);
            }
            for (int index = 0; index < trader.TraderData.PrimaryInventory.Count && offers.Count < MaximumOffers; index++)
            {
                TraderData.Entry entry = trader.TraderData.PrimaryInventory[index];
                ItemStack stack = entry?.Item;
                if (stack == null || stack.IsEmpty() || stack.itemValue.ItemClass == null) continue;
                int bundle = GetBundleSize(stack.itemValue, stack.itemValue.ItemClass);
                if (stack.count < bundle) continue;
                int price;
                try { price = GetBuyPrice(pricePlayer, trader.TraderData, entry, stack.itemValue, bundle, index); }
                catch (Exception ex)
                {
                    NetworkLog.Debug("Trader offer omitted because price calculation failed. trader=" + trader.entityId + "; index=" + index + "; item=" + stack.itemValue.type + "; exception=" + ex.Message);
                    continue;
                }
                if (price <= 0) continue;
                offers.Add(new TraderOffer
                {
                    Value = stack.itemValue.Clone(), Count = stack.count, BundleSize = bundle, BundlePrice = price,
                    TraderEntityId = trader.entityId, InventoryIndex = index
                });
            }
        }
        return offers;
    }

    private static int GetBuyPrice(EntityPlayer player, TraderData traderData, TraderData.Entry entry, ItemValue value, int count, int index)
    {
        ItemClass itemClass = value.ItemClass;
        float economicValue = itemClass.IsBlock() ? Block.list[value.type].EconomicValue
            : EffectManager.GetValue(PassiveEffects.EconomicValue, value, itemClass.EconomicValue, player);
        int bundleSize = GetBundleSize(value, itemClass);
        if (economicValue == 0f) return 0;
        TraderInfo info = traderData?.TraderInfo;
        float markup = 0f;
        bool priceAlreadyFinal = false;
        if (info == null) markup = TraderInfo.BuyMarkup;
        else if (info.Rentable || info.PlayerOwned)
        {
            if (index >= 0 && index < traderData.PrimaryInventory.Count)
            {
                markup = 1f + traderData.PrimaryInventory[index].Markup * 0.2f;
                priceAlreadyFinal = true;
            }
        }
        else
        {
            priceAlreadyFinal = info.OverrideBuyMarkup != -1f;
            markup = priceAlreadyFinal ? info.OverrideBuyMarkup : TraderInfo.BuyMarkup;
        }
        float price = 0f;
        if (value.HasQuality)
        {
            price = economicValue * markup;
            float quality = ((int)value.Quality - 1f) / 5f;
            price *= itemClass.TraderQualityMinMod <= 0f && itemClass.TraderQualityMaxMod <= 0f
                ? Mathf.Lerp(TraderInfo.QualityMinMod, TraderInfo.QualityMaxMod, quality)
                : Mathf.Lerp(itemClass.TraderQualityMinMod, itemClass.TraderQualityMaxMod, quality);
            price *= value.PercentUsesLeft;
        }
        else if (itemClass.HasSubItems)
        {
            for (int i = 0; i < value.ModificationCount; i++)
            {
                ItemValue modification = value.GetModification(i);
                if (!modification.IsEmpty()) price += GetBuyPrice(player, traderData, entry, modification, 1, -1);
            }
        }
        else price = economicValue * markup;
        if (!priceAlreadyFinal) price -= price * EffectManager.GetValue(PassiveEffects.BarteringBuying, null, 0f, player, null, itemClass.ItemTags);
        int basePrice = (int)(price * (count / bundleSize));
        return Mathf.CeilToInt(basePrice * SandboxOptionManager.GetFloat(global::SandboxOptions.SandboxOptions.TraderBuyPrices));
    }

    private static int GetBundleSize(ItemValue value, ItemClass itemClass)
        => Math.Max(1, itemClass.IsBlock() ? Block.list[value.type].EconomicBundleSize : itemClass.EconomicBundleSize);

    private static bool IsValid(EntityTrader trader)
        => (bool)trader && !trader.IsDead() && !trader.IsDespawned && !trader.IsMarkedForUnload() && trader.NPCInfo != null;

    private static void ReleaseUnsafe(int playerEntityId)
    {
        if (!Sessions.TryGetValue(playerEntityId, out Session session)) return;
        Sessions.Remove(playerEntityId);
        foreach (int traderId in session.Traders.Keys)
            if (TraderLeases.TryGetValue(traderId, out string token) && string.Equals(token, session.Token, StringComparison.Ordinal)) TraderLeases.Remove(traderId);
        GameManager manager = GameManager.Instance;
        if (manager != null)
            foreach (ChunkManager.ChunkObserver observer in session.Observers)
                try { manager.RemoveChunkObserver(observer); }
                catch (Exception ex) { NetworkLog.Error("Trader area observer release failed. player=" + playerEntityId + "; exception=" + ex); }
        NetworkLog.Debug("Trader market leases released. player=" + playerEntityId + "; session=" + session.Token + "; traders=" + session.Traders.Count + "; observers=" + session.Observers.Count);
    }

    private static int CalculateViewDim(TraderArea area, Vector3 center)
    {
        int playerX = World.toChunkXZ(Mathf.FloorToInt(center.x));
        int playerZ = World.toChunkXZ(Mathf.FloorToInt(center.z));
        int minX = World.toChunkXZ(area.Position.x - 1);
        int maxX = World.toChunkXZ(area.Position.x + area.PrefabSize.x + 1);
        int minZ = World.toChunkXZ(area.Position.z - 1);
        int maxZ = World.toChunkXZ(area.Position.z + area.PrefabSize.z + 1);
        return Mathf.Clamp(Math.Max(Math.Max(Math.Abs(playerX - minX), Math.Abs(maxX - playerX)),
            Math.Max(Math.Abs(playerZ - minZ), Math.Abs(maxZ - playerZ))) - 1, 1, 12);
    }

    private static Vector3 Vector3IntToVector(Vector3i position) => new Vector3(position.x, position.y, position.z);

    private static bool IsDiscoveredTraderArea(EntityPlayer player, TraderArea area)
    {
        List<UnityEngine.Vector2> discovered = player?.QuestJournal?.TraderPOIs;
        if (discovered == null || area == null) return false;
        float minX = area.Position.x - 1f;
        float maxX = area.Position.x + area.PrefabSize.x + 1f;
        float minZ = area.Position.z - 1f;
        float maxZ = area.Position.z + area.PrefabSize.z + 1f;
        foreach (UnityEngine.Vector2 poi in discovered)
            if (poi.x >= minX && poi.x <= maxX && poi.y >= minZ && poi.y <= maxZ) return true;
        return false;
    }

    internal static void RecordTraderInteraction(World world, EntityPlayer player, EntityTrader trader)
    {
        if (!(bool)player || !IsValid(trader) || player.QuestJournal == null) return;
        TraderArea area = trader.traderArea ?? world?.GetTraderAreaAt(new Vector3i(trader.position));
        if (area == null) return;
        Vector2 origin = new Vector2(area.Position.x, area.Position.z);
        if (!player.QuestJournal.HasTraderPOI(origin))
        {
            player.QuestJournal.AddTraderPOI(origin, trader.NPCInfo.QuestFaction);
            NetworkLog.Debug("Trader discovered by verified interaction. player=" + player.entityId + "; trader=" + trader.entityId
                + "; area=" + area.Position);
        }
    }

    private static void DiscoverNearbyTrader(EntityPlayer player, List<TraderArea> areas)
    {
        if (player.QuestJournal == null) return;
        foreach (TraderArea area in areas)
        {
            if (area == null || IsDiscoveredTraderArea(player, area)) continue;
            float dx = Mathf.Max(area.Position.x - player.position.x, 0f,
                player.position.x - (area.Position.x + area.PrefabSize.x));
            float dz = Mathf.Max(area.Position.z - player.position.z, 0f,
                player.position.z - (area.Position.z + area.PrefabSize.z));
            if (dx * dx + dz * dz > 30f * 30f) continue;
            EntityTrader trader = area.GetTrader();
            if (IsValid(trader)) RecordTraderInteraction(GameManager.Instance?.World, player, trader);
        }
    }

    private static bool TryGetInventory(EntityPlayer player, PlayerDataFile playerData, out Bag bag, out ItemStackGrid toolbelt,
        out byte selectedSlot, out MemoryStream bagSnapshot, out MemoryStream toolbeltSnapshot)
    {
        bag = null; toolbelt = null; selectedSlot = 0;
        bagSnapshot = playerData?.bagData;
        toolbeltSnapshot = playerData?.inventoryData;
        if ((bagSnapshot == null || toolbeltSnapshot == null) && player is EntityPlayerLocal local)
        {
            bagSnapshot = StreamUtils.ToBlob(writer => local.bag.Write(writer, StreamModeWrite.Persistency));
            toolbeltSnapshot = StreamUtils.ToBlob(writer => local.inventory.Write(writer, StreamModeWrite.Persistency));
        }
        if (bagSnapshot == null || toolbeltSnapshot == null) return false;
        try
        {
            bag = NetworkRpcServer.ReadBag(bagSnapshot);
            using MemoryStream input = new MemoryStream(toolbeltSnapshot.ToArray(), writable: false);
            using PooledBinaryReader reader = MemoryPools.poolBinaryReader.AllocSync(false);
            reader.SetBaseStream(input);
            if (reader.ReadByte() != 1) return false;
            toolbelt = ItemStackGrid.Read(reader, StreamModeRead.Persistency);
            selectedSlot = reader.ReadByte();
            return input.Position == input.Length;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Trader inventory could not be parsed. player=" + player.entityId + "; exception=" + ex);
            return false;
        }
    }

    private static long CountCurrency(Bag bag, ItemStackGrid toolbelt, ItemValue currency)
    {
        long total = 0;
        for (int i = 0; i < bag.SlotCount; i++)
        {
            ItemStack stack = bag.ItemGrid[i];
            if (stack != null && !stack.IsEmpty() && NetworkStorage.NormalizeKey(stack.itemValue).Equals(NetworkStorage.NormalizeKey(currency))) total += stack.count;
        }
        for (int i = 0; i < toolbelt.Length; i++)
        {
            ItemStack stack = toolbelt[i];
            if (stack != null && !stack.IsEmpty() && NetworkStorage.NormalizeKey(stack.itemValue).Equals(NetworkStorage.NormalizeKey(currency))) total += stack.count;
        }
        return total;
    }

    private static bool RemoveCurrency(Bag bag, ItemStackGrid toolbelt, ItemValue currency, int count)
    {
        int remaining = count;
        for (int i = 0; i < bag.SlotCount && remaining > 0; i++)
        {
            ItemStack stack = bag.ItemGrid[i];
            if (stack == null || stack.IsEmpty() || !NetworkStorage.NormalizeKey(stack.itemValue).Equals(NetworkStorage.NormalizeKey(currency))) continue;
            int take = Math.Min(remaining, stack.count);
            stack.count -= take; remaining -= take;
            bag.ItemGrid.SetItem(i, stack.count > 0 ? stack : ItemStack.Empty.Clone());
        }
        for (int i = 0; i < toolbelt.Length && remaining > 0; i++)
        {
            ItemStack stack = toolbelt[i];
            if (stack == null || stack.IsEmpty() || !NetworkStorage.NormalizeKey(stack.itemValue).Equals(NetworkStorage.NormalizeKey(currency))) continue;
            int take = Math.Min(remaining, stack.count);
            stack.count -= take; remaining -= take;
            toolbelt.SetItem(i, stack.count > 0 ? stack : ItemStack.Empty.Clone());
        }
        return remaining == 0;
    }

    private static MemoryStream WriteToolbelt(ItemStackGrid toolbelt, byte selectedSlot)
    {
        using MemoryStream output = new MemoryStream();
        using PooledBinaryWriter writer = MemoryPools.poolBinaryWriter.AllocSync(false);
        writer.SetBaseStream(output);
        BinaryWriter binary = writer;
        binary.Write((byte)1);
        toolbelt.Write(writer, StreamModeWrite.Persistency);
        binary.Write(selectedSlot);
        writer.Flush();
        return new MemoryStream(output.ToArray(), writable: false);
    }

    private static bool ApplyPlayerInventory(EntityPlayer player, PlayerDataFile data, MemoryStream bag, MemoryStream toolbelt)
    {
        try
        {
            NetworkRpcServer.ApplyPlayerInventorySnapshot(player, bag, false);
            NetworkRpcServer.ApplyPlayerInventorySnapshot(player, toolbelt, true);
            if (data != null) { data.bagData = bag; data.inventoryData = toolbelt; data.bModifiedSinceLastSave = true; }
            return true;
        }
        catch (Exception ex) { NetworkLog.Error("Trader purchase payment could not be applied to player inventory. player=" + player.entityId + "; exception=" + ex); return false; }
    }

    private static void RestorePlayerInventory(EntityPlayer player, PlayerDataFile data, MemoryStream bag, MemoryStream toolbelt,
        MemoryStream oldBagData, MemoryStream oldToolbeltData)
    {
        try
        {
            NetworkRpcServer.ApplyPlayerInventorySnapshot(player, bag, false);
            NetworkRpcServer.ApplyPlayerInventorySnapshot(player, toolbelt, true);
            if (data != null) { data.bagData = oldBagData; data.inventoryData = oldToolbeltData; data.bModifiedSinceLastSave = true; }
        }
        catch (Exception ex) { NetworkLog.Error("Trader purchase payment rollback could not restore player inventory. player=" + player.entityId + "; exception=" + ex); }
    }

    private static NetworkRemoteResult Failure(NetworkRemoteRequest request, string error)
        => new NetworkRemoteResult { RequestId = request?.RequestId ?? 0, Success = false, Error = error };
}
