using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HyperdimensionalNetwork;

// Opt-in, scoped wall-clock profiler for this mod. Nested scopes are inclusive.
internal static class NetworkProfiler
{
    private sealed class Counter
    {
        public long Calls;
        public long TotalTicks;
        public long MaxTicks;
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly string name;
        private readonly long started;
        private readonly int generation;

        internal Scope(string name, long started, int generation)
        {
            this.name = name;
            this.started = started;
            this.generation = generation;
        }

        public void Dispose()
        {
            if (started != 0) Record(name, started, generation);
        }
    }

    private static readonly object Sync = new object();
    private static readonly Dictionary<string, Counter> Counters = new Dictionary<string, Counter>(StringComparer.Ordinal);
    private static volatile bool active;
    private static int generation;
    private static long startedAt;
    private static long stoppedAt;
    private static long deadline;
    private static int[] initialGc = new int[3];
    private static int[] finalGc = new int[3];

    internal static Scope Measure(string name)
    {
        if (!active) return default;
        return new Scope(name, Stopwatch.GetTimestamp(), generation);
    }

    internal static string Start(int seconds)
    {
        lock (Sync)
        {
            Counters.Clear();
            generation++;
            startedAt = Stopwatch.GetTimestamp();
            stoppedAt = 0;
            deadline = startedAt + (long)seconds * Stopwatch.Frequency;
            initialGc = GcCounts();
            finalGc = initialGc;
            active = true;
            return "Profiling started for " + seconds + " seconds. Use hdn-profile report after playing the scenario.";
        }
    }

    internal static string Stop()
    {
        lock (Sync)
        {
            Finish(Stopwatch.GetTimestamp());
            return ReportLocked(15);
        }
    }

    internal static string Status()
    {
        lock (Sync)
        {
            long now = Stopwatch.GetTimestamp();
            if (active && now >= deadline) Finish(deadline);
            if (startedAt == 0) return "No profile captured. Use hdn-profile start [seconds].";
            double elapsed = (active ? now - startedAt : stoppedAt - startedAt) * 1d / Stopwatch.Frequency;
            return "Profiler " + (active ? "running" : "stopped") + "; elapsed=" + elapsed.ToString("F1")
                + "s; areas=" + Counters.Count + "; use hdn-profile report [top].";
        }
    }

    internal static string Report(int top)
    {
        lock (Sync)
        {
            if (active && Stopwatch.GetTimestamp() >= deadline) Finish(deadline);
            return ReportLocked(top);
        }
    }

    private static void Record(string name, long start, int scopeGeneration)
    {
        long end = Stopwatch.GetTimestamp();
        lock (Sync)
        {
            if (!active || scopeGeneration != generation) return;
            if (end >= deadline) Finish(deadline);
            if (!Counters.TryGetValue(name, out Counter counter)) Counters[name] = counter = new Counter();
            long elapsed = Math.Max(0, Math.Min(end, stoppedAt == 0 ? end : stoppedAt) - start);
            counter.Calls++;
            counter.TotalTicks += elapsed;
            counter.MaxTicks = Math.Max(counter.MaxTicks, elapsed);
        }
    }

    private static void Finish(long at)
    {
        if (!active) return;
        active = false;
        stoppedAt = at;
        finalGc = GcCounts();
        NetworkLog.Debug("Performance profile finished. areas=" + Counters.Count);
        // ConsoleCmdAbstract.Output is visible in F1 but is not copied into the game log.
        // Persist one compact report through DebugLog so live performance captures can be inspected later.
        foreach (string line in ReportLocked(40).Split('\n'))
            NetworkLog.Debug("Performance profile: " + line.TrimEnd('\r'));
    }

    private static string ReportLocked(int top)
    {
        if (startedAt == 0) return "No profile captured. Use hdn-profile start [seconds].";
        double durationMs = (active ? Stopwatch.GetTimestamp() - startedAt : stoppedAt - startedAt) * 1000d / Stopwatch.Frequency;
        int[] gc = active ? GcCounts() : finalGc;
        List<string> lines = new List<string>
        {
            "Profile " + (active ? "running" : "finished") + "; wall=" + durationMs.ToString("F0")
                + "ms; GC collections gen0/1/2=" + (gc[0] - initialGc[0]) + "/" + (gc[1] - initialGc[1]) + "/" + (gc[2] - initialGc[2]),
            "Inclusive area timings: total ms | calls | avg ms | max ms (nested totals can overlap)"
        };
        foreach (var entry in Counters.OrderByDescending(pair => pair.Value.TotalTicks).Take(top))
        {
            double total = entry.Value.TotalTicks * 1000d / Stopwatch.Frequency;
            double max = entry.Value.MaxTicks * 1000d / Stopwatch.Frequency;
            lines.Add(entry.Key + ": " + total.ToString("F2") + " | " + entry.Value.Calls
                + " | " + (total / entry.Value.Calls).ToString("F3") + " | " + max.ToString("F3"));
        }
        if (Counters.Count == 0) lines.Add("No instrumented mod work ran during this capture.");
        return string.Join("\n", lines);
    }

    private static int[] GcCounts() => new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
}
