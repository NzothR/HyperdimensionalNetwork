using System.Collections.Generic;

namespace HyperdimensionalNetwork;

internal static class NetworkStackCompatibility
{
    private const string ExtendedCountMetadata = "YiWanStackCount";

    public static bool RestoreExtendedCount(ItemStack stack)
    {
        ItemValue value = stack?.itemValue;
        if (value == null) return false;
        bool restored = false;
        if (stack.count == ushort.MaxValue
            && value.TryGetMetadata(ExtendedCountMetadata, out int extendedCount)
            && extendedCount > ushort.MaxValue)
        {
            restored = true;
            stack.count = extendedCount;
            NetworkLog.Debug("Restored extended stack count from inventory metadata. item=" + value.type + "; count=" + extendedCount);
        }
        value.RemoveMetaData(ExtendedCountMetadata);
        return restored;
    }

    public static void RestoreExtendedCounts(IEnumerable<ItemStack> stacks)
    {
        if (stacks == null) return;
        foreach (ItemStack stack in stacks) RestoreExtendedCount(stack);
    }

    public static List<ItemValue> PrepareForWrite(IEnumerable<ItemStack> stacks)
    {
        List<ItemValue> prepared = new List<ItemValue>();
        if (stacks == null) return prepared;
        foreach (ItemStack stack in stacks)
        {
            ItemValue value = stack?.itemValue;
            if (value == null || stack.count <= ushort.MaxValue) continue;
            value.SetMetadata(ExtendedCountMetadata, stack.count);
            prepared.Add(value);
        }
        return prepared;
    }

    public static void ClearWriteMetadata(IEnumerable<ItemValue> values)
    {
        if (values == null) return;
        foreach (ItemValue value in values) value?.RemoveMetaData(ExtendedCountMetadata);
    }

    public static IEnumerable<ItemStack> GetSlots(Bag bag)
    {
        for (int i = 0; i < (bag?.SlotCount ?? 0); i++) yield return bag.ItemGrid[i];
    }

    public static IEnumerable<ItemStack> GetSlots(ItemStackGrid grid)
    {
        for (int i = 0; i < (grid?.Length ?? 0); i++) yield return grid[i];
    }
}
