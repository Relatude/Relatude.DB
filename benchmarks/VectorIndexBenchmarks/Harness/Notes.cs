namespace VectorIndexBenchmarks.Harness;

/// <summary>
/// What the tables mean, printed under them: what each engine is, what every column measures, and
/// which comparisons the numbers support — so a table read out of context cannot be read wrongly.
/// </summary>
public static class Notes {
    public static void Print(BenchOptions options, Corpus corpus) {
        foreach (var e in options.EngineNames)
            Console.WriteLine($"  {Engines.DisplayName(e),-28} {Engines.Description(e)}");
        Console.WriteLine($$"""

            Notes
              - In every column the best value is green and the runner-up cyan, the rest dimmed; the
                millisecond and footprint columns rank low-to-high, the rest high-to-low. "n/a" is a
                capability the implementation does not have — not a slow result. Redirected output
                (and NO_COLOR) stays plain text.
              - Speed without accuracy means nothing here. MemorySemanticIndex and sqlite-vec are exact by
                construction — they look at every vector. IVSVectorIndex probes the {{options.Accuracy:P0}} of clusters
                nearest the query, while HnswVectorIndex and USearch walk an HNSW graph with an expansion
                of {{options.HnswExpansionSearch}}; all three may miss a neighbour they never visited. Read the search rates
                and the recall columns together. --hnsw-ef means the same thing to both graph indexes, so
                those two are comparable as they stand; --accuracy is a fraction of the clusters in the
                index and not the same kind of dial at all, so compare the IVF index against the graphs at
                matched recall rather than at matched settings.
              - Recall% is the share of the exact first page of {{Corpus.RecallTopK}} a ranked search returns; Filter%
                the share of the exact above-threshold set an unranked filter search returns. Both are
                measured against a brute-force scan of the freshly loaded vectors, over {{Corpus.RecallQueryCount}} queries, and
                both are 100% for an implementation that searches exactly (anything less is reported as
                an error rather than a trade-off).
              - The Relatude implementations are driven only through ISemanticIndex, the interface the data
                store uses, so what is measured for them is the production path. A semantic search arrives
                there as text the index embeds itself; the benchmark supplies an AI engine that maps each
                query text back to its generated vector and caches it, so what is timed is the index and
                not an embedding call. The third-party libraries take the same vector directly.
              - Not every implementation has every operation, and "n/a" means absent rather than slow.
                USearch answers "the best k" and has no unranked threshold query, so its Filter columns
                are blank — emulating one with a k of the whole index would measure a query nobody would
                write. HnswVectorIndex does answer one, by flooding outward from the query through
                everything above the threshold, which is an approximation of a different shape than its
                top-k walk — read its Filter% next to its Recall%. Only the two disk indexes have a
                post-WAL-flush durability hook.
              - The third-party libraries keep their data in native memory (USearch's graph) or in a page
                cache (sqlite-vec), so almost nothing of theirs appears in the managed Mem/Warm columns —
                for them, only WSet MB and Disk MB carry information. That asymmetry is a property of the
                measurement, not a result: do not read their low Mem MB as frugality.
              - sqlite-vec has no approximate index. Every ranked query scans and ranks every stored
                vector, and a similarity floor buys it nothing (a vec0 KNN query takes a k and nothing
                else, so the floor is applied to the rows that come back). It is the exact-answer
                reference next to ivs-exact, and it is why a full run takes as long as it does.
              - The vectors are synthetic: unit vectors drawn around {{(options.Clusters > 0 ? options.Clusters + " random cluster centers" : "no centers at all")}}, and the
                queries are drawn from the same distribution, so a query has real near neighbours the way
                a real one does. Clustered data is what embeddings look like and what an IVF index is
                built for; --clusters=0 removes the structure entirely, which is its worst case.
              - Top10 is a first page of {{BenchRunner.PageSize}} out of {{BenchRunner.MaxHitsEvaluated}} evaluated hits and Top100 a page of {{BenchRunner.DeepPageSize}}
                out of {{BenchRunner.DeepMaxHitsEvaluated:N0}}, both at a minimum similarity of {{corpus.MinSimilarity:0.000}} — the {{Corpus.FilterRank}}th exact
                neighbour, so about that many vectors clear the floor, which is what a semantic query
                with a minimum-similarity setting looks like. NoFloor is the same page of {{BenchRunner.PageSize}} with no
                floor at all: every vector in the index is then a candidate and none can be discarded
                before it reaches the implementation's top-k structure, which is a different bottleneck
                and worth reading next to Top10. Filter is the unranked id-set path a semantic
                WhereSearch filter uses, at the same floor, measured with the store's set cache disabled
                so the index answers every call.
              - Index/s is vectors per second including the state save at the end of the
                load{{(options.PersistEveryBatch ? " (--persist=batch: after every batch instead)" : "")}}. Building a graph is the slow part of HNSW and shows up here: linking a
                node runs a search and then re-selects the neighbour lists of everything it attached to,
                which is a few tens of thousands of distance computations per vector against the IVF
                index's one cluster assignment. Save ms is one state save after a small delta: the
                in-memory index writes a file containing every vector it holds, the IVF index writes the
                delta and swaps a manifest, and the graph index writes the edges its inserts changed into
                place — the read-modify-write of a few tens of thousands of scattered records, which is
                why its state save is the dearest here. Flush ms is the post-WAL-flush hook the two disk
                indexes have and the in-memory one does not, and it is where that trade pays off: the
                graph index appends the same edges to a log in one sequential write instead, so the path
                the store takes after every WAL flush is its cheapest column and its periodic state save
                is its most expensive one.
              - Update replaces the vector of an existing id; Remove drops ids at random. Mixed
                interleaves inserts, searches and deletes, so searches run against an index that is
                churning rather than holding still. All three run a fixed {{BenchRunner.WritePhaseOps:N0}} / {{BenchRunner.WritePhaseOps:N0}} / {{BenchRunner.MixedPhaseWrites:N0}} operations
                rather than a share of the corpus, so they measure cost per operation and their numbers
                do not depend on --n.
              - Open ms reopens the persisted index; Cold ms is the first search after that, un-warmed.
                The in-memory index reads every vector back into the heap to open; the IVF index reads its
                manifest, centroids and one block directory per segment, and the graph index its manifest,
                node table and upper-layer edges — then both pay for the vectors they touch on the first
                searches.
              - Mem MB is managed heap growth right after the load (full GC), before the index has served
                anything: for the in-memory index that is the vector data itself, for the IVF index the
                ids, offsets and centroids, and for the graph index its node table and upper layers plus
                whatever of the load is still in its record cache. Warm MB is the same measurement after the search phases, which
                is the only place a read-through cache appears — the disk index has by then pulled the
                blocks its searches touched into its {{options.CacheMB}} MB budget, and that memory is what its search
                rates were bought with. The two HnswVectorIndex rows are that trade priced outright: one
                implementation, one set of files, run once with room to cache the graph and once pinned
                to the low-memory budget threshold, at which the index keeps the graph and its upper
                layers on disk behind small caches. Read their Mem/Warm/WSet columns against their
                search and Index rates — that ratio is the whole decision the budget exists for. The same
                comparison for the IVF index is --engines=ivs,ivs-lowmem, which is a cache budget
                rather than a mode.
                WSet MB is working-set growth at the same point, which also covers native memory and file
                reads. Disk MB is the index on disk after the load was made durable. The generated vectors
                are {{options.N * (long)options.Dimensions * 4 / (1024.0 * 1024.0):N0}} MB of raw float32, held by the harness and outside all of these numbers:
                every vector is handed to an index as a fresh copy, so what an index keeps is its own.
              - Each engine runs in its own child process, so its memory numbers are not polluted by the
                other's. --in-process runs them together, which is faster and noisier.
            """);
    }
}
