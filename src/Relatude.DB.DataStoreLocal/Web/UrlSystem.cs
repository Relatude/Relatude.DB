using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace Relatude.DB.Web;

/// <summary>
/// The store-internal URL facade around the <see cref="IUrlManager"/>. The manager decides what
/// URLs look like; the facade owns everything a manager should not have to deal with:
/// <list type="bullet">
/// <item>Token encoding - files, adjusted files and embedded-content deeplinks are packed into
/// compact id-based tokens (the <see cref="InternalUrlProvider"/> grammar); managers only place
/// them in URL space.</item>
/// <item>Context filtering - candidates from <see cref="IUrlManager.GetMatches"/> are filtered
/// through the QueryContext (publication, access, revision) before anything reaches the web layer.</item>
/// <item>The commit-time address step - normalization, uniqueness probing through the manager and
/// the suffix loop.</item>
/// <item>The internal link format of HTML and Markdown properties - stored values carry id-based
/// "rdb:" tokens that survive renames, rewritten to public URLs on the way out and back to tokens
/// on the way in.</item>
/// </list>
/// </summary>
internal sealed class UrlSystem {
    internal const string TokenScheme = "rdb:";

    readonly DataStoreLocal _db;
    readonly IUrlManager _manager;
    readonly InternalUrlProvider _tokenEncoder;
    Dictionary<Guid, Guid[]>? _contentPropsByType; // node type id -> ids of HTML/Markdown string properties

    public UrlSystem(DataStoreLocal db, IUrlManager manager) {
        _db = db;
        _manager = manager;
        _tokenEncoder = new InternalUrlProvider();
        _tokenEncoder.Initialize(db);
    }
    public IUrlManager Manager => _manager;

    // outbound //////////////////////////////////////////////////////////////////////////////////

    public string GetUrl(NodeKey nodeKey, bool absolute, QueryContext? ctx) {
        if (_db.TryGetNodeMeta(nodeKey, out var meta, ctx)) {
            var url = _manager.TryGetUrl(meta, absolute);
            if (url != null) return url;
        }
        // not routable (or not visible in this context): fall back to a parseable id-based asset url
        return _manager.GetAssetUrl(new AssetUrl {
            Token = _tokenEncoder.GetUrl(nodeKey, false),
            Target = UrlTarget.Node,
            Owner = nodeKey,
        }, absolute);
    }
    public string GetUrl(NodePath nodePath, bool absolute, QueryContext? ctx) {
        if (nodePath.Path.Length == 0) return GetUrl(nodePath.NodeKey, absolute, ctx);
        return _manager.GetAssetUrl(new AssetUrl {
            Token = _tokenEncoder.GetUrl(nodePath, false),
            Target = UrlTarget.EmbeddedNode,
            Owner = nodePath.NodeKey,
        }, absolute);
    }
    public string GetUrl(PropertyPath property, string? contentVersionId, bool absolute, string? fileName) {
        return _manager.GetAssetUrl(new AssetUrl {
            Token = _tokenEncoder.GetUrl(property, contentVersionId, false),
            Target = UrlTarget.Property,
            Owner = property.NodePath.NodeKey,
            FileName = fileName,
            // for managers that render the target readably instead of inside the token:
            PropertyPath = property,
            ContentVersionId = contentVersionId,
        }, absolute);
    }
    public string GetUrl(PropertyPath property, FileAdjustmentBase adjustment, string? contentVersionId, bool absolute, string? fileName) {
        var ext = FileFormatUtil.GetExtensionWithDot(adjustment.RequestedFormat);
        if (fileName != null && ext != null) fileName = Path.GetFileNameWithoutExtension(fileName) + ext;
        return _manager.GetAssetUrl(new AssetUrl {
            Token = _tokenEncoder.GetUrl(property, adjustment, contentVersionId, false),
            Target = UrlTarget.PropertyAdjusted,
            Owner = property.NodePath.NodeKey,
            FileName = fileName,
            // for managers that render the adjustment and target readably instead of inside the token:
            BaseToken = _tokenEncoder.GetUrl(property, contentVersionId, false),
            Adjustment = adjustment,
            PropertyPath = property,
            ContentVersionId = contentVersionId,
        }, absolute);
    }

