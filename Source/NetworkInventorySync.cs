using System;
using System.IO;

namespace HyperdimensionalNetwork;

internal static class NetworkInventorySync
{
    public static bool Apply(EntityPlayerLocal player, NetworkRemoteResult result)
    {
        if (!(bool)player || result == null) return false;
        try
        {
            if (result.BagData != null)
            {
                using (MemoryStream bagSnapshot = new MemoryStream(result.BagData, writable: false))
                    StreamUtils.FromBlob(bagSnapshot, reader => player.bag.ReadInto(reader, StreamModeRead.Persistency));
            }
            if (result.InventoryData != null)
            {
                int selectedSlot = player.inventory.holdingItemIdx;
                using (MemoryStream inventorySnapshot = new MemoryStream(result.InventoryData, writable: false))
                    StreamUtils.FromBlob(inventorySnapshot, reader => player.inventory.ReadInto(reader, StreamModeRead.Persistency));
                if (selectedSlot >= 0 && selectedSlot < player.inventory.Length) player.inventory.SetSelectedSlot(selectedSlot);
                player.inventory.CallOnToolbeltChangedInternal();
            }
            NetworkLog.Debug("Confirmed player inventory snapshot applied. entity=" + player.entityId
                + "; bag=" + (result.BagData?.Length ?? 0) + "; toolbelt=" + (result.InventoryData?.Length ?? 0)
                + "; requestId=" + result.RequestId);
            return true;
        }
        catch (Exception ex)
        {
            NetworkLog.Error("Could not apply the server-confirmed player inventory snapshot. entity=" + player.entityId
                + "; requestId=" + result.RequestId + "; exception=" + ex);
            return false;
        }
    }
}
