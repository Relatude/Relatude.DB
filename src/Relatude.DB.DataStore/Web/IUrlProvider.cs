using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.FileConverter;

namespace Relatude.DB.Web;

public class UrlProviderSettings {
    public Guid HaskKey { get; set; } = Guid.Empty;
    public string[]? LocalDomainPatterns { get; set; } = null;
    public bool EncryptAdjustments { get; set; } = true;
}
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
    /// <summary>
    /// Pattern matching for internal urls. If the url matches the pattern, it will be treated as an internal url and processed when stored and regenerated when retrieved. The pattern can contain wildcards * for matching. 
    /// </summary>
    /// <param name="url">url string with wildcards * for pattern matching</param>
    string GetUrl(FileIdWithAdjustment fileIdWithAdjustment);
    string GetUrl(INodeData node);
    UrlTargetType GetUrlTargetType(string url);
    FileIdWithAdjustment GetFileIdWithAdjustment(string url);
    IdKeyWithCultureId GetIdKeyWithCultureId(string url);
}


