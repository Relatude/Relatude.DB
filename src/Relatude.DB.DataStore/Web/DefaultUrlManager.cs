using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.Web;

/// <summary>One host served by a <see cref="DefaultUrlManager"/>, mapped to the root node of its tree.</summary>
public class UrlDomain {
    /// <summary>Host name, e.g. "www.domain1.no". Compared case-insensitively, without port.</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>The node whose subtree this host serves. The root itself is served at "/", and its address is not part of any URL.</summary>
    public Guid RootId { get; set; }
}

/// <summary>How <see cref="DefaultUrlManager"/> renders the page URL of a node.</summary>
public enum NodeUrlFormat {
    /// <summary>The segment path alone. Nodes without an address have no page URL. The default.</summary>
    Address,
    /// <summary>The segment path, or "/{internal id}" for nodes without an address.</summary>
    AddressOrIntId,
    /// <summary>The segment path, or "/{guid}" for nodes without an address.</summary>
    AddressOrGuidId,
    /// <summary>"/{internal id}/{segment path}". Resolved by the id alone, so the readable part is cosmetic and old URLs survive renames.</summary>
    IntIdAndAddress,
    /// <summary>"/{guid}/{segment path}". Resolved by the id alone, so the readable part is cosmetic and old URLs survive renames.</summary>
    GuidIdAndAddress,
    /// <summary>"/{internal id}".</summary>
    IntIdOnly,
    /// <summary>"/{guid}".</summary>
    GuidIdOnly,
}

/// <summary>How <see cref="DefaultUrlManager"/> places asset URLs (files, adjusted files, deeplinks).</summary>
public enum AssetUrlStyle {
    /// <summary>Under the reserved asset root: "{AssetUrlRoot}{token}/{fileName}". The default.</summary>
    AssetRoot,
    /// <summary>On top of the owning node's page URL: "{pageUrl}/{fileName}?{AssetUrlParamName}={token}". Falls back to the asset root when the owner has no page URL.</summary>
    UnderPageUrl,
}

public class DefaultUrlManagerOptions {
    /// <summary>The relation that connects a node to its parent. When neither this nor <see cref="ParentRelationName"/> is set, the manager runs flat: every node is top level and its URL is "/{address}".</summary>
    public Guid ParentRelationId { get; set; }
    /// <summary>CodeName or full name of the parent relation, resolved against the datamodel when the store initializes.</summary>
    public string? ParentRelationName { get; set; }
    /// <summary>True when the parent is on the source side of the relation (a node reaches its parent by following the relation from target to source).</summary>
    public bool ParentIsRelationSource { get; set; } = true;
    /// <summary>Host to root mappings. When empty, every node is routable and the top of the tree is part of the path.</summary>
    public List<UrlDomain> Domains { get; set; } = [];
    /// <summary>Root used for requests with an unknown or missing host (local development, staging). Defaults to the first configured domain's root.</summary>
    public Guid? FallbackRootId { get; set; }
    /// <summary>Scheme used for absolute URLs.</summary>
    public string Scheme { get; set; } = "https";
    /// <summary>Upper bound on the parent-chain walk, guarding against relation cycles.</summary>
    public int MaxDepth { get; set; } = 32;

    /// <summary>How page URLs are rendered: readable paths, ids, or both.</summary>
    public NodeUrlFormat UrlFormat { get; set; } = NodeUrlFormat.Address;
    /// <summary>Optional prefix for every page URL, e.g. "/content".</summary>
    public string? UrlNodeRoot { get; set; }
    /// <summary>Appends a trailing slash to page URLs.</summary>
    public bool IncludeTrailingSlash { get; set; }
    /// <summary>
    /// Base address prepended to every page URL, outermost - before <see cref="UrlNodeRoot"/>.
    /// May be a path ("/app") or include scheme and host ("https://www.site.com/app"), which makes
    /// page URLs always absolute. Inbound URLs are matched by their path, so both absolute and
    /// relative requests resolve.
    /// </summary>
    public string? BaseAddressPages { get; set; }
    /// <summary>Base address prepended to every asset URL, e.g. a CDN origin ("https://cdn.site.com") or a path ("/files"). See <see cref="UrlManagerBase.BaseAddressAssets"/>.</summary>
    public string? BaseAddressAssets { get; set; }

