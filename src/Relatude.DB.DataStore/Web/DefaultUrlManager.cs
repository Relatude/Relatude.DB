using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.Web;

/// <summary>
/// The simplest url manager: the address of a node is its complete URL path, and addresses stay
/// globally unique (the classic behavior, expressed through the manager contract). A reference
/// implementation and a starting point for custom managers.
/// </summary>
public class DefaultUrlManager : IUrlManager {
    IDataStore _db = default!;
    public void Initialize(IDataStore store) => _db = store;

    public IdKeyWithCultureId[] GetMatches(string completeUrl) {
        var path = UrlUtil.GetPath(completeUrl);
        if (path.Length <= 1) return [];
        return _db.GetNodeIdsFromAddress(path[1..]);
    }

    public string? TryGetUrl(NodeMeta meta, bool absolute) {
        if (string.IsNullOrEmpty(meta.Address)) return null;
        return "/" + meta.Address;
    }

    public bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address) {
        var self = ResolveInternalId(_db, node);
        foreach (var owner in _db.GetNodeIdsFromAddress(address)) {
            if (owner.IdKey.Int == self && owner.CultureId == cultureId) continue; // the node itself
            return false;
        }
        return true;
    }

    /// <summary>The internal id of the node, or 0 when the node does not exist (yet).</summary>
    public static int ResolveInternalId(IDataStore db, NodeKey node) {
        if (node.HasInt) return node.Int;
        if (node.HasGuid && db.TryGetNodeMeta(node.Guid, out var meta)) return meta.InternalId;
        return 0;
    }
}
