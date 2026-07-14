using KvStore.Serialization;

namespace KvStore;

/// <summary>
/// An embedded, crash-safe ordered map of <typeparamref name="TKey"/> to
/// <typeparamref name="TValue"/> — the single-store face over a database file's default table.
/// Keys and values are serialized to bytes by codecs; entries are ordered by the key codec's
/// <see cref="IKeyCodec{TKey}.Compare"/>, so range scans return keys in their natural typed order
/// regardless of byte layout. For multiple named stores in one file (and file-wide transactions)
/// use <see cref="DatabaseFile"/>.
///
/// <para>Built-in codecs cover the common scalar types (see <see cref="Codecs"/>); for your own
/// types, pass custom codecs to the <see cref="Open(string, IKeyCodec{TKey}, IValueCodec{TValue}, DatabaseOptions?)"/>
/// overload.</para>
///
/// <para>Concurrency and durability match <see cref="Database"/>: single writer / many readers,
/// synchronous fsync per commit by default, with opt-in async flushing via
/// <see cref="DatabaseOptions"/>.</para>
/// </summary>
public sealed class Database<TKey, TValue> : IDisposable
{
    /// <summary>Maximum combined on-disk size of one encoded key/value entry, including framing.</summary>
    public const int MaxEntrySize = StorageEngine.MaxEntrySize;

    private readonly DatabaseFile _file;
    private readonly KeyValueStore<TKey, TValue> _store;
    private bool _disposed;

    private Database(DatabaseFile file, KeyValueStore<TKey, TValue> store)
    {
        _file = file;
        _store = store;
    }

    /// <summary>
    /// Opens the database using built-in codecs for <typeparamref name="TKey"/> and
    /// <typeparamref name="TValue"/>. Throws <see cref="NotSupportedException"/> if either type has
    /// no built-in codec — use the codec-accepting overload for custom types.
    /// </summary>
    public static Database<TKey, TValue> Open(string path, DatabaseOptions? options = null)
        => Open(path, Codecs.For<TKey>(), Codecs.For<TValue>(), options);

    /// <summary>Opens the database with explicit codecs (for custom key/value types).</summary>
    public static Database<TKey, TValue> Open(
        string path, IKeyCodec<TKey> keyCodec, IValueCodec<TValue> valueCodec, DatabaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(keyCodec);
        ArgumentNullException.ThrowIfNull(valueCodec);
        var file = DatabaseFile.Open(path, options);
        return new Database<TKey, TValue>(file, file.GetStore("", keyCodec, valueCodec));
    }

    /// <summary>Number of entries currently stored.</summary>
    public long Count => _store.Count;

    // Test hooks (exposed to KvStore.Tests via InternalsVisibleTo).
    internal int DebugCleanCacheCount => _file.Engine.CleanCacheCount;
    internal int DebugStagedPageCount => _file.Engine.StagedPageCount;

    /// <summary>Gets the value for <paramref name="key"/>; returns false if absent.</summary>
    public bool TryGet(TKey key, out TValue value) => _store.TryGet(key, out value);

    /// <summary>Returns the value for <paramref name="key"/>, or <c>default</c> if absent.
    /// Prefer <see cref="TryGet"/> when the default value is itself a valid stored value.</summary>
    public TValue? GetValueOrDefault(TKey key) => _store.GetValueOrDefault(key);

    public bool ContainsKey(TKey key) => _store.ContainsKey(key);

    /// <summary>The smallest-key entry; see <see cref="KeyValueStore{TKey,TValue}.TryGetMin"/>.</summary>
    public bool TryGetMin(out TKey key, out TValue value) => _store.TryGetMin(out key, out value);

    /// <summary>The largest-key entry; see <see cref="KeyValueStore{TKey,TValue}.TryGetMax"/>.</summary>
    public bool TryGetMax(out TKey key, out TValue value) => _store.TryGetMax(out key, out value);

    /// <summary>The value at the smallest key; see <see cref="KeyValueStore{TKey,TValue}.MinValue"/>.</summary>
    public TValue MinValue => _store.MinValue;

