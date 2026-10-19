using System;
using System.IO;
using System.Linq;
using System.Text;

namespace HyperdimensionalNetwork;

public sealed class NetPackageHyperdimensionalResponse : NetPackage
{
    private int _entityId;
    private NetworkRemoteResult _result;

    public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

    internal NetPackageHyperdimensionalResponse Setup(int entityId, NetworkRemoteResult result)
    {
        _entityId = entityId;
        _result = result;
        return this;
    }

    public override void read(PooledBinaryReader reader)
    {
        _entityId = reader.ReadInt32();
        _result = new NetworkRemoteResult
        {
            RequestId = reader.ReadInt64(),
            Success = reader.ReadBoolean(),
            Error = ReadBoundedString(reader, 256),
            Revision = reader.ReadInt64(),
            NetworkId = ReadBoundedString(reader, 128),
            TotalEntries = reader.ReadInt32(),
            Page = reader.ReadInt32(),
            PageSize = reader.ReadInt32(),
            Amount = reader.ReadInt32(),
            UpdatedSourceCount = reader.ReadInt32()
        };
        if (reader.ReadBoolean())
        {
            _result.Value = new ItemValue();
            _result.Value.Read(reader);
        }
        int entryCount = reader.ReadInt32();
        if (entryCount < 0 || entryCount > 4096) throw new InvalidDataException("Invalid network response entry count: " + entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            ItemValue value = new ItemValue();
            value.Read(reader);
            long count = reader.ReadInt64();
            if (count <= 0) throw new InvalidDataException("Invalid item count in network page response.");
            _result.Entries.Add(new StoredEntry { Value = value, Count = count });
        }
        _result.BagData = ReadBoundedBlob(reader);
        _result.InventoryData = ReadBoundedBlob(reader);
        _result.RestockSettingsLoaded = reader.ReadBoolean();
        _result.AutoRestockEnabled = reader.ReadBoolean();
        _result.RestockTargetMode = (RestockTargetMode)reader.ReadByte();
        _result.IsNetworkAdministrator = reader.ReadBoolean();
        _result.AutoRestockCooldownMs = reader.ReadInt32();
        _result.ReplenishmentEnabled = reader.ReadBoolean();
        _result.AmmoBankEnabled = reader.ReadBoolean();
        _result.RetainSearchOnClose = reader.ReadBoolean();
        _result.ManualSaleEnabled = reader.ReadBoolean();
        _result.TraderSessionId = ReadBoundedString(reader, 64);
        int traderOfferCount = reader.ReadInt32();
        if (traderOfferCount < 0 || traderOfferCount > 2048) throw new InvalidDataException("Invalid trader offer count: " + traderOfferCount);
        for (int i = 0; i < traderOfferCount; i++)
        {
            ItemValue value = new ItemValue();
            value.Read(reader);
            _result.TraderOffers.Add(new TraderOffer
            {
                Value = value,
                Count = reader.ReadInt32(),
                BundleSize = reader.ReadInt32(),
                BundlePrice = reader.ReadInt32(),
                TraderEntityId = reader.ReadInt32(),
                InventoryIndex = reader.ReadInt32()
            });
        }
        int ruleCount = reader.ReadInt32();
        if (ruleCount < 0 || ruleCount > 2048) throw new InvalidDataException("Invalid trader rule count: " + ruleCount);
        for (int i = 0; i < ruleCount; i++)
            _result.TraderRules.Add(new TraderAutomationRule
            {
                Id = ReadBoundedString(reader, 64), Kind = (TraderAutomationRuleKind)reader.ReadByte(), Enabled = reader.ReadBoolean(),
                ItemType = reader.ReadInt32(), QualityMode = (TraderQualityMatchMode)reader.ReadByte(), Quality = reader.ReadInt32(),
                MaximumCount = reader.ReadInt32(), MinimumNetworkCount = reader.ReadInt32(), NetworkCountComparison = (TraderRuleComparison)reader.ReadByte(),
                MinimumCurrencyCount = reader.ReadInt32(), CurrencyCountComparison = (TraderRuleComparison)reader.ReadByte(), MaximumSpend = reader.ReadInt32(),
                Scheduled = reader.ReadBoolean(), ScheduledHour = reader.ReadInt32(), IntervalDays = reader.ReadInt32(), LastScheduledDay = reader.ReadInt32(),
                Scope = (TraderRuleScope)reader.ReadByte(), Category = ReadBoundedString(reader, 128),
                ExcludedCategories = ReadBoundedString(reader, 1024), ExcludedItemTypes = ReadBoundedString(reader, 1024)
            });
        _result.TraderRunProgressAvailable = reader.ReadBoolean();
        _result.TraderRunActive = reader.ReadBoolean();
        _result.TraderRunCurrentRule = reader.ReadInt32();
        _result.TraderRunTotalRules = reader.ReadInt32();
        _result.TraderRunMessage = ReadBoundedString(reader, 256);
    }

