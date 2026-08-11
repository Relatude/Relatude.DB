using System.Diagnostics;
using VectorIndexBenchmarks.Engines;

namespace VectorIndexBenchmarks.Harness;

public sealed class PhaseResult {
    public string Name { get; set; } = "";
    public long Ops { get; set; }
    public double Seconds { get; set; }
    public double Rate => Seconds > 0 ? Ops / Seconds : 0;
}

public sealed class BenchResult {
    public string Engine { get; set; } = "";
    public int N { get; set; }
    public List<PhaseResult> Phases { get; set; } = [];
    /// <summary>Single-shot measurements in milliseconds (state save, WAL flush, reopen, first cold search).</summary>
    public Dictionary<string, double> Millis { get; set; } = [];
    /// <summary>Quality measurements in 0..1 (recall of the ranked page, of the filter set).</summary>
    public Dictionary<string, double> Quality { get; set; } = [];
    /// <summary>Phases the implementation does not have; printed as "n/a" instead of a number.</summary>
    public List<string> Unsupported { get; set; } = [];
    /// <summary>Managed heap growth right after the load, before the index has served anything.</summary>
    public double ManagedMB { get; set; }
    /// <summary>Managed heap growth after the search phases, so a read-through cache is counted.</summary>
    public double WarmManagedMB { get; set; }
    /// <summary>Working-set growth after the search phases, which is where native memory shows up.</summary>
    public double WorkingSetMB { get; set; }
    public double DiskMB { get; set; }
    public string? Error { get; set; }

    public PhaseResult? Phase(string name) => Phases.FirstOrDefault(p => p.Name == name);
    public double? Rate(string name) => Phase(name)?.Rate;
    public double? Ms(string name) => Millis.TryGetValue(name, out var v) ? v : null;
    public double? Quality0To1(string name) => Quality.TryGetValue(name, out var v) ? v : null;
    public bool IsUnsupported(string name) => Unsupported.Contains(name);
}

/// <summary>
/// Runs every phase against one implementation. All of them see the same vectors, the same query
/// stream and the same state-save boundaries; the only thing that varies is the index behind
/// <see cref="IBenchVectorIndex"/>.
/// </summary>
public static class BenchRunner {
    /// <summary>The ranked searches ask for a first page of 10 out of this many evaluated hits —
    /// what a semantic query with a page size of 10 passes down.</summary>
    public const int PageSize = 10;
    public const int MaxHitsEvaluated = 100;
    /// <summary>The deep-page search: a page of 100 out of 1000 evaluated, which makes an index
    /// that keeps a small top-k heap work considerably harder.</summary>
    public const int DeepPageSize = 100;
    public const int DeepMaxHitsEvaluated = 1_000;

    /// <summary>Vectors the update and remove phases touch, and writes the mixed phase makes.</summary>
    public const int WritePhaseOps = 2_000;
    public const int MixedPhaseWrites = 1_000;

    /// <summary>Milliseconds of untimed work a read phase does before it is measured, so the
    /// numbers are steady state rather than the JIT warming up and the cache filling.</summary>
    const int warmupMs = 500;

