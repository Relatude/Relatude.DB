namespace Relatude.DB.DataStores.Indexes.VectorIndex;
public struct VectorHit(int nodeId, float similarity) {
    public int NodeId = nodeId;
    public float Similarity = similarity;
}
