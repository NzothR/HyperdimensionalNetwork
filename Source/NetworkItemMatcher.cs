using System;

namespace HyperdimensionalNetwork;

internal static class NetworkItemMatcher
{
    public static bool Matches(ItemValue candidate, ItemValue requirement, bool includeModded = false)
    {
        if (candidate == null || requirement == null || candidate.type != requirement.type) return false;
        if (requirement.Quality > 0 && candidate.Quality != requirement.Quality) return false;
        if (requirement.Meta != 0 && candidate.Meta != requirement.Meta) return false;
        if (requirement.SelectedAmmoTypeIndex != 0 && candidate.SelectedAmmoTypeIndex != requirement.SelectedAmmoTypeIndex) return false;
        if (requirement.HasMods() || requirement.HasCosmetics())
            return NetworkStorage.NormalizeKey(candidate).Equals(NetworkStorage.NormalizeKey(requirement));
        return includeModded || !candidate.HasMods();
    }

    public static int ClampCount(long count) => (int)Math.Max(0, Math.Min(count, int.MaxValue));

    public static int AddClamped(int physical, long network)
    {
        long total = Math.Max(0L, network);
        if (total >= int.MaxValue) return int.MaxValue;
        return ClampCount(total + Math.Max(0, physical));
    }
}
