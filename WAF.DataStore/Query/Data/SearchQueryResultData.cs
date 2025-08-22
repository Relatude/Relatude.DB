using WAF.Datamodels;
using System.Text;
using WAF.Common;

namespace WAF.Query.Data;
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

