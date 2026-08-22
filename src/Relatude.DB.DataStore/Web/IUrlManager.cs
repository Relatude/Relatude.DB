using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.Web;

/// <summary>
/// Pluggable mapping between public page URLs and nodes. One instance per store.
/// <para>
/// The manager only handles page URLs. Files, image adjustments and embedded-content deeplinks are
/// handled by the store's fixed asset machinery and never reach this interface. The manager is also
/// never responsible for security: publication, access and revision filtering is applied by the store
/// (driven by the QueryContext) after <see cref="GetMatches"/> returns.
/// </para>
/// <para>
/// With a manager configured, node addresses are treated as local URL segments rather than complete
/// paths, and several nodes may share the same address as long as the manager produces unique
/// complete URLs for them (typically by prefixing dynamic data such as the parent chain).
/// </para>
/// </summary>
public interface IUrlManager {

    /// <summary>Called once, before the store opens. The store reference can be kept and queried later.</summary>
    void Initialize(IDataStore store);

    /// <summary>
    /// Inbound resolution. Returns every node the URL could refer to, best candidate first.
    /// The store hands the manager the URL as received by the web layer (path and query, and when
    /// available scheme and host). Return an empty array when the URL is not recognized.
    /// The store filters the candidates through the current QueryContext afterwards, so the manager
    /// should return matches regardless of publication or access.
    /// </summary>
    IdKeyWithCultureId[] GetMatches(string completeUrl);

    /// <summary>
    /// Outbound generation. The public URL of a node, or null when the node has no public URL.
    /// The meta carries Id, InternalId, Address, CultureId and NodeTypeId; anything else
    /// (parents, domain roots) is read from the store given at <see cref="Initialize"/>.
    /// </summary>
    string? TryGetUrl(NodeMeta meta, bool absolute);

    /// <summary>
    /// True when giving this node this address produces a complete URL that collides with no other
    /// node's URL. Used for editor-side validation before saving, and by the store's commit-time
    /// suffix loop (when false, the store appends -2, -3 ... until the address passes).
    /// The node itself must be excluded from the collision check. The node may not exist yet when
    /// validating an address for a new node.
    /// </summary>
    bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address);

}
