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
    char urlTypeChar(UrlTarget target) {
        return target switch {
            UrlTarget.Node => 'n',
            UrlTarget.EmbeddedNode => 'e',
            UrlTarget.Property => 'p',
            UrlTarget.PropertyAdjusted => 'a',
            _ => throw new NotImplementedException(),
        };
    }
    bool tryGetUrlType(char c, [MaybeNullWhen(false)] out UrlTarget target) {
        switch (c) {
            case 'n': target = UrlTarget.Node; return true;
            case 'e': target = UrlTarget.EmbeddedNode; return true;
            case 'p': target = UrlTarget.Property; return true;
            case 'a': target = UrlTarget.PropertyAdjusted; return true;
            default: target = default; return false;
        }
    }
    public void Initialize(IDataStore dataStore) { }
    public string GetUrl(NodeKey idKey, bool absolute) {
        return urlTypeChar(UrlTarget.Node) + B64.EncodeForUrl(idKey.ToBytes());
    }
    public string GetUrl(NodePath nodePath, bool absolute) {
        return urlTypeChar(UrlTarget.EmbeddedNode) + B64.EncodeForUrl(nodePath.ToBytes());
    }
    public string GetUrl(PropertyPath property, string? contentVersionId, bool absolute) {
        var url = urlTypeChar(UrlTarget.Property) + B64.EncodeForUrl(property.ToBytes());
        if (contentVersionId != null) url += dot + contentVersionId;
        return url;
    }
    public string GetUrl(PropertyPath property, FileAdjustment adjustment, string? contentVersionId, bool absolute) {
        var url = urlTypeChar(UrlTarget.PropertyAdjusted) + B64.EncodeForUrl(property.ToBytes()) + dot + _encoder.GetEncodedString(adjustment);
        if (contentVersionId != null) url += dot + contentVersionId;
        return url;
    }
    public bool TryParseUrlTarget(string localUrl, out UrlTarget target) {
        if (string.IsNullOrEmpty(localUrl)) { target = default; return false; }
        return tryGetUrlType(localUrl[0], out target);
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
