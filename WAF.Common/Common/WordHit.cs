namespace WAF.Common; 
readonly public struct WordHit {
    public WordHit(int nodeId, byte hits) {
        NodeId = nodeId;
        Hits = hits;
    }
    readonly public int NodeId;
    readonly public byte Hits;
    public static IEqualityComparer<WordHit> Comparer = new WordHitComparer();
}
class WordHitComparer : IEqualityComparer<WordHit> {
    public bool Equals(WordHit x, WordHit y) => x.NodeId == y.NodeId;
    public int GetHashCode(WordHit obj) => obj.NodeId.GetHashCode();
}