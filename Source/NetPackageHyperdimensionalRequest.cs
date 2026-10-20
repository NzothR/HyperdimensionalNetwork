using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace HyperdimensionalNetwork;

public sealed class NetPackageHyperdimensionalRequest : NetPackage
{
    private NetworkRemoteRequest _request;

    public override NetPackageDirection PackageDirection => NetPackageDirection.ToServer;

    internal NetPackageHyperdimensionalRequest Setup(NetworkRemoteRequest request)
    {
        _request = request;
        return this;
    }

    public override void read(PooledBinaryReader reader)
    {
        _request = new NetworkRemoteRequest
        {
            EntityId = reader.ReadInt32(),
            RequestId = reader.ReadInt64(),
            OriginalRequestId = reader.ReadInt64(),
            ExpectedNetworkId = ReadBoundedString(reader, 128),
            Operation = (NetworkRequestOperation)reader.ReadByte(),
            SourceLocation = (XUiC_ItemStack.StackLocationTypes)reader.ReadByte(),
            Category = ReadBoundedString(reader, 256),
            Search = ReadBoundedString(reader, 256),
            Page = reader.ReadInt32(),
            PageSize = reader.ReadInt32()
        };
        if (reader.ReadBoolean())
        {
            _request.Value = new ItemValue();
            _request.Value.Read(reader);
        }
        _request.Count = reader.ReadInt32();
        _request.Slot = reader.ReadInt32();
        _request.Position = StreamUtils.ReadVector3i(reader);
        _request.TargetEntityId = reader.ReadInt32();
        _request.ConsumptionSource = (NetworkConsumptionSource)reader.ReadByte();
        int requirementCount = reader.ReadInt32();
        if (requirementCount < 0 || requirementCount > 128) throw new InvalidDataException("Invalid network material requirement count: " + requirementCount);
        for (int i = 0; i < requirementCount; i++)
        {
            ItemValue value = new ItemValue();
            value.Read(reader);
            int count = reader.ReadInt32();
            if (count <= 0) throw new InvalidDataException("Invalid required material count.");
            _request.Requirements.Add(new ItemStack(value, count));
        }
        _request.RestockSetting = (RestockSettingKey)reader.ReadByte();
        _request.RestockSettingValue = reader.ReadInt32();
        _request.TraderNetworkSource = reader.ReadBoolean();
        _request.TraderPersonalPayout = reader.ReadBoolean();
        _request.TraderBuyToBackpack = reader.ReadBoolean();
        _request.TraderAutomation = reader.ReadBoolean();
        _request.TraderSessionId = ReadBoundedString(reader, 64);
        _request.TraderEntityId = reader.ReadInt32();
        _request.TraderInventoryIndex = reader.ReadInt32();
        ReadSlotList(reader, _request.RestockToolbeltSlots);
        ReadSlotList(reader, _request.RestockBackpackSlots);
        _request.TraderRuleId = ReadBoundedString(reader, 64);
        _request.TraderRuleMove = reader.ReadInt32();
        if (reader.ReadBoolean()) _request.TraderRule = ReadTraderRule(reader);
        _request.MembershipTargetPlayerId = ReadBoundedString(reader, 128);
        _request.MembershipOperationId = ReadBoundedString(reader, 64);
        _request.MembershipCandidatePage = reader.ReadInt32();
        int forgeCount = reader.ReadInt32();
        if (forgeCount < 0 || forgeCount > ForgeFeedService.MaximumForges)
            throw new InvalidDataException("Invalid forge feed selection count: " + forgeCount);
        for (int i = 0; i < forgeCount; i++) _request.ForgePositions.Add(StreamUtils.ReadVector3i(reader));
    }

