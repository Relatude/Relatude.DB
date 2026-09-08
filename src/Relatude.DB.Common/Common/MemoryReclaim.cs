using System.Diagnostics;
using System.Runtime;
namespace Relatude.DB.Common;

public readonly record struct MemoryReclaimResult(
    long ManagedBefore, long ManagedAfter,
    long Committed,
    long WorkingSetBefore, long WorkingSetAfter,
    int Collections, long ElapsedMs) {
    public long ManagedFreed => Math.Max(0, ManagedBefore - ManagedAfter);
    public long WorkingSetFreed => Math.Max(0, WorkingSetBefore - WorkingSetAfter);
}

public static class MemoryReclaim {
    public static MemoryReclaimResult Collect(int finalizerRounds = 2) {
        if (finalizerRounds < 0) finalizerRounds = 0;
        var watch = Stopwatch.StartNew();
        var managedBefore = GC.GetTotalMemory(false);
        var workingSetBefore = workingSet();
        var collections = 0;
        for (var round = 0; round < finalizerRounds; round++) {
            collectOnce();
            collections++;
            GC.WaitForPendingFinalizers();
        }
        collectOnce();
        collections++;
        watch.Stop();
        return new(
            managedBefore, GC.GetTotalMemory(false),
            committed(),
            workingSetBefore, workingSet(),
            collections, watch.ElapsedMilliseconds);
    }
    static void collectOnce() {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
    static long committed() {
        try {
            return GC.GetGCMemoryInfo().TotalCommittedBytes;
        } catch {
            return 0;
        }
    }
    static long workingSet() {
        try {
            return Environment.WorkingSet;
        } catch {
            return 0;
        }
    }
}
