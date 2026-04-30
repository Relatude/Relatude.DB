using Relatude.DB.Datamodels;
using System.Text;
using Relatude.DB.Common;

namespace Relatude.DB.Query.Data;
public sealed class SearchResultHitData(INodeDataExternal node, double score, TextSample sample) {
    public INodeDataExternal NodeData { get; } = node;
    public TextSample Sample { get; } = sample;
    public double Score { get; } = score;
}

