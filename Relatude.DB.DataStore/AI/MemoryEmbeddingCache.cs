using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Common;

namespace Relatude.DB.AI;
public class MemoryEmbeddingCache : IEmbeddingCache {
    readonly Cache<ulong, float[]> _cache;
    public MemoryEmbeddingCache(int size) {
        _cache = new(size);
    }
    public void Set(ulong hash, float[] embedding) {
        _cache.Set(hash, embedding, 1);
    }
    public void SetMany(IEnumerable<Tuple<ulong, float[]>> values) {
        foreach (var (hash, embedding) in values) {
            _cache.Set(hash, embedding, 1);
        }
    }
    public bool TryGet(ulong hash, [MaybeNullWhen(false)]out float[] embedding) {
        return _cache.TryGet(hash, out embedding);
    }
    public void ClearAll() {
        _cache.ClearAll_NotSize0();
    }
    public void Dispose() {
        _cache.ClearAll_NotSize0();
    }
}
