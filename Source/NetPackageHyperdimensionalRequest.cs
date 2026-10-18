using System;
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
        _request.ConsumptionSource = (NetworkConsumptionSource)reader.ReadByte();
        int requirementCount = reader.ReadInt32();
        if (requirementCount < 0 || requirementCount > 64) throw new InvalidDataException("Invalid network material requirement count: " + requirementCount);
        for (int i = 0; i < requirementCount; i++)
        {
            ItemValue value = new ItemValue();
            value.Read(reader);
            int count = reader.ReadInt32();
            if (count <= 0) throw new InvalidDataException("Invalid required material count.");
            _request.Requirements.Add(new ItemStack(value, count));
        }
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
        binary.Write((byte)_request.ConsumptionSource);
        int requirementCount = _request.Requirements?.Count ?? 0;
        if (requirementCount > 64) throw new InvalidDataException("Too many network material requirements: " + requirementCount);
        binary.Write(requirementCount);
        if (_request.Requirements != null)
            foreach (ItemStack requirement in _request.Requirements)
            {
                requirement.itemValue.Write(writer);
                binary.Write(requirement.count);
            }
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
        return 64 + Encoding.UTF8.GetByteCount(_request?.ExpectedNetworkId ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_request?.Category ?? string.Empty)
            + Encoding.UTF8.GetByteCount(_request?.Search ?? string.Empty) + valueBytes + requirementsBytes;
    }

    private static string ReadBoundedString(BinaryReader reader, int maximumBytes)
    {
        string value = reader.ReadString();
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes) throw new InvalidDataException("Hyperdimensional network request string exceeded its limit.");
        return value;
    }
}
