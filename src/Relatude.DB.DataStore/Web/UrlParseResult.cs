using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.FileConversion;

namespace Relatude.DB.Web;

public class UrlParseResult {
    public NodeKey NodeKey { get; set; }
    public NodePath? NodePath { get; set; }
    public PropertyPath? PropertyPath { get; set; }
    public FileAdjustment? Adjustment { get; set; }
    public UrlType UrlType { get; set; }
}
public class UrlParseResultContent(UrlParseResult parseResult) {
    public UrlParseResult ParseResult { get; } = parseResult;
    public INodeDataExternal? NodeData { get; set; } = null;
    public bool? AsAttachment { get; set; }
    public string? ContentType { get; set; }
    public Stream? Stream { get; set; }
    public string? UsedFileName { get; set; }
    public FileFormat? FileFormat { get; set; }
    public FileValue? FileValue { get; set; }
    public bool? Cacheable { get; set; }
    public object? PropertyValue { get; set; }
}