    public static BenchResult Run(string engineName, Corpus corpus, BenchOptions options, string dir) {
        var result = new BenchResult { Engine = engineName, N = corpus.Count };
        var rnd = new Random(4711);
        var n = corpus.Count;

        // Node id ranges, kept disjoint so no phase ever adds an id another phase still holds.
        var deltaBase = n;                          // ids n+1 .. n+delta
        var mixedBase = n + corpus.DeltaCount;

        var removeOrder = Enumerable.Range(0, n).ToArray();
        rnd.Shuffle(removeOrder);

        forceGc();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var wsBefore = Environment.WorkingSet;

        using var bench = Engines.Create(engineName, dir, options, corpus);
        long ts = 0;
        var sw = new Stopwatch();

        // ---- Index ------------------------------------------------------------------------------
        Progress.Phase("indexing");
        sw.Restart();
        for (var i = 0; i < n;) {
            var end = Math.Min(n, i + options.BatchSize);
            for (; i < end; i++) {
                bench.Add(Corpus.NodeId(i), corpus.Vector(i));
                Progress.Item(i + 1, n);
            }
            if (options.PersistEveryBatch) bench.SaveState(++ts);
        }
        if (!options.PersistEveryBatch) bench.SaveState(++ts); // the load must be durable before it is measured
        sw.Stop();
        result.Phases.Add(new() { Name = "Index", Ops = n, Seconds = sw.Elapsed.TotalSeconds });

        // ---- Footprint of the loaded, durably persisted index --------------------------------------
        // What the index holds before it has served anything: for the in-memory implementation that
        // is the vector data itself, for the disk one the ids, offsets and centroids. Its caches are
        // still empty here — they are measured again after the searches.
        forceGc();
        result.ManagedMB = Math.Max(0, (GC.GetTotalMemory(forceFullCollection: true) - managedBefore) / (1024.0 * 1024.0));
        result.DiskMB = bench.DiskBytes / (1024.0 * 1024.0);

        // ---- Accuracy against the exact answers ----------------------------------------------------
        // Measured here, on the freshly loaded corpus the exact neighbours were computed from, and
        // before any phase changes it. An approximate index trades recall for speed, so the search
        // rates below only mean something next to this.
        Progress.Phase("measuring recall");
        measureAccuracy(result, bench, corpus);

        // ---- One durability checkpoint after a small delta -----------------------------------------
        // Where the two designs differ most: the in-memory index writes a state file containing
        // every vector it holds, the disk index writes the delta and swaps a manifest.
        Progress.Phase("saving state");
        for (var i = 0; i < corpus.DeltaCount; i++) bench.Add(deltaBase + i + 1, corpus.DeltaVector(i));
        sw.Restart();
        bench.SaveState(++ts);
        sw.Stop();
        result.Millis["Save"] = sw.Elapsed.TotalMilliseconds;

        // The post-WAL-flush hook, for the implementation that has one: the same delta again, made
        // durable without the state save's maintenance. The in-memory index has no equivalent —
        // the store replays the WAL for anything newer than its last state file.
        if (bench.Supported.HasFlag(Features.IncrementalDurability)) {
            for (var i = 0; i < corpus.DeltaCount; i++) bench.Add(mixedBase + corpus.MixedCount + i + 1, corpus.DeltaVector(i));
            sw.Restart();
            bench.MakeDurable(++ts);
            sw.Stop();
            result.Millis["Flush"] = sw.Elapsed.TotalMilliseconds;
        } else {
            result.Unsupported.Add("Flush");
        }

        // ---- Searches -------------------------------------------------------------------------------
        // The first two carry a minimum similarity, which is how a semantic query is normally asked
        // and lets both implementations discard a vector before it reaches their top-k structure.
        searchPhase(result, "Top10", q => bench.SearchRanked(q, PageSize, MaxHitsEvaluated, corpus.MinSimilarity).Count);
        searchPhase(result, "Top100", q => bench.SearchRanked(q, DeepPageSize, DeepMaxHitsEvaluated, corpus.MinSimilarity).Count);
        // No floor: every vector in the index is a candidate for the page, so nothing can be
        // discarded early and the whole corpus passes through the ranking.
        searchPhase(result, "NoFloor", q => bench.SearchRanked(q, PageSize, MaxHitsEvaluated, -1f).Count);
        // The unranked path a semantic WhereSearch filter uses. A pure top-k library has no such
        // query, so it is skipped rather than emulated with a k of the whole index, which would
        // measure a query nobody would write.
        if (bench.Supported.HasFlag(Features.UnrankedFilter))
            searchPhase(result, "Filter", q => bench.SearchIds(q, corpus.MinSimilarity).Count);
        else result.Unsupported.Add("Filter");

        // ---- Footprint of an index that has been serving -------------------------------------------
        // The steady state of a running store, and the only place a read-through cache shows up: the
        // disk index has now pulled every block its searches touched into its budget, so this is the
        // memory its search rates above were actually bought with.
        forceGc();
        result.WarmManagedMB = Math.Max(0, (GC.GetTotalMemory(forceFullCollection: true) - managedBefore) / (1024.0 * 1024.0));
        result.WorkingSetMB = Math.Max(0, (Environment.WorkingSet - wsBefore) / (1024.0 * 1024.0));

        // ---- Update (the store's node update: the vector for an id replaced by a new one) ------------
        Progress.Phase("updating");
        var updateCount = Math.Min(options.UpdateCount, corpus.UpdateCount);
        sw.Restart();
        for (var i = 0; i < updateCount; i++) bench.Update(Corpus.NodeId(i), corpus.UpdateVector(i));
        sw.Stop();
        result.Phases.Add(new() { Name = "Update", Ops = updateCount, Seconds = sw.Elapsed.TotalSeconds });

        // ---- Mixed: writes, searches and deletes interleaved ------------------------------------------
        // Every other phase measures an index that holds still. Here searches run against one that
        // is churning: new vectors arriving, older ones being deleted.
        Progress.Phase("mixed load");
        const int readsPerWrite = 2;
        var mixedWrites = Math.Min(MixedPhaseWrites, corpus.MixedCount);
        var mixedRemoveLag = Math.Max(2, (mixedWrites / 4) & ~1); // even, so deletes stay on even indexes
        long mixedHits = 0, mixedRemoves = 0;
        sw.Restart();
        for (var i = 0; i < mixedWrites; i++) {
            bench.Add(mixedBase + i + 1, corpus.MixedVector(i));
            for (var r = 0; r < readsPerWrite; r++)
                mixedHits += bench.SearchRanked(corpus.Queries[(i * readsPerWrite + r) % Corpus.QueryCount], PageSize, MaxHitsEvaluated, corpus.MinSimilarity).Count;
            if (i >= mixedRemoveLag && (i & 1) == 0) {
                bench.Remove(mixedBase + (i - mixedRemoveLag) + 1);
                mixedRemoves++;
            }
            Progress.Item(i + 1, mixedWrites);
        }
        sw.Stop();
        var mixedOps = mixedWrites + (long)mixedWrites * readsPerWrite + mixedRemoves;
        result.Phases.Add(new() { Name = "Mixed", Ops = mixedOps, Seconds = sw.Elapsed.TotalSeconds });
        if (mixedHits == 0) result.Error ??= "sanity: no search in the mixed phase found anything";

        // ---- Remove ------------------------------------------------------------------------------------
        Progress.Phase("removing");
        var removeCount = Math.Min(options.RemoveCount, n);
        sw.Restart();
        for (var i = 0; i < removeCount; i++) bench.Remove(Corpus.NodeId(removeOrder[i]));
        sw.Stop();
        result.Phases.Add(new() { Name = "Remove", Ops = removeCount, Seconds = sw.Elapsed.TotalSeconds });

        // ---- Restart: reopen the persisted index and search it cold --------------------------------------
        Progress.Phase("reopening");
        bench.SaveState(++ts);
        bench.Dispose();
        forceGc();
        sw.Restart();
        using var reopened = Engines.Create(engineName, dir, options, corpus);
        sw.Stop();
        result.Millis["Open"] = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var coldHits = reopened.SearchRanked(corpus.Queries[0], PageSize, MaxHitsEvaluated, corpus.MinSimilarity).Count;
        sw.Stop();
        result.Millis["Cold"] = sw.Elapsed.TotalMilliseconds;
        // A reopened index that answers nothing is the failure this whole exercise is about, so it
        // is a hard error rather than a fast number: a fraction of the corpus was deleted, not all of it.
        if (coldHits == 0 && reopened.SearchRanked(corpus.Queries[1], PageSize, MaxHitsEvaluated, corpus.MinSimilarity).Count == 0)
            result.Error ??= "sanity: the reopened index found nothing (data lost on restart?)";
        return result;

        void searchPhase(BenchResult r, string name, Func<BenchQuery, int> search) {
            Progress.Phase($"warming up {name.ToLowerInvariant()} searches");
            warm(() => { foreach (var q in corpus.Queries) search(q); });
            Progress.Phase($"{name.ToLowerInvariant()} searches");
            long hits = 0;
            sw.Restart();
            for (var i = 0; i < corpus.Queries.Length; i++) {
                hits += search(corpus.Queries[i]);
                Progress.Item(i + 1, corpus.Queries.Length);
            }
            sw.Stop();
            r.Phases.Add(new() { Name = name, Ops = corpus.Queries.Length, Seconds = sw.Elapsed.TotalSeconds });
            if (hits == 0) r.Error ??= $"sanity: the {name} phase found no hits at all";
        }
    }

