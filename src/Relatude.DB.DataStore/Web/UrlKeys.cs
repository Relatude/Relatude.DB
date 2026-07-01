using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.FileConversion;

namespace Relatude.DB.Web;

public class UrlKeys {
    public UrlTarget Target { get; set; }
    public NodeKey NodeKey { get; set; }
    public NodePath? NodePath { get; set; }
    public PropertyPath? PropertyPath { get; set; }
    public FileAdjustment? Adjustment { get; set; }
}
public class UrlContent(UrlKeys keys) {
    public UrlKeys Id { get; } = keys;
    public INodeDataExternal? NodeData { get; set; } = null;
    public bool? Attachment { get; set; }
    public string? ContentType { get; set; }
    public Stream? Stream { get; set; }
    public string? FileName { get; set; }
    public FileFormat? FileFormat { get; set; }
    public FileValue? FileValue { get; set; }
    public bool? Cacheable { get; set; }
    public object? PropertyValue { get; set; }
}
