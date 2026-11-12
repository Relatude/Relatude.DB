using Relatude.DB.Datastores.Indexes.BTreeIndex;
using Relatude.DB.DataStores.Indexes.KvStore;

namespace Relatude.DB.Common;

/// <summary>
/// The Native KV file under a cache (AI embeddings, service answers). A cache is only a copy, so it
/// never keeps a database from opening: a file it cannot read is set aside and started empty, and a
/// file another process still holds leaves the cache in memory until the file is free, tried again
/// on use at most every <see cref="RetryInterval"/>. The owner holds its own lock around every call.
/// </summary>
internal sealed class NativeKvCacheFile<T> : IDisposable where T : class {
    public static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    readonly string? _filePath;
    readonly BPlusTreeEngineOptions _options;
    readonly string _name;
    readonly Func<BPlusTreeStorageEngine, T> _openIndex;
    DateTime _nextAttemptUtc = DateTime.MaxValue;

    public BPlusTreeStorageEngine Storage { get; private set; }
    public T Index { get; private set; }
    /// <summary>What the open had to do instead of the plain open, worth a warning in the database log; null when nothing.</summary>
    public string? Notice { get; private set; }
    /// <summary>True while the file is held elsewhere and the cache lives in memory.</summary>
    public bool WaitingForFile => _nextAttemptUtc != DateTime.MaxValue;

    public NativeKvCacheFile(string? filePath, BPlusTreeEngineOptions options, string name, TimeSpan? lockWait, Func<BPlusTreeStorageEngine, T> openIndex) {
        _filePath = filePath;
        _options = options;
        _name = name;
        _openIndex = openIndex;
        try {
            Storage = open(filePath, lockWait ?? DefaultLockWait, out var index, out var notice);
            Index = index;
            Notice = notice;
        } catch (Exception e) when (filePath != null && FileOpenRetry.IsSharingViolation(e)) {
            Storage = open(null, TimeSpan.Zero, out var index, out _);
            Index = index;
            Notice = "The " + name + " file is held by another process, so the cache is kept in memory until it is free. " + e.Message;
            _nextAttemptUtc = DateTime.UtcNow + RetryInterval;
        }
    }

    public void AttachFileIfDue() {
        if (DateTime.UtcNow < _nextAttemptUtc) return;
        try {
            var storage = open(_filePath, TimeSpan.Zero, out var index, out _);
            var inMemory = Storage;
            Storage = storage;
            Index = index;
            _nextAttemptUtc = DateTime.MaxValue;
            inMemory.Dispose();
        } catch (Exception e) when (FileOpenRetry.IsSharingViolation(e)) {
            _nextAttemptUtc = DateTime.UtcNow + RetryInterval;
        } catch {
            _nextAttemptUtc = DateTime.MaxValue; // it would fail the same way again
        }
    }

    BPlusTreeStorageEngine open(string? path, TimeSpan lockWait, out T index, out string? notice) {
        var options = new BPlusTreeEngineOptions {
            PageCacheBytes = _options.PageCacheBytes,
            PendingWriteBytes = _options.PendingWriteBytes,
            ValueCacheEntries = _options.ValueCacheEntries,
            FileLockTimeout = lockWait,
        };
        for (var attempt = 0; ; attempt++) {
            var storage = NativeKvFileRecovery.Open(path, options, "The " + _name, out notice);
            try {
                try {
                    index = _openIndex(storage);
                } catch (InvalidOperationException) {
                    storage.DeleteAll(); // an older cache layout
                    index = _openIndex(storage);
                }
                return storage;
            } catch (Exception e) when (attempt == 0 && path != null && NativeKvFileRecovery.IsCorruption(e)) {
                storage.Dispose();
                NativeKvFileRecovery.MarkCorrupt(path, e.GetType().Name + ": " + e.Message); // the next Open sets it aside
            } catch {
                storage.Dispose();
                throw;
            }
        }
    }

    public void Dispose() => Storage.Dispose();
}
