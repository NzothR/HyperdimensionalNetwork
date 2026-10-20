namespace HyperdimensionalNetwork;

internal static class NetworkItemPolicy
{
    internal static bool CanEnterOrDestroy(ItemValue value)
        => value?.ItemClass != null && value.ItemClass.CanDrop(value);
}
