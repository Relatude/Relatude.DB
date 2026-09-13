using Relatude.DB.DataStores.Sets;
using Relatude.DB.Query.Data;
namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// The optional half of <see cref="IWordIndex"/>: reading the index the other way round, from the
/// terms towards the nodes, to say which words a set of nodes holds. A search asks for one word and
/// gets the nodes; this asks for the nodes and gets every word, which means walking the whole term
/// dictionary and is why not every engine offers it. The memory index and the native text index do;
/// Lucene and SQLite are deliberately left out, and a caller finding an index that does not
/// implement this - or implements it and answers false - has no second way to the same answer.
///
/// Every word index is wrapped in <see cref="OptimizedWordIndex"/>, which implements this whatever
/// it wraps, so the support question is always <see cref="CanCountWords"/> and never a type test.
/// </summary>
public interface IWordCountIndex {
    /// <summary>Whether this index can count words at all. False on a wrapper around an engine that
    /// cannot, which is the only reason a type test is not enough.</summary>
    bool CanCountWords { get; }
    /// <summary>
    /// The commonest words held by the given nodes. Cost is the size of the index, not the size of
    /// the subset - every term is visited and its postings tested for membership - so a caller with
    /// a large index should set <see cref="WordCountOptions.MaxPostingsEvaluated"/>.
    /// </summary>
    WordCountSet CountWords(IdSet subset, WordCountOptions options);
    /// <summary>
    /// About how long <see cref="CountWords"/> would take on this index right now, with these
    /// options, whatever the subset - the cost is the index and not the set. For a caller deciding
    /// whether to run a count without being asked to; see <see cref="WordCountCostModel"/> for how
    /// the engines arrive at it and why it leans towards answering slow.
    /// </summary>
    TimeSpan EstimateCountWordsDuration(WordCountOptions options);
}

/// <summary>
/// What a word count is expected to cost, for an engine to answer
/// <see cref="IWordCountIndex.EstimateCountWordsDuration"/> with. The work of a count is one
/// membership test per posting, so the cost is postings times a rate. The postings a walk visits
/// are bounded above by the number of words the documents hold in all - each word of a document is
/// at most one posting - which both engines keep for BM25 anyway, and below by the caller's budget.
///
/// The rate starts at a deliberately slow default: the question is asked to decide whether to run a
/// count unasked, so the safe mistake is to call a fast index slow, never the other way round. Once
/// the index has actually run a count over enough postings for the timing to mean something, its
/// own measured rate takes over, so a slow disk or a fast machine both end up described as what
/// they are.
/// </summary>
public sealed class WordCountCostModel {
    // About twice what a warm walk measured (23 ns per posting, memory trie, 1.5M postings, 2026
    // laptop): a large index has worse cache locality than a small one, and a busy server less
    // cache per core than a laptop.
    public const double DefaultNsPerPosting = 50;
    /// <summary>A count over fewer postings than this does not calibrate the rate: its timing is
    /// dominated by fixed costs and would set the rate to noise.</summary>
    public const long CalibrationMinPostings = 1_000_000;
    double _nsPerPosting = DefaultNsPerPosting; // a benign race: written under the read lock, any value read is a real one
    /// <summary>The rate the estimate is made at, in nanoseconds per posting: the default until a big enough count has run.</summary>
    public double NsPerPosting => _nsPerPosting;

    /// <param name="totalWords">The words the index's documents hold in all - the most postings a walk can visit.</param>
    public TimeSpan Estimate(long totalWords, WordCountOptions options) {
        var postings = Math.Max(0, totalWords);
        if (options.MaxPostingsEvaluated > 0) postings = Math.Min(postings, options.MaxPostingsEvaluated);
        return TimeSpan.FromMilliseconds(postings * _nsPerPosting / 1e6);
    }
    /// <summary>What a count just cost: from now on the estimate uses this index's own rate.</summary>
    public void Record(long postingsEvaluated, TimeSpan elapsed) {
        if (postingsEvaluated < CalibrationMinPostings) return;
        _nsPerPosting = Math.Max(1, elapsed.TotalMilliseconds * 1e6 / postingsEvaluated);
    }
}
