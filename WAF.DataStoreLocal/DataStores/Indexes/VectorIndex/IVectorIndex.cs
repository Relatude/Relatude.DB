using WAF.IO;

namespace WAF.DataStores.Indexes.VectorIndex {
    public interface IVectorIndex {
        void Clear(int nodeId);
        void Set(int nodeId, float[] vectorsForEachParagraph);
        List<VectorHit> Search(float[] u, int skip, int take, float minVectorDistance);
        void ReadState(IReadStream stream);
        void SaveState(IAppendStream stream);
        void CompressMemory();
    }
}