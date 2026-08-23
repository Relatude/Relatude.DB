using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.Web;

/// <summary>
/// Pluggable mapping between public URLs and content. One instance per store; every store has one
/// (the store creates a flat <see cref="DefaultUrlManager"/> when none is configured).
/// <para>
/// The manager owns two kinds of URLs. Page URLs map nodes to readable paths: addresses are local
/// segments, several nodes may share an address as long as the manager produces unique complete
/// URLs (typically by prefixing dynamic data such as the parent chain). Asset URLs carry an opaque
/// store-encoded token addressing a file, an adjusted file or an embedded-content deeplink - the
/// manager only decides where that token lives in URL space, never what is inside it.
/// </para>
/// <para>
/// The manager is never responsible for security: publication, access and revision filtering is
/// applied by the store (driven by the QueryContext) after <see cref="GetMatches"/> returns.
/// Derive from <see cref="UrlManagerBase"/> to inherit the default asset URL behavior.
/// </para>
/// </summary>
public interface IUrlManager {

    /// <summary>Called once, before the store opens. The store reference can be kept and queried later.</summary>
    void Initialize(IDataStore store);

    /// <summary>
    /// Inbound page resolution. Returns every node the URL could refer to, best candidate first.
    /// The store hands the manager the URL as received by the web layer (path and query, and when
    /// available scheme and host). Return an empty array when the URL is not recognized.
    /// The store filters the candidates through the current QueryContext afterwards, so the manager
    /// should return matches regardless of publication or access.
    /// </summary>
    IdKeyWithCultureId[] GetMatches(string completeUrl);

    /// <summary>
    /// Outbound page generation. The public URL of a node, or null when the node has no public URL.
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

    /// <summary>
    /// Outbound asset placement: the URL of a file, an adjusted file, an embedded-content deeplink
    /// or the fallback URL of a node the manager declared unroutable. The token is opaque - emit it
    /// unmodified somewhere <see cref="TryGetAssetToken"/> can find it again.
    /// </summary>
    string GetAssetUrl(AssetUrl asset, bool absolute);

    /// <summary>
    /// Inbound asset detection: extracts the token from a URL that <see cref="GetAssetUrl"/>
    /// produced, or null when the URL is not an asset URL. The store calls this before
    /// <see cref="GetMatches"/>, so asset URLs never reach page resolution.
    /// </summary>
    string? TryGetAssetToken(string completeUrl);

}

/// <summary>What <see cref="IUrlManager.GetAssetUrl"/> is asked to place in URL space.</summary>
public sealed class AssetUrl {
    /// <summary>Opaque, URL-safe payload encoding the target (and any adjustment and content version). The store encodes and parses it; managers pass it through unmodified.</summary>
    public required string Token { get; init; }
    /// <summary>Property (a file), PropertyAdjusted (a file variant), EmbeddedNode (a deeplink), or Node (the fallback URL of a node without a page URL).</summary>
    public UrlTarget Target { get; init; }
    /// <summary>The node the asset belongs to, so a manager can build asset URLs on top of the owner's page URL.</summary>
    public NodeKey Owner { get; init; }
    /// <summary>Cosmetic file name, extension already corrected for adjustments. Null when the target has no file.</summary>
    public string? FileName { get; init; }
}