    /// <summary>The value at the largest key; see <see cref="KeyValueStore{TKey,TValue}.MaxValue"/>.</summary>
    public TValue MaxValue => _store.MaxValue;

    /// <summary>Gets the value for a key (throws <see cref="KeyNotFoundException"/> if absent), or
    /// inserts/overwrites it.</summary>
    public TValue this[TKey key]
    {
        get => _store[key];
        set => _store[key] = value;
    }

    /// <summary>Inserts or overwrites a single entry and commits.</summary>
    public void Put(TKey key, TValue value) => _store.Put(key, value);

    /// <summary>Removes a single entry and commits. Returns true if the key existed.</summary>
    public bool Delete(TKey key) => _store.Delete(key);

    /// <summary>All entries in key order, descending when <paramref name="reverse"/> is set.</summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> RangeAll(bool reverse = false)
        => _store.RangeAll(reverse);

    /// <summary>All keys in key order, decoded without touching the values; see
    /// <see cref="KeyValueStore{TKey,TValue}.GetAllKeys"/>.</summary>
    public IReadOnlyList<TKey> GetAllKeys(bool reverse = false) => _store.GetAllKeys(reverse);

    /// <summary>All values in key order, decoded without touching the keys; see
    /// <see cref="KeyValueStore{TKey,TValue}.GetAllValues"/>.</summary>
    public IReadOnlyList<TValue> GetAllValues(bool reverse = false) => _store.GetAllValues(reverse);

    /// <summary>Releases the in-memory page cache; see
    /// <see cref="KeyValueStore{TKey,TValue}.ClearCache"/>.</summary>
    public void ClearCache() => _store.ClearCache();

    /// <summary>
    /// Entries with key between <paramref name="from"/> and <paramref name="to"/>. By default the
    /// interval is half-open <c>[from, to)</c> — <paramref name="fromInclusive"/> true,
    /// <paramref name="toInclusive"/> false; flip either flag to include/exclude that bound. Set
    /// <paramref name="reverse"/> to return the entries in descending key order.
    /// </summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> Range(
        TKey from, TKey to,
        bool fromInclusive = true, bool toInclusive = false, bool reverse = false)
        => _store.Range(from, to, fromInclusive, toInclusive, reverse);

    /// <summary>
    /// Entries with key at or after <paramref name="from"/> (exclusive of it when
    /// <paramref name="fromInclusive"/> is false), in ascending key order — descending when
    /// <paramref name="reverse"/> is set.
    /// </summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> RangeFrom(
        TKey from, bool fromInclusive = true, bool reverse = false)
        => _store.RangeFrom(from, fromInclusive, reverse);

    /// <summary>
    /// Entries with key before <paramref name="to"/> (inclusive of it when
    /// <paramref name="toInclusive"/> is true), in ascending key order — descending when
    /// <paramref name="reverse"/> is set.
    /// </summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> RangeTo(
        TKey to, bool toInclusive = false, bool reverse = false)
        => _store.RangeTo(to, toInclusive, reverse);

    /// <summary>
    /// Runs <paramref name="body"/> as a single atomic batch: all of its writes commit together,
    /// or none do if it throws. Set <paramref name="forceFlush"/> to make the batch durable
    /// immediately even in <see cref="FlushMode.Async"/> mode.
    /// </summary>
    public void Batch(Action<KeyValueStore<TKey, TValue>.WriteBatch> body, bool forceFlush = false)
        => _store.Batch(body, forceFlush);

    /// <summary>
    /// Makes all committed-but-unflushed batches durable now. This is how you persist writes
    /// in <see cref="FlushMode.Manual"/> mode, where commits stay in memory until you ask;
    /// it forces an immediate flush in <see cref="FlushMode.Async"/> mode and is a no-op in
    /// <see cref="FlushMode.Sync"/> mode (every commit already flushed).
    /// </summary>
    public void Flush() => _file.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
    }
}
