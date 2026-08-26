using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
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

    string _assetBase = string.Empty;     // "" | "/files" | "https://cdn.example.com/files", no trailing slash
    string _assetBasePath = string.Empty; // the path portion, used to match inbound URLs
    /// <summary>
    /// Base address prepended to every asset URL, outermost. May be a path ("/files") or include
    /// scheme and host ("https://cdn.example.com"), which makes asset URLs absolute - the classic
    /// CDN offload. Inbound URLs are matched by their path, so both absolute and relative requests
    /// resolve.
    /// </summary>
    public string? BaseAddressAssets {
        get => _assetBase.Length == 0 ? null : _assetBase;
        set => (_assetBase, _assetBasePath) = NormalizeBaseAddress(value);
    }

    public abstract void Initialize(IDataStore store);
    public abstract NodeKeyWithCulture[] GetMatches(string completeUrl);
    public abstract string? TryGetUrl(NodeMeta meta, bool absolute);
    public abstract bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address);

    public virtual string GetAssetUrl(AssetUrl asset, bool absolute) {
        var url = _assetBase + AssetUrlRoot + SignTokenIfConfigured(asset.Token);
        if (!string.IsNullOrEmpty(asset.FileName)) url += "/" + UrlSafeFileName(asset.FileName);
        return url;
    }
    public virtual string? TryGetAssetToken(string completeUrl) {
        var path = TryStripBasePath(UrlUtil.GetPath(completeUrl), _assetBasePath);
        if (path == null || !path.StartsWith(AssetUrlRoot, StringComparison.Ordinal)) return null;
        var start = AssetUrlRoot.Length;
        var end = start;
        while (end < path.Length && path[end] != '/') end++; // an optional cosmetic file name may follow the token
        if (end == start) return null;
        return ValidateAndStripSignature(path[start..end]);
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
        var expected = computeSignature(payload);
        var valid = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(expected));
        return valid ? payload : null;
    }
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
