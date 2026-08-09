using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes;
using TextIndexBenchmarks.Engines;

namespace TextIndexBenchmarks.Harness;

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
    /// <summary>Single-shot measurements in milliseconds (checkpoint, reopen, first cold query).</summary>
    public Dictionary<string, double> Millis { get; set; } = [];
    /// <summary>Phases the engine does not implement; printed as "n/a" instead of a number.</summary>
    public List<string> Unsupported { get; set; } = [];
    public double ManagedMB { get; set; }
    public double WorkingSetMB { get; set; }
    public double DiskMB { get; set; }
    public string? Error { get; set; }

    public PhaseResult? Phase(string name) => Phases.FirstOrDefault(p => p.Name == name);
    public double? Rate(string name) => Phase(name)?.Rate;
    public double? Ms(string name) => Millis.TryGetValue(name, out var v) ? v : null;
    public bool IsUnsupported(string name) => Unsupported.Contains(name);
}

/// <summary>
/// Runs every phase against one implementation. All four see the same corpus, the same query
/// stream and the same transaction boundaries; the only thing that varies is the index behind
/// <see cref="IWordIndex"/>.
/// </summary>
public static class BenchRunner {
    /// <summary>Ranked searches ask for the first page of 20, evaluating at most this many hits per
    /// word — the cap the store passes down, and what stops a common term from scoring every
    /// document it appears in.</summary>
    public const int PageSize = 20;
    public const int MaxHitsEvaluated = 10_000;
    public const int MaxWordsEvaluated = 100;

    /// <summary>Documents the update and remove phases touch, and writes the mixed phase makes —
    /// fixed, not a share of the corpus, so a slow engine cannot decide the suite's runtime.</summary>
    public const int WritePhaseOps = 2_000;
    public const int MixedPhaseWrites = 1_000;

    /// <summary>Milliseconds of untimed work a read phase does before it is measured, so the
    /// numbers are steady state rather than the JIT warming up.</summary>
    const int warmupMs = 300;

