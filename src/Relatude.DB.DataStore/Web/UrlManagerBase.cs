using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using System.Security.Cryptography;
using System.Text;

namespace Relatude.DB.Web;

/// <summary>
/// Base class for url managers. Implements the asset side of the contract with the default
/// placement - "{AssetUrlRoot}{token}/{fileName}" - so a manager that only cares about page URLs
/// implements just the three page methods. Managers can override the asset pair to place tokens
/// anywhere (for instance on top of the owner's page URL, like <see cref="DefaultUrlManager"/> does
/// with <see cref="AssetUrlStyle.UnderPageUrl"/>).
/// <para>
/// When <see cref="AssetUrlSignatureKey"/> is set, every emitted token carries an HMAC signature
/// and unsigned or tampered asset URLs stop resolving - which makes file URLs unguessable.
/// </para>
/// </summary>
public abstract class UrlManagerBase : IUrlManager {
    public const string DefaultAssetUrlRoot = "/assets/";

    string _assetRoot = DefaultAssetUrlRoot;
    /// <summary>URL root of asset URLs, "/assets/" unless changed. Always stored with leading and trailing slash.</summary>
    public string AssetUrlRoot {
        get => _assetRoot;
        set {
            var root = string.IsNullOrWhiteSpace(value) ? DefaultAssetUrlRoot : value.Trim();
            if (!root.StartsWith('/')) root = "/" + root;
            if (!root.EndsWith('/')) root += "/";
            _assetRoot = root;
        }
    }
    /// <summary>When set (not Guid.Empty), asset tokens are HMAC signed with this key and URLs with a missing or invalid signature stop resolving. Use a stable secret, for instance the store id.</summary>
    public Guid AssetUrlSignatureKey { get; set; }

    string _primaryBase = string.Empty;   // "" | "/app" | "https://www.site.com/app", no trailing slash
    string _assetBaseGiven = string.Empty; // as configured, before the primary base is applied
    string _assetBase = string.Empty;      // effective: primary + given, no trailing slash
    string _assetBasePath = string.Empty;  // the path portion, used to match inbound URLs
    /// <summary>
    /// Base address prepended to every URL of this manager, pages and assets alike, and applied
    /// before <see cref="BaseAddressAssets"/>. May be a path ("/app") or include scheme and host
    /// ("https://www.site.com"), which makes URLs absolute. A lane base that carries its own scheme
    /// and host (a CDN origin) is a complete origin and replaces this rather than being appended to it.
    /// </summary>
    public string? PrimaryBaseAddress {
        get => _primaryBase.Length == 0 ? null : _primaryBase;
        set {
            (_primaryBase, _) = NormalizeBaseAddress(value);
            applyAssetBase();
        }
    }
    /// <summary>
    /// Base address prepended to every asset URL, after <see cref="PrimaryBaseAddress"/>. May be a
    /// path ("/files") or include scheme and host ("https://cdn.example.com"), which makes asset
    /// URLs absolute - the classic CDN offload - and then replaces the primary base. Inbound URLs
    /// are matched by their path, so both absolute and relative requests resolve.
    /// </summary>
    public string? BaseAddressAssets {
        get => _assetBaseGiven.Length == 0 ? null : _assetBaseGiven;
        set {
            (_assetBaseGiven, _) = NormalizeBaseAddress(value);
            applyAssetBase();
        }
    }
    void applyAssetBase() => (_assetBase, _assetBasePath) = CombineBaseAddresses(_primaryBase, _assetBaseGiven);