    /// <summary>
    /// Recall of both search paths against the brute-force answers: the share of the exact first
    /// page a ranked search returns, and the share of the exact above-threshold set an unranked
    /// filter search returns. The in-memory index is exact by construction, so anything below 100%
    /// there is a bug, not a trade-off — which is what makes it a usable reference.
    /// </summary>
    static void measureAccuracy(BenchResult result, IBenchVectorIndex bench, Corpus corpus) {
        var hasFilter = bench.Supported.HasFlag(Features.UnrankedFilter);
        double rankedRecall = 0, filterRecall = 0;
        int rankedQueries = 0, filterQueries = 0;
        for (var q = 0; q < corpus.ExactNeighbours.Length; q++) {
            Progress.Item(q + 1, corpus.ExactNeighbours.Length);
            var exact = corpus.ExactNeighbours[q];
            var sims = corpus.ExactSimilarities[q];
            var exactPage = exact.Take(Corpus.RecallTopK).ToHashSet();
            if (exactPage.Count > 0) {
                var hits = bench.SearchRanked(corpus.Queries[q], Corpus.RecallTopK, MaxHitsEvaluated, -1f);
                rankedRecall += hits.Count(exactPage.Contains) / (double)exactPage.Count;
                rankedQueries++;
            }
            if (!hasFilter) continue;
            // The exact answer to this filter search: the neighbours at or above the threshold. When
            // the threshold lies below this query's own cutoff there are matches beyond the ones
            // computed, so an index may legitimately return more than these — never fewer.
            var expected = exact.Where((_, i) => sims[i] >= corpus.MinSimilarity).ToArray();
            if (expected.Length == 0) continue;
            var found = bench.SearchIds(corpus.Queries[q], corpus.MinSimilarity);
            filterRecall += expected.Count(found.Has) / (double)expected.Length;
            filterQueries++;
        }
        if (rankedQueries > 0) result.Quality["Recall"] = rankedRecall / rankedQueries;
        if (filterQueries > 0) result.Quality["FilterRecall"] = filterRecall / filterQueries;
        else result.Unsupported.Add("FilterRecall");
    }

    static void warm(Action body) {
        var clock = Stopwatch.StartNew();
        do { body(); } while (clock.ElapsedMilliseconds < warmupMs);
    }
    static void forceGc() {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