    public override void write(PooledBinaryWriter writer)
    {
        base.write(writer);
        BinaryWriter binary = writer;
        binary.Write(_request.EntityId);
        binary.Write(_request.RequestId);
        binary.Write(_request.OriginalRequestId);
        binary.Write(_request.ExpectedNetworkId ?? string.Empty);
        binary.Write((byte)_request.Operation);
        binary.Write((byte)_request.SourceLocation);
        binary.Write(_request.Category ?? string.Empty);
        binary.Write(_request.Search ?? string.Empty);
        binary.Write(_request.Page);
        binary.Write(_request.PageSize);
        binary.Write(_request.Value != null);
        _request.Value?.Write(writer);
        binary.Write(_request.Count);
        binary.Write(_request.Slot);
        StreamUtils.Write(writer, _request.Position);
        binary.Write(_request.TargetEntityId);
        binary.Write((byte)_request.ConsumptionSource);
        int requirementCount = _request.Requirements?.Count ?? 0;
        if (requirementCount > 128) throw new InvalidDataException("Too many network material requirements: " + requirementCount);
        binary.Write(requirementCount);
        if (_request.Requirements != null)
            foreach (ItemStack requirement in _request.Requirements)
            {
                requirement.itemValue.Write(writer);
                binary.Write(requirement.count);
            }
        binary.Write((byte)_request.RestockSetting);
        binary.Write(_request.RestockSettingValue);
        binary.Write(_request.TraderNetworkSource);
        binary.Write(_request.TraderPersonalPayout);
        binary.Write(_request.TraderBuyToBackpack);
        binary.Write(_request.TraderAutomation);
        binary.Write(_request.TraderSessionId ?? string.Empty);
        binary.Write(_request.TraderEntityId);
        binary.Write(_request.TraderInventoryIndex);
        WriteSlotList(binary, _request.RestockToolbeltSlots);
        WriteSlotList(binary, _request.RestockBackpackSlots);
        binary.Write(_request.TraderRuleId ?? string.Empty);
        binary.Write(_request.TraderRuleMove);
        binary.Write(_request.TraderRule != null);
        if (_request.TraderRule != null) WriteTraderRule(binary, _request.TraderRule);
        binary.Write(_request.MembershipTargetPlayerId ?? string.Empty);
        binary.Write(_request.MembershipOperationId ?? string.Empty);
        binary.Write(_request.MembershipCandidatePage);
        int forgeCount = _request.ForgePositions?.Count ?? 0;
        if (forgeCount > ForgeFeedService.MaximumForges) throw new InvalidDataException("Too many selected forge positions: " + forgeCount);
        binary.Write(forgeCount);
        if (_request.ForgePositions != null)
            foreach (Vector3i position in _request.ForgePositions) StreamUtils.Write(writer, position);
    }

    public override void ProcessPackage(World world, GameManager callbacks)
    {
        ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (world == null || !(bool)connection || !connection.IsServer || _request == null) return;
        if (!ValidEntityIdForSender(_request.EntityId)) return;
        EntityPlayer player = world.GetEntity(_request.EntityId) as EntityPlayer;
        if (!(bool)player)
        {
            NetworkLog.Error("Rejected network request because the authenticated player entity was unavailable. entity=" + _request.EntityId + "; requestId=" + _request.RequestId);
            return;
        }
        NetworkRpcServer.ProcessRequest(world, player, Sender, _request);
    }

    public int GetLength()
    {
        int valueBytes = _request?.Value == null ? 0 : 128;
        int requirementsBytes = (_request?.Requirements?.Count ?? 0) * 512;
        int slotBytes = ((_request?.RestockToolbeltSlots?.Count ?? 0) + (_request?.RestockBackpackSlots?.Count ?? 0)) * sizeof(int);
        return 100 + Encoding.UTF8.GetByteCount(_request?.ExpectedNetworkId ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_request?.Category ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_request?.Search ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_request?.TraderSessionId ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_request?.TraderRuleId ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_request?.MembershipTargetPlayerId ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_request?.MembershipOperationId ?? string.Empty)
            + (_request?.TraderRule == null ? 0 : 105 + Encoding.UTF8.GetByteCount(_request.TraderRule.Category ?? string.Empty)
                + Encoding.UTF8.GetByteCount(_request.TraderRule.ExcludedCategories ?? string.Empty)
                + Encoding.UTF8.GetByteCount(_request.TraderRule.ExcludedItemTypes ?? string.Empty))
            + valueBytes + requirementsBytes + slotBytes + 4 + (_request?.ForgePositions?.Count ?? 0) * 12;
    }

