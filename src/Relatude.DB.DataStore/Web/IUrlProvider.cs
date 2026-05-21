using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.FileConversion;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Web;

public enum UrlType {

    LocalUrl,
    LocalNode,
    LocalEmbeddedNode,
    LocalProperty,
    LocalAdjusted,
    LocalOther,

    ExternalUrl,
    ExternalEmail,
    ExternalOther,

}
public struct UrlBase {
    public UrlType TargetType { get; init; }
}
public interface IUrlProvider {

    string GetExternalUrl(string internalUrl, bool absolute);
    string GetInternalUrl(string externalUrl);
    
    string GetInternalUrl(IdKey idKey);
    string GetInternalUrl(NodePath nodePath);
    string GetInternalUrl(PropertyPath property, string? contentVersionId);
    string GetInternalUrl(PropertyPath property, FileAdjustmentBase adjustment, string? contentVersionId);

    bool TryParseInternalForUrlType(string internalUrl, out UrlType type);

    bool TryParseInternalUrlForPathWithFileAdjustments(string internalUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustmentBase adjustment);
    bool TryParseInternalUrlForIdKey(string internalUrl, [MaybeNullWhen(false)] out IdKey idKey);
    bool TryParseInternalUrlForNodePath(string internalUrl, [MaybeNullWhen(false)] out NodePath nodePath);
    bool TryParseInternalUrlForPropertyPath(string internalUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath);
}