    /// <summary>Where asset URLs live: under <see cref="UrlManagerBase.AssetUrlRoot"/> or on top of the owner's page URL.</summary>
    public AssetUrlStyle AssetUrlStyle { get; set; } = AssetUrlStyle.AssetRoot;
    /// <summary>Query parameter carrying the asset token when <see cref="AssetUrlStyle.UnderPageUrl"/> is used.</summary>
    public string AssetUrlParamName { get; set; } = "asset";
    /// <summary>URL root of asset URLs, "/assets/" unless changed.</summary>
    public string? AssetUrlRoot { get; set; }
    /// <summary>When set, asset tokens are HMAC signed and tampered or guessed asset URLs stop resolving. See <see cref="UrlManagerBase.AssetUrlSignatureKey"/>.</summary>
    public Guid AssetUrlSignatureKey { get; set; }
}

/// <summary>
/// The built-in url manager, and the default when none is configured. URLs follow the node tree:
/// the address of every node on the path from (but not including) the domain root down to the node
/// itself, joined by slashes. Addresses only have to be unique among nodes that share the same
/// parent-chain path, so "tv/sony-x90/info" and "mobile/pixel/info" can both carry the plain
/// address segment "info". Domains map hosts to root nodes, so the same path can resolve to
/// different nodes per host, while nothing about the domain is ever stored on the nodes.
/// Without a parent relation configured the manager runs flat - every node is top level - which is
/// the plain "/{address}" behavior. <see cref="NodeUrlFormat"/> adds id based URL variants, and
/// <see cref="AssetUrlStyle"/> lets asset URLs build on top of the owning node's page URL.
/// </summary>
public class DefaultUrlManager : UrlManagerBase {
    readonly DefaultUrlManagerOptions _o;
    IDataStore _db = default!;
    Guid _relationId;
    readonly Dictionary<string, Guid> _rootByHost = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Guid, string> _hostByRoot = new();
    Guid _fallbackRootId;
    string _urlNodeRoot = string.Empty; // "" or "/prefix", no trailing slash

    string _basePages = string.Empty;     // "" | "/app" | "https://www.site.com/app", no trailing slash
    string _basePagesPath = string.Empty; // the path portion, used to match inbound URLs

    public DefaultUrlManager(DefaultUrlManagerOptions options) {
        _o = options;
        if (options.AssetUrlRoot != null) AssetUrlRoot = options.AssetUrlRoot;
        AssetUrlSignatureKey = options.AssetUrlSignatureKey;
        BaseAddressAssets = options.BaseAddressAssets;
        (_basePages, _basePagesPath) = NormalizeBaseAddress(options.BaseAddressPages);
    }
    public override void Initialize(IDataStore store) {
        _db = store;
        _relationId = _o.ParentRelationId;
        if (_relationId == Guid.Empty && !string.IsNullOrEmpty(_o.ParentRelationName)) {
            var relation = store.Datamodel.Relations.Values.FirstOrDefault(r =>
                string.Equals(r.FullName(), _o.ParentRelationName, StringComparison.Ordinal)
                || string.Equals(r.CodeName, _o.ParentRelationName, StringComparison.Ordinal));
            if (relation == null) throw new Exception("TreeUrlManager could not find the relation \"" + _o.ParentRelationName + "\" in the datamodel.");
            _relationId = relation.Id;
        }
        foreach (var domain in _o.Domains) {
            if (string.IsNullOrWhiteSpace(domain.Host) || domain.RootId == Guid.Empty) throw new Exception("TreeUrlManager domains require both Host and RootId.");
            _rootByHost[domain.Host.Trim()] = domain.RootId;
            if (!_hostByRoot.ContainsKey(domain.RootId)) _hostByRoot[domain.RootId] = domain.Host.Trim();
        }
        _fallbackRootId = _o.FallbackRootId ?? (_o.Domains.Count > 0 ? _o.Domains[0].RootId : Guid.Empty);
        if (!string.IsNullOrWhiteSpace(_o.UrlNodeRoot)) {
            var root = _o.UrlNodeRoot.Trim().TrimEnd('/');
            _urlNodeRoot = root.StartsWith('/') ? root : "/" + root;
        }
    }
    bool isFlat => _relationId == Guid.Empty;

    // pages, outbound ///////////////////////////////////////////////////////////////////////////

    public override string? TryGetUrl(NodeMeta meta, bool absolute) {
        string? path = tryBuildPath(meta, out var segmentPath, out var rootId) ? segmentPath : null;
        string? url = _o.UrlFormat switch {
            NodeUrlFormat.Address => path,
            NodeUrlFormat.AddressOrIntId => path ?? (meta.InternalId != 0 ? "/" + meta.InternalId : null),
            NodeUrlFormat.AddressOrGuidId => path ?? "/" + meta.Id,
            NodeUrlFormat.IntIdAndAddress => meta.InternalId == 0 ? null : "/" + meta.InternalId + (path == null || path == "/" ? "" : path),
            NodeUrlFormat.GuidIdAndAddress => "/" + meta.Id + (path == null || path == "/" ? "" : path),
            NodeUrlFormat.IntIdOnly => meta.InternalId != 0 ? "/" + meta.InternalId : null,
            NodeUrlFormat.GuidIdOnly => "/" + meta.Id,
            _ => throw new NotImplementedException(),
        };
        if (url == null) return null;
        url = _urlNodeRoot + url;
        if (_o.IncludeTrailingSlash && !url.EndsWith('/')) url += "/";
        url = _basePages + url;
        if (!absolute || _basePages.Contains("://", StringComparison.Ordinal)) return url; // a base with scheme and host is absolute already
        if (rootId != Guid.Empty && _hostByRoot.TryGetValue(rootId, out var host)) return _o.Scheme + "://" + host + url;
        return url; // no domain to make it absolute against
    }

    // pages, inbound ////////////////////////////////////////////////////////////////////////////

    public override IdKeyWithCultureId[] GetMatches(string completeUrl) {
        var path = TryStripBasePath(UrlUtil.GetPath(completeUrl), _basePagesPath);
        if (path == null) return []; // outside the base address
        if (_urlNodeRoot.Length > 0) {
            if (!path.StartsWith(_urlNodeRoot, StringComparison.Ordinal)) return [];
            path = path.Length == _urlNodeRoot.Length ? "/" : path[_urlNodeRoot.Length..];
            if (!path.StartsWith('/')) return []; // prefix must end on a segment boundary
        }
        switch (_o.UrlFormat) {
            case NodeUrlFormat.IntIdOnly:
            case NodeUrlFormat.IntIdAndAddress:
                return matchByFirstSegmentId(path, parseGuids: false);
            case NodeUrlFormat.GuidIdOnly:
            case NodeUrlFormat.GuidIdAndAddress:
                return matchByFirstSegmentId(path, parseGuids: true);
            case NodeUrlFormat.AddressOrIntId: {
                    var byTree = matchByTree(completeUrl, path);
                    return byTree.Length > 0 ? byTree : matchByFirstSegmentId(path, parseGuids: false, requireSingleSegment: true);
                }
            case NodeUrlFormat.AddressOrGuidId: {
                    var byTree = matchByTree(completeUrl, path);
                    return byTree.Length > 0 ? byTree : matchByFirstSegmentId(path, parseGuids: true, requireSingleSegment: true);
                }
            default:
                return matchByTree(completeUrl, path);
        }
    }
    IdKeyWithCultureId[] matchByFirstSegmentId(string path, bool parseGuids, bool requireSingleSegment = false) {
        if (path.Length <= 1) return [];
        var posEnd = path.IndexOf('/', 1);
        if (posEnd == -1) posEnd = path.Length;
        else if (requireSingleSegment) return [];
        var segment = path[1..posEnd];
        if (parseGuids) {
            if (Guid.TryParse(segment, out var guid)) return [new IdKeyWithCultureId(new NodeKey(guid), Guid.Empty)];
        } else {
            if (int.TryParse(segment, out var id) && id > 0) return [new IdKeyWithCultureId(new NodeKey(id), Guid.Empty)];
        }
        return []; // existence and access are checked by the store
    }
    IdKeyWithCultureId[] matchByTree(string completeUrl, string path) {
        var host = UrlUtil.GetHost(completeUrl);
        var rootId = resolveRoot(host);
        if (path.Length <= 1) {
            // the root node itself
            if (rootId == Guid.Empty) return [];
            return [new IdKeyWithCultureId(new NodeKey(rootId), Guid.Empty)];
        }
        var pos = path.LastIndexOf('/');
        var last = path[(pos + 1)..];
        var matches = new List<IdKeyWithCultureId>();
        foreach (var candidate in _db.GetNodeIdsFromAddress(last)) {
            if (!tryGetMeta(candidate.IdKey, candidate.CultureId, out var meta)) continue;
            if (!tryBuildPath(meta, out var candidatePath, out var candidateRoot)) continue;
            if (!string.Equals(candidatePath, path, StringComparison.Ordinal)) continue;
            if (rootId != Guid.Empty && candidateRoot != Guid.Empty && candidateRoot != rootId) continue; // other domain
            matches.Add(candidate);
        }
        return [.. matches];
    }

    // addresses /////////////////////////////////////////////////////////////////////////////////

    public override bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address) {
        switch (_o.UrlFormat) {
            case NodeUrlFormat.IntIdOnly:
            case NodeUrlFormat.GuidIdOnly:
            case NodeUrlFormat.IntIdAndAddress:
            case NodeUrlFormat.GuidIdAndAddress:
                return true; // URLs resolve by id, so duplicate addresses never collide
        }
        var owners = _db.GetNodeIdsFromAddress(address);
        if (owners.Length == 0) return true;
        var self = ResolveInternalId(_db, node);
        string prospectivePath;
        Guid prospectiveRoot;
        if (self == 0 || !tryGetMeta(new NodeKey(self), cultureId, out var meta)) {
            // the node does not exist yet, so its parent chain is unknown
            if (!isFlat) return true; // checked again on later updates, once the tree position is committed
            prospectivePath = "/" + address; // flat: every node is top level, the complete URL is known
            prospectiveRoot = Guid.Empty;
        } else {
            if (isRoot(meta.Id)) return true; // a root is served at "/", its address is not part of any URL
            var parent = getParentMeta(meta);
            if (parent == null) {
                prospectivePath = "/" + address;
                prospectiveRoot = Guid.Empty;
            } else {
                if (!tryBuildPath(parent, out var parentPath, out prospectiveRoot)) return true; // parent is not routable, so neither is this node
                prospectivePath = (parentPath == "/" ? "/" : parentPath + "/") + address;
            }
        }
        foreach (var owner in owners) {
            if (owner.IdKey.Int == self && self != 0) continue; // the node itself, any culture
            if (owner.CultureId != cultureId) continue; // urls are culture scoped
            if (!tryGetMeta(owner.IdKey, owner.CultureId, out var ownerMeta)) continue;
            if (!tryBuildPath(ownerMeta, out var ownerPath, out var ownerRoot)) continue;
            if (ownerRoot != prospectiveRoot) continue; // another domain: the same path is fine
            if (string.Equals(ownerPath, prospectivePath, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // assets ////////////////////////////////////////////////////////////////////////////////////

    public override string GetAssetUrl(AssetUrl asset, bool absolute) {
        if (_o.AssetUrlStyle == AssetUrlStyle.UnderPageUrl && asset.Target != UrlTarget.Node) {
            if (tryGetMeta(asset.Owner, Guid.Empty, out var ownerMeta)) {
                var ownerUrl = TryGetUrl(ownerMeta, absolute);
                if (ownerUrl != null) {
                    if (!string.IsNullOrEmpty(asset.FileName)) {
                        if (!ownerUrl.EndsWith('/')) ownerUrl += "/";
                        ownerUrl += UrlSafeFileName(asset.FileName);
                    }
                    return ownerUrl + "?" + _o.AssetUrlParamName + "=" + SignTokenIfConfigured(asset.Token);
                }
            }
            // the owner has no page URL: fall through to the default placement
        }
        return base.GetAssetUrl(asset, absolute);
    }
    public override string? TryGetAssetToken(string completeUrl) {
        if (_o.AssetUrlStyle == AssetUrlStyle.UnderPageUrl) {
            var raw = UrlUtil.GetQueryParameter(completeUrl, _o.AssetUrlParamName);
            if (raw != null) return ValidateAndStripSignature(raw);
        }
        return base.TryGetAssetToken(completeUrl);
    }

    // tree walk /////////////////////////////////////////////////////////////////////////////////

    Guid resolveRoot(string? host) {
        if (host != null && _rootByHost.TryGetValue(host, out var rootId)) return rootId;
        return _fallbackRootId;
    }
    bool isRoot(Guid nodeId) {
        if (nodeId == Guid.Empty) return false;
        return _hostByRoot.ContainsKey(nodeId) || nodeId == _fallbackRootId;
    }
    bool tryGetMeta(NodeKey key, Guid cultureId, out NodeMeta meta) {
        var ctx = cultureId == Guid.Empty ? QueryContext.MasterAdmin : QueryContext.MasterAdmin.Culture(cultureId);
        return _db.TryGetNodeMeta(key, out meta!, ctx);
    }
    NodeMeta? getParentMeta(NodeMeta meta) {
        if (isFlat) return null;
        // parent lookup: when the parent is the relation source, the node is the target and the walk goes target -> source
        foreach (var parentId in _db.GetRelatedNodeIdsFromRelationId(_relationId, meta.Id, _o.ParentIsRelationSource)) {
            if (tryGetMeta(new NodeKey(parentId), meta.CultureId, out var parentMeta)) return parentMeta;
            return null;
        }
        return null;
    }
    /// <summary>
    /// The relative segment path of the node: ancestor addresses joined from the domain root
    /// (exclusive) down to the node. False when the node has no path: an empty address on the
    /// chain, a cycle, or domains are configured and the chain does not end in a configured root.
    /// </summary>
    bool tryBuildPath(NodeMeta meta, out string path, out Guid rootId) {
        path = string.Empty;
        rootId = Guid.Empty;
        var segments = new List<string>(8);
        var current = meta;
        var depth = 0;
        while (true) {
            if (isRoot(current.Id)) {
                rootId = current.Id;
                break;
            }
            if (string.IsNullOrEmpty(current.Address)) return false; // a node without an address has no path
            segments.Add(current.Address);
            var parent = getParentMeta(current);
            if (parent == null) break; // top of the tree
            current = parent;
            if (++depth > _o.MaxDepth) return false; // cycle guard
        }
        if (rootId == Guid.Empty && (_rootByHost.Count > 0 || _fallbackRootId != Guid.Empty)) {
            return false; // domains are configured and this node is not under any configured root
        }
        if (segments.Count == 0) {
            path = "/";
            return true;
        }
        segments.Reverse();
        path = "/" + string.Join('/', segments);
        return true;
    }
}