    private static TraderAutomationRule ReadTraderRule(BinaryReader reader)
        => new TraderAutomationRule
        {
            Id = ReadBoundedString(reader, 64), Kind = (TraderAutomationRuleKind)reader.ReadByte(), Enabled = reader.ReadBoolean(),
            ItemType = reader.ReadInt32(), QualityMode = (TraderQualityMatchMode)reader.ReadByte(), Quality = reader.ReadInt32(),
            MaximumCount = reader.ReadInt32(), MinimumNetworkCount = reader.ReadInt32(), NetworkCountComparison = (TraderRuleComparison)reader.ReadByte(),
            MinimumCurrencyCount = reader.ReadInt32(), CurrencyCountComparison = (TraderRuleComparison)reader.ReadByte(), MaximumSpend = reader.ReadInt32(),
            Scheduled = reader.ReadBoolean(), ScheduledHour = reader.ReadInt32(), IntervalDays = reader.ReadInt32(), LastScheduledDay = reader.ReadInt32(),
            Scope = (TraderRuleScope)reader.ReadByte(), Category = ReadBoundedString(reader, 128),
            ExcludedCategories = ReadBoundedString(reader, 1024), ExcludedItemTypes = ReadBoundedString(reader, 1024),
            AutoSellSource = (TraderAutoSellSource)reader.ReadByte(),
            Personal = reader.ReadBoolean()
        };

    private static void WriteTraderRule(BinaryWriter writer, TraderAutomationRule rule)
    {
        writer.Write(rule.Id ?? string.Empty); writer.Write((byte)rule.Kind); writer.Write(rule.Enabled); writer.Write(rule.ItemType);
        writer.Write((byte)rule.QualityMode); writer.Write(rule.Quality); writer.Write(rule.MaximumCount); writer.Write(rule.MinimumNetworkCount);
        writer.Write((byte)rule.NetworkCountComparison); writer.Write(rule.MinimumCurrencyCount); writer.Write((byte)rule.CurrencyCountComparison);
        writer.Write(rule.MaximumSpend); writer.Write(rule.Scheduled); writer.Write(rule.ScheduledHour); writer.Write(rule.IntervalDays); writer.Write(rule.LastScheduledDay);
        writer.Write((byte)rule.Scope); writer.Write(rule.Category ?? string.Empty);
        writer.Write(rule.ExcludedCategories ?? string.Empty); writer.Write(rule.ExcludedItemTypes ?? string.Empty);
        writer.Write((byte)rule.AutoSellSource);
        writer.Write(rule.Personal);
    }

    private static void ReadSlotList(BinaryReader reader, List<int> slots)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 128) throw new InvalidDataException("Invalid restock slot count: " + count);
        for (int i = 0; i < count; i++)
        {
            int slot = reader.ReadInt32();
            if (slot < 0) throw new InvalidDataException("Invalid negative restock slot index.");
            slots.Add(slot);
        }
    }

    private static void WriteSlotList(BinaryWriter writer, List<int> slots)
    {
        int count = slots?.Count ?? 0;
        if (count > 128) throw new InvalidDataException("Too many restock slot indexes: " + count);
        writer.Write(count);
        if (slots != null) foreach (int slot in slots)
        {
            if (slot < 0) throw new InvalidDataException("Invalid negative restock slot index.");
            writer.Write(slot);
        }
    }

    private static string ReadBoundedString(BinaryReader reader, int maximumBytes)
    {
        string value = reader.ReadString();
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes) throw new InvalidDataException("Hyperdimensional network request string exceeded its limit.");
        return value;
    }
}