    // inbound ///////////////////////////////////////////////////////////////////////////////////

    public bool TryParseUrl(string url, [MaybeNullWhen(false)] out UrlKeys result, QueryContext ctx) {
        result = null;
        // asset lane first: asset URLs never reach page resolution
        AssetTokenMatch? assetMatch = null;
        try {
            assetMatch = _manager.TryGetAssetToken(url);
        } catch {
            // a manager choking on a malformed URL is a non-match, not an error
        }
        if (assetMatch != null) {
            // an asset URL the manager refused (bad or missing signature) is a dead end: falling back
            // to page resolution here would serve the page an asset URL was built on top of
            if (assetMatch.IsRejected) return false;
            if (assetMatch.PropertyPath != null) {
                // the manager rendered the target readably: no token involved
                result = new UrlKeys {
                    Target = UrlTarget.Property,
                    NodeKey = assetMatch.PropertyPath.NodePath.NodeKey,
                    NodePath = assetMatch.PropertyPath.NodePath,
                    PropertyPath = assetMatch.PropertyPath,
                };
            } else {
                if (assetMatch.Token == null || !tryParseToken(assetMatch.Token, out result)) return false;
            }
            if (assetMatch.Adjustment != null && result.Target == UrlTarget.Property) {
                // the manager rendered the adjustment readably: combine it with the target
                result.Target = UrlTarget.PropertyAdjusted;
                result.Adjustment = assetMatch.Adjustment;
            }
            return true;
        }
        // page lane: the manager proposes candidates, the store filters them through the context
        NodeKeyWithCulture[] matches;
        try {
            matches = _manager.GetMatches(url);
        } catch {
            return false;
        }
        foreach (var match in matches) {
            var cultureCode = tryGetCultureCode(match.CultureId);
            var matchCtx = match.CultureId == Guid.Empty ? ctx : ctx.Culture(match.CultureId);
            if (!nodeIsVisible(match.IdKey, matchCtx)) continue;
            result = new UrlKeys {
                Target = UrlTarget.Node,
                NodeKey = match.IdKey,
                CultureId = match.CultureId,
                CultureCode = cultureCode,
            };
            return true;
        }
        return false;
    }
    bool nodeExists(NodeKey key) {
        // existence regardless of publication or access, so unpublished targets still link for those who may see them
        return nodeIsVisible(key, QueryContext.MasterAdmin);
    }
    bool nodeIsVisible(NodeKey key, QueryContext ctx) {
        try {
            if (key.HasInt) return _db.TryGet(key.Int, out _, ctx);
            if (key.HasGuid) return _db.TryGet(key.Guid, out _, ctx);
        } catch {
            // fall through: an unresolvable candidate is a non-match
        }
        return false;
    }
    string? tryGetCultureCode(Guid cultureId) {
        if (cultureId == Guid.Empty) return null;
        return _db._nativeModelStore.TryGetCultureCode(cultureId, out var code) ? code : null;
    }
    bool tryParseToken(string token, [MaybeNullWhen(false)] out UrlKeys result) {
        result = null;
        if (!_tokenEncoder.TryParseUrlTarget(token, out var target)) return false;
        switch (target) {
            case UrlTarget.Node:
                if (_tokenEncoder.TryParseUrlNodeKey(token, out var nodeKey)) {
                    result = new UrlKeys { Target = target, NodeKey = nodeKey };
                }
                break;
            case UrlTarget.EmbeddedNode:
                if (_tokenEncoder.TryParseUrlNodePath(token, out var nodePath)) {
                    result = new UrlKeys { Target = target, NodeKey = nodePath.NodeKey, NodePath = nodePath };
                }
                break;
            case UrlTarget.Property:
                if (_tokenEncoder.TryParseUrlPropertyPath(token, out var propertyPath)) {
                    result = new UrlKeys { Target = target, NodeKey = propertyPath.NodePath.NodeKey, NodePath = propertyPath.NodePath, PropertyPath = propertyPath };
                }
                break;
            case UrlTarget.PropertyAdjusted:
                if (_tokenEncoder.TryParseUrlAdjustments(token, out var adjustedPath, out var adjustment)) {
                    result = new UrlKeys { Target = target, NodeKey = adjustedPath.NodePath.NodeKey, NodePath = adjustedPath.NodePath, PropertyPath = adjustedPath, Adjustment = adjustment };
                }
                break;
            default: break;
        }
        return result != null;
    }

