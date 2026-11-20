using Relatude.DB.Datamodels;

namespace Relatude.DB.Query.Data;

internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource {
    /// <summary>
    /// The nodes that have, or have not, a version published in the culture: those readers of the culture read as its
    /// own, at the collection's time, without falling back to another culture. The set of them is the meta index's id set
    /// for that context, cached per type and culture and kept up to date as nodes are written; it does not depend on the
    /// user, whose read access the collection has already applied. So no node is read: the answer is a set operation,
    /// and the collection keeps its order.
    /// </summary>
    public IStoreNodeDataCollection WherePublishedIn(string? culture, bool has) {
        var readers = QueryContext.MasterAdmin.Hidden().CultureFallbacks(false).Now(_ctx.NowUtc);
        if (Guid.TryParse(culture, out var cultureId)) readers = readers.Culture(cultureId);
        else if (!string.IsNullOrWhiteSpace(culture)) readers = readers.Culture(culture.Trim());
        var published = _def.GetAllIdsForType(_nodeType.Id, readers);
        // a ∩ b as a - (a - b): a difference keeps the order of the set it is taken from, an intersection may not
        var without = _def.Sets.Difference(_ids, published);
        var ids = has ? _def.Sets.Difference(_ids, without) : without;
        return new NodeCollectionData(_db, _ctx, _metrics, ids, _nodeType, _includeBranches);
    }
}
