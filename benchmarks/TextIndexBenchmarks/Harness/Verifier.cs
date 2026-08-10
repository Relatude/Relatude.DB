using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes;

namespace TextIndexBenchmarks.Harness;

/// <summary>
/// Correctness check before any timing: the same documents, updates and deletes go into the
/// reference implementation (the in-memory trie) and into the candidate, and after every round the
/// two are asked the same searches. A benchmark of an index that answers the wrong documents is
/// worthless, so this runs first.
///
/// <para>Only the id sets of the unranked search are compared, over the query classes every
/// implementation claims to answer the same way (single term, AND, OR, prefix). Ranked results
/// deliberately are not: each engine has its own scoring, so the first page of 20 is allowed to
/// differ — but which documents match at all is not. Infix, fuzzy and spelling suggestions are
/// capability differences (see <see cref="Engines.Description"/>), not correctness ones, and are
/// covered by the feature flags instead.</para>
///
/// <para>The searches here lift the word-expansion cap the benchmark itself uses. A prefix over a
/// large vocabulary matches more words than the cap allows, and each implementation then evaluates
/// a different subset of them (Lucene and FTS5 do not cap at all) — a difference in where the cut
/// falls, not in which documents contain the words. Capping is part of what the timings compare;
/// it would only make this comparison meaningless.</para>
/// </summary>
public static class Verifier {
    const int documents = 3_000;
    const int chunk = 500;
    const int queriesPerRound = 60;
    const int uncappedWords = 1_000_000;

    public static string? Run(string engineName, Corpus corpus, BenchOptions options, string dir) {
        if (engineName == Engines.Reference) return null;
        var referenceDir = Path.Combine(dir, "_reference");
        using var reference = Engines.Create(Engines.Reference, referenceDir, options);
        using var candidate = Engines.Create(engineName, dir, options);
        var a = reference.Index;
        var b = candidate.Index;
        // a small run is verified on whatever it has; the point is agreement, not scale
        var docCount = Math.Min(documents, corpus.Documents.Length);
        long ts = 0;

        for (var i = 0; i < docCount;) {
            reference.Begin();
            candidate.Begin();
            var end = Math.Min(docCount, i + chunk);
            for (; i < end; i++) {
                a.Add(Corpus.NodeId(i), corpus.Documents[i]);
                b.Add(Corpus.NodeId(i), corpus.Documents[i]);
            }
            ts++;
            reference.Commit(ts);
            candidate.Commit(ts);
            var err = compare(a, b, corpus, engineName, $"after {end} documents");
            if (err != null) return err;
        }

        // updates: the old text out, a new one in — the same pair the store issues on a node update
        var updateCount = Math.Min(docCount / 4, corpus.UpdatedDocuments.Length);
        reference.Begin();
        candidate.Begin();
        for (var i = 0; i < updateCount; i++) {
            a.Remove(Corpus.NodeId(i), corpus.Documents[i]);
            a.Add(Corpus.NodeId(i), corpus.UpdatedDocuments[i]);
            b.Remove(Corpus.NodeId(i), corpus.Documents[i]);
            b.Add(Corpus.NodeId(i), corpus.UpdatedDocuments[i]);
        }
        ts++;
        reference.Commit(ts);
        candidate.Commit(ts);
        var updateErr = compare(a, b, corpus, engineName, "after updates");
        if (updateErr != null) return updateErr;

        // deletes, then the same battery again: a tombstone that fails to hide its document, or
        // hides one document too many, shows up here
        // deleted from the half that was not updated, so the removed text is the indexed one
        reference.Begin();
        candidate.Begin();
        for (var i = docCount / 2; i < docCount / 2 + docCount / 4; i++) {
            a.Remove(Corpus.NodeId(i), corpus.Documents[i]);
            b.Remove(Corpus.NodeId(i), corpus.Documents[i]);
        }
        ts++;
        reference.Commit(ts);
        candidate.Commit(ts);
        var removeErr = compare(a, b, corpus, engineName, "after deletes");
        if (removeErr != null) return removeErr;

        // and once more after a durability checkpoint and a full reopen, which is where a
        // persisted index can silently come back empty
        reference.Persist(++ts);
        candidate.Persist(ts);
        candidate.Dispose();
        using var reopened = Engines.Create(engineName, dir, options, reopen: true);
        return compare(a, reopened.Index, corpus, engineName, "after reopen");
    }

    static string? compare(IWordIndex a, IWordIndex b, Corpus corpus, string engineName, string where) {
        foreach (var (name, queries) in new (string, TermSet[])[] {
            ("term", corpus.TermQueries),
            ("and", corpus.AndQueries),
            ("or", corpus.OrQueries),
            ("prefix", corpus.PrefixQueries),
        }) {
            var orSearch = name != "and";
            for (var i = 0; i < Math.Min(queriesPerRound, queries.Length); i++) {
                var expected = ids(a, queries[i], orSearch);
                var actual = ids(b, queries[i], orSearch);
                if (expected.SequenceEqual(actual)) continue;
                var missing = expected.Except(actual).Take(5).ToArray();
                var extra = actual.Except(expected).Take(5).ToArray();
                return $"{where}: {name} search \"{queries[i]}\" returned {actual.Length} ids, "
                    + $"the reference {expected.Length}"
                    + (missing.Length > 0 ? $"; missing {string.Join(",", missing)}" : "")
                    + (extra.Length > 0 ? $"; unexpected {string.Join(",", extra)}" : "");
            }
        }
        return null;
    }

    static int[] ids(IWordIndex index, TermSet query, bool orSearch)
        => [.. index.SearchForIdSetUnranked(query, orSearch, uncappedWords).Enumerate().Order()];
}
