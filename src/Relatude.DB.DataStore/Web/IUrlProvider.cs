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
    string GetInternalUrl(string externalUrl);
    
    IdKey GetIdKey(string url);
    string GetInternalUrl(IdKey idKey);
    string GetInternalUrl(NodePath nodePath);
    string GetInternalUrl(PropertyPath property);
    string GetInternalUrl(PropertyPath property, FileAdjustmentBase adjustment);

    NodePath GetNodePath(string url);
    PropertyPath GetPropertyPath(string url);
    
    bool TryParseLocalUrlType(string internalUrl, out UrlTargetType type);

    bool TryParseAdjusted(string internalUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustmentBase adjustment);
    bool TryParseIdKey(string internalUrl, [MaybeNullWhen(false)] out IdKey idKey);
    bool TryParseNodePath(string internalUrl, [MaybeNullWhen(false)] out NodePath nodePath);
    bool TryParsePropertyPath(string internalUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath);
}


