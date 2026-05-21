using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Web;

public class DefaultUrlProvider : IUrlProvider {
    readonly UrlFileAdjustmentEncoder _encoder;
    const char DELIMITER = '.';
    readonly IDataStore _dataStore;
    public DefaultUrlProvider(IDataStore dataStore, Guid secretHashKey) {
        _dataStore = dataStore;
        _encoder = new(secretHashKey);
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

    public string GetInternalUrl(IdKey idKey) => urlTypeChar(UrlType.LocalUrl) + B64.EncodeForUrl(idKey.ToBytes());
    public string GetInternalUrl(NodePath nodePath) => urlTypeChar(UrlType.LocalNode) + B64.EncodeForUrl(nodePath.ToBytes());
    public string GetInternalUrl(PropertyPath property) => urlTypeChar(UrlType.LocalProperty) + B64.EncodeForUrl(property.ToBytes());
    public string GetInternalUrl(PropertyPath property, FileAdjustmentBase adjustment) =>
        string.Concat(urlTypeChar(UrlType.LocalAdjusted), B64.EncodeForUrl(property.ToBytes()), DELIMITER, _encoder.GetEncodedString(adjustment));

    public string GetExternalUrl(string internalUrl, bool absolute) {
        if (absolute) throw new NotImplementedException();
        return internalUrl;
    }
    public string GetInternalUrl(string externalUrl) {
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
        if (!PropertyPath.TryParse(localUrl[1..], out propertyPath)) return false;
        return true;
    }
    public bool TryParseInternalUrlForPathWithFileAdjustments(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustmentBase adjustment) {
        propertyPath = default;
        adjustment = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        var dotIndex = localUrl.IndexOf('.');
        if (dotIndex < 2) return false;
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



















