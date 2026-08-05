using Relatude.DB.Datastores.Indexes.BTreeIndex;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.AI;

public class NativeKvEmbeddingCache : IEmbeddingCache {
    readonly object _lock = new();
    readonly BPlusTreeStorageEngine _fileStorage;
    readonly ISortedUlongIndex<byte[]> _embeddings;
    bool _disposed;
    public NativeKvEmbeddingCache(string filePath) {
        var options = new BPlusTreeEngineOptions() {
            //PageCacheBytes = 64L * 1024 * 1024 * 100, // 64 MB
            //ValueCacheEntries = 10000,
        };
        _fileStorage = new BPlusTreeStorageEngine(filePath, options);
        try {
            _embeddings = _fileStorage.OpenOrCreateUlongIndex<byte[]>("embeddings");
        } catch (InvalidOperationException) {
            // the file uses an older cache layout; it is only a cache, so discard it and start fresh
            _fileStorage.DeleteAll();
            _embeddings = _fileStorage.OpenOrCreateUlongIndex<byte[]>("embeddings");
        }
    }

    public void ClearAll() {
        lock (_lock) {
            throwIfDisposed();
            _fileStorage.DeleteAll();
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _fileStorage.Dispose();
            _disposed = true;
        }
    }

    public void Set(ulong hash, float[] embedding) {
        ArgumentNullException.ThrowIfNull(embedding);
        lock (_lock) {
            throwIfDisposed();
            write(() => _embeddings.Set(hash, toBytes(embedding)));
        }
    }

    public void SetMany(IEnumerable<Tuple<ulong, float[]>> values) {
        ArgumentNullException.ThrowIfNull(values);
        lock (_lock) {
            throwIfDisposed();
            write(() => {
                foreach (var (hash, embedding) in values) {
                    ArgumentNullException.ThrowIfNull(embedding);
                    _embeddings.Set(hash, toBytes(embedding));
                }
            });
        }
    }

    public bool TryGet(ulong hash, [MaybeNullWhen(false)] out float[] embedding) {
        lock (_lock) {
            throwIfDisposed();
            if (_embeddings.TryGetValue(hash, out var value)) {
                embedding = toFloats(value);
                return true;
            }
        }
        embedding = null;
        return false;
    }

    void write(Action action) {
        _fileStorage.BeginTransaction();
        try {
            action();
            _fileStorage.CommitTransaction(DateTime.UtcNow.Ticks, false);
        } catch {
            if (_fileStorage.IsInTransaction) _fileStorage.RollbackTransaction();
            throw;
        }
    }

    static byte[] toBytes(float[] embedding) {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    static float[] toFloats(byte[] bytes) {
        var embedding = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
        return embedding;
    }

    void throwIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