    public override void write(PooledBinaryWriter writer)
    {
        base.write(writer);
        BinaryWriter binary = writer;
        NetworkRemoteResult result = _result ?? new NetworkRemoteResult { Success = false, Error = "Invalid response" };
        binary.Write(_entityId);
        binary.Write(result.RequestId);
        binary.Write(result.Success);
        binary.Write(result.Error ?? string.Empty);
        binary.Write(result.Revision);
        binary.Write(result.NetworkId ?? string.Empty);
        binary.Write(result.TotalEntries);
        binary.Write(result.Page);
        binary.Write(result.PageSize);
        binary.Write(result.Amount);
        binary.Write(result.UpdatedSourceCount);
        binary.Write(result.Value != null);
        result.Value?.Write(writer);
        int entryCount = result.Entries?.Count ?? 0;
        if (entryCount > 4096) throw new InvalidDataException("Network response entry count exceeds the protocol limit: " + entryCount);
        binary.Write(entryCount);
        if (result.Entries != null)
        {
            foreach (StoredEntry entry in result.Entries)
            {
                entry.Value.Write(writer);
                binary.Write(entry.Count);
            }
        }
        WriteBoundedBlob(binary, result.BagData);
        WriteBoundedBlob(binary, result.InventoryData);
        binary.Write(result.RestockSettingsLoaded);
        binary.Write(result.AutoRestockEnabled);
        binary.Write((byte)result.RestockTargetMode);
        binary.Write(result.IsNetworkAdministrator);
        binary.Write(result.AutoRestockCooldownMs);
        binary.Write(result.ReplenishmentEnabled);
        binary.Write(result.AmmoBankEnabled);
        binary.Write(result.RetainSearchOnClose);
        binary.Write(result.ManualSaleEnabled);
        binary.Write(result.TraderSessionId ?? string.Empty);
        int offerCount = result.TraderOffers?.Count ?? 0;
        if (offerCount > 2048) throw new InvalidDataException("Trader offer count exceeds the response limit: " + offerCount);
        binary.Write(offerCount);
        if (result.TraderOffers != null)
            foreach (TraderOffer offer in result.TraderOffers)
            {
                if (offer?.Value == null || offer.Count <= 0 || offer.BundleSize <= 0 || offer.BundlePrice < 0
                    || offer.TraderEntityId <= 0 || offer.InventoryIndex < 0)
                    throw new InvalidDataException("Invalid trader offer in response.");
                offer.Value.Write(writer);
                binary.Write(offer.Count);
                binary.Write(offer.BundleSize);
                binary.Write(offer.BundlePrice);
                binary.Write(offer.TraderEntityId);
                binary.Write(offer.InventoryIndex);
            }
        int ruleCount = result.TraderRules?.Count ?? 0;
        if (ruleCount > 2048) throw new InvalidDataException("Trader rule count exceeds the response limit: " + ruleCount);
        binary.Write(ruleCount);
        if (result.TraderRules != null)
            foreach (TraderAutomationRule rule in result.TraderRules)
            {
                if (rule == null) throw new InvalidDataException("Invalid trader rule in response.");
                binary.Write(rule.Id ?? string.Empty); binary.Write((byte)rule.Kind); binary.Write(rule.Enabled); binary.Write(rule.ItemType);
                binary.Write((byte)rule.QualityMode); binary.Write(rule.Quality); binary.Write(rule.MaximumCount); binary.Write(rule.MinimumNetworkCount);
                binary.Write((byte)rule.NetworkCountComparison); binary.Write(rule.MinimumCurrencyCount); binary.Write((byte)rule.CurrencyCountComparison);
                binary.Write(rule.MaximumSpend); binary.Write(rule.Scheduled); binary.Write(rule.ScheduledHour); binary.Write(rule.IntervalDays); binary.Write(rule.LastScheduledDay);
                binary.Write((byte)rule.Scope); binary.Write(rule.Category ?? string.Empty);
                binary.Write(rule.ExcludedCategories ?? string.Empty); binary.Write(rule.ExcludedItemTypes ?? string.Empty);
            }
        binary.Write(result.TraderRunProgressAvailable);
        binary.Write(result.TraderRunActive);
        binary.Write(result.TraderRunCurrentRule);
        binary.Write(result.TraderRunTotalRules);
        binary.Write(result.TraderRunMessage ?? string.Empty);
    }

    public override void ProcessPackage(World world, GameManager callbacks)
    {
        if (!(bool)SingletonMonoBehaviour<ConnectionManager>.Instance || !SingletonMonoBehaviour<ConnectionManager>.Instance.IsClient) return;
        EntityPlayerLocal player = world?.GetEntity(_entityId) as EntityPlayerLocal;
        if (!(bool)player || _result == null)
        {
            NetworkLog.Debug("Ignored network response for a non-local or unavailable player entity. entity=" + _entityId);
            return;
        }
        NetworkRemoteClient.HandleResponse(_entityId, _result);
    }

    public int GetLength()
    {
        int entries = _result?.Entries?.Count ?? 0;
        if (entries > 4096) throw new InvalidDataException("Network response entry count exceeds the protocol limit: " + entries);
        int bytes = (_result?.BagData?.Length ?? 0) + (_result?.InventoryData?.Length ?? 0);
        int valueBytes = _result?.Value == null ? 0 : 512;
        int offers = _result?.TraderOffers?.Count ?? 0;
        if (offers > 2048) throw new InvalidDataException("Trader offer count exceeds the response limit: " + offers);
        int rules = _result?.TraderRules?.Count ?? 0;
        if (rules > 2048) throw new InvalidDataException("Trader rule count exceeds the response limit: " + rules);
        int ruleTextBytes = _result?.TraderRules?.Sum(rule => Encoding.UTF8.GetByteCount(rule?.Category ?? string.Empty)
            + Encoding.UTF8.GetByteCount(rule?.ExcludedCategories ?? string.Empty)
            + Encoding.UTF8.GetByteCount(rule?.ExcludedItemTypes ?? string.Empty)) ?? 0;
        return 127 + entries * 512 + offers * 640 + rules * 136 + ruleTextBytes + bytes + valueBytes + Encoding.UTF8.GetByteCount(_result?.Error ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_result?.NetworkId ?? string.Empty) + Encoding.UTF8.GetByteCount(_result?.TraderSessionId ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_result?.TraderRunMessage ?? string.Empty);
    }

    private static string ReadBoundedString(BinaryReader reader, int maximumBytes)
    {
        string value = reader.ReadString();
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes) throw new InvalidDataException("Hyperdimensional network response string exceeded its limit.");
        return value;
    }

    private static byte[] ReadBoundedBlob(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid inventory snapshot size in network response: " + length);
        byte[] result = reader.ReadBytes(length);
        if (result.Length != length) throw new EndOfStreamException("Incomplete inventory snapshot in network response.");
        return length == 0 ? null : result;
    }

    private static void WriteBoundedBlob(BinaryWriter writer, byte[] data)
    {
        if (data == null) { writer.Write(0); return; }
        if (data.Length > 16 * 1024 * 1024) throw new InvalidDataException("Inventory snapshot exceeds the network response limit.");
        writer.Write(data.Length);
        writer.Write(data);
    }
}
