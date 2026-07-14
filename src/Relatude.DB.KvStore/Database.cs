using KvStore.BTree;

namespace KvStore;

/// <summary>
/// An embedded, crash-safe key/value store — the raw <c>byte[]</c> face over a database file's
/// default table. Keys and values are arbitrary byte arrays and entries are ordered by unsigned
/// byte (lexicographic) comparison, which makes <see cref="Range"/> scans efficient.
///
/// <para>For typed keys/values (with custom ordering and serialization) use
/// <see cref="Database{TKey,TValue}"/>; for multiple named stores in one file (and file-wide
/// transactions) use <see cref="DatabaseFile"/>.</para>
///
/// <para><b>Concurrency:</b> single writer, many concurrent readers; the instance is safe to
/// share across threads.</para>
///
/// <para><b>Durability:</b> by default every commit is fsync'd through a write-ahead log. Opt
/// into <see cref="FlushMode.Async"/> via <see cref="DatabaseOptions"/> to batch commits on a
/// background thread, and pass <c>forceFlush</c> to <see cref="Batch"/> to force a durable flush
/// for a specific batch.</para>
///
/// <para><b>Limits (v1):</b> a single key/value pair must fit in a quarter page; there are no
/// overflow pages for large values yet (see <see cref="MaxEntrySize"/>).</para>
/// </summary>
public sealed class Database : IKeyValueStore
{
    /// <summary>Maximum combined on-disk size of one key/value entry, including framing overhead.</summary>
    public const int MaxEntrySize = StorageEngine.MaxEntrySize;

    private readonly StorageEngine _engine;
    private readonly StorageEngine.Table _table;
    private bool _disposed;

    private Database(StorageEngine engine, StorageEngine.Table table)
    {
        _engine = engine;
        _table = table;
    }

    /// <summary>Opens (creating if necessary) the database at <paramref name="path"/>.</summary>
    public static Database Open(string path, DatabaseOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var engine = StorageEngine.Open(path, options ?? DatabaseOptions.Default);
        return new Database(engine, engine.GetTable("", default(RawByteComparer)));
    }

    /// <summary>Number of entries currently stored.</summary>
    public long Count => _engine.Count(_table);

    // Test hooks (exposed to KvStore.Tests via InternalsVisibleTo).
    internal int DebugCleanCacheCount => _engine.CleanCacheCount;
    internal int DebugStagedPageCount => _engine.StagedPageCount;
    internal int DebugWalPageCount => _engine.WalPageCount;
    internal void SimulateCrash() => _engine.SimulateCrash();

    /// <summary>Returns the value for <paramref name="key"/>, or null if absent.</summary>
    public byte[]? Get(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _engine.Get(_table, key);
    }

    public bool ContainsKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _engine.TryGet(_table, key, static _ => true, out _);
    }

    /// <summary>Inserts or overwrites a single entry and commits.</summary>
    public void Put(byte[] key, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _engine.PutOne(_table, key, value, forceFlush: false);
    }

    /// <summary>Removes a single entry and commits. Returns true if the key existed.</summary>
    public bool Delete(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _engine.DeleteOne(_table, key, forceFlush: false);
    }

    /// <summary>
    /// Returns the entries with key between <paramref name="start"/> and <paramref name="end"/> in
    /// ascending key order — descending when <paramref name="reverse"/> is set. Pass null for
    /// <paramref name="start"/> to scan from the beginning, or null for <paramref name="end"/> to
    /// scan to the end. Each present bound is included per its <c>*Inclusive</c> flag, so the
    /// default is the half-open interval <c>[start, end)</c>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<byte[], byte[]>> Range(
        byte[]? start = null, byte[]? end = null,
        bool startInclusive = true, bool endInclusive = false, bool reverse = false)
        => _engine.Range(_table, start, end,
            static (k, v) => new KeyValuePair<byte[], byte[]>(k.ToArray(), v.ToArray()),
            startInclusive, endInclusive, reverse);

    /// <summary>
    /// Runs <paramref name="body"/> as a single atomic batch: all of its writes commit
    /// together, or none do if it throws. Set <paramref name="forceFlush"/> to make the
    /// batch durable immediately even in <see cref="FlushMode.Async"/> mode.
    /// </summary>
    public void Batch(Action<IWriteBatch> body, bool forceFlush = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        _engine.Batch(_table, b => body(new WriteBatch(b)), forceFlush);
    }

    /// <summary>
    /// Makes all committed-but-unflushed batches durable now. This is how you persist writes
    /// in <see cref="FlushMode.Manual"/> mode, where commits stay in memory until you ask;
    /// it forces an immediate flush in <see cref="FlushMode.Async"/> mode and is a no-op in
    /// <see cref="FlushMode.Sync"/> mode (every commit already flushed).
    /// </summary>
    public void Flush() => _engine.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Dispose();
    }

    /// <summary>
    /// The set of mutations applied within a <see cref="Batch"/> call. Reads inside the
    /// batch observe its own uncommitted writes.
    /// </summary>
    public sealed class WriteBatch : IWriteBatch
    {
        private readonly StorageEngine.ByteWriteBatch _batch;
        internal WriteBatch(StorageEngine.ByteWriteBatch batch) => _batch = batch;

        public void Put(byte[] key, byte[] value) => _batch.Put(key, value);
        public bool Delete(byte[] key) => _batch.Delete(key);
        public byte[]? Get(byte[] key) => _batch.Get(key);
    }
}
