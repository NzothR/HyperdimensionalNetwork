using System;
using System.IO;
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
        return 106 + entries * 512 + bytes + Encoding.UTF8.GetByteCount(_result?.Error ?? string.Empty) + Encoding.UTF8.GetByteCount(_result?.NetworkId ?? string.Empty);
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
