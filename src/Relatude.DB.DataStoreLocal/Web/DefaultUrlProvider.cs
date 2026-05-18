using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.FileConverter;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Web;


public class DefaultUrlProvider : IUrlProvider {
    readonly UrlFileAdjustmentEncoder _encoder;
    const char dotChar = '.';
    readonly IDataStore _dataStore;
    IdKey _masterDefaultIdKey;
    Dictionary<string, IdKey> _knownInternalHosts = new(StringComparer.OrdinalIgnoreCase);
    public DefaultUrlProvider(IDataStore dataStore, Guid secretHashKey) {
        _dataStore = dataStore;
        _encoder = new(secretHashKey);
    }
    public IdKey GetIdKey(string url) {
        throw new NotImplementedException();
    }
    public NodePath GetNodePath(string url) {
        throw new NotImplementedException();
    }

    public PropertyPath GetPropertyPath(string url) {
        throw new NotImplementedException();
    }
    char getUrlTypeChar(UrlTargetType type) {
        return type switch {
            UrlTargetType.LocalUrl => 'u',
            UrlTargetType.LocalNode => 'n',
            UrlTargetType.LocalEmbeddedNode => 'e',
            UrlTargetType.LocalProperty => 'p',
            UrlTargetType.LocalAdjusted => 'a',
            UrlTargetType.LocalEmail => 'm',
            UrlTargetType.LocalOther => 'o',
            UrlTargetType.ExternalUrl => 'U',
            UrlTargetType.ExternalEmail => 'M',
            UrlTargetType.ExternalOther => 'O',
            _ => throw new NotImplementedException(),
        };
    }
    UrlTargetType getUrlTypeFromChar(char c) {
        return c switch {
            'u' => UrlTargetType.LocalUrl,
            'n' => UrlTargetType.LocalNode,
            'e' => UrlTargetType.LocalEmbeddedNode,
            'p' => UrlTargetType.LocalProperty,
            'a' => UrlTargetType.LocalAdjusted,
            'm' => UrlTargetType.LocalEmail,
            'o' => UrlTargetType.LocalOther,
            'U' => UrlTargetType.ExternalUrl,
            'M' => UrlTargetType.ExternalEmail,
            'O' => UrlTargetType.ExternalOther,
            _ => throw new NotImplementedException(),
        };
    }

    public string GetInternalUrl(IdKey idKey) {
        return getUrlTypeChar(UrlTargetType.LocalUrl) + B64.EncodeForUrlParameter(idKey.ToBytes());
    }
    public string GetInternalUrl(NodePath nodePath) {
        return getUrlTypeChar(UrlTargetType.LocalNode) + B64.EncodeForUrlParameter(nodePath.ToBytes());
    }
    public string GetInternalUrl(PropertyPath property) {
        return getUrlTypeChar(UrlTargetType.LocalProperty) + B64.EncodeForUrlParameter(property.ToBytes());
    }
    public string GetInternalUrl(PropertyPath property, FileAdjustmentBase adjustment) {
        return string.Concat(getUrlTypeChar(UrlTargetType.LocalAdjusted), B64.EncodeForUrlParameter(property.ToBytes()), dotChar, _encoder.GetEncodedString(adjustment));
    }

    public string GetExternalUrl(string internalUrl, bool absolute) {
        if (absolute) throw new NotImplementedException();
        return internalUrl;
    }
    public string GetInternalUrl(string externalUrl) {
        return externalUrl;
        throw new NotImplementedException();
        Uri uri = new Uri(externalUrl);
        string pathWithoutQuery = uri.GetLeftPart(UriPartial.Path);
        if (pathWithoutQuery == "/") pathWithoutQuery = ""; // treat root path as empty for easier matching
        if (uri.IsAbsoluteUri) { // has scheme and authority, potentially external url
            var host = uri.Host;
            if (!_knownInternalHosts.ContainsKey(host)) return externalUrl; // external url, return as is
        } else {
            // basically "/" or "" and no host            
            if (string.IsNullOrEmpty(pathWithoutQuery)) return GetInternalUrl(_masterDefaultIdKey);
        }
        if (_dataStore.TryGetNodeIdFromAddress(pathWithoutQuery, out int id, out string? cultureCode)) {

        } else {

        }
    }

    public bool TryParseLocalUrlType(string localUrl, out UrlTargetType type) {
        type = default;
        if (string.IsNullOrEmpty(localUrl)) return false;
        try {
            type = getUrlTypeFromChar(localUrl[0]);
            return true;
        } catch {
            return false;
        }
    }
    public bool TryParseIdKey(string localUrl, [MaybeNullWhen(false)] out IdKey idKey) {
        idKey = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        if (!B64.TryDecodeFromUrlParameter(localUrl[1..], out var bytes)) return false;
        if (!IdKey.TryParse(localUrl[1..], out idKey)) return false;
        return true;
    }
    public bool TryParseNodePath(string localUrl, [MaybeNullWhen(false)] out NodePath nodePath) {
        nodePath = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        if (!B64.TryDecodeFromUrlParameter(localUrl[1..], out var bytes)) return false;
        if (!NodePath.TryParse(localUrl[1..], out nodePath)) return false;
        return true;
    }
    public bool TryParsePropertyPath(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath) {
        propertyPath = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        if (!B64.TryDecodeFromUrlParameter(localUrl[1..], out var bytes)) return false;
        if (!PropertyPath.TryParse(localUrl[1..], out propertyPath)) return false;
        return true;
    }
    public bool TryParseAdjusted(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustmentBase adjustment) {
        propertyPath = default;
        adjustment = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        var dotIndex = localUrl.IndexOf('.');
        if (dotIndex < 2) return false;
        var propertyPart = localUrl[0..dotIndex];
        var adjustmentPart = localUrl[(dotIndex + 1)..];
        if (!TryParsePropertyPath(propertyPart, out propertyPath)) return false;
        try {
            adjustment = _encoder.GetAdjustmentFromEncodedString(adjustmentPart);
            return true;
        } catch {
            return false;
        }
    }

}



















