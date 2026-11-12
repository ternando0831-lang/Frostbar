using System.Collections.Concurrent;

namespace Frostbar.Platform.Infrastructure;

/// <summary>Event counters for the diagnostics pipe ("stats"): what wakes the shell while it should be idle.</summary>
public static class Stats
{
    static readonly ConcurrentDictionary<string, long> Counts = new();

    public static void Inc(string name) => Counts.AddOrUpdate(name, 1, (_, n) => n + 1);

    public static IReadOnlyDictionary<string, long> Snapshot() => new SortedDictionary<string, long>(Counts);
}
