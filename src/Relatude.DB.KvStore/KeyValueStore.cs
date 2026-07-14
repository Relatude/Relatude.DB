using KvStore.BTree;
using KvStore.Serialization;

namespace KvStore;

/// <summary>
/// One named, typed key/value table inside a <see cref="DatabaseFile"/>: an ordered map of
/// <typeparamref name="TKey"/> to <typeparamref name="TValue"/>, serialized by codecs and ordered
/// by the key codec's <see cref="IKeyCodec{TKey}.Compare"/>. Obtain instances via
/// <see cref="DatabaseFile.GetStore{TKey,TValue}(string)"/>; the store has no lifetime of its own —
/// durability, flushing, and disposal belong to the owning file.
///
/// <para><b>Write units:</b> <see cref="Put"/>/<see cref="Delete"/> commit individually;
/// <see cref="Batch"/> groups writes into one atomic commit. While the owning file has a
/// transaction open (<see cref="DatabaseFile.StartTransaction"/>), all of these — and all reads
/// from the transacting thread — become part of that transaction instead.</para>
/// </summary>
public sealed class KeyValueStore<TKey, TValue>
{
    /// <summary>Maximum combined on-disk size of one encoded key/value entry, including framing.</summary>
    public const int MaxEntrySize = StorageEngine.MaxEntrySize;

    private readonly StorageEngine _engine;
    private readonly StorageEngine.Table _table;
    private readonly IKeyCodec<TKey> _keyCodec;
    private readonly IValueCodec<TValue> _valueCodec;

    // Decode straight from the page spans (no intermediate byte[] copies). Cached so the hot
    // read paths don't allocate a fresh delegate per call. The key-only/value-only selectors
    // decode just their side of each entry — the other side's bytes are never touched.
    private readonly ValueSelector<TValue> _decodeValue;
    private readonly EntrySelector<KeyValuePair<TKey, TValue>> _decodeEntry;
    private readonly EntrySelector<TKey> _decodeKeyOnly;
    private readonly EntrySelector<TValue> _decodeValueOnly;

    internal KeyValueStore(
        StorageEngine engine, StorageEngine.Table table,
        IKeyCodec<TKey> keyCodec, IValueCodec<TValue> valueCodec)
    {
        _engine = engine;
        _table = table;
        _keyCodec = keyCodec;
        _valueCodec = valueCodec;
        _decodeValue = valueCodec.Decode;
        _decodeEntry = (k, v) => new KeyValuePair<TKey, TValue>(keyCodec.Decode(k), valueCodec.Decode(v));
        _decodeKeyOnly = (k, _) => keyCodec.Decode(k);
        _decodeValueOnly = (_, v) => valueCodec.Decode(v);
    }

    /// <summary>Number of entries currently stored.</summary>
    public long Count => _engine.Count(_table);

    /// <summary>Byte budget for encoding point-lookup keys on the stack instead of the heap.</summary>
    private const int KeyStackBytes = 128;

    /// <summary>Byte budget for encoding a value on the stack; larger values fall back to Encode.</summary>
    private const int ValueStackBytes = 256;

    /// <summary>Encodes into the caller's (stack) scratch buffer when the codec supports it —
    /// all built-ins do for values this small — else falls back to the allocating Encode.
    /// The scratch must outlive the returned span, so it always lives in the caller's frame.</summary>
    private static ReadOnlySpan<byte> EncodeSpan<T>(IValueCodec<T> codec, T value, Span<byte> scratch)
        => codec.TryEncode(value, scratch, out int len) ? scratch[..len] : codec.Encode(value);

