using System.Text;
namespace Relatude.DB.Common;

public class TextExtract(int nodeId, string text, Guid? revisionId) {
    public int NodeId { get; } = nodeId;
    public string Text { get; } = text;
    public Guid? RevisionId { get; } = revisionId;
}

public enum TextIndexType { 
    PlainTextSearch,
    SemanticTextSearch,
}