using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;

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

/// <summary>How <see cref="DefaultUrlManager"/> renders the adjustment part of asset URLs (resized images, converted formats).</summary>
public enum AssetUrlFormat {
    /// <summary>The adjustment travels inside the opaque token. The default; supports every adjustment, and with <see cref="UrlManagerBase.AssetUrlSignatureKey"/> the whole variant is tamper proof.</summary>
    Encrypted,
    /// <summary>Readable query parameters, e.g. "?w=100&amp;h=200&amp;f=jpeg" (see <see cref="FileAdjustmentUrlCodec"/>). Hand editable, so anyone who can see a file can request other variants of it - unless <see cref="UrlManagerBase.AssetUrlSignatureKey"/> is set, which adds a "sig" parameter covering the adjustment and makes edited URLs stop resolving.</summary>
    QueryParameters,
    /// <summary>A short readable path segment after the token, e.g. "w100h200fjpeg" (see <see cref="FileAdjustmentUrlCodec"/>). Hand editable like <see cref="QueryParameters"/>, and equally covered by the "sig" parameter when <see cref="UrlManagerBase.AssetUrlSignatureKey"/> is set. With <see cref="AssetUrlStyle.UnderPageUrl"/> the adjustment is rendered as query parameters instead, since the path belongs to the page.</summary>
    FriendlyShortString,
}

/// <summary>How <see cref="DefaultUrlManager"/> renders the target of asset URLs: which file property on which node.</summary>
public enum PropertyPathUrlFormat {
    /// <summary>The target travels inside the opaque token. The default; supports embedded-content deeplinks and, with <see cref="UrlManagerBase.AssetUrlSignatureKey"/>, tamper proofing.</summary>
    Encrypted,
    /// <summary>Readable query parameters: "?pn={propertyName}&amp;pid={node id}". Guessable unless <see cref="UrlManagerBase.AssetUrlSignatureKey"/> is set, which adds a "sig" parameter covering the target and the adjustment - then only URLs the store handed out resolve.</summary>
    QueryParameters,
    /// <summary>A readable path segment in the token's place: "{propertyName}-{internal int id}", e.g. "/assets/File-123/pic.jpg". Guessable unless <see cref="UrlManagerBase.AssetUrlSignatureKey"/> is set, like <see cref="QueryParameters"/>. With <see cref="AssetUrlStyle.UnderPageUrl"/> the target is rendered as query parameters instead, since the path belongs to the page.</summary>
    FriendlyShortString,
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
    /// <summary>
    /// Base address prepended to every URL, pages and assets alike, and applied first - before
    /// <see cref="BaseAddressPages"/> and <see cref="BaseAddressAssets"/>. May be a path ("/app")
    /// or include scheme and host ("https://www.site.com"), which makes every URL absolute.
    /// A lane base that carries its own scheme and host (a CDN origin) is a complete origin and
    /// replaces this rather than being appended to it. Inbound URLs are matched by their path, so
    /// both absolute and relative requests resolve.
    /// </summary>
    public string? PrimaryBaseAddress { get; set; }
    /// <summary>Appends a trailing slash to page URLs.</summary>
    public bool IncludeTrailingSlash { get; set; }
    /// <summary>
    /// Base address prepended to page URLs, after <see cref="PrimaryBaseAddress"/>, e.g. "/content".
    /// May also include scheme and host, which then replaces the primary base.
    /// </summary>
    public string? BaseAddressPages { get; set; }
    /// <summary>Base address prepended to asset URLs, after <see cref="PrimaryBaseAddress"/>, e.g. a CDN origin ("https://cdn.site.com") or a path ("/files"). See <see cref="UrlManagerBase.BaseAddressAssets"/>.</summary>
    public string? BaseAddressAssets { get; set; }

    /// <summary>How the target of asset URLs (which file property on which node) is rendered: inside the opaque token (Encrypted), as readable query parameters, or as a readable path segment. Readable targets only apply to plain node properties - embedded-content deeplinks always use the token - and require the adjustment to be readable too (see <see cref="AssetUrlFormat"/>), since an adjusted token cannot address a readable target.</summary>
    public PropertyPathUrlFormat PropertyPathFormat { get; set; } = PropertyPathUrlFormat.Encrypted;

    /// <summary>How the adjustment part of asset URLs is rendered: inside the opaque token (Encrypted), as readable query parameters, or as a short readable path segment.</summary>
    public AssetUrlFormat AssetUrlFormat { get; set; } = AssetUrlFormat.Encrypted;
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

    string _basePages = string.Empty;     // effective page base: primary + pages, no trailing slash
    string _basePagesPath = string.Empty; // the path portion, used to match inbound URLs

    public DefaultUrlManager(DefaultUrlManagerOptions options) {
        _o = options;
        if (options.AssetUrlRoot != null) AssetUrlRoot = options.AssetUrlRoot;
        AssetUrlSignatureKey = options.AssetUrlSignatureKey;
        PrimaryBaseAddress = options.PrimaryBaseAddress; // applies to both lanes, before the lane bases
        BaseAddressAssets = options.BaseAddressAssets;
        (_basePages, _basePagesPath) = CombineBaseAddresses(options.PrimaryBaseAddress, options.BaseAddressPages);
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
        if (_o.IncludeTrailingSlash && !url.EndsWith('/')) url += "/";
        url = _basePages + url; // primary base + page base
        if (!absolute || _basePages.Contains("://", StringComparison.Ordinal)) return url; // a base with scheme and host is absolute already
        if (rootId != Guid.Empty && _hostByRoot.TryGetValue(rootId, out var host)) return _o.Scheme + "://" + host + url;
        return url; // no domain to make it absolute against
    }

    // pages, inbound ////////////////////////////////////////////////////////////////////////////

    public override NodeKeyWithCulture[] GetMatches(string completeUrl) {
        var path = TryStripBasePath(UrlUtil.GetPath(completeUrl), _basePagesPath);
        if (path == null) return []; // outside the base address
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
    NodeKeyWithCulture[] matchByFirstSegmentId(string path, bool parseGuids, bool requireSingleSegment = false) {
        if (path.Length <= 1) return [];
        var posEnd = path.IndexOf('/', 1);
        if (posEnd == -1) posEnd = path.Length;
        else if (requireSingleSegment) return [];
        var segment = path[1..posEnd];
        if (parseGuids) {
            if (Guid.TryParse(segment, out var guid)) return [new NodeKeyWithCulture(new NodeKey(guid), Guid.Empty)];
        } else {
            if (int.TryParse(segment, out var id) && id > 0) return [new NodeKeyWithCulture(new NodeKey(id), Guid.Empty)];
        }
        return []; // existence and access are checked by the store
    }
    NodeKeyWithCulture[] matchByTree(string completeUrl, string path) {
        var host = UrlUtil.GetHost(completeUrl);
        var rootId = resolveRoot(host);
        if (path.Length <= 1) {
            // the root node itself
            if (rootId == Guid.Empty) return [];
            return [new NodeKeyWithCulture(new NodeKey(rootId), Guid.Empty)];
        }
        var pos = path.LastIndexOf('/');
        var last = path[(pos + 1)..];
        var matches = new List<NodeKeyWithCulture>();
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
        // readable formats keep the adjustment out of the token: the base token addresses the
        // original file, and the adjustment travels as query parameters or a short path segment
        var token = asset.Token;
        string? adjustmentQuery = null;   // "w=100&h=200"
        string? adjustmentSegment = null; // "w100h200"
        if (_o.AssetUrlFormat != AssetUrlFormat.Encrypted && asset.Adjustment != null && asset.BaseToken != null) {
            if (_o.AssetUrlFormat == AssetUrlFormat.QueryParameters || _o.AssetUrlStyle == AssetUrlStyle.UnderPageUrl) {
                if (FileAdjustmentUrlCodec.TryToQueryString(asset.Adjustment, out var query)) {
                    token = asset.BaseToken;
                    adjustmentQuery = query;
                }
            } else {
                if (FileAdjustmentUrlCodec.TryToShortString(asset.Adjustment, out var segment)) {
                    token = asset.BaseToken;
                    adjustmentSegment = segment;
                }
            }
        }
        // a readable property target replaces the token entirely: "pn=File&pid=123" or "File-123".
        // Only for plain node properties, and only when no adjustment is left inside the token
        // (an adjusted token cannot address a readable target):
        string? targetQuery = null;   // "pn=File&pid=123[&v=...]"
        string? targetSegment = null; // "File-123"
        string? targetText = null;    // canonical "{propertyName}-{id}", what the signature covers
        if (_o.PropertyPathFormat != PropertyPathUrlFormat.Encrypted
            && asset.PropertyPath is { } propertyPath && propertyPath.NodePath.Path.Length == 0
            && (asset.Adjustment == null || adjustmentQuery != null || adjustmentSegment != null)
            && tryGetPropertyName(propertyPath.PropertyId, out var propertyName)) {
            var key = propertyPath.NodePath.NodeKey;
            if (_o.PropertyPathFormat == PropertyPathUrlFormat.FriendlyShortString && _o.AssetUrlStyle == AssetUrlStyle.AssetRoot) {
                // the readable segment always uses the short internal id, resolved when the path is guid addressed
                var intId = ResolveInternalId(_db, key);
                if (intId != 0) targetText = targetSegment = propertyName + "-" + intId;
                // no int id (the node is not stored yet): keep the token
            } else {
                var idText = key.HasInt ? key.Int.ToString() : key.Guid.ToString();
                targetQuery = "pn=" + propertyName + "&pid=" + idText;
                targetText = propertyName + "-" + idText;
            }
            if (asset.ContentVersionId != null && targetText != null) { // cache buster; ignored on the way in
                targetQuery = targetQuery == null ? "v=" + asset.ContentVersionId : targetQuery + "&v=" + asset.ContentVersionId;
            }
        }
        // when a target or an adjustment is readable it sits outside the signed token, so the URL
        // carries its own signature binding the three together
        var isReadable = targetText != null || adjustmentQuery != null || adjustmentSegment != null;
        var signature = isReadable ? TrySignReadableAssetUrl(targetText != null ? null : token, targetText, asset.Adjustment) : null;
        if (_o.AssetUrlStyle == AssetUrlStyle.UnderPageUrl && asset.Target != UrlTarget.Node) {
            if (tryGetMeta(asset.Owner, Guid.Empty, out var ownerMeta)) {
                var ownerUrl = TryGetUrl(ownerMeta, absolute);
                if (ownerUrl != null) {
                    if (!string.IsNullOrEmpty(asset.FileName)) {
                        if (!ownerUrl.EndsWith('/')) ownerUrl += "/";
                        ownerUrl += UrlSafeFileName(asset.FileName);
                    }
                    var first = targetQuery ?? _o.AssetUrlParamName + "=" + SignTokenIfConfigured(token);
                    return ownerUrl + "?" + join(join(first, adjustmentQuery), signatureQuery(signature));
                }
            }
            // the owner has no page URL: fall through to the default placement
        }
        var url = AssetBaseAddress + AssetUrlRoot;
        if (targetSegment != null) url += targetSegment;
        else if (targetQuery != null) url += UrlSafeFileName(string.IsNullOrEmpty(asset.FileName) ? "file" : asset.FileName); // the path is cosmetic, the query addresses the target
        else url += SignTokenIfConfigured(token);
        if (adjustmentSegment != null) url += "/" + adjustmentSegment;
        if (targetQuery == null && !string.IsNullOrEmpty(asset.FileName)) url += "/" + UrlSafeFileName(asset.FileName);
        var fullQuery = join(join(targetQuery, adjustmentQuery), signatureQuery(signature));
        return fullQuery == null ? url : url + "?" + fullQuery;

        static string? signatureQuery(string? sig) => sig == null ? null : SignatureParamName + "=" + sig;
        static string? join(string? a, string? b) => a == null ? b : b == null ? a : a + "&" + b;
    }
    public override AssetTokenMatch? TryGetAssetToken(string completeUrl) {
        if (_o.AssetUrlStyle == AssetUrlStyle.UnderPageUrl) {
            var byTarget = tryMatchReadableTargetFromQuery(completeUrl);
            if (byTarget != null) return byTarget;
            var raw = UrlUtil.GetQueryParameter(completeUrl, _o.AssetUrlParamName);
            if (raw != null) {
                var token = ValidateAndStripSignature(raw);
                if (token == null) return null;
                return tokenMatch(completeUrl, token, readableAdjustmentFromQuery(completeUrl));
            }
            // no asset parameter: the default placement below is the fallback for owners without a page URL
        }
        if (!TryGetAssetRootParts(completeUrl, out var rawToken, out var nextSegment)) return null;
        FileAdjustmentBase? adjustmentFor(string url) => _o.AssetUrlFormat switch {
            AssetUrlFormat.QueryParameters => readableAdjustmentFromQuery(url),
            AssetUrlFormat.FriendlyShortString => nextSegment == null ? null : FileAdjustmentUrlCodec.TryParseShortString(nextSegment),
            _ => null, // Encrypted: the token is self contained
        };
        if (_o.PropertyPathFormat != PropertyPathUrlFormat.Encrypted) {
            var byQuery = tryMatchReadableTargetFromQuery(completeUrl);
            if (byQuery != null) return byQuery;
            if (_o.PropertyPathFormat == PropertyPathUrlFormat.FriendlyShortString) {
                var target = tryParseTargetSegment(rawToken);
                if (target != null) return targetMatch(completeUrl, target, adjustmentFor(completeUrl));
            }
        }
        var assetToken = ValidateAndStripSignature(rawToken);
        if (assetToken == null) return null;
        return tokenMatch(completeUrl, assetToken, adjustmentFor(completeUrl));
    }
    /// <summary>A token addressed match. A readable adjustment sits outside the signed token, so the URL's own signature has to cover it.</summary>
    AssetTokenMatch? tokenMatch(string completeUrl, string token, FileAdjustmentBase? adjustment) {
        if (adjustment != null && !ValidateReadableAssetUrl(completeUrl, token, null, adjustment)) return null;
        return new AssetTokenMatch { Token = token, Adjustment = adjustment };
    }
    /// <summary>A readable target match. Nothing here is inside a token, so the URL's own signature covers the whole request.</summary>
    AssetTokenMatch? targetMatch(string completeUrl, PropertyPath path, FileAdjustmentBase? adjustment) {
        var targetText = readableTargetText(path);
        if (targetText == null) return null;
        if (!ValidateReadableAssetUrl(completeUrl, null, targetText, adjustment)) return null;
        return new AssetTokenMatch { PropertyPath = path, Adjustment = adjustment };
    }
    /// <summary>The canonical "{propertyName}-{id}" text of a target, the form the signature covers regardless of how it was framed in the URL.</summary>
    string? readableTargetText(PropertyPath path) {
        if (!tryGetPropertyName(path.PropertyId, out var name)) return null;
        var key = path.NodePath.NodeKey;
        return name + "-" + (key.HasInt ? key.Int.ToString() : key.Guid.ToString());
    }
    FileAdjustmentBase? readableAdjustmentFromQuery(string completeUrl) {
        if (_o.AssetUrlFormat == AssetUrlFormat.Encrypted) return null;
        return FileAdjustmentUrlCodec.TryParseQuery(completeUrl);
    }
    AssetTokenMatch? tryMatchReadableTargetFromQuery(string completeUrl) {
        if (_o.PropertyPathFormat == PropertyPathUrlFormat.Encrypted) return null;
        var propertyName = UrlUtil.GetQueryParameter(completeUrl, "pn");
        var idText = UrlUtil.GetQueryParameter(completeUrl, "pid");
        if (propertyName == null || idText == null) return null;
        var propertyPath = tryResolvePropertyPath(propertyName, idText);
        if (propertyPath == null) return null;
        return targetMatch(completeUrl, propertyPath, readableAdjustmentFromQuery(completeUrl));
    }
    /// <summary>"{propertyName}-{node id}", the readable target segment. Property code names never contain '-', so the first dash is the separator.</summary>
    PropertyPath? tryParseTargetSegment(string segment) {
        var dash = segment.IndexOf('-');
        if (dash <= 0 || dash == segment.Length - 1) return null;
        return tryResolvePropertyPath(segment[..dash], segment[(dash + 1)..]);
    }
    PropertyPath? tryResolvePropertyPath(string propertyName, string idText) {
        NodeKey key;
        if (int.TryParse(idText, out var intId) && intId > 0) key = new NodeKey(intId);
        else if (Guid.TryParse(idText, out var guid)) key = new NodeKey(guid);
        else return null;
        try {
            var typeId = _db.GetNodeType(key);
            if (!_db.Datamodel.NodeTypes.TryGetValue(typeId, out var type)) return null;
            var property = type.AllProperties.Values.FirstOrDefault(p => string.Equals(p.CodeName, propertyName, StringComparison.OrdinalIgnoreCase));
            if (property == null) return null;
            return new NodePath(key).CreatePropertyPath(property.Id);
        } catch {
            return null; // an unknown node is a non-match, not an error
        }
    }
    bool tryGetPropertyName(Guid propertyId, out string propertyName) {
        propertyName = string.Empty;
        if (!_db.Datamodel.Properties.TryGetValue(propertyId, out var property) || string.IsNullOrEmpty(property.CodeName)) return false;
        propertyName = property.CodeName;
        return true;
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
