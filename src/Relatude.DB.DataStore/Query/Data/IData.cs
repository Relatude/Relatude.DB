using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Datamodels.Properties;
using System.Text;
using Relatude.DB.DataStores;

namespace Relatude.DB.Query.Data;
public interface ICollectionBase {
    double DurationMs { get; set; }
    int Count { get; }
    int TotalCount { get; }
}
public interface ICollectionData : ICollectionBase {
    IEnumerable<object?> Values { get; }
    ICollectionData ReOrder(IEnumerable<int> newPos);
    ICollectionData Filter(bool[] keep);
    ICollectionData Page(int pageIndex, int pageSize);
    ICollectionData Take(int take);
    ICollectionData Skip(int skip);
    int PageIndexUsed { get; }
    int? PageSizeUsed { get; }
    PropertyType GetPropertyType(string name);
}
public interface IStoreNodeDataCollection : ICollectionData, IIncludeBranches {
    IStoreNodeDataCollection FilterAsMuchAsPossibleUsingIndexes(Variables vars, IExpression orgFilter, out IExpression? remainingFilter);
    IStoreNodeDataCollection Relates(Guid propertyId, Guid nodeId);
    IStoreNodeDataCollection RelatesNot(Guid propertyId, Guid nodeId);
    IStoreNodeDataCollection RelatesAny(Guid propertyId, IEnumerable<Guid> nodeId);
    IStoreNodeDataCollection WhereIn(Guid propertyId, IEnumerable<object?> values);
    IStoreNodeDataCollection WhereInIds(IEnumerable<Guid> values);
    IEnumerable<INodeDataExternal> NodeValues { get; }
    IEnumerable<int> NodeIds { get; }
    IEnumerable<Guid> NodeGuids { get; }
    ObjectData ToObjectCollection();
    bool TryOrderByIndexes(string propertyName, bool descending);
    IStoreNodeDataCollection FilterByTypes(Guid[] types, bool includeDescendants);
}
public interface ISearchQueryResultData : IIncludeBranches, ICollectionBase {
    bool Capped { get; }
    double InnerSearchTimeMs { get; }
    List<SearchResultHitData> Hits { get; }
    int PageIndexUsed { get; }
    int? PageSizeUsed { get; }
    string Search { get; }
}
public interface IGraphCollection : IStoreNodeDataCollection {
    IStoreNodeDataCollection Traverse(Guid relationPropertyId, int minLevel, int maxLevel, GraphDirection direction, int? maxVisited);
    IGraphPathResultData ShortestPath(Guid relationPropertyId, Guid fromNodeId, Guid toNodeId, int maxLevel, GraphDirection direction, int? maxVisited);
}
public interface IGraphPathResultData : IIncludeBranches, ICollectionBase {
    bool Found { get; }
    int Length { get; } // number of edges in the path, 0 when not found or from == to
    List<Guid> NodeIds { get; } // node ids along the path, from -> to, inclusive
    List<INodeDataExternal> Nodes { get; } // node data along the path, from -> to, inclusive
}
public interface IFacetSource : IStoreNodeDataCollection {
    Dictionary<Guid, Facets> EvaluateFacetsAndFilter(Dictionary<Guid, Facets> given, Dictionary<Guid, Facets> set, out IFacetSource filteredSource, int pageIndex, int? pageSize, QueryContext ctx);
    /// <summary>Applies the facet selection as a filter and nothing else: no buckets are built or counted.</summary>
    IFacetSource FilterBySelection(Dictionary<Guid, Facets> given, Dictionary<Guid, Facets> set, QueryContext ctx);
    Datamodel Datamodel { get; }
}
public interface IPivotSource : IStoreNodeDataCollection {
    PivotQueryResultData EvaluatePivot(PivotSpec spec, QueryContext ctx);
}
/// <summary>
/// One bucket of a collection: a value of a property - or a range of them - and the ids of the
/// collection's nodes that hold it. A null <see cref="FacetValue.Value"/> is the nodes without a value.
/// </summary>
public sealed class ValueBucket {
    public ValueBucket(FacetValue value, IEnumerable<int> ids, int count) {
        Value = value;
        Ids = ids;
        Count = count;
    }
    public FacetValue Value { get; }
    /// <summary>The ids of this collection's nodes in the bucket, in the bucket's own order.</summary>
    public IEnumerable<int> Ids { get; }
    public int Count { get; }
}
/// <summary>
/// A collection whose nodes can be sorted into buckets by a property without reading any of them:
/// the facet machinery, handed back as sets of ids rather than counts. What a view that has to place
/// every node of a large result by a value of it needs - a colour per value, a bar per value.
/// </summary>
public interface IBucketSource : IStoreNodeDataCollection {
    /// <summary>
    /// Buckets the nodes of this collection by a property the way a facet does: one bucket per value,
    /// or ranges when <paramref name="isRange"/> says so (null lets the property decide, as the
    /// automatic facets do - a scalar with many distinct values gets ranges). Nodes without a value
    /// form a bucket of their own when <paramref name="includeMissing"/>. Empty buckets are left out.
    /// When there are more buckets than <paramref name="maxBuckets"/> (0 = no limit) the largest are
    /// kept, so a node may end up in no bucket at all. Buckets come in the property's natural order:
    /// values sorted, ranges as generated, the missing bucket last.
    /// </summary>
    IReadOnlyList<ValueBucket> Bucket(Guid propertyId, bool? isRange, bool includeMissing, int maxBuckets, QueryContext ctx);
}
/// <summary>
/// Where the nodes of a collection are: their coordinates, and nothing else about them. A node
/// without one - <see cref="GeoCoordinate.Empty"/>, which is what "no location" is stored as - is
/// left out, so the answer is the located part of the collection. In no particular order: it comes
/// out in whichever order was cheaper to read it in, and what is done with it - drawn on a map,
/// counted into cells - is a set rather than a sequence.
/// </summary>
public sealed class CoordinateSet {
    public CoordinateSet(int[] ids, GeoCoordinate[] coordinates) {
        Ids = ids;
        Coordinates = coordinates;
    }
    /// <summary>The nodes that have a coordinate, in the collection's order.</summary>
    public int[] Ids { get; }
    /// <summary>Where each of them is: <c>Coordinates[i]</c> belongs to <c>Ids[i]</c>.</summary>
    public GeoCoordinate[] Coordinates { get; }
    public int Count => Ids.Length;
}

