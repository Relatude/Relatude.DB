using Relatude.DB.DataStores.Indexes;

namespace TextIndexBenchmarks.Engines;

/// <summary>Capabilities that are not part of every implementation. A phase measuring one of these
/// is skipped (and printed as "-") for engines that do not have it, rather than timing a call that
/// throws or silently answers a different question.</summary>
[Flags]
public enum Features {
    None = 0,
    /// <summary>Fuzzy terms (<c>word~</c>) expand to similar words in the dictionary.</summary>
    Fuzzy = 1,
    /// <summary>Infix terms (<c>*word</c>) match anywhere inside a word.</summary>
    Infix = 2,
    /// <summary><see cref="IWordIndex.SuggestSpelling"/> is implemented.</summary>
    Suggest = 4,
}

/// <summary>
/// One benchmarked word index, driven the way the data store drives it: writes inside a
/// begin/commit pair, and a durability checkpoint separate from the commit. The two are separate
/// because the implementations differ exactly there — the persisted engines checkpoint after every
/// WAL flush, while the memory trie is written out as a whole state file at intervals.
/// </summary>
public interface IBenchWordIndex : IDisposable {
    /// <summary>The index under test, wrapped in <see cref="OptimizedWordIndex"/> exactly as the
    /// data store hands it out, so the queued-remove behavior is part of what is measured.</summary>
    IWordIndex Index { get; }
    Features Supported { get; }
    void Begin();
    void Commit(long timestamp);
    /// <summary>Durably persist everything committed so far (engine flush, or full state file).</summary>
    void Persist(long timestamp);
    long DiskBytes { get; }
}
