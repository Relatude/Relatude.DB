using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
namespace Relatude.DB.Query.Data;

internal partial class NodeCollectionData : IWordSource {

    /// <summary>
    /// Whether the words of this collection can be counted by the given property (see
    /// <see cref="IWordSource"/>). A question about the database rather than about the collection:
    /// the same property answers the same way for every result set, so a page can ask once and
    /// decide whether to offer the view at all.
    /// </summary>
    public bool CanCountWords(Guid propertyId, QueryContext ctx)
        => _def.Properties.TryGetValue(propertyId, out var prop) && prop is IWordCountProperty counter && counter.CanCountWords(ctx);

    /// <summary>
    /// Which words the nodes of this collection hold in the given property, and how often. Read
    /// from the word index without touching a node, which is also why it can refuse: an index whose
    /// engine cannot walk its own terms has no way to answer, and there is no second route to the
    /// same answer to fall back to.
    /// </summary>
    public WordCountSet Words(Guid propertyId, WordCountOptions options, QueryContext ctx) {
        if (!_def.Properties.TryGetValue(propertyId, out var prop)) throw new Exception("Unknown property " + propertyId + ". ");
        if (prop is not IWordCountProperty counter) throw new Exception("The property \"" + prop.CodeName + "\" holds " + prop.PropertyType + " rather than text, so it has no words to count. ");
        if (_ids.Count == 0) return WordCountSet.Empty;
        if (counter.TryCountWords(_ids, options, ctx, out var words)) return words;
        throw new Exception("The words of \"" + prop.CodeName + "\" cannot be counted: either it is not indexed by words, or its text index is one that cannot list the words it holds. Only the memory and the native text index can. ");
    }
}