    public static BenchResult Run(string engineName, Corpus corpus, BenchOptions options, string dir) {
        var result = new BenchResult { Engine = engineName, N = corpus.DocumentCount };
        var rnd = new Random(4711);
        var n = corpus.DocumentCount;

        // Node id ranges, kept disjoint so no phase ever adds an id another phase still holds
        // (the trie's document table rejects a duplicate id outright).
        var deltaBase = n;                                   // ids n+1 .. n+delta
        var mixedBase = n + corpus.PersistDeltaDocuments.Length;
        var current = (string[])corpus.Documents.Clone();    // the text each document currently has

        // The write phases run a fixed number of operations rather than a share of the corpus, so
        // they measure cost per operation and an engine whose per-operation cost grows with the
        // corpus cannot decide how long the suite takes (nor have its own numbers depend on --n).
        var updateCount = Math.Min(WritePhaseOps, corpus.UpdatedDocuments.Length);
        var removeCount = Math.Min(WritePhaseOps, n);
        var removeOrder = Enumerable.Range(0, n).ToArray();
        rnd.Shuffle(removeOrder);
        var mixedWrites = Math.Min(MixedPhaseWrites, corpus.MixedDocuments.Length);

        forceGc();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var wsBefore = Environment.WorkingSet;

        using var bench = Engines.Create(engineName, dir, options);
        var idx = bench.Index;
        long ts = 0;
        var sw = new Stopwatch();

        // ---- Index ------------------------------------------------------------------------------
        progress("index");
        sw.Restart();
        for (var i = 0; i < n;) {
            bench.Begin();
            var end = Math.Min(n, i + options.BatchSize);
            for (; i < end; i++) idx.Add(Corpus.NodeId(i), corpus.Documents[i]);
            bench.Commit(++ts);
            if (options.PersistEveryBatch) bench.Persist(ts);
        }
        if (!options.PersistEveryBatch) bench.Persist(ts); // the load must be durable before it is measured
        sw.Stop();
        result.Phases.Add(new() { Name = "Index", Ops = n, Seconds = sw.Elapsed.TotalSeconds });

        // ---- Footprint of the loaded, durably persisted index -------------------------------------
        forceGc();
        result.ManagedMB = Math.Max(0, (GC.GetTotalMemory(forceFullCollection: true) - managedBefore) / (1024.0 * 1024.0));
        result.WorkingSetMB = Math.Max(0, (Environment.WorkingSet - wsBefore) / (1024.0 * 1024.0));
        result.DiskMB = bench.DiskBytes / (1024.0 * 1024.0);

        // ---- One durability checkpoint after a small delta ----------------------------------------
        // Separated from the load because this is where the designs differ most: an LSM flush and a
        // Lucene commit write the delta, a full state file writes the whole index every time.
        // Commit and checkpoint are timed together, because the implementations draw the line
        // between them differently — SQLite makes the data durable in the commit itself and has
        // nothing left to do at the checkpoint, the other three defer everything to it.
        progress("checkpoint");
        bench.Begin();
        for (var i = 0; i < corpus.PersistDeltaDocuments.Length; i++)
            idx.Add(deltaBase + i + 1, corpus.PersistDeltaDocuments[i]);
        sw.Restart();
        bench.Commit(++ts);
        bench.Persist(ts);
        sw.Stop();
        result.Millis["Persist"] = sw.Elapsed.TotalMilliseconds;

        // ---- Searches -----------------------------------------------------------------------------
        searchPhase(result, "Term", corpus.TermQueries, q => ranked(idx, q, orSearch: true));
        searchPhase(result, "And", corpus.AndQueries, q => ranked(idx, q, orSearch: false));
        searchPhase(result, "Or", corpus.OrQueries, q => ranked(idx, q, orSearch: true));
        searchPhase(result, "Prefix", corpus.PrefixQueries, q => ranked(idx, q, orSearch: true));
        if (bench.Supported.HasFlag(Features.Infix))
            searchPhase(result, "Infix", corpus.InfixQueries, q => ranked(idx, q, orSearch: true));
        else result.Unsupported.Add("Infix");
        if (bench.Supported.HasFlag(Features.Fuzzy))
            searchPhase(result, "Fuzzy", corpus.FuzzyQueries, q => ranked(idx, q, orSearch: true));
        else result.Unsupported.Add("Fuzzy");
        // the unranked path: the id set a WhereSearch filter combines with the rest of a query
        searchPhase(result, "Filter", corpus.TermQueries, q => idx.SearchForIdSetUnranked(q, true, MaxWordsEvaluated).Count);
        if (bench.Supported.HasFlag(Features.Suggest)) {
            progress("suggest");
            warm(() => { foreach (var w in corpus.SuggestWords) idx.SuggestSpelling(w, true).Count(); });
            long suggestions = 0;
            sw.Restart();
            foreach (var w in corpus.SuggestWords) suggestions += idx.SuggestSpelling(w, true).Count();
            sw.Stop();
            result.Phases.Add(new() { Name = "Suggest", Ops = corpus.SuggestWords.Length, Seconds = sw.Elapsed.TotalSeconds });
            if (suggestions == 0) result.Error ??= "sanity: no spelling suggestion was returned";
        } else {
            result.Unsupported.Add("Suggest");
        }

        // ---- Update (remove the old text, add the new one, as the store does) ----------------------
        progress("update");
        sw.Restart();
        for (var i = 0; i < updateCount;) {
            bench.Begin();
            var end = Math.Min(updateCount, i + options.BatchSize);
            for (; i < end; i++) {
                var id = Corpus.NodeId(i);
                idx.Remove(id, current[i]);
                idx.Add(id, corpus.UpdatedDocuments[i]);
            }
            bench.Commit(++ts);
        }
        sw.Stop();
        for (var i = 0; i < updateCount; i++) current[i] = corpus.UpdatedDocuments[i];
        result.Phases.Add(new() { Name = "Update", Ops = updateCount, Seconds = sw.Elapsed.TotalSeconds });

        // ---- Mixed: writes, searches and deletes interleaved ---------------------------------------
        // Every other phase measures an index that holds still. Here searches run against one that
        // is churning: new documents arriving, older ones being deleted, in short transactions.
        progress("mixed");
        const int mixedBatch = 500, readsPerWrite = 2;
        var mixedRemoveLag = Math.Max(2, (mixedWrites / 4) & ~1); // even, so deletes stay on even indexes
        long mixedHits = 0, mixedRemoves = 0;
        sw.Restart();
        for (var i = 0; i < mixedWrites;) {
            bench.Begin();
            var end = Math.Min(mixedWrites, i + mixedBatch);
            for (; i < end; i++) {
                idx.Add(mixedBase + i + 1, corpus.MixedDocuments[i]);
                for (var r = 0; r < readsPerWrite; r++)
                    mixedHits += ranked(idx, corpus.TermQueries[(i * readsPerWrite + r) % corpus.TermQueries.Length], true);
                if (i >= mixedRemoveLag && (i & 1) == 0) {
                    var older = i - mixedRemoveLag;
                    idx.Remove(mixedBase + older + 1, corpus.MixedDocuments[older]);
                    mixedRemoves++;
                }
            }
            bench.Commit(++ts);
        }
        sw.Stop();
        var mixedOps = mixedWrites + (long)mixedWrites * readsPerWrite + mixedRemoves;
        result.Phases.Add(new() { Name = "Mixed", Ops = mixedOps, Seconds = sw.Elapsed.TotalSeconds });
        if (mixedHits == 0) result.Error ??= "sanity: no search in the mixed phase found anything";

        // ---- Remove ---------------------------------------------------------------------------------
        progress("remove");
        sw.Restart();
        for (var i = 0; i < removeCount;) {
            bench.Begin();
            var end = Math.Min(removeCount, i + options.BatchSize);
            for (; i < end; i++) {
                var doc = removeOrder[i];
                idx.Remove(Corpus.NodeId(doc), current[doc]);
            }
            bench.Commit(++ts);
        }
        sw.Stop();
        result.Phases.Add(new() { Name = "Remove", Ops = removeCount, Seconds = sw.Elapsed.TotalSeconds });

        // ---- Restart: reopen the persisted index and search it cold ----------------------------------
        progress("reopen");
        bench.Persist(++ts);
        bench.Dispose();
        forceGc();
        sw.Restart();
        using var reopened = Engines.Create(engineName, dir, options, reopen: true);
        sw.Stop();
        result.Millis["Open"] = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var coldHits = ranked(reopened.Index, corpus.TermQueries[0], true);
        sw.Stop();
        result.Millis["Cold"] = sw.Elapsed.TotalMilliseconds;
        // A reopened index that answers nothing is the failure this whole exercise is about, so it
        // is a hard error rather than a fast number: 25% of the corpus was deleted, not all of it.
        if (coldHits == 0 && ranked(reopened.Index, corpus.TermQueries[1], true) == 0)
            result.Error ??= "sanity: the reopened index found nothing (data lost on restart?)";
        return result;

        void searchPhase(BenchResult r, string name, TermSet[] queries, Func<TermSet, int> search) {
            progress(name.ToLowerInvariant());
            warm(() => { foreach (var q in queries) search(q); });
            long hits = 0;
            sw.Restart();
            foreach (var q in queries) hits += search(q);
            sw.Stop();
            r.Phases.Add(new() { Name = name, Ops = queries.Length, Seconds = sw.Elapsed.TotalSeconds });
            if (hits == 0) r.Error ??= $"sanity: the {name} phase found no hits at all";
        }
    }

    static int ranked(IWordIndex idx, TermSet query, bool orSearch)
        => idx.SearchForRankedHitData(query, 0, PageSize, MaxHitsEvaluated, MaxWordsEvaluated, orSearch, out _).Count;

    static void warm(Action body) {
        var clock = Stopwatch.StartNew();
        do { body(); } while (clock.ElapsedMilliseconds < warmupMs);
    }
    static void progress(string phase) => Console.Error.Write($" {phase}…");
    static void forceGc() {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
