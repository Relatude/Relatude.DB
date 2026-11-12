using Relatude.DB.Common;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.AI;

public class NativeKvEmbeddingCache : IEmbeddingCache {
    readonly object _lock = new();
    readonly NativeKvCacheFile<IUlongIndex<float[]>> _file;
    readonly Cache<ulong, float[]> _cache;
    bool _disposed;
    /// <param name="filePath">The cache file; null keeps the cache in memory only.</param>
    /// <param name="lockWait">How long the open waits while another process holds the file before the
    /// cache starts in memory and takes the file once it is free.</param>
    public NativeKvEmbeddingCache(string? filePath, TimeSpan? lockWait = null) {
        _file = new(filePath, new() {
            PageCacheBytes = 2L * 1024 * 1024, // 2 MB
            PendingWriteBytes = 4L * 1024 * 1024, // 4 MB
            ValueCacheEntries = 0,
        }, "AI embedding cache", lockWait, storage => storage.OpenOrCreateUlongHashIndex<float[]>("embeddings"));
        _cache = new Cache<ulong, float[]>(1000); // 1000 entries, ~4 MB for 128-dim embeddings
    }

    /// <summary>What the open had to do instead of the plain open (a file set aside, or held elsewhere); null when nothing.</summary>
    public string? OpenNotice => _file.Notice;

    public void ClearAll() {
        lock (_lock) {
            throwIfDisposed();
            _file.AttachFileIfDue();
            _file.Storage.DeleteAll();
            _cache.ClearAll_NotSize0();
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            _file.Dispose();
        }
    }

    public void Set(ulong hash, float[] embedding) {
        ArgumentNullException.ThrowIfNull(embedding);
        lock (_lock) {
            throwIfDisposed();
            _file.AttachFileIfDue();
            write(() => _file.Index.Set(hash, embedding));
            _cache.Set(hash, embedding, 1);
        }
    }

    public void SetMany(IEnumerable<Tuple<ulong, float[]>> values) {
        ArgumentNullException.ThrowIfNull(values);
        lock (_lock) {
            throwIfDisposed();
            _file.AttachFileIfDue();
            write(() => {
                foreach (var (hash, embedding) in values) {
                    _file.Index.Set(hash, embedding);
                }
            });
            foreach (var (hash, embedding) in values) _cache.Set(hash, embedding, 1);
        }
    }

    public bool TryGet(ulong hash, [MaybeNullWhen(false)] out float[] embedding) {
        lock (_lock) {
            throwIfDisposed();
            if (_cache.TryGet(hash, out embedding)) return true;
            _file.AttachFileIfDue();
            if (_file.Index.TryGetValue(hash, out var value)) {
                embedding = value;
                _cache.Set(hash, embedding, 1);
                return true;
            }
        }
        embedding = null;
        return false;
    }

    void write(Action action) {
        var storage = _file.Storage;
        storage.BeginTransaction();
        try {
            action();
            storage.CommitTransaction(DateTime.UtcNow.Ticks, false);
            storage.MakeDurable(true);
        } catch {
            if (storage.IsInTransaction) storage.RollbackTransaction();
            throw;
        }
    }
    void throwIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