/// <summary>
/// A collection that can say where its nodes are without reading them: the geo coordinate index,
/// handed back as a value per id. What a map of a large result set needs - a point per node - in the
/// same spirit as <see cref="IBucketSource"/>, which gives it a colour per node.
/// </summary>
public interface ICoordinateSource : IStoreNodeDataCollection {
    /// <summary>
    /// Where the nodes of this collection are, by a geo coordinate property of theirs. An indexed
    /// property is read from its index; an unindexed one falls back to reading the nodes, which is
    /// the only way to answer at all and is why indexing one is worth it.
    /// </summary>
    CoordinateSet Coordinates(Guid propertyId, QueryContext ctx);
}

/// <summary>
/// How often one word occurs in a set of nodes, as the word index has it. The word comes out of the
/// index rather than out of the text, so it is already lowercased and stripped of its punctuation,
/// it is at least as long as the property's minimum word length, and a stop word is not there at
/// all - none of that is stored in the first place. Which is the point: every word here is exactly
/// what a search for it would look for, so a cloud of these is a cloud of things that can be
/// clicked.
/// </summary>
/// <param name="Word">The word, as indexed.</param>
/// <param name="Documents">How many nodes of the set hold it.</param>
/// <param name="Occurrences">How many times in all, counting repeats inside a node. The index keeps
/// no more than 255 of them per node, so a node repeating one word past that counts 255.</param>
/// <param name="DocumentsInIndex">How many nodes hold it in the whole index, the set aside. What
/// tells a word that is common here from a word that is common everywhere, and the only number here
/// a caller cannot work out for itself.</param>
public readonly record struct WordCount(string Word, int Documents, long Occurrences, int DocumentsInIndex) {
    /// <summary>Commonest first. Ties fall back to the occurrences and then to the word itself, so
    /// the order is total: two indexes counting the same nodes hand back the same list, and a test
    /// can compare them.</summary>
    public static IComparer<WordCount> Descending { get; } = new DescendingOrder();
    sealed class DescendingOrder : IComparer<WordCount> {
        public int Compare(WordCount a, WordCount b) {
            var c = b.Documents.CompareTo(a.Documents);
            if (c != 0) return c;
            c = b.Occurrences.CompareTo(a.Occurrences);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Word, b.Word);
        }
    }
}

