namespace WAF.Common; 
readonly public struct SearchHit {
    public SearchHit(int nodeId, double score) {
        NodeId = nodeId;
        Score = score;
    }
    readonly public int NodeId;
    readonly public double Score;
}