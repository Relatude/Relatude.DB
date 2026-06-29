using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Web;

public class InternalUrlProvider : IUrlProvider {
    const char dot = '.';
    readonly IUrlFileAdjustmentEncoder _encoder;
    public InternalUrlProvider() {
        _encoder = new BinaryUrlFileAdjustmentEncoder(Guid.Empty);
    }
    char urlTypeChar(UrlType type) {
        return type switch {
            UrlType.LocalUrl => 'u',
            UrlType.LocalNode => 'n',
            UrlType.LocalEmbeddedNode => 'e',
            UrlType.LocalProperty => 'p',
            UrlType.LocalAdjusted => 'a',
            //UrlType.LocalOther => 'o',
            UrlType.RemoteUrl => 'U',
            UrlType.Email => 'M',
            //UrlType.ExternalOther => 'O',
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
            //case 'o': type = UrlType.LocalOther; return true;
            case 'U': type = UrlType.RemoteUrl; return true;
            case 'M': type = UrlType.Email; return true;
            //case 'O': type = UrlType.ExternalOther; return true;
            default: type = default; return false;
        }
    }
    public void Initialize(IDataStore dataStore) { }
    public bool IsUrlRelevant(string url) {
        return true;
    }
    public string GetUrl(NodeKey idKey, bool absolute) {
        return urlTypeChar(UrlType.LocalNode) + B64.EncodeForUrl(idKey.ToBytes());
    }
    public string GetUrl(NodePath nodePath, bool absolute) {
        return urlTypeChar(UrlType.LocalNode) + B64.EncodeForUrl(nodePath.ToBytes());
    }
    public string GetUrl(PropertyPath property, string? contentVersionId, bool absolute) {
        var url = urlTypeChar(UrlType.LocalProperty) + B64.EncodeForUrl(property.ToBytes());
        if (contentVersionId != null) url += dot + contentVersionId;
        return url;
    }
    public string GetUrl(PropertyPath property, FileAdjustment adjustment, string? contentVersionId, bool absolute) {
        var url = urlTypeChar(UrlType.LocalAdjusted) + B64.EncodeForUrl(property.ToBytes()) + dot + _encoder.GetEncodedString(adjustment);
        if (contentVersionId != null) url += dot + contentVersionId;
        return url;
    }
    public bool TryParseUrlType(string localUrl, out UrlType type) {
        if (string.IsNullOrEmpty(localUrl)) { type = default; return false; }
        return tryGetUrlType(localUrl[0], out type);
    }
    public bool TryParseUrlNodeKey(string localUrl, out NodeKey idKey) {
        idKey = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        if (!NodeKey.TryParse(localUrl[1..], out idKey)) return false;
        return true;
    }
    public bool TryParseUrlNodePath(string localUrl, [MaybeNullWhen(false)] out NodePath nodePath) {
        nodePath = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        if (!NodePath.TryParse(localUrl[1..], out nodePath)) return false;
        return true;
    }
    public bool TryParseUrlPropertyPath(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath) {
        propertyPath = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        var dotIndexLast = localUrl.LastIndexOf('.');
        if (dotIndexLast > 2) localUrl = localUrl.Remove(dotIndexLast); // remove content version id if present
        if (!B64.TryDecodeFromUrlParameter(localUrl[1..], out var bytes)) return false;
        if (!PropertyPath.TryParse(localUrl[1..], out propertyPath)) return false;
        return true;
    }
    public bool TryParseUrlAdjustments(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustment adjustment) {
        propertyPath = default;
        adjustment = default;
        if (string.IsNullOrWhiteSpace(localUrl) || localUrl.Length <= 2) return false;
        var dotIndex = localUrl.IndexOf('.');
        if (dotIndex < 2) return false;
        var dotIndexLast = localUrl.LastIndexOf('.');
        if (dotIndexLast > dotIndex) localUrl = localUrl.Remove(dotIndexLast); // remove content version id if present
        var propertyPart = localUrl[0..dotIndex];
        var adjustmentPart = localUrl[(dotIndex + 1)..];
        if (!TryParseUrlPropertyPath(propertyPart, out propertyPath)) return false;
        try {
            adjustment = _encoder.GetAdjustmentFromEncodedString(adjustmentPart);
            return true;
        } catch {
            return false;
        }
    }

}
