using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.FileConverter;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Web;

public enum UrlTargetType {

    LocalUrl,
    LocalNode,
    LocalEmbeddedNode,
    LocalProperty,
    LocalAdjusted,
    LocalEmail,
    LocalOther,

    ExternalUrl,
    ExternalEmail,
    ExternalOther,

}
public struct UrlBase {
    public UrlTargetType TargetType { get; init; }
}
public interface IUrlProvider {
    string GetExternalUrl(string internalUrl, bool absolute);
    IdKey GetIdKey(string url);
    string GetInternalUrl(IdKey idKey);
    string GetInternalUrl(NodePath nodePath);
    string GetInternalUrl(PropertyPath property);
    string GetInternalUrl(PropertyPath property, FileAdjustmentBase adjustment);
    string GetInternalUrl(string externalUrl);
    NodePath GetNodePath(string url);
    PropertyPath GetPropertyPath(string url);
    bool TryParseAdjusted(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustmentBase adjustment);
    bool TryParseIdKey(string localUrl, [MaybeNullWhen(false)] out IdKey idKey);
    bool TryParseLocalUrlType(string localUrl, out UrlTargetType type);
    bool TryParseNodePath(string localUrl, [MaybeNullWhen(false)] out NodePath nodePath);
    bool TryParsePropertyPath(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath);
}


