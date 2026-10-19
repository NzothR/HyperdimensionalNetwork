using System;
using System.IO;

namespace HyperdimensionalNetwork;

internal static class NetworkInventorySync
{
    public static bool ApplyEntityBag(Entity entity, byte[] data)
    {
        if (!(bool)entity || entity.bag == null || data == null || data.Length == 0) return false;
        try
        {
            using (MemoryStream snapshot = new MemoryStream(data, writable: false))
                StreamUtils.FromBlob(snapshot, reader => entity.bag.ReadInto(reader, StreamModeRead.FromServer));
            NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(entity.bag));
            NetworkLog.Debug("Server-confirmed entity bag snapshot applied. entity=" + entity.entityId + "; type=" + entity.GetType().Name + "; bytes=" + data.Length);
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not apply the server-confirmed entity bag snapshot. entity=" + entity.entityId + "; type=" + entity.GetType().Name + "; exception=" + ex);
            return false;
        }
    }

    public static bool Apply(EntityPlayerLocal player, NetworkRemoteResult result, bool suppressRestockEvents = false)
    {
        if (!(bool)player || result == null) return false;
        IDisposable suppression = suppressRestockEvents ? AutoRestockClient.SuppressInventoryEvents(player) : null;
        try
        {
            if (result.BagData != null)
            {
                using (MemoryStream bagSnapshot = new MemoryStream(result.BagData, writable: false))
                    StreamUtils.FromBlob(bagSnapshot, reader => player.bag.ReadInto(reader, StreamModeRead.Persistency));
                NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(player.bag));
            }
            if (result.InventoryData != null)
            {
                int selectedSlot = player.inventory.holdingItemIdx;
                using (MemoryStream inventorySnapshot = new MemoryStream(result.InventoryData, writable: false))
                    StreamUtils.FromBlob(inventorySnapshot, reader => player.inventory.ReadInto(reader, StreamModeRead.Persistency));
                NetworkStackCompatibility.RestoreExtendedCounts(NetworkStackCompatibility.GetSlots(player.inventory.ItemGrid));
                if (selectedSlot >= 0 && selectedSlot < player.inventory.Length) player.inventory.SetSelectedSlot(selectedSlot);
                player.inventory.CallOnToolbeltChangedInternal();
            }
            NetworkLog.Debug("Confirmed player inventory snapshot applied. entity=" + player.entityId
                + "; bag=" + (result.BagData?.Length ?? 0) + "; toolbelt=" + (result.InventoryData?.Length ?? 0)
                + "; requestId=" + result.RequestId);
            if (suppressRestockEvents) AutoRestockClient.RefreshBaseline(player);
            else AutoRestockClient.OnInventorySnapshotApplied(player, result.BagData != null, result.InventoryData != null);
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not apply the server-confirmed player inventory snapshot. entity=" + player.entityId
                + "; requestId=" + result.RequestId + "; exception=" + ex);
            return false;
        }
        finally { suppression?.Dispose(); }
    }
}
