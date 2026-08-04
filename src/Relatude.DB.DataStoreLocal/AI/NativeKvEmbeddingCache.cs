using Relatude.DB.Datastores.Indexes.BTreeIndex;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.AI;

public class NativeKvEmbeddingCache : IEmbeddingCache {
    readonly object _lock = new();
    readonly BPlusTreeStorageEngine _fileStorage;
    readonly ISortedIndex<ulong> _hashes;
    readonly ISortedIndex<byte[]> _embeddings;
    int _nextId;
    bool _disposed;
    public NativeKvEmbeddingCache(string filePath) {
        var options = new BPlusTreeEngineOptions() {
            //PageCacheBytes = 64L * 1024 * 1024 * 100, // 64 MB
            ValueCacheEntries = 10000,
        };
        _fileStorage = new BPlusTreeStorageEngine(filePath, options);
        _hashes = _fileStorage.OpenOrCreateIndex<ulong>("embedding-hashes");
        _embeddings = _fileStorage.OpenOrCreateIndex<byte[]>("embeddings");
        _nextId = _hashes.Keys.DefaultIfEmpty(-1).Max() + 1;
    }

    public void ClearAll() {
        lock (_lock) {
            throwIfDisposed();
            _fileStorage.DeleteAll();
            _nextId = 0;
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
            write(() => set(hash, embedding));
        }
    }

    public void SetMany(IEnumerable<Tuple<ulong, float[]>> values) {
        ArgumentNullException.ThrowIfNull(values);
        lock (_lock) {
            throwIfDisposed();
            write(() => {
                foreach (var (hash, embedding) in values) {
                    ArgumentNullException.ThrowIfNull(embedding);
                    set(hash, embedding);
                }
            });
        }
    }

    public bool TryGet(ulong hash, [MaybeNullWhen(false)] out float[] embedding) {
        lock (_lock) {
            throwIfDisposed();
            var id = getId(hash);
            if (id.HasValue && _embeddings.TryGetValue(id.Value, out var value)) {
                embedding = toFloats(value);
                return true;
            }
        }
        embedding = null;
        return false;
    }

    void set(ulong hash, float[] embedding) {
        var id = getId(hash) ?? _nextId++;
        _hashes.Set(id, hash);
        _embeddings.Set(id, toBytes(embedding));
    }

    int? getId(ulong hash) {
        foreach (var id in _hashes.GetIds(hash)) return id;
        return null;
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

