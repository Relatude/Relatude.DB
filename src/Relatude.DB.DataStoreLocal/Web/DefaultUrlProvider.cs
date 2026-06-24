using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Web;

public class DefaultUrlProvider : IUrlProvider {
    readonly IUrlFileAdjustmentEncoder _encoder;
    const char DELIMITER = '.';
    readonly string _urlFileRoot;
    readonly string _urlNodeRoot;
    public DefaultUrlProvider(Guid secretHashKey, string? urlFileRoot = "files", string? urlNodeRoot = null) {
        _urlFileRoot = urlFileRoot ?? string.Empty;
        _urlNodeRoot = urlNodeRoot ?? string.Empty;
        _encoder = new BinaryUrlFileAdjustmentEncoder(secretHashKey);
        if (string.IsNullOrWhiteSpace(_urlFileRoot)) throw new ArgumentException("URL root cannot be null or whitespace.", nameof(urlFileRoot));
        if (!_urlFileRoot.EndsWith("/")) _urlFileRoot += "/";
        if (!_urlFileRoot.StartsWith("/")) _urlFileRoot = "/" + _urlFileRoot;
    }

    char urlTypeChar(UrlType type) {
        return type switch {
            UrlType.LocalUrl => 'u',
            UrlType.LocalNode => 'n',
            UrlType.LocalEmbeddedNode => 'e',
            UrlType.LocalProperty => 'p',
            UrlType.LocalAdjusted => 'a',
            UrlType.LocalOther => 'o',
            UrlType.ExternalUrl => 'U',
            UrlType.ExternalEmail => 'M',
            UrlType.ExternalOther => 'O',
            _ => throw new NotImplementedException(),
        };
    }
    bool tryGetUrlType(char c, [MaybeNullWhen(false)] out UrlType type) {
        switch (c) {
            case 'u': type = UrlType.LocalUrl; return true;
            case 'n': type = UrlType.LocalNode; return true;
            case 'e': type = UrlType.LocalEmbeddedNode; return true;
            case 'p': type = UrlType.LocalProperty; return true;
            case 'a': type = UrlType.LocalAdjusted; return true;
            case 'o': type = UrlType.LocalOther; return true;
            case 'U': type = UrlType.ExternalUrl; return true;
            case 'M': type = UrlType.ExternalEmail; return true;
            case 'O': type = UrlType.ExternalOther; return true;
            default: type = default; return false;
        }
    }

    public string GetInternalUrl(IdKey idKey) {
        return urlTypeChar(UrlType.LocalUrl) + B64.EncodeForUrl(idKey.ToBytes());
    }
    public string GetInternalUrl(NodePath nodePath) {
        return urlTypeChar(UrlType.LocalNode) + B64.EncodeForUrl(nodePath.ToBytes());
    }
    public string GetInternalUrl(PropertyPath property, string? contentVersionId) {
        var url = urlTypeChar(UrlType.LocalProperty) + B64.EncodeForUrl(property.ToBytes());
        if (contentVersionId != null) url += DELIMITER + contentVersionId;
        return url;
    }
    public string GetInternalUrl(PropertyPath property, FileAdjustment adjustment, string? contentVersionId) {
        var url = urlTypeChar(UrlType.LocalAdjusted) + B64.EncodeForUrl(property.ToBytes()) + DELIMITER + _encoder.GetEncodedString(adjustment);
        if (contentVersionId != null) url += DELIMITER + contentVersionId;
        return url;
    }

    public string GetExternalUrl(string internalUrl, bool absolute) {
        if (absolute) throw new NotImplementedException();
        return _urlFileRoot + internalUrl;
    }
    public string GetExternalUrl(NodePath nodePath, bool absolute) {
        var internalUrl = GetInternalUrl(nodePath);
        return GetExternalUrl(internalUrl, absolute);
    }
    public string GetInternalUrl(string externalUrl) {
        if (externalUrl == null) return string.Empty;
        string internalUrl = externalUrl;

        if (!internalUrl.StartsWith("/")) internalUrl = "/" + internalUrl;
        if (internalUrl.StartsWith(_urlFileRoot, StringComparison.OrdinalIgnoreCase))
            return internalUrl[(_urlFileRoot.Length + 1)..];

        return externalUrl;

        // TODO: implement a way to resolve a shorter, and more readable url. No need for guids, use prop names and node addresses
        // External URL are constantly being generated on the fly, and need to be less permanent, 
        // Internal URLs are more permanent, as they are stored in HTML fields, that are transformed to external URLs on the fly

        //if (_dataStore.TryGetNodeIdFromAddress(pathWithoutQuery, out int id, out string? cultureCode)) {
        //} else {
        //}
    }

    public bool TryParseInternalForUrlType(string localUrl, out UrlType type) {
        if (string.IsNullOrEmpty(localUrl)) { type = default; return false; }
        return tryGetUrlType(localUrl[0], out type);
    }
    public bool TryParseInternalUrlForIdKey(string localUrl, out IdKey idKey) {
        idKey = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        if (!IdKey.TryParse(localUrl[1..], out idKey)) return false;
        return true;
    }
    public bool TryParseInternalUrlForNodePath(string localUrl, [MaybeNullWhen(false)] out NodePath nodePath) {
        nodePath = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        if (!NodePath.TryParse(localUrl[1..], out nodePath)) return false;
        return true;
    }
    public bool TryParseInternalUrlForPropertyPath(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath) {
        propertyPath = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        var dotIndexLast = localUrl.LastIndexOf('.');
        if (dotIndexLast > 2) localUrl = localUrl.Remove(dotIndexLast); // remove content version id if present
        if (!B64.TryDecodeFromUrlParameter(localUrl[1..], out var bytes)) return false;
        if (!PropertyPath.TryParse(localUrl[1..], out propertyPath)) return false;
        return true;
    }
    public bool TryParseInternalUrlForPathWithFileAdjustments(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustment adjustment) {
        propertyPath = default;
        adjustment = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        var dotIndex = localUrl.IndexOf('.');
        if (dotIndex < 2) return false;
        var dotIndexLast = localUrl.LastIndexOf('.');
        if (dotIndexLast > dotIndex) localUrl = localUrl.Remove(dotIndexLast); // remove content version id if present
        var propertyPart = localUrl[0..dotIndex];
        var adjustmentPart = localUrl[(dotIndex + 1)..];
        if (!TryParseInternalUrlForPropertyPath(propertyPart, out propertyPath)) return false;
        try {
            adjustment = _encoder.GetAdjustmentFromEncodedString(adjustmentPart);
            return true;
        } catch {
            return false;
        }
    }

}

