using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Web;

public enum UrlTarget {
    Node,
    EmbeddedNode,
    Property,
    PropertyAdjusted,
}

public enum UrlType {

    LocalNode,
    LocalEmbeddedNode,
    LocalProperty,
    LocalPropertyAdjusted,

    LocalUrl, // any url not pointing to a node or property, but still a local url, like a custom controller or static file
    RemoteUrl,
    Email,

}
public struct UrlBase {
    public UrlType TargetType { get; init; }
}
public interface IUrlProvider {

    void Initialize(IDataStore dataStore);

    string GetUrl(NodeKey nodeKey, bool absolute);
    string GetUrl(NodePath nodePath, bool absolute);
    string GetUrl(PropertyPath property, string? contentVersionId, bool absolute);
    string GetUrl(PropertyPath property, FileAdjustment adjustment, string? contentVersionId, bool absolute);

    bool TryParseUrlTarget(string url, out UrlTarget target);
    bool TryParseUrlAdjustments(string url, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustment adjustment);
    bool TryParseUrlNodeKey(string url, [MaybeNullWhen(false)] out NodeKey nodeKey);
    bool TryParseUrlNodePath(string url, [MaybeNullWhen(false)] out NodePath nodePath);
    bool TryParseUrlPropertyPath(string url, [MaybeNullWhen(false)] out PropertyPath propertyPath);

}


