using System.Diagnostics;
using System.Runtime;
namespace Relatude.DB.Common;

/// <summary>
/// What a call to <see cref="MemoryReclaim.Collect"/> gave back, all figures in bytes.
/// <para><see cref="Committed"/> has no "before" on purpose. The only figure the runtime offers for
/// it comes from <see cref="GC.GetGCMemoryInfo()"/>, which describes the <i>last</i> collection - so
/// read before this one it is stale, and can be a fraction of what the process actually held. Read
/// after, it is exactly right, because the collection that just ran is the one it describes.</para>
/// </summary>
public readonly record struct MemoryReclaimResult(
    long ManagedBefore, long ManagedAfter,
    long Committed,
    long WorkingSetBefore, long WorkingSetAfter,
    int Collections, long ElapsedMs) {
    /// <summary>Never negative: another thread allocating during the collection can leave the heap
    /// larger than it was, which reads as nonsense. Nothing freed is the honest floor. </summary>
    public long ManagedFreed => Math.Max(0, ManagedBefore - ManagedAfter);
    public long WorkingSetFreed => Math.Max(0, WorkingSetBefore - WorkingSetAfter);
}

/// <summary>
/// The deepest managed collection the runtime offers, in one place so every caller gets the same
/// one. Used by the "collect garbage" buttons in the admin UI and by the maintenance actions that
/// exist to hand memory back.
/// <para>Three things make a collection deep, and all three are needed - dropping any one of them
/// leaves memory in the process:</para>
/// <list type="bullet">
/// <item><b>Aggressive, blocking, compacting, max generation.</b> A background or non-compacting
/// collection leaves the freed bytes as holes inside committed regions and returns nothing to the
/// operating system, so the process never shrinks. Aggressive is the only mode that decommits the
/// emptied regions instead of keeping them for later reuse.</item>
/// <item><b>Large object heap compaction.</b> Where the big arrays are - the caches, the index
/// mirrors, the vector arenas - and the one heap that is swept, not compacted, unless asked.</item>
/// <item><b>More than one pass, with the finalizers run in between.</b> A finalizable object
/// survives the collection that queues it and only dies in the next one, and the framework caches
/// that hook the gen2 collection (<c>ArrayPool&lt;T&gt;.Shared</c> among them) drop their buffers
/// during the collection, which turns them into garbage for the pass after it. One pass reclaims
/// neither.</item>
/// </list>
/// <para>Deliberately not done: trimming the working set (<c>SetProcessWorkingSetSize</c> /
/// <c>EmptyWorkingSet</c> on Windows). It pages the process out rather than freeing anything, so the
/// reported figure drops while the memory is still committed, and the pages fault straight back in
/// on the next request. A number that looks better than the truth is worse than no number.</para>
/// </summary>
public static class MemoryReclaim {
    /// <summary>
    /// Collects as deeply as the runtime allows and reports what came back. Blocking and not cheap:
    /// every thread is suspended for the whole of each pass, which on a large heap is measured in
    /// seconds, so this belongs behind an explicit action or a maintenance window - never on a
    /// request path that runs by itself.
    /// </summary>
    /// <param name="finalizerRounds">How many times to wait for the finalizers and collect again.
    /// One clears objects held only by their own finalizer; the default of two also clears whatever
    /// those finalizers in turn released. There is always one more collection than there are rounds,
    /// so the last round's finalizers are not left uncollected.</param>
    public static MemoryReclaimResult Collect(int finalizerRounds = 2) {
        if (finalizerRounds < 0) finalizerRounds = 0;
        var watch = Stopwatch.StartNew();
        var managedBefore = GC.GetTotalMemory(false);
        var workingSetBefore = workingSet();
        var collections = 0;
        for (var round = 0; round < finalizerRounds; round++) {
            collectOnce();
            collections++;
            // the finalizer thread runs on its own and would otherwise still be working long after
            // this returns, leaving the memory it releases uncollected until some later gen2
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
        // CompactOnce resets itself to Default after every blocking gen2 collection, so it has to be
        // set again for each pass. Current runtimes compact the large object heap for an induced
        // compacting collection anyway; asking explicitly costs nothing and does not rely on that.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Aggressive is only legal on the max generation and only with both blocking and compacting
        // true - the arguments are not free choices, they are what the mode requires.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
    /// <summary>What the runtime holds committed as of the collection just finished, heaps and their
    /// free space together: the figure that says whether the memory actually went back to the
    /// operating system rather than being kept as free space inside the process. Only meaningful
    /// straight after a collection - see <see cref="MemoryReclaimResult"/>. </summary>
    static long committed() {
        try {
            return GC.GetGCMemoryInfo().TotalCommittedBytes;
        } catch {
            return 0;
        }
    }
    /// <summary>What the operating system has resident, or 0 where it cannot be read. </summary>
    static long workingSet() {
        try {
            return Environment.WorkingSet;
        } catch {
            return 0;
        }
    }
}
