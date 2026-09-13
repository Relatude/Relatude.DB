using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Query.Data;

/// <summary>
/// The store-level answer to a Words() clause: the commonest words of the result in one of its
/// word-indexed properties (<see cref="WordCountSet"/>). Count is the number of words handed back,
/// TotalCount the number of nodes they were counted over.
/// </summary>
public class WordsQueryResultData : ICollectionData {
    public WordsQueryResultData(Guid propertyId, WordCountSet words, int totalCount) {
        PropertyId = propertyId;
        Words = words;
        TotalCount = totalCount;
    }
    /// <summary>The string property the words were read from.</summary>
    public Guid PropertyId { get; }
    public WordCountSet Words { get; }
    /// <summary>How many nodes the words were counted over.</summary>
    public int TotalCount { get; }
    public int Count => Words.Words.Length;
    public double DurationMs { get; set; }
    public IEnumerable<object?> Values => Words.Words.Select(w => (object?)w);
    public int PageIndexUsed => 0;
    public int? PageSizeUsed => null;
    public ICollectionData ReOrder(IEnumerable<int> newPos) => throw new NotSupportedException("A word count cannot be reordered; the words come commonest first.");
    public ICollectionData Filter(bool[] keep) => throw new NotSupportedException("A word count cannot be filtered; filter the nodes before Words(), or leave words out with IgnoreWords().");
    public ICollectionData Page(int pageIndex, int pageSize) => throw new NotSupportedException("A word count cannot be paged; ask for fewer words instead.");
    public ICollectionData Take(int take) => throw new NotSupportedException("A word count cannot be paged; ask for fewer words instead.");
    public ICollectionData Skip(int skip) => throw new NotSupportedException("A word count cannot be paged; ask for fewer words instead.");
    public PropertyType GetPropertyType(string name) => throw new NotSupportedException();
}
