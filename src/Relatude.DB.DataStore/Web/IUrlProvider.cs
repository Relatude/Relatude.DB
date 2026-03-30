using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.FileConverter;

namespace Relatude.DB.Web;

public enum UrlTargetType {

    LocalUrl,
    LocalNode,
    LocalFile,
    LocalAdjustedFile,
    LocalEmail,

    ExternalUrl,
    ExternalEmail,

}
public struct UrlBase {
    public UrlTargetType TargetType { get; init; }
}
public interface IUrlProvider {
    void RegisterlUrlAsInternal(string url);
    string GetUrl(FileIdWithAdjustment fileIdWithAdjustment);
    string GetUrl(INodeData node);
    UrlTargetType GetUrlTargetType(string url);
    FileIdWithAdjustment GetFileIdWithAdjustment(string url);
    IdKeyWithCultureId GetIdKeyWithCultureId(string url);
}
public interface DefaultUrlProvider : IUrlProvider {
    
}