using Relatude.DB.Datamodels;
using System.Text;
using Relatude.DB.Common;

namespace Relatude.DB.Query.Data;
public sealed class SearchResultHitData {
    public SearchResultHitData(INodeData node, double score, TextSample sample) {
        NodeData = node;
        Score = score;
        Sample = sample;
    }
    public INodeData NodeData { get; }
    public TextSample Sample { get; }
    public double Score { get; }
}

