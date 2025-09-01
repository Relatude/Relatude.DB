using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.AI;
public interface IEmbeddingCache : IDisposable {
    public void Set(ulong hash, float[] embedding);
    void SetMany(IEnumerable<Tuple<ulong, float[]>> values);
    public bool TryGet(ulong hash, [MaybeNullWhen(false)] out float[] embedding);
    public void ClearAll();
}
