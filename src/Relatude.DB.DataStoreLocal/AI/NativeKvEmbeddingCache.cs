using Relatude.DB.Common;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.AI;

public class NativeKvEmbeddingCache : IEmbeddingCache {
    readonly object _lock = new();
    readonly BPlusTreeStorageEngine _fileStorage;
    readonly ISortedUlongIndex<byte[]> _embeddings;
    readonly Cache<ulong, float[]> _cache = new(1000);
    bool _disposed;
    public NativeKvEmbeddingCache(string filePath) {
        var options = new BPlusTreeEngineOptions() {
            PageCacheBytes = 2L * 1024 * 1024 * 100, // 2 MB
            PendingWriteBytes = 4L * 1024 * 1024, // 4 MB
            ValueCacheEntries = 0,
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
            _cache.ClearAll_NotSize0();
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
            _cache.Set(hash, embedding, 1);
        }
    }

    public void SetMany(IEnumerable<Tuple<ulong, float[]>> values) {
        ArgumentNullException.ThrowIfNull(values);
        lock (_lock) {
            throwIfDisposed();
            write(() => {
                foreach (var (hash, embedding) in values) {
                    _embeddings.Set(hash, toBytes(embedding));
                }
            });
            foreach (var (hash, embedding) in values) _cache.Set(hash, embedding, 1);
        }
    }

    public bool TryGet(ulong hash, [MaybeNullWhen(false)] out float[] embedding) {
        lock (_lock) {
            throwIfDisposed();
            if (_cache.TryGet(hash, out embedding)) return true;
            if (_embeddings.TryGetValue(hash, out var value)) {
                embedding = toFloats(value);
                _cache.Set(hash, embedding, 1);
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
            _fileStorage.MakeDurable(true);
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