    // addresses /////////////////////////////////////////////////////////////////////////////////

    public bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address) {
        var normalized = _db._addresses.NormalizeAddress(address, out _) ?? string.Empty;
        return _manager.WillAddressResultInUniqueUrl(node, cultureId, normalized);
    }

    /// <summary>
    /// The commit-time address step: normalizes the address of the node being written, probes
    /// uniqueness through the manager, suffixes the address when the probe fails, registers the
    /// final address and writes it back to the node when it had to change. Runs inside the store's
    /// write transaction.
    /// </summary>
    public void RegisterAddressAtCommit(INodeData source, INodeData assignTo) {
        var culture = source.Meta?.CultureId;
        var address = _db._addresses.NormalizeAddress(source.Address, out var changed);
        if (address != null) {
            var key = new NodeKey(source.Id, source.__Id);
            var cultureId = culture ?? Guid.Empty;
            if (!isUnique(key, source.Id, cultureId, address)) {
                address = makeUnique(address, key, cultureId, source.__Id, source.Id);
                changed = true;
            }
        }
        var inBatch = _batch != null && _batch.Nodes.Contains(source.Id);
        string? before = null;
        if (inBatch) _db._addresses.TryGetAddressAndTryMatchCulture(source.__Id, culture, out before);
        _db._addresses.Register(source.__Id, address, culture);
        if (changed) assignTo.Address = address;
        if (inBatch) {
            if (address != null) _batch!.Addressed.Add(source.Id);
            batchManager?.AddressChanged(source.Id, source.__Id, culture ?? Guid.Empty, before, address);
        }
    }

    // an address batch under way (NodeAddressesAction): its nodes, and those of them it has given an address so far
    sealed class AddressBatch(HashSet<Guid> nodes) {
        public readonly HashSet<Guid> Nodes = nodes;
        public readonly HashSet<Guid> Addressed = [];
    }
    AddressBatch? _batch;
    IUrlManagerBatch? batchManager => _manager as IUrlManagerBatch;

    /// <summary>
    /// Starts an address batch (NodeAddressesAction): until it ends, a node of it whose parent it has already given an
    /// address is checked against that parent's other children alone, as only they can have its URL once the parent's
    /// is unique, and the manager keeps what the checks read. The action ends it, and the transaction does however it ends.
    /// </summary>
    public void BeginAddressBatch(IEnumerable<Guid> nodes) {
        EndAddressBatch();
        _batch = new([.. nodes]);
        batchManager?.BeginBatch();
    }
    public void EndAddressBatch() {
        if (_batch == null) return;
        _batch = null;
        batchManager?.EndBatch();
    }
    bool isUnique(NodeKey key, Guid nodeId, Guid cultureId, string address) {
        if (_batch != null && _batch.Nodes.Contains(nodeId) && batchManager is { } manager
            && manager.TryGetUrlParent(key, cultureId, out var parentId) && _batch.Addressed.Contains(parentId)) {
            return manager.WillAddressResultInUniqueUrlAmongSiblings(key, cultureId, address, parentId);
        }
        return _manager.WillAddressResultInUniqueUrl(key, cultureId, address);
    }
    /// <summary>
    /// The address with a number after it that no other node's URL has: "-2" first (an empty address takes the node's
    /// id), then a number from each of the ranges above it, doubling in size (3-4, 5-8, 9-16, ...). With n numbers taken,
    /// as n copies beside each other take them, a free one turns up in about log2(n) checks rather than n, and is no
    /// longer than the taken ones need. The number in a range is the node's own (pickIn), so the same node finds the
    /// same one, and nodes added beside each other spread over it. Past 2^40 a guid takes over.
    /// </summary>
    string makeUnique(string address, NodeKey key, Guid cultureId, int id, Guid nodeId) {
        string withSuffix(long number) => address.Length > 0 ? address + "-" + number : number.ToString();
        long first = address.Length == 0 ? id : 2;
        var candidate = withSuffix(first);
        if (isUnique(key, nodeId, cultureId, candidate)) return candidate;
        long from = first + 1, size = 2;
        for (var range = 0; range < 40; range++) {
            candidate = withSuffix(from + pickIn(nodeId, range, size));
            if (isUnique(key, nodeId, cultureId, candidate)) return candidate;
            from += size;
            size *= 2;
        }
        while (true) {
            candidate = (address.Length > 0 ? address + "-" : "") + Guid.NewGuid().ToString("N");
            if (isUnique(key, nodeId, cultureId, candidate)) return candidate;
        }
    }
    /// <summary>
    /// A number below <paramref name="size"/> made from the node's id and the range: random to look at, but the same for
    /// the same node, so giving a node its address again gives what it gave before, as a batch of addresses gives what
    /// they would one at a time (SplitMix64 over the id's halves).
    /// </summary>
    static long pickIn(Guid nodeId, int range, long size) {
        Span<byte> bytes = stackalloc byte[16];
        nodeId.TryWriteBytes(bytes);
        var x = BitConverter.ToUInt64(bytes) ^ BitConverter.ToUInt64(bytes[8..]) ^ ((ulong)range * 0x9E3779B97F4A7C15UL);
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (long)(x % (ulong)size);
    }

    // content links (HTML and Markdown properties) //////////////////////////////////////////////

    static readonly Regex _attributeUrls = new("(?<pre>\\b(?:href|src)\\s*=\\s*(?<q>[\"']))(?<url>[^\"']+)(?=\\k<q>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex _markdownUrls = new("(?<pre>\\]\\()(?<url>[^)\\s]+)", RegexOptions.Compiled);

    /// <summary>Rewrites resolvable public URLs in an HTML/Markdown value to internal rdb: tokens. Idempotent; unresolvable and external URLs pass through.</summary>
    public string InternalizeContentLinks(string content, QueryContext ctx) {
        if (string.IsNullOrEmpty(content)) return content;
        var result = _attributeUrls.Replace(content, m => m.Groups["pre"].Value + internalizeUrl(m.Groups["url"].Value, ctx));
        result = _markdownUrls.Replace(result, m => m.Groups["pre"].Value + internalizeUrl(m.Groups["url"].Value, ctx));
        return result;
    }
    string internalizeUrl(string url, QueryContext ctx) {
        if (url.Length == 0) return url;
        if (url.StartsWith(TokenScheme, StringComparison.Ordinal)) return url; // already internal
        var c = url[0];
        if (c == '#') return url; // in-page anchor
        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return url;
        if (!TryParseUrl(url, out var keys, ctx)) return url; // external or unresolvable: leave as-is
        return TokenScheme + keys.Target switch {
            UrlTarget.Node => _tokenEncoder.GetUrl(keys.NodeKey, false),
            UrlTarget.EmbeddedNode => _tokenEncoder.GetUrl(keys.NodePath!, false),
            UrlTarget.Property => _tokenEncoder.GetUrl(keys.PropertyPath!, null, false), // content version id is startup specific, never stored
            UrlTarget.PropertyAdjusted => _tokenEncoder.GetUrl(keys.PropertyPath!, keys.Adjustment!, null, false),
            _ => throw new NotImplementedException(),
        };
    }

    // Guards against unbounded recursion: resolving a token calls the url manager, and a manager that
    // materializes node objects with HTML properties triggers externalization of THOSE values, which
    // resolve their tokens through the manager again - two pages linking to each other would otherwise
    // recurse forever. Nested calls keep the raw tokens; only the outermost call serves users.
    [ThreadStatic] static bool _isExternalizing;

    /// <summary>Rewrites the internal rdb: tokens in an HTML/Markdown value to current public URLs. Tokens whose target no longer exists become "#".</summary>
    public string ExternalizeContentLinks(string content, QueryContext ctx) {
        if (string.IsNullOrEmpty(content)) return content;
        var idx = content.IndexOf(TokenScheme, StringComparison.Ordinal);
        if (idx < 0) return content;
        if (_isExternalizing) return content; // re-entrant read during URL building, see note above
        _isExternalizing = true;
        try {
            var sb = new StringBuilder(content.Length + 64);
            var pos = 0;
            while (idx >= 0) {
                sb.Append(content, pos, idx - pos);
                var end = idx + TokenScheme.Length;
                while (end < content.Length && isTokenChar(content[end])) end++;
                var token = content[(idx + TokenScheme.Length)..end];
                sb.Append(externalizeToken(token, ctx));
                pos = end;
                idx = pos >= content.Length ? -1 : content.IndexOf(TokenScheme, pos, StringComparison.Ordinal);
            }
            if (pos < content.Length) sb.Append(content, pos, content.Length - pos);
            return sb.ToString();
        } finally {
            _isExternalizing = false;
        }
    }
    static bool isTokenChar(char c) {
        return char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.';
    }
    string externalizeToken(string token, QueryContext ctx) {
        try {
            if (!tryParseToken(token, out var keys)) return "#";
            switch (keys.Target) {
                case UrlTarget.Node:
                    if (!nodeExists(keys.NodeKey)) return "#"; // deleted target: a dead link, not an error
                    return GetUrl(keys.NodeKey, false, ctx);
                case UrlTarget.EmbeddedNode:
                    if (!nodeExists(keys.NodeKey)) return "#";
                    return GetUrl(keys.NodePath!, false, ctx);
                case UrlTarget.Property: return _db.GetUrl(keys.PropertyPath!, false, ctx); // refreshes the content version id
                case UrlTarget.PropertyAdjusted: return _db.GetUrl(keys.PropertyPath!, keys.Adjustment!, false, ctx);
                default: return "#";
            }
        } catch {
            return "#"; // deleted target or unreadable value: a dead link, not an error
        }
    }

    /// <summary>Rewrites public URLs to internal tokens in every HTML/Markdown string property of the node. Called at commit time so the stored form is canonical regardless of the write path.</summary>
    public void InternalizeContentValues(INodeData node, QueryContext ctx) {
        var props = getContentProps(node.NodeType);
        if (props.Length == 0) return;
        foreach (var pid in props) {
            if (node.TryGetValue(pid, out var v) && v is string s && s.Length > 0) {
                var rewritten = InternalizeContentLinks(s, ctx);
                if (!ReferenceEquals(rewritten, s)) node.AddOrUpdate(pid, rewritten);
            }
        }
    }
    Guid[] getContentProps(Guid nodeTypeId) {
        var map = _contentPropsByType;
        if (map == null) {
            map = new Dictionary<Guid, Guid[]>();
            foreach (var type in _db.Datamodel.NodeTypes.Values) {
                var ids = type.AllProperties.Values
                    .OfType<StringPropertyModel>()
                    .Where(p => p.StringType == StringValueType.HTML || p.StringType == StringValueType.Markdown)
                    .Select(p => p.Id)
                    .ToArray();
                map[type.Id] = ids;
            }
            _contentPropsByType = map; // benign race: identical content if built twice
        }
        return map.TryGetValue(nodeTypeId, out var props) ? props : [];
    }
}