    public abstract void Initialize(IDataStore store);
    public abstract NodeKeyWithCulture[] GetMatches(string completeUrl);
    public abstract string? TryGetUrl(NodeMeta meta, bool absolute);
    public abstract bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address);

    /// <summary>The normalized asset base address ("" when none), for derived classes composing asset URLs themselves.</summary>
    protected string AssetBaseAddress => _assetBase;
    /// <summary>The path portion of <see cref="BaseAddressAssets"/> ("" when none), what inbound URLs are matched against.</summary>
    protected string AssetBasePath => _assetBasePath;

    public virtual string GetAssetUrl(AssetUrl asset, bool absolute) {
        var url = _assetBase + AssetUrlRoot + SignTokenIfConfigured(asset.Token);
        if (!string.IsNullOrEmpty(asset.FileName)) url += "/" + UrlSafeFileName(asset.FileName);
        return url;
    }
    public virtual AssetTokenMatch? TryGetAssetToken(string completeUrl) {
        if (!TryGetAssetRootParts(completeUrl, out var rawToken, out _)) return null;
        return ValidateAndStripSignature(rawToken);
    }

    /// <summary>
    /// Splits a URL under the asset root into the raw token (signature still attached) and the path
    /// segment following it, when present. False when the URL is not under the asset root.
    /// </summary>
    protected bool TryGetAssetRootParts(string completeUrl, out string rawToken, out string? nextSegment) {
        rawToken = string.Empty;
        nextSegment = null;
        var path = TryStripBasePath(UrlUtil.GetPath(completeUrl), _assetBasePath);
        if (path == null || !path.StartsWith(AssetUrlRoot, StringComparison.Ordinal)) return false;
        var start = AssetUrlRoot.Length;
        var end = start;
        while (end < path.Length && path[end] != '/') end++;
        if (end == start) return false;
        rawToken = path[start..end];
        if (end < path.Length - 1) {
            var nextStart = end + 1;
            var nextEnd = nextStart;
            while (nextEnd < path.Length && path[nextEnd] != '/') nextEnd++;
            if (nextEnd > nextStart) nextSegment = path[nextStart..nextEnd];
        }
        return true;
    }

    /// <summary>
    /// Combines a primary base address with a lane specific one (pages or assets), the primary
    /// first. A lane base carrying scheme and host is a complete origin and replaces the primary.
    /// Returns (full, path) like <see cref="NormalizeBaseAddress"/>.
    /// </summary>
    protected static (string full, string path) CombineBaseAddresses(string? primary, string? lane) {
        var (primaryFull, primaryPath) = NormalizeBaseAddress(primary);
        var (laneFull, lanePath) = NormalizeBaseAddress(lane);
        if (laneFull.Contains("://", StringComparison.Ordinal)) return (laneFull, lanePath); // an absolute lane base is its own origin
        if (primaryFull.Length == 0) return (laneFull, lanePath);
        if (laneFull.Length == 0) return (primaryFull, primaryPath);
        return (primaryFull + laneFull, primaryPath + lanePath);
    }

    /// <summary>Normalizes a base address to (full, path): full is what URLs are prefixed with, path is what inbound URLs are matched against. Both empty when no base is given.</summary>
    protected static (string full, string path) NormalizeBaseAddress(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return (string.Empty, string.Empty);
        var full = value.Trim().TrimEnd('/');
        if (full.Length == 0) return (string.Empty, string.Empty);
        if (full.Contains("://", StringComparison.Ordinal)) {
            var path = UrlUtil.GetPath(full);
            return (full, path == "/" ? string.Empty : path);
        }
        if (!full.StartsWith('/')) full = "/" + full;
        return (full, full);
    }
    /// <summary>Removes the base path from an inbound path. Null when the path is outside the base; "/" when it equals the base. The base must end on a segment boundary.</summary>
    protected static string? TryStripBasePath(string path, string basePath) {
        if (basePath.Length == 0) return path;
        if (!path.StartsWith(basePath, StringComparison.Ordinal)) return null;
        if (path.Length == basePath.Length) return "/";
        var rest = path[basePath.Length..];
        return rest.StartsWith('/') ? rest : null;
    }

    /// <summary>Query parameter carrying the signature of an asset URL whose target or adjustment is readable rather than inside the token.</summary>
    public const string SignatureParamName = "sig";

    /// <summary>True when <see cref="AssetUrlSignatureKey"/> is set, so asset URLs are signed and unsigned ones stop resolving.</summary>
    protected bool AssetUrlsAreSigned => AssetUrlSignatureKey != Guid.Empty;

    /// <summary>
    /// The signature of the readable parts of an asset URL: binds the token, the readable target and
    /// the readable adjustment together, so none of them can be edited or moved to another file.
    /// Null when signing is off. Emit it as the <see cref="SignatureParamName"/> query parameter.
    /// </summary>
    protected string? TrySignReadableAssetUrl(string? token, string? targetText, FileAdjustmentBase? adjustment) {
        if (!AssetUrlsAreSigned) return null;
        return computeSignature(readablePayload(token, targetText, adjustment));
    }
    /// <summary>
    /// Verifies the signature of an asset URL whose target or adjustment was read from the URL
    /// itself. True when signing is off. Pass exactly what <see cref="TrySignReadableAssetUrl"/>
    /// was given at render time - the unsigned token, the canonical target text and the adjustment.
    /// </summary>
    protected bool ValidateReadableAssetUrl(string completeUrl, string? token, string? targetText, FileAdjustmentBase? adjustment) {
        if (!AssetUrlsAreSigned) return true;
        var given = UrlUtil.GetQueryParameter(completeUrl, SignatureParamName);
        if (given == null) return false; // signing is on, so an unsigned readable URL does not resolve
        return fixedTimeEquals(given, computeSignature(readablePayload(token, targetText, adjustment)));
    }
    // the payload is built from canonical values rather than the raw URL, so the signature is
    // independent of query parameter order, of relative versus absolute form, of the base
    // addresses, of the cosmetic file name and of unrelated parameters
    static string readablePayload(string? token, string? targetText, FileAdjustmentBase? adjustment) {
        var adjustmentText = string.Empty;
        if (adjustment != null && FileAdjustmentUrlCodec.TryToShortString(adjustment, out var canonical)) adjustmentText = canonical;
        return (token ?? string.Empty) + "|" + (targetText ?? string.Empty) + "|" + adjustmentText;
    }

    /// <summary>Appends the HMAC signature to the token when <see cref="AssetUrlSignatureKey"/> is set, otherwise returns the token unchanged.</summary>
    protected string SignTokenIfConfigured(string token) {
        if (AssetUrlSignatureKey == Guid.Empty) return token;
        return token + "." + computeSignature(token);
    }
    /// <summary>The reverse of <see cref="SignTokenIfConfigured"/>: verifies and removes the signature. Null when signing is on and the signature is missing or wrong.</summary>
    protected string? ValidateAndStripSignature(string token) {
        if (AssetUrlSignatureKey == Guid.Empty) return token;
        var pos = token.LastIndexOf('.');
        if (pos <= 0 || pos == token.Length - 1) return null;
        var payload = token[..pos];
        var signature = token[(pos + 1)..];
        return fixedTimeEquals(signature, computeSignature(payload)) ? payload : null;
    }
    static bool fixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    string computeSignature(string payload) {
        using var hmac = new HMACSHA256(AssetUrlSignatureKey.ToByteArray());
        var hash = hmac.ComputeHash(Encoding.ASCII.GetBytes(payload));
        return B64.EncodeForUrl(hash[..16]); // 16 bytes is ample for URL guessing protection and keeps URLs short
    }

    /// <summary>The internal id of the node, or 0 when the node does not exist (yet).</summary>
    protected static int ResolveInternalId(IDataStore db, NodeKey node) {
        if (node.HasInt) return node.Int;
        if (node.HasGuid && db.TryGetNodeMeta(node.Guid, out var meta)) return meta.InternalId;
        return 0;
    }
    /// <summary>A file name reduced to URL safe characters, capped at 40 characters.</summary>
    protected static string UrlSafeFileName(string name) {
        var sb = new StringBuilder(Math.Min(name.Length, 40));
        foreach (var c in name) {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') sb.Append(c);
            else sb.Append('_');
            if (sb.Length >= 40) break;
        }
        return sb.ToString();
    }
}
