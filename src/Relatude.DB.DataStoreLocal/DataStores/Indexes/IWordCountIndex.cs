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
}