/// <summary>What the caller wants counted, and how much work it may cost.</summary>
public sealed record WordCountOptions {
    /// <summary>How many words come back, the commonest first. The rest are counted but dropped.</summary>
    public int MaxWords { get; init; } = 200;
    /// <summary>A word held by fewer nodes of the set than this is left out.</summary>
    public int MinDocuments { get; init; } = 1;
    /// <summary>A word shorter than this is skipped without being counted. The index has its own
    /// minimum already (the property's MinWordLength); this only raises it.</summary>
    public int MinWordLength { get; init; } = 0;
    /// <summary>Words to skip, whatever their count - the markup and boilerplate that is genuinely
    /// in the index and only ever in the way. Compared as indexed: lowercase, no punctuation.</summary>
    public IReadOnlySet<string>? Ignore { get; init; }
    /// <summary>Stops the count once this many postings have been looked at, handing back what was
    /// found so far with <see cref="WordCountSet.Truncated"/> set. 0 is no limit. The cost of a
    /// count is the size of the index rather than the size of the set, so a large index wants one.</summary>
    public long MaxPostingsEvaluated { get; init; } = 0;
}

/// <summary>
/// Which words the text of a set of nodes holds, and how often: the word index read the other way
/// round, term by term rather than by a search for one. No node is read, so the cost is the index's
/// size and not the set's - a count over three nodes costs what a count over three million does.
/// </summary>
public sealed class WordCountSet {
    public WordCountSet(WordCount[] words, int distinctWords, int indexDocuments, long postingsEvaluated, bool truncated) {
        Words = words;
        DistinctWords = distinctWords;
        IndexDocuments = indexDocuments;
        PostingsEvaluated = postingsEvaluated;
        Truncated = truncated;
    }
    public static WordCountSet Empty { get; } = new([], 0, 0, 0, false);
    /// <summary>The commonest words of the set, at most <see cref="WordCountOptions.MaxWords"/> of
    /// them, ordered by <see cref="WordCount.Descending"/>.</summary>
    public WordCount[] Words { get; }
    /// <summary>How many distinct words the set holds in all - before the minimum count and the cap,
    /// so a view can say it is showing two hundred of eleven thousand.</summary>
    public int DistinctWords { get; }
    /// <summary>How many documents the whole index holds. With
    /// <see cref="WordCount.DocumentsInIndex"/> this is everything an inverse document frequency
    /// needs, which is what separates the words a result is about from the words everything in the
    /// database is written with.</summary>
    public int IndexDocuments { get; }
    /// <summary>How many postings the count looked at. What it cost, in the only unit that means
    /// anything across engines.</summary>
    public long PostingsEvaluated { get; }
    /// <summary>Whether the budget stopped the count early, leaving the tail of the index uncounted.
    /// The words that came back are still correctly counted; there may be commoner ones missing.</summary>
    public bool Truncated { get; }
}

/// <summary>
/// A collection that can say which words its nodes hold without reading any of them, by reading a
/// word-indexed string property of theirs backwards. Not every text index can: it means walking a
/// term dictionary, which the memory index and the native text index can do and the Lucene and
/// SQLite ones are not asked to - so this is the one source interface whose answer is "no" often
/// enough that <see cref="CanCountWords"/> exists and a view has to ask it before offering itself.
/// </summary>
public interface IWordSource : IStoreNodeDataCollection {
    /// <summary>Whether <see cref="Words"/> can answer for this property at all: it holds text
    /// indexed by words, and the engine behind that index can walk its own terms. False is a plain
    /// fact about the database, not a failure - ask it before offering a word cloud, and do not
    /// call <see cref="Words"/> when it says no.</summary>
    bool CanCountWords(Guid propertyId, QueryContext ctx);
    /// <summary>The commonest words the nodes of this collection hold in the given property.
    /// Throws when <see cref="CanCountWords"/> is false, because there is no second way to answer -
    /// unlike a coordinate, a word cannot be fetched from the node itself without tokenizing it
    /// again, which would be a different answer arrived at a different way.</summary>
    WordCountSet Words(Guid propertyId, WordCountOptions options, QueryContext ctx);
}

public interface ISearchCollection : IStoreNodeDataCollection {
    ISearchQueryResultData Search(string search, Guid searchPropertyId, double? ratioSemantic, float? minimumVectorSimilarity, bool? orSearch, int pageIndex, int pageSize, int? maxHitsEvaluated, int? maxWordsEvaluated);
    IStoreNodeDataCollection FilterBySearch(string text, Guid propertyId, double? ratioSemantic, float? minimumVectorSimilarity, bool? orSearch, int? maxWordVariations);
}
public interface IStoreNodeData {
    IDataStore Store { get; }
    INodeDataExternal NodeData { get; }
    object? GetValue(string propertyName);
    ObjectData ToObjectData();
}


