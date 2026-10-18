using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace HyperdimensionalNetwork;

internal static class NetworkStressTest
{
    private const string ItemName = "HyperdimensionalNetworkStressTest";
    private const string ExtremeItemName = "HyperdimensionalNetworkStressTestMax";
    private static readonly string[] QualityMaximumMemberNames = { "QualityMax", "MaxQuality", "qualityMax", "maxQuality", "QualityMaxLevel", "MaxQualityLevel", "QualityMaxTier", "MaxQualityTier" };

    public static bool IsTestItem(ItemClass itemClass) => itemClass != null && (string.Equals(itemClass.GetItemName(), ItemName, StringComparison.Ordinal) || string.Equals(itemClass.GetItemName(), ExtremeItemName, StringComparison.Ordinal));

    public static bool IsExtreme(ItemClass itemClass) => itemClass != null && string.Equals(itemClass.GetItemName(), ExtremeItemName, StringComparison.Ordinal);

    public static void Run(EntityPlayerLocal player, bool extreme)
    {
        if (!(bool)player) return;
        Stopwatch total = Stopwatch.StartNew();
        ItemClass[] classes = ItemClass.list;
        if (classes == null || classes.Length == 0)
        {
            NetworkLog.Error("Stress test aborted: ItemClass.list is empty.");
            return;
        }

        List<StorageAddition> additions = new List<StorageAddition>(classes.Length * 2);
        HashSet<int> seen = new HashSet<int>();
        int candidates = 0, variants = 0, skipped = 0, qualityClasses = 0, fallbackQualityMax = 0;
        long units = 0;
        Stopwatch build = Stopwatch.StartNew();
        foreach (ItemClass itemClass in classes)
        {
            if (itemClass == null || itemClass.Id <= 0 || !seen.Add(itemClass.Id)) { skipped++; continue; }
            int stackMax;
            try { stackMax = itemClass.MaxCount; }
            catch { skipped++; continue; }
            if (stackMax <= 0) { skipped++; continue; }
            long amountPerVariant = extreme && stackMax > 1 ? int.MaxValue : stackMax;
            candidates++;
            int qMin = 0, qMax = 0;
            if (itemClass.HasQuality)
            {
                qualityClasses++;
                qMin = Math.Max(1, (int)itemClass.QualityMin);
                if (!TryGetQualityMaximum(itemClass, out qMax))
                {
                    qMax = 6;
                    fallbackQualityMax++;
                }
                qMax = Math.Max(qMin, Math.Min(ushort.MaxValue, qMax));
            }
            int first = itemClass.HasQuality ? qMin : 0;
            int last = itemClass.HasQuality ? qMax : 0;
            for (int quality = first; quality <= last; quality++)
            {
                try
                {
                    ItemValue value = itemClass.HasQuality
                        ? new ItemValue(itemClass.Id, quality, quality, false)
                        : new ItemValue(itemClass.Id, false);
                    additions.Add(new StorageAddition { Value = value, Count = amountPerVariant });
                    units += amountPerVariant;
                    variants++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    NetworkLog.Debug("Stress test skipped item variant. item=" + itemClass.GetItemName() + "; quality=" + quality + "; exception=" + ex.Message);
                }
            }
        }
        build.Stop();
        Stopwatch add = Stopwatch.StartNew();
        bool saved = NetworkStorage.AddBatch(player, additions);
        add.Stop();
        total.Stop();
        NetworkLog.Info("Stress test finished. mode=" + (extreme ? "int32-max-stackable" : "native-stack-limit") + "; player=" + player.PlayerDisplayName + "; itemClassEntries=" + classes.Length + "; itemClasses=" + candidates + "; qualityClasses=" + qualityClasses + "; variantsAdded=" + variants + "; fallbackQualityMax=" + fallbackQualityMax + "; skipped=" + skipped + "; unitsRequested=" + units + "; buildMs=" + build.Elapsed.TotalMilliseconds.ToString("F2") + "; transactionMs=" + add.Elapsed.TotalMilliseconds.ToString("F2") + "; totalMs=" + total.Elapsed.TotalMilliseconds.ToString("F2") + "; saved=" + saved + "; revision=" + NetworkStorage.GetRevision(player));
        GameManager.ShowTooltip(player, saved
            ? "超维网络压力测试完成：加入 " + variants.ToString("N0") + " 种物品变体。"
            : "超维网络压力测试未能写入，请检查游戏日志。");
        if (fallbackQualityMax > 0) NetworkLog.Info("Some item classes did not expose a quality maximum; stress test used native fallback maximum 6 for those classes.");
    }

    private static bool TryGetQualityMaximum(ItemClass itemClass, out int value)
    {
        Type type = itemClass.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string name in QualityMaximumMemberNames)
        {
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.CanRead)
            {
                object raw = property.GetValue(itemClass, null);
                if (TryInt(raw, out value)) return true;
            }
            FieldInfo field = type.GetField(name, flags);
            if (field != null && TryInt(field.GetValue(itemClass), out value)) return true;
        }
        value = 0;
        return false;
    }

    private static bool TryInt(object value, out int result)
    {
        try
        {
            if (value != null)
            {
                PropertyInfo wrappedValue = value.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (wrappedValue != null && wrappedValue.CanRead && wrappedValue.PropertyType != value.GetType()) value = wrappedValue.GetValue(value, null);
            }
            result = Convert.ToInt32(value);
            return result > 0;
        }
        catch { result = 0; return false; }
    }
}
