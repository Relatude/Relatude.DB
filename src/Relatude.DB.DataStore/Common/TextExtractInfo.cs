using System.Text;
namespace Relatude.DB.Common;

public class TextExtractInfo(int nodeId, TextExtract[] textExtract) {
    public int NodeId { get; } = nodeId;
    public TextExtract[] TextExtract { get; } = textExtract;
}
public class TextExtract(string text, Guid cultureId) {
    public string Text { get; } = text;
    public Guid CultureId { get; } = cultureId;
}
