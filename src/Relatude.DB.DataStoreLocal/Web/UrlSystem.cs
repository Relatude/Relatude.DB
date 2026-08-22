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
/// The store-internal URL facade. Routes every URL, inbound and outbound, into one of two lanes:
/// <list type="bullet">
/// <item>The asset lane - files, adjusted files and embedded-content deeplinks. Fixed machinery,
/// not customizable. With a url manager configured these live under a reserved root (default
/// "/assets/") carrying self-contained id-based payloads.</item>
/// <item>The page lane - node URLs. Delegates to the configured <see cref="IUrlManager"/>, then
/// filters the candidates through the QueryContext. Without a manager, everything (pages and
/// assets alike) passes through to the classic <see cref="IUrlProvider"/> so behavior is
/// unchanged.</item>
/// </list>
/// The facade also owns the internal link format for HTML and Markdown properties: stored values
/// carry id-based "rdb:" tokens that survive renames, rewritten to public URLs on the way out and
/// back to tokens on the way in.
/// </summary>
internal sealed class UrlSystem {
    internal const string TokenScheme = "rdb:";
    internal const string DefaultAssetUrlRoot = "/assets/";

    readonly DataStoreLocal _db;
    readonly IUrlProvider _publicProvider;
    readonly IUrlManager? _manager;
    readonly InternalUrlProvider _tokenEncoder;
    readonly string _assetRoot;
    Dictionary<Guid, Guid[]>? _contentPropsByType; // node type id -> ids of HTML/Markdown string properties

    public UrlSystem(DataStoreLocal db, IUrlProvider publicProvider, IUrlManager? manager, string? assetUrlRoot) {
        _db = db;
        _publicProvider = publicProvider;
        _manager = manager;
        _tokenEncoder = new InternalUrlProvider();
        _tokenEncoder.Initialize(db);
        var root = string.IsNullOrWhiteSpace(assetUrlRoot) ? DefaultAssetUrlRoot : assetUrlRoot;
        if (!root.StartsWith('/')) root = "/" + root;
        if (!root.EndsWith('/')) root += "/";
        _assetRoot = root;
    }
    public IUrlManager? Manager => _manager;
    public bool HasManager => _manager != null;

    // outbound //////////////////////////////////////////////////////////////////////////////////

    public string GetUrl(NodeKey nodeKey, bool absolute, QueryContext? ctx) {
        if (_manager == null) return _publicProvider.GetUrl(nodeKey, absolute);
        if (_db.TryGetNodeMeta(nodeKey, out var meta, ctx)) {
            var url = _manager.TryGetUrl(meta, absolute);
            if (url != null) return url;
        }
        // not routable (or not visible in this context): fall back to a parseable asset-lane node url
        return _assetRoot + _tokenEncoder.GetUrl(nodeKey, false);
    }
    public string GetUrl(NodePath nodePath, bool absolute, QueryContext? ctx) {
        if (nodePath.Path.Length == 0) return GetUrl(nodePath.NodeKey, absolute, ctx);
        if (_manager == null) return _publicProvider.GetUrl(nodePath, absolute);
        return _assetRoot + _tokenEncoder.GetUrl(nodePath, false);
    }
    public string GetUrl(PropertyPath property, string? contentVersionId, bool absolute, string? fileName) {
        if (_manager == null) return _publicProvider.GetUrl(property, contentVersionId, absolute);
        return assetUrl(_tokenEncoder.GetUrl(property, contentVersionId, false), fileName);
    }
    public string GetUrl(PropertyPath property, FileAdjustment adjustment, string? contentVersionId, bool absolute, string? fileName) {
        if (_manager == null) return _publicProvider.GetUrl(property, adjustment, contentVersionId, absolute);
        var ext = FileFormatUtil.GetExtensionWithDot(adjustment.RequestedFormat);
        if (fileName != null && ext != null) fileName = Path.GetFileNameWithoutExtension(fileName) + ext;
        return assetUrl(_tokenEncoder.GetUrl(property, adjustment, contentVersionId, false), fileName);
    }
    string assetUrl(string token, string? fileName) {
        if (string.IsNullOrEmpty(fileName)) return _assetRoot + token;
        return _assetRoot + token + "/" + urlSafeName(fileName);
    }
    static string urlSafeName(string name) {
        var sb = new StringBuilder(Math.Min(name.Length, 40));
        foreach (var c in name) {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') sb.Append(c);
            else sb.Append('_');
            if (sb.Length >= 40) break;
        }
        return sb.ToString();
    }

    // inbound ///////////////////////////////////////////////////////////////////////////////////

    public bool TryParseUrl(string url, [MaybeNullWhen(false)] out UrlKeys result, QueryContext ctx) {
        result = null;
        if (_manager == null) return tryParseWithProvider(url, out result);
        if (tryGetAssetToken(url, out var token)) return tryParseToken(token, out result);
        // page lane: the manager proposes candidates, the store filters them through the context
        IdKeyWithCultureId[] matches;
        try {
            matches = _manager.GetMatches(url);
        } catch {
            return false; // a malformed URL is a non-match, not an error
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
    bool tryGetAssetToken(string url, [MaybeNullWhen(false)] out string token) {
        token = null;
        var path = UrlUtil.GetPath(url); // scheme, host, query and fragment removed
        if (!path.StartsWith(_assetRoot, StringComparison.Ordinal)) return false;
        var start = _assetRoot.Length;
        var end = start;
        while (end < path.Length && path[end] != '/') end++; // an optional cosmetic file name may follow the token
        if (end == start) return false;
        token = path[start..end];
        return true;
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
    bool tryParseWithProvider(string url, [MaybeNullWhen(false)] out UrlKeys result) {
        result = null;
        if (!_publicProvider.TryParseUrlTarget(url, out var type)) return false;
        switch (type) {
            case UrlTarget.Node: {
                    if (_publicProvider.TryParseUrlNodeKey(url, out var nodeKey)) {
                        result = new UrlKeys { Target = type, NodeKey = nodeKey };
                    }
                }
                break;
            case UrlTarget.EmbeddedNode: {
                    if (_publicProvider.TryParseUrlNodePath(url, out var nodePath)) {
                        result = new UrlKeys { Target = type, NodeKey = nodePath.NodeKey, NodePath = nodePath };
                    }
                }
                break;
            case UrlTarget.Property: {
                    if (_publicProvider.TryParseUrlPropertyPath(url, out var propertyPath)) {
                        result = new UrlKeys { Target = type, NodeKey = propertyPath.NodePath.NodeKey, NodePath = propertyPath.NodePath, PropertyPath = propertyPath };
                    }
                }
                break;
            case UrlTarget.PropertyAdjusted: {
                    if (_publicProvider.TryParseUrlAdjustments(url, out var propertyPath, out var adjustment)) {
                        result = new UrlKeys { Target = type, NodeKey = propertyPath.NodePath.NodeKey, NodePath = propertyPath.NodePath, PropertyPath = propertyPath, Adjustment = adjustment };
                    }
                }
                break;
            default: break;
        }
        return result != null;
    }

    // addresses /////////////////////////////////////////////////////////////////////////////////

    public bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address) {
        var normalized = _db._addresses.NormalizeAddress(address, out _) ?? string.Empty;
        if (_manager != null) return _manager.WillAddressResultInUniqueUrl(node, cultureId, normalized);
        var id = node.HasInt ? node.Int : (node.HasGuid && _db._guids.TryGetId(node.Guid, out var intId) ? intId : 0);
        return !_db._addresses.IsAddressTakenByOther(normalized, id, cultureId == Guid.Empty ? null : cultureId);
    }

    /// <summary>
    /// The commit-time address step: normalizes the address of the node being written, probes
    /// uniqueness (through the manager when one is configured, otherwise globally), suffixes the
    /// address when the probe fails, registers the final address and writes it back to the node
    /// when it had to change. Runs inside the store's write transaction.
    /// </summary>
    public void RegisterAddressAtCommit(INodeData source, INodeData assignTo) {
        var culture = source.Meta?.CultureId;
        var address = _db._addresses.NormalizeAddress(source.Address, out var changed);
        if (address != null) {
            var key = new NodeKey(source.Id, source.__Id);
            var cultureId = culture ?? Guid.Empty;
            if (!willBeUnique(key, cultureId, address)) {
                address = makeUnique(address, key, cultureId, source.__Id);
                changed = true;
            }
        }
        _db._addresses.Register(source.__Id, address, culture);
        if (changed) assignTo.Address = address;
    }
    bool willBeUnique(NodeKey key, Guid cultureId, string address) {
        if (_manager != null) return _manager.WillAddressResultInUniqueUrl(key, cultureId, address);
        return !_db._addresses.IsAddressTakenByOther(address, key.Int, cultureId == Guid.Empty ? null : cultureId);
    }
    string makeUnique(string address, NodeKey key, Guid cultureId, int id) {
        var suffix = address.Length == 0 ? id : 2;
        var attemptCount = 0;
        var rnd = Random.Shared;
        while (true) {
            string candidate;
            if (attemptCount < 10) {
                candidate = address.Length > 0 ? address + "-" + suffix : suffix.ToString();
            } else if (attemptCount < 20) {
                candidate = address + "-" + rnd.Next(1000, 9999).ToString();
            } else {
                candidate = address + "-" + Guid.NewGuid().ToString("N").ToLower();
            }
            if (willBeUnique(key, cultureId, candidate)) return candidate;
            attemptCount++;
            suffix++;
        }
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

    /// <summary>Rewrites the internal rdb: tokens in an HTML/Markdown value to current public URLs. Tokens whose target no longer exists become "#".</summary>
    public string ExternalizeContentLinks(string content, QueryContext ctx) {
        if (string.IsNullOrEmpty(content)) return content;
        var idx = content.IndexOf(TokenScheme, StringComparison.Ordinal);
        if (idx < 0) return content;
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
