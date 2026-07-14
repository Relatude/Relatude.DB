using KvStore.Serialization;

namespace KvStore;

/// <summary>
/// An embedded, crash-safe database file holding any number of named key/value stores (tables),
/// each with its own key/value types — like tables in a database. Obtain typed stores with
/// <see cref="GetStore{TKey,TValue}(string)"/>; every store shares this file's write-ahead log,
/// page cache, durability mode, and locks.
///
/// <para><b>Write units.</b> Each store's <c>Put</c>/<c>Delete</c>/<c>Batch</c> is an atomic
/// auto-commit batch. To group writes across stores — with reads that see the uncommitted state —
/// open a file-wide <b>transaction</b>: <see cref="StartTransaction"/>, then any operations on
/// any stores from the same thread, then <see cref="CommitTransaction"/> (stamping your
/// timestamp) or <see cref="CancelTransaction"/>. One transaction at a time: other threads'
/// reads and writes block until it ends. <see cref="CommitTimestamp"/> returns the last committed
/// transaction's timestamp, persisted crash-safely with the data — read it after
/// <see cref="Open"/> to learn where a previous run left off.</para>
///
/// <para>For the common single-table case, <see cref="Database"/> and
/// <see cref="Database{TKey,TValue}"/> remain as thin single-store faces over the same engine.</para>
/// </summary>
public sealed class DatabaseFile : IDisposable
{
    private readonly StorageEngine _engine;

    // One face per store name; the same instance is returned for repeated GetStore calls, and a
    // second call with different type arguments is rejected rather than silently misreading data.
    private readonly Dictionary<string, object> _stores = new();
    private readonly object _storesGate = new();
    private bool _disposed;

    private DatabaseFile(StorageEngine engine) => _engine = engine;

    /// <summary>Opens (creating if necessary) the database file at <paramref name="path"/>.</summary>
    public static DatabaseFile Open(string path, DatabaseOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new DatabaseFile(StorageEngine.Open(path, options ?? DatabaseOptions.Default));
    }

    /// <summary>
    /// Returns the named store using built-in codecs for its types, creating it (lazily — an
    /// empty store costs nothing on disk) if it doesn't exist. Repeated calls return the same
    /// instance; a store must always be opened with the same key/value types and codecs.
    /// </summary>
    public KeyValueStore<TKey, TValue> GetStore<TKey, TValue>(string name)
        => GetStore(name, Codecs.For<TKey>(), Codecs.For<TValue>());

    /// <summary>Returns the named store with explicit codecs (for custom key/value types).</summary>
    public KeyValueStore<TKey, TValue> GetStore<TKey, TValue>(
        string name, IKeyCodec<TKey> keyCodec, IValueCodec<TValue> valueCodec)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(keyCodec);
        ArgumentNullException.ThrowIfNull(valueCodec);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_storesGate)
        {
            if (_stores.TryGetValue(name, out var cached))
                return cached as KeyValueStore<TKey, TValue>
                    ?? throw new InvalidOperationException(
                        $"Store '{name}' was already opened with different key/value types.");

            var table = _engine.GetTable(name, keyCodec);
            var store = new KeyValueStore<TKey, TValue>(_engine, table, keyCodec, valueCodec);
            _stores[name] = store;
            return store;
        }
    }

    /// <summary>Names of all stores that exist on disk or hold data in memory (a store that was
    /// opened but never written to is not listed — it has no on-disk footprint yet).</summary>
    public IReadOnlyList<string> StoreNames => _engine.TableNames;

    /// <summary>The timestamp passed to the last committed <see cref="CommitTransaction"/>
    /// (0 for a fresh file). Persisted crash-safely; readable immediately after
    /// <see cref="Open"/>.</summary>
    public long CommitTimestamp => _engine.CommitTimestamp;

    /// <summary>
    /// Opens a file-wide transaction on the calling thread. Until
    /// <see cref="CommitTransaction"/> or <see cref="CancelTransaction"/>, every operation on
    /// every store from this thread is part of the transaction — including reads, which see its
    /// uncommitted writes — while other threads block. One transaction at a time; a concurrent
    /// caller blocks until the current one ends, and a nested call on the same thread throws.
    /// </summary>
    public void StartTransaction() => _engine.StartTransaction();

    /// <summary>Atomically commits the open transaction and stamps <paramref name="timestamp"/>
    /// as the file's <see cref="CommitTimestamp"/>. Durability follows the file's
    /// <see cref="FlushMode"/>, exactly like a batch.</summary>
    public void CommitTransaction(long timestamp) => _engine.CommitTransaction(timestamp);

    /// <summary>Discards every write made since <see cref="StartTransaction"/>.</summary>
    public void CancelTransaction() => _engine.CancelTransaction();

    /// <summary>
    /// Makes all committed-but-unflushed batches/transactions durable now. This is how you persist
    /// writes in <see cref="FlushMode.Manual"/> mode; it forces an immediate flush in
    /// <see cref="FlushMode.Async"/> mode and is a no-op in <see cref="FlushMode.Sync"/> mode.
    /// </summary>
    public void Flush() => _engine.Flush();

    /// <summary>
    /// Releases the file's in-memory page cache and buffer pool (shared by every store). Only
    /// re-readable pages are dropped — anything not yet flushed or checkpointed is retained, so
    /// durability is unaffected — and subsequent reads repopulate the cache from disk.
    /// </summary>
    public void ClearCache() => _engine.ClearCache();

    /// <summary>
    /// Bytes the database currently occupies on disk: the main file plus the write-ahead-log
    /// sidecar (<c>path + "-wal"</c>). Read from the open file handles (the files are exclusively
    /// locked while the database is open), so it is cheap and safe to call from any thread. A
    /// point-in-time snapshot: the WAL portion grows with commits and shrinks to zero at every
    /// checkpoint, and freed pages are reused rather than shrinking the main file.
    /// </summary>
    public long GetTotalDiskSpace() => _engine.TotalDiskBytes;

    /// <summary>
    /// Deletes every entry in every store — the whole file's contents, including stores that are
    /// not currently open — as one atomic commit. O(1) in the data size (the file resets to its
    /// fresh, empty state and the old pages' space is reused by later writes; the physical file
    /// does not shrink). <see cref="CommitTimestamp"/> is preserved. Existing
    /// <see cref="KeyValueStore{TKey,TValue}"/> handles remain valid and simply see empty stores.
    /// Inside an open transaction the wipe joins it — <see cref="CancelTransaction"/> restores
    /// everything; otherwise durability follows the file's <see cref="FlushMode"/>, or pass
    /// <paramref name="forceFlush"/> to make it durable immediately.
    /// </summary>
    public void DeleteAll(bool forceFlush = false) => _engine.DeleteAll(forceFlush);

    // Test hooks (exposed to KvStore.Tests via InternalsVisibleTo).
    internal StorageEngine Engine => _engine;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Dispose();
    }
}
