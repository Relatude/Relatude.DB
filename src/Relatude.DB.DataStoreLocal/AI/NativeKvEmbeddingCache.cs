using Relatude.DB.Common;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.AI;

public class NativeKvEmbeddingCache : IEmbeddingCache {
    readonly object _lock = new();
    readonly BPlusTreeStorageEngine _storage;
    readonly IUlongIndex<float[]> _embeddings;
    readonly Cache<ulong, float[]> _cache;
    bool _disposed;
    public NativeKvEmbeddingCache(string? filePath) {        
        // if filePath is null, the cache will be in-memory only
        _storage = new (filePath, new () {
            PageCacheBytes = 2L * 1024 * 1024, // 2 MB
            PendingWriteBytes = 4L * 1024 * 1024, // 4 MB
            ValueCacheEntries = 0,
        });
        _cache = new Cache<ulong, float[]>(1000); // 1000 entries, ~4 MB for 128-dim embeddings
        try {
            _embeddings = _storage.OpenOrCreateUlongHashIndex<float[]>("embeddings");
        } catch (InvalidOperationException) {
            // the file uses an older cache layout; it is only a cache, so discard it and start fresh
            _storage.DeleteAll();
            _embeddings = _storage.OpenOrCreateUlongHashIndex<float[]>("embeddings");
        }
    }

    public void ClearAll() {
        lock (_lock) {
            throwIfDisposed();
            _storage.DeleteAll();
            _cache.ClearAll_NotSize0();
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _storage.Dispose();
            _disposed = true;
        }
    }

    public void Set(ulong hash, float[] embedding) {
        ArgumentNullException.ThrowIfNull(embedding);
        lock (_lock) {
            throwIfDisposed();
            write(() => _embeddings.Set(hash, embedding));
            _cache.Set(hash, embedding, 1);
        }
    }

    public void SetMany(IEnumerable<Tuple<ulong, float[]>> values) {
        ArgumentNullException.ThrowIfNull(values);
        lock (_lock) {
            throwIfDisposed();
            write(() => {
                foreach (var (hash, embedding) in values) {
                    _embeddings.Set(hash, embedding);
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
                embedding = value;
                _cache.Set(hash, embedding, 1);
                return true;
            }
        }
        embedding = null;
        return false;
    }

    void write(Action action) {
        _storage.BeginTransaction();
        try {
            action();
            _storage.CommitTransaction(DateTime.UtcNow.Ticks, false);
            _storage.MakeDurable(true);
        } catch {
            if (_storage.IsInTransaction) _storage.RollbackTransaction();
            throw;
        }
    }
    void throwIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
