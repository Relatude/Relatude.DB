using WAF.Common;
using WAF.Datamodels;
using System.Text;

namespace WAF.Query;
public sealed class SearchResultHit<T> {
    public SearchResultHit(T node, double score, TextSample sample) {
        Node = node;
        Score = score;
        Sample = sample;
    }
    public T Node { get; }
    public TextSample Sample { get; }
    public double Score { get; }
}