    /// <summary>Gets the value for <paramref name="key"/>; returns false if absent.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        Span<byte> buf = stackalloc byte[KeyStackBytes];
        return _engine.TryGet(_table, EncodeSpan(_keyCodec, key, buf), _decodeValue, out value);
    }

    /// <summary>Returns the value for <paramref name="key"/>, or <c>default</c> if absent.
    /// Prefer <see cref="TryGet"/> when the default value is itself a valid stored value.</summary>
    public TValue? GetValueOrDefault(TKey key)
        => TryGet(key, out var value) ? value : default;

    public bool ContainsKey(TKey key)
    {
        Span<byte> buf = stackalloc byte[KeyStackBytes];
        return _engine.TryGet(_table, EncodeSpan(_keyCodec, key, buf), static _ => true, out _);
    }

    /// <summary>
    /// The entry with the smallest key: false when the store is empty. One root-to-leaf descent —
    /// O(tree depth), independent of store size.
    /// </summary>
    public bool TryGetMin(out TKey key, out TValue value)
    {
        bool found = _engine.TryFirst(_table, _decodeEntry, out var entry);
        (key, value) = (entry.Key, entry.Value);
        return found;
    }

    /// <summary>
    /// The entry with the largest key: false when the store is empty. One root-to-leaf descent —
    /// O(tree depth), independent of store size.
    /// </summary>
    public bool TryGetMax(out TKey key, out TValue value)
    {
        bool found = _engine.TryLast(_table, _decodeEntry, out var entry);
        (key, value) = (entry.Key, entry.Value);
        return found;
    }

    /// <summary>The value stored at the <b>smallest key</b> (keys define the store's order;
    /// values themselves are unordered). One root-to-leaf descent, decoding only the value.
    /// Throws <see cref="InvalidOperationException"/> when the store is empty — use
    /// <see cref="TryGetMin"/> for a non-throwing variant that also returns the key.</summary>
    public TValue MinValue
        => _engine.TryFirst(_table, _decodeValueOnly, out var value)
            ? value
            : throw new InvalidOperationException("The store is empty.");

    /// <summary>The value stored at the <b>largest key</b>; see <see cref="MinValue"/>.
    /// Throws <see cref="InvalidOperationException"/> when the store is empty — use
    /// <see cref="TryGetMax"/> for a non-throwing variant that also returns the key.</summary>
    public TValue MaxValue
        => _engine.TryLast(_table, _decodeValueOnly, out var value)
            ? value
            : throw new InvalidOperationException("The store is empty.");

    /// <summary>Gets the value for a key (throws <see cref="KeyNotFoundException"/> if absent), or
    /// inserts/overwrites it.</summary>
    public TValue this[TKey key]
    {
        get => TryGet(key, out var value)
            ? value
            : throw new KeyNotFoundException("The given key was not present in the store.");
        set => Put(key, value);
    }

    /// <summary>Inserts or overwrites a single entry and commits. Uses the engine's single-op
    /// commit path: both encodings live on the stack, so the common put allocates nothing.</summary>
    public void Put(TKey key, TValue value)
    {
        Span<byte> kbuf = stackalloc byte[KeyStackBytes];
        Span<byte> vbuf = stackalloc byte[ValueStackBytes];
        _engine.PutOne(_table, EncodeSpan(_keyCodec, key, kbuf), EncodeSpan(_valueCodec, value, vbuf), forceFlush: false);
    }

    /// <summary>Removes a single entry and commits. Returns true if the key existed.</summary>
    public bool Delete(TKey key)
    {
        Span<byte> buf = stackalloc byte[KeyStackBytes];
        return _engine.DeleteOne(_table, EncodeSpan(_keyCodec, key, buf), forceFlush: false);
    }

    /// <summary>All entries in key order, descending when <paramref name="reverse"/> is set.</summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> RangeAll(bool reverse = false)
        => RangeBytes(null, null, true, false, reverse);

    /// <summary>
    /// All keys in key order, descending when <paramref name="reverse"/> is set. One walk of the
    /// leaf chain into a pre-sized list, decoding keys straight from the page spans — values are
    /// never decoded or copied, so this is cheaper than <see cref="RangeAll"/> whenever values
    /// carry any weight.
    /// </summary>
    public IReadOnlyList<TKey> GetAllKeys(bool reverse = false)
        => _engine.Range(_table, null, null, _decodeKeyOnly, true, false, reverse);

    /// <summary>
    /// All values in key order, descending when <paramref name="reverse"/> is set. One walk of
    /// the leaf chain into a pre-sized list, decoding values straight from the page spans — keys
    /// are never decoded or copied.
    /// </summary>
    public IReadOnlyList<TValue> GetAllValues(bool reverse = false)
        => _engine.Range(_table, null, null, _decodeValueOnly, true, false, reverse);

    /// <summary>
    /// Releases the owning file's in-memory page cache and buffer pool (shared by every store in
    /// the file, so this affects them all). Only re-readable pages are dropped — anything not yet
    /// flushed or checkpointed is retained, so durability is unaffected — and subsequent reads
    /// repopulate the cache from disk. Useful to hand memory back after a large scan.
    /// </summary>
    public void ClearCache() => _engine.ClearCache();

    /// <summary>
    /// Entries with key between <paramref name="from"/> and <paramref name="to"/>. By default the
    /// interval is half-open <c>[from, to)</c> — <paramref name="fromInclusive"/> true,
    /// <paramref name="toInclusive"/> false; flip either flag to include/exclude that bound. Set
    /// <paramref name="reverse"/> to return the entries in descending key order.
    /// </summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> Range(
        TKey from, TKey to,
        bool fromInclusive = true, bool toInclusive = false, bool reverse = false)
        => RangeBytes(_keyCodec.Encode(from), _keyCodec.Encode(to), fromInclusive, toInclusive, reverse);

    /// <summary>
    /// Entries with key at or after <paramref name="from"/> (exclusive of it when
    /// <paramref name="fromInclusive"/> is false), in ascending key order — descending when
    /// <paramref name="reverse"/> is set.
    /// </summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> RangeFrom(
        TKey from, bool fromInclusive = true, bool reverse = false)
        => RangeBytes(_keyCodec.Encode(from), null, fromInclusive, false, reverse);

    /// <summary>
    /// Entries with key before <paramref name="to"/> (inclusive of it when
    /// <paramref name="toInclusive"/> is true), in ascending key order — descending when
    /// <paramref name="reverse"/> is set.
    /// </summary>
    public IReadOnlyList<KeyValuePair<TKey, TValue>> RangeTo(
        TKey to, bool toInclusive = false, bool reverse = false)
        => RangeBytes(null, _keyCodec.Encode(to), true, toInclusive, reverse);

    private IReadOnlyList<KeyValuePair<TKey, TValue>> RangeBytes(
        byte[]? start, byte[]? end, bool startInclusive, bool endInclusive, bool reverse)
        => _engine.Range(_table, start, end, _decodeEntry, startInclusive, endInclusive, reverse);

    /// <summary>
    /// Runs <paramref name="body"/> as a single atomic batch: all of its writes commit together,
    /// or none do if it throws. Set <paramref name="forceFlush"/> to make the batch durable
    /// immediately even in <see cref="FlushMode.Async"/> mode. Inside an open file transaction
    /// (same thread) the batch simply joins the transaction.
    /// </summary>
    public void Batch(Action<WriteBatch> body, bool forceFlush = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        _engine.Batch(_table, b => body(new WriteBatch(this, b)), forceFlush);
    }

    /// <summary>
    /// The set of mutations applied within a <see cref="Batch"/> call. Reads inside the batch
    /// observe its own uncommitted writes.
    /// </summary>
    public sealed class WriteBatch
    {
        private readonly KeyValueStore<TKey, TValue> _store;
        private readonly StorageEngine.ByteWriteBatch _batch;

        internal WriteBatch(KeyValueStore<TKey, TValue> store, StorageEngine.ByteWriteBatch batch)
        {
            _store = store;
            _batch = batch;
        }

        public void Put(TKey key, TValue value)
        {
            // Encode both on the stack when the codecs support it — the engine copies the spans
            // into the page, so the common put allocates nothing.
            Span<byte> kbuf = stackalloc byte[KeyStackBytes];
            Span<byte> vbuf = stackalloc byte[ValueStackBytes];
            _batch.Put(EncodeSpan(_store._keyCodec, key, kbuf), EncodeSpan(_store._valueCodec, value, vbuf));
        }

        public bool Delete(TKey key)
        {
            Span<byte> buf = stackalloc byte[KeyStackBytes];
            return _batch.Delete(EncodeSpan(_store._keyCodec, key, buf));
        }

        public bool TryGet(TKey key, out TValue value)
        {
            Span<byte> buf = stackalloc byte[KeyStackBytes];
            var bytes = _batch.Get(EncodeSpan(_store._keyCodec, key, buf));
            if (bytes is null) { value = default!; return false; }
            value = _store._valueCodec.Decode(bytes);
            return true;
        }
    }
}
