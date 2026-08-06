using System.Buffers;
using System.Runtime.CompilerServices;
using Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex;

/// <summary>
/// Bidirectional disk-based index backed by two B+Trees, generic over the id type
/// (int, ulong or Guid — see <see cref="IdCodec{TId}"/>; the sealed leaves below bind each
/// id type to its public interface):
/// an id tree (encoded id → encoded value) serving <see cref="GetValue"/> and <see cref="Entries"/>,
/// and a value tree keyed by the composite (encoded value + encoded id) with empty payloads,
/// serving <see cref="GetIds"/> and <see cref="GetIdsInRange"/> via prefix/range scans.
/// Value encodings are order-preserving and prefix-free, and id encodings are order-preserving
/// and fixed-size, so byte-wise key order equals logical (value, id) order and prefix scans can
/// never bleed into a different value.
/// Mutations cost one descent per tree: the previous value and duplicate-value presence
/// (for <see cref="DistinctValueCount"/>) are resolved inside the tree operations themselves via
/// <see cref="WriteExtras"/>, with a fallback lookup only when a leaf boundary is inconclusive.
/// </summary>
[SkipLocalsInit] // Set/Remove stackalloc ~1.5 KB of scratch per call; zeroing it is pure cost (every read is length-bounded to written bytes)
internal abstract class BPlusTreeIndex<TId, T>(BPlusTreeStorageEngine engine, string name, bool hasEngineTimestamp)
    : ISortedDictionaryIndex<TId, T>, IValueCacheOwner, IIndexTimestamp
    where TId : unmanaged
    where T : notnull
{
    private const int StackBufferSize = 512;
    private const int MaxIdSize = 16; // largest supported id encoding (Guid); stack buffers use this so their size stays a JIT constant

    // A property forwarding to IdCodec<TId>, not a static field on this class: TId is always a
    // value type, so IdCodec<TId> is an exact instantiation whose readonly Size JIT-folds to a
    // constant even inside this class's canonically shared code (reference-type T) — a static
    // field here would instead cost a shared-statics lookup per access.
    private static int IdSize => IdCodec<TId>.Size;

    private readonly IKeyCodec<T> _codec = KeyCodec.Get<T>();

    // true when this index is synchronized with the engine timestamp: set for an opened existing
    // index and after every commit/SetTimestamp on the engine; a newly created index reports 0
    private volatile bool _hasEngineTimestamp = hasEngineTimestamp;

    public long GetTimestamp() => _hasEngineTimestamp ? engine.GetTimestamp() : 0;

    public void SetTimestamp(long timestamp)
    {
        if (timestamp == 0)
        {
            _hasEngineTimestamp = false;
            return;
        }
        if (timestamp != engine.GetTimestamp())
            throw new ArgumentException($"An index timestamp is always 0 or the engine's; pass 0 or the engine's current timestamp ({engine.GetTimestamp()}), not {timestamp}.", nameof(timestamp));
        _hasEngineTimestamp = true;
    }

    void IIndexTimestamp.AdoptEngineTimestamp() => _hasEngineTimestamp = true;
    private readonly ValueCache<TId, T>? _valueCache =
        engine.ValueCacheEntries > 0 ? new ValueCache<TId, T>(engine.ValueCacheEntries) : null;

    // Per-op state lookups memoized, same as HashIndex: one MutableIndexState per (txn, index)
    // and one IndexState per (snapshot, index), both stable for that txn/snapshot, so the
    // string-keyed dictionary probe happens once per txn/snapshot instead of once per operation.
    // _lastTxn/_lastTxnState: txn-owner thread only (consecutive txns synchronize through the
    // engine's write lock). _snapState: any thread; the immutable holder makes a stale read a
    // miss, never a mixed pair.
    private BPlusTreeStorageEngine.WriteTxn? _lastTxn;
    private BPlusTreeStorageEngine.MutableIndexState? _lastTxnState;
    private SnapState? _snapState;

    private sealed class SnapState(BPlusTreeStorageEngine.EngineSnapshot snap, BPlusTreeStorageEngine.IndexState state)
    {
        public readonly BPlusTreeStorageEngine.EngineSnapshot Snap = snap;
        public readonly BPlusTreeStorageEngine.IndexState State = state;
    }

    private BPlusTreeStorageEngine.MutableIndexState TxnState(BPlusTreeStorageEngine.WriteTxn txn)
    {
        if (ReferenceEquals(_lastTxn, txn))
            return _lastTxnState!;
        var st = engine.GetTxnState(txn, name);
        _lastTxn = txn;
        _lastTxnState = st;
        return st;
    }

    private BPlusTreeStorageEngine.IndexState CommittedState(BPlusTreeStorageEngine.EngineSnapshot snap)
    {
        SnapState? c = _snapState;
        if (c is not null && ReferenceEquals(c.Snap, snap))
            return c.State;
        var st = engine.GetCommittedState(snap, name);
        _snapState = new SnapState(snap, st);
        return st;
    }

    public int Count
    {
        get
        {
            using var read = engine.BeginRead();
            return read.Txn is not null
                ? TxnState(read.Txn).IdCount
                : CommittedState(read.Snapshot!).IdCount;
        }
    }

    public int DistinctValueCount
    {
        get
        {
            using var read = engine.BeginRead();
            return read.Txn is not null
                ? TxnState(read.Txn).ValueCount
                : CommittedState(read.Snapshot!).ValueCount;
        }
    }

    public void Set(TId id, T value)
    {
        var txn = engine.RequireTxn();
        var st = TxnState(txn);

        int maxSize = _codec.GetMaxSize(value) + IdSize;
        byte[]? rented = maxSize > StackBufferSize ? ArrayPool<byte>.Shared.Rent(maxSize) : null;
        Span<byte> buf = rented ?? stackalloc byte[StackBufferSize];
        Span<byte> oldBuf = stackalloc byte[NodePage.MaxValueSize + MaxIdSize];
        try
        {
            int valueLen = _codec.Encode(buf, value);
            // Validate the composite up front: failing on the second tree would leave the two trees inconsistent.
            if (valueLen + IdSize > NodePage.MaxKeySize)
                throw new ArgumentException($"Encoded value is {valueLen} bytes; the maximum is {NodePage.MaxKeySize - IdSize}.");

            IdCodec<TId>.Encode(buf[valueLen..], id);
            Span<byte> composite = buf[..(valueLen + IdSize)];
            Span<byte> valueBytes = buf[..valueLen];
            Span<byte> idKey = buf.Slice(valueLen, IdSize);

            var idExtras = new WriteExtras { OldValue = oldBuf };
            st.IdRoot = BTree.Insert(txn, st.IdRoot, idKey, valueBytes, ref idExtras);
            if (idExtras.Outcome == InsertOutcome.NoChange)
                return; // same mapping already present: nothing was written
            RecordTouched(st, id);

            if (idExtras.Outcome == InsertOutcome.Replaced)
            {
                // Unlink (oldValue, id) from the value tree.
                idKey.CopyTo(oldBuf[idExtras.OldValueLength..]);
                Span<byte> oldComposite = oldBuf[..(idExtras.OldValueLength + IdSize)];
                var oldExtras = new WriteExtras { PrefixLength = idExtras.OldValueLength };
                st.ValueRoot = BTree.Delete(txn, st.ValueRoot, oldComposite, out _, ref oldExtras);
                if (oldExtras.Presence == PrefixPresence.No ||
                    (oldExtras.Presence == PrefixPresence.Unknown && !HasValue(txn, st.ValueRoot, oldBuf[..idExtras.OldValueLength])))
                {
                    st.ValueCount--;
                }
            }
            else
            {
                st.IdCount++;
            }

            var newExtras = new WriteExtras { PrefixLength = valueLen };
            st.ValueRoot = BTree.Insert(txn, st.ValueRoot, composite, [], ref newExtras);
            if (newExtras.Presence == PrefixPresence.No ||
                (newExtras.Presence == PrefixPresence.Unknown && !HasValueOtherThan(txn, st.ValueRoot, valueBytes, idKey)))
            {
                st.ValueCount++;
            }
            st.Dirty = true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public bool Remove(TId id)
    {
        var txn = engine.RequireTxn();
        var st = TxnState(txn);

        Span<byte> idKey = stackalloc byte[MaxIdSize];
        idKey = idKey[..IdSize];
        IdCodec<TId>.Encode(idKey, id);
        Span<byte> oldBuf = stackalloc byte[NodePage.MaxValueSize + MaxIdSize];

        var idExtras = new WriteExtras { OldValue = oldBuf };
        st.IdRoot = BTree.Delete(txn, st.IdRoot, idKey, out bool removed, ref idExtras);
        if (!removed)
            return false;
        RecordTouched(st, id);

        idKey.CopyTo(oldBuf[idExtras.OldValueLength..]);
        Span<byte> composite = oldBuf[..(idExtras.OldValueLength + IdSize)];
        var valExtras = new WriteExtras { PrefixLength = idExtras.OldValueLength };
        st.ValueRoot = BTree.Delete(txn, st.ValueRoot, composite, out _, ref valExtras);

        st.IdCount--;
        if (valExtras.Presence == PrefixPresence.No ||
            (valExtras.Presence == PrefixPresence.Unknown && !HasValue(txn, st.ValueRoot, oldBuf[..idExtras.OldValueLength])))
        {
            st.ValueCount--;
        }
        st.Dirty = true;
        return true;
    }

    private static bool HasValue(IPageSource src, uint valueRoot, ReadOnlySpan<byte> valueBytes)
    {
        var cursor = new BTreeCursor(src);
        return cursor.Seek(valueRoot, valueBytes) && cursor.Key.StartsWith(valueBytes);
    }

    /// <summary>True if the value tree holds (valueBytes, anyOtherId). Same-value keys are contiguous, so two entries decide.</summary>
    private static bool HasValueOtherThan(IPageSource src, uint valueRoot, ReadOnlySpan<byte> valueBytes, ReadOnlySpan<byte> idKey)
    {
        var cursor = new BTreeCursor(src);
        if (!cursor.Seek(valueRoot, valueBytes) || !cursor.Key.StartsWith(valueBytes))
            return false;
        if (!cursor.Key[^IdSize..].SequenceEqual(idKey))
            return true;
        return cursor.MoveNext() && cursor.Key.StartsWith(valueBytes);
    }

    public T GetValue(TId id)
        => TryGetValue(id, out T value)
            ? value
            : throw new KeyNotFoundException($"Id {id} is not present in index '{name}'.");

    public bool TryGetValue(TId id, out T value)
    {
        using var read = engine.BeginRead();
        // The cache only ever serves committed snapshots; the writer inside its own
        // transaction (read.Snapshot is null) must see uncommitted data and bypasses it.
        bool cacheable = _valueCache is not null && read.Snapshot is not null;
        if (cacheable && _valueCache!.TryGet(id, read.Snapshot!.TxId, out value!))
            return true;

        Span<byte> idKey = stackalloc byte[MaxIdSize];
        idKey = idKey[..IdSize];
        IdCodec<TId>.Encode(idKey, id);
        if (!BTree.TryGet(read.Source, RootsFor(read).IdRoot, idKey, out byte[] leaf, out int pos))
        {
            value = default!;
            return false;
        }
        value = _codec.Decode(NodePage.LeafValue(leaf, pos));

        if (cacheable)
        {
            // Populate, then re-check: if a commit published while we were reading, its
            // eviction pass may already have missed our entry — undo the insert.
            long snapTxId = read.Snapshot!.TxId;
            if (_valueCache!.TryAdd(id, snapTxId, value) && engine.CommittedTxId != snapTxId)
                _valueCache.RemoveIfFrom(id, snapTxId);
        }
        return true;
    }

    public bool ContainsKey(TId id)
    {
        using var read = engine.BeginRead();
        if (_valueCache is not null && read.Snapshot is not null && _valueCache.TryGet(id, read.Snapshot.TxId, out _))
            return true;

        Span<byte> idKey = stackalloc byte[MaxIdSize];
        idKey = idKey[..IdSize];
        IdCodec<TId>.Encode(idKey, id);
        return BTree.TryGet(read.Source, RootsFor(read).IdRoot, idKey, out _, out _);
    }

    public bool ContainsValue(T value)
    {
        byte[] prefix = EncodeToArray(value);
        using var read = engine.BeginRead();
        return HasValue(read.Source, RootsFor(read).ValueRoot, prefix);
    }

    void IValueCacheOwner.EvictCommittedSlots(List<int>? touchedSlots, bool overflow)
    {
        if (_valueCache is null)
            return;
        if (overflow || touchedSlots is null)
        {
            _valueCache.Clear();
            return;
        }
        foreach (int slotHash in touchedSlots)
            _valueCache.EvictSlot(slotHash);
    }

    private void RecordTouched(BPlusTreeStorageEngine.MutableIndexState st, TId id)
    {
        if (_valueCache is null || st.TouchedOverflow)
            return;
        var list = st.TouchedSlots ??= new List<int>();
        if (list.Count >= engine.ValueCacheEntries)
        {
            // The txn touched more ids than the cache can hold: clearing at commit is cheaper.
            st.TouchedSlots = null;
            st.TouchedOverflow = true;
            return;
        }
        list.Add(IdCodec<TId>.SlotHash(id));
    }

    public IEnumerable<TId> GetIds(T value)
    {
        byte[] prefix = EncodeToArray(value);
        using var read = engine.BeginRead();
        var cursor = new BTreeCursor(read.Source);
        if (!cursor.Seek(RootsFor(read).ValueRoot, prefix))
            yield break;
        do
        {
            ReadOnlySpan<byte> key = cursor.Key;
            if (!key.StartsWith(prefix))
                yield break;
            TId id = IdCodec<TId>.Decode(key[^IdSize..]);
            yield return id;
        } while (cursor.MoveNext());
    }

    public IEnumerable<KeyValuePair<TId, T>> Entries
    {
        get
        {
            using var read = engine.BeginRead();
            var cursor = new BTreeCursor(read.Source);
            if (!cursor.SeekFirst(RootsFor(read).IdRoot))
                yield break;
            do
            {
                TId id = IdCodec<TId>.Decode(cursor.Key);
                T value = _codec.Decode(cursor.Value);
                yield return new KeyValuePair<TId, T>(id, value);
            } while (cursor.MoveNext());
        }
    }

    public IEnumerable<TId> Keys
    {
        get
        {
            using var read = engine.BeginRead();
            var cursor = new BTreeCursor(read.Source);
            if (!cursor.SeekFirst(RootsFor(read).IdRoot))
                yield break;
            do
            {
                yield return IdCodec<TId>.Decode(cursor.Key);
            } while (cursor.MoveNext());
        }
    }

    public T GetMinValue()
    {
        // One descent down the leftmost spine of the value tree; only the value prefix is decoded.
        using var read = engine.BeginRead();
        var cursor = new BTreeCursor(read.Source);
        if (!cursor.SeekFirst(RootsFor(read).ValueRoot))
            throw new InvalidOperationException($"Index '{name}' is empty.");
        return _codec.Decode(cursor.Key[..^IdSize]);
    }

    public T GetMaxValue()
    {
        using var read = engine.BeginRead();
        var cursor = new BTreeCursor(read.Source);
        if (!cursor.SeekLast(RootsFor(read).ValueRoot))
            throw new InvalidOperationException($"Index '{name}' is empty.");
        return _codec.Decode(cursor.Key[..^IdSize]);
    }

    public IEnumerable<T> DistinctValues
    {
        get
        {
            using var read = engine.BeginRead();
            var root = RootsFor(read).ValueRoot;
            var cursor = new BTreeCursor(read.Source);
            if (!cursor.SeekFirst(root))
                yield break;
            while (true)
            {
                byte[] valueBytes = cursor.Key[..^IdSize].ToArray(); // the key span dies when the cursor moves
                yield return _codec.Decode(valueBytes);
                // Skip-scan: same-value composites are contiguous, so seek directly past the largest
                // possible (value, id) composite instead of stepping through every duplicate id.
                // The codec is prefix-free, so no other value's composite can sort inside that range,
                // making this O(distinct · log n) instead of O(n).
                Span<byte> seekKey = new byte[valueBytes.Length + IdSize];
                valueBytes.CopyTo(seekKey);
                seekKey[valueBytes.Length..].Fill(0xFF);
                if (!cursor.Seek(root, seekKey))
                    yield break;
                // all-0xFF is the largest possible encoded id: if such an id exists the seek lands on it
                if (cursor.Key[..^IdSize].SequenceEqual(valueBytes) && !cursor.MoveNext())
                    yield break;
            }
        }
    }

    /// <summary>
    /// Half-open scan bounds [startKey, stopKey) over the (value, id) composite key space.
    /// Prefix-freedom guarantees neither bound can collide with a stored composite.
    /// </summary>
    private (byte[] StartKey, byte[] StopKey) BuildRangeKeys(T from, T to, bool includeFrom, bool includeTo)
        => (BuildStartKey(from, includeFrom), BuildStopKey(to, includeTo));

    // Composite keys are (value, id); encoded ids span 0x00... to 0xFF... .
    private byte[] BuildStartKey(T from, bool includeFrom)
    {
        byte[] encFrom = EncodeToArray(from);
        if (includeFrom)
            return encFrom; // sorts before every (from, id) composite
        byte[] startKey = new byte[encFrom.Length + IdSize + 1];
        encFrom.CopyTo(startKey, 0);
        startKey.AsSpan(encFrom.Length, IdSize).Fill(0xFF); // past the last (from, id) composite
        return startKey;
    }

    private byte[] BuildStopKey(T to, bool includeTo)
    {
        // The stop key is the first key NOT in range.
        byte[] encTo = EncodeToArray(to);
        if (!includeTo)
            return encTo; // prefix-freedom: every smaller value's composite compares below this
        byte[] stopKey = new byte[encTo.Length + IdSize + 1];
        encTo.CopyTo(stopKey, 0);
        stopKey.AsSpan(encTo.Length, IdSize).Fill(0xFF);
        return stopKey;
    }

    public IEnumerable<TId> GetIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
    {
        var (startKey, stopKey) = BuildRangeKeys(from, to, includeFrom, includeTo);

        using var read = engine.BeginRead();
        var cursor = new BTreeCursor(read.Source);
        if (descending)
        {
            // Neither boundary key can collide with a stored composite (prefix-freedom),
            // so "last key < stopKey" is exactly the last in-range entry.
            if (!cursor.SeekLastBelow(RootsFor(read).ValueRoot, stopKey))
                yield break;
            do
            {
                ReadOnlySpan<byte> key = cursor.Key;
                if (key.SequenceCompareTo(startKey) < 0)
                    yield break;
                TId id = IdCodec<TId>.Decode(key[^IdSize..]);
                yield return id;
            } while (cursor.MovePrevious());
        }
        else
        {
            if (!cursor.Seek(RootsFor(read).ValueRoot, startKey))
                yield break;
            do
            {
                ReadOnlySpan<byte> key = cursor.Key;
                if (key.SequenceCompareTo(stopKey) >= 0)
                    yield break;
                TId id = IdCodec<TId>.Decode(key[^IdSize..]);
                yield return id;
            } while (cursor.MoveNext());
        }
    }

    public IEnumerable<KeyValuePair<TId, T>> GetEntriesInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
    {
        var (startKey, stopKey) = BuildRangeKeys(from, to, includeFrom, includeTo);

        // The value is embedded in the composite key, so no per-id lookup is needed.
        // Consecutive entries often share a value; decode only when the prefix changes.
        byte[]? lastValueBytes = null;
        T lastValue = default!;

        using var read = engine.BeginRead();
        var cursor = new BTreeCursor(read.Source);
        if (descending)
        {
            if (!cursor.SeekLastBelow(RootsFor(read).ValueRoot, stopKey))
                yield break;
            do
            {
                ReadOnlySpan<byte> key = cursor.Key;
                if (key.SequenceCompareTo(startKey) < 0)
                    yield break;
                yield return DecodeEntry(key, ref lastValueBytes, ref lastValue);
            } while (cursor.MovePrevious());
        }
        else
        {
            if (!cursor.Seek(RootsFor(read).ValueRoot, startKey))
                yield break;
            do
            {
                ReadOnlySpan<byte> key = cursor.Key;
                if (key.SequenceCompareTo(stopKey) >= 0)
                    yield break;
                yield return DecodeEntry(key, ref lastValueBytes, ref lastValue);
            } while (cursor.MoveNext());
        }
    }

    public IEnumerable<TId> GetIdsGreaterThan(T value, bool includeValue = true, bool descending = false)
    {
        byte[] startKey = BuildStartKey(value, includeValue);
        using var read = engine.BeginRead();
        var cursor = new BTreeCursor(read.Source);
        if (descending)
        {
            if (!cursor.SeekLast(RootsFor(read).ValueRoot))
                yield break;
            do
            {
                ReadOnlySpan<byte> key = cursor.Key;
                if (key.SequenceCompareTo(startKey) < 0)
                    yield break;
                yield return IdCodec<TId>.Decode(key[^IdSize..]);
            } while (cursor.MovePrevious());
        }
        else
        {
            if (!cursor.Seek(RootsFor(read).ValueRoot, startKey))
                yield break;
            do
            {
                yield return IdCodec<TId>.Decode(cursor.Key[^IdSize..]);
            } while (cursor.MoveNext());
        }
    }

    public IEnumerable<TId> GetIdsSmallerThan(T value, bool includeValue = true, bool descending = false)
    {
        byte[] stopKey = BuildStopKey(value, includeValue);
        using var read = engine.BeginRead();
        var cursor = new BTreeCursor(read.Source);
        if (descending)
        {
            // Every key below stopKey is in range: no per-entry bound check needed.
            if (!cursor.SeekLastBelow(RootsFor(read).ValueRoot, stopKey))
                yield break;
            do
            {
                yield return IdCodec<TId>.Decode(cursor.Key[^IdSize..]);
            } while (cursor.MovePrevious());
        }
        else
        {
            if (!cursor.SeekFirst(RootsFor(read).ValueRoot))
                yield break;
            do
            {
                ReadOnlySpan<byte> key = cursor.Key;
                if (key.SequenceCompareTo(stopKey) >= 0)
                    yield break;
                yield return IdCodec<TId>.Decode(key[^IdSize..]);
            } while (cursor.MoveNext());
        }
    }

    // Counts descend the value tree's branch counts (see BTree.CountLessThan) instead of scanning
    // leaves: [startKey, stopKey) bounds are prefix-free, so "entries < boundary" is exact.

    public int CountIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true)
    {
        var (startKey, stopKey) = BuildRangeKeys(from, to, includeFrom, includeTo);
        using var read = engine.BeginRead();
        uint valueRoot = RootsFor(read).ValueRoot;
        int n = BTree.CountLessThan(read.Source, valueRoot, stopKey)
              - BTree.CountLessThan(read.Source, valueRoot, startKey);
        return n > 0 ? n : 0; // an inverted range (from > to) matches nothing
    }

    public int CountIdsGreaterThan(T value, bool includeValue = true)
    {
        byte[] startKey = BuildStartKey(value, includeValue);
        using var read = engine.BeginRead();
        int total = read.Txn is not null
            ? TxnState(read.Txn).IdCount
            : CommittedState(read.Snapshot!).IdCount;
        return total - BTree.CountLessThan(read.Source, RootsFor(read).ValueRoot, startKey);
    }

    public int CountIdsSmallerThan(T value, bool includeValue = true)
    {
        byte[] stopKey = BuildStopKey(value, includeValue);
        using var read = engine.BeginRead();
        return BTree.CountLessThan(read.Source, RootsFor(read).ValueRoot, stopKey);
    }

    /// <summary>Splits a composite key into (id, decoded value), reusing the last decode for repeated values.</summary>
    private KeyValuePair<TId, T> DecodeEntry(ReadOnlySpan<byte> key, ref byte[]? lastValueBytes, ref T lastValue)
    {
        ReadOnlySpan<byte> valueBytes = key[..^IdSize];
        if (lastValueBytes is null || !valueBytes.SequenceEqual(lastValueBytes))
        {
            lastValueBytes = valueBytes.ToArray(); // the key span dies when the cursor moves
            lastValue = _codec.Decode(valueBytes);
        }
        return new KeyValuePair<TId, T>(IdCodec<TId>.Decode(key[^IdSize..]), lastValue);
    }

    private (uint ValueRoot, uint IdRoot) RootsFor(in BPlusTreeStorageEngine.ReadHandle read)
    {
        if (read.Txn is not null)
        {
            var st = TxnState(read.Txn);
            return (st.ValueRoot, st.IdRoot);
        }
        var committed = CommittedState(read.Snapshot!);
        return (committed.ValueRoot, committed.IdRoot);
    }

    private byte[] EncodeToArray(T value)
    {
        int maxSize = _codec.GetMaxSize(value);
        byte[]? rented = maxSize > StackBufferSize ? ArrayPool<byte>.Shared.Rent(maxSize) : null;
        Span<byte> buf = rented ?? stackalloc byte[StackBufferSize];
        int len = _codec.Encode(buf, value);
        byte[] result = buf[..len].ToArray();
        if (rented is not null)
            ArrayPool<byte>.Shared.Return(rented);
        return result;
    }
}

internal sealed class BPlusTreeIntIndex<T>(BPlusTreeStorageEngine engine, string name, bool hasEngineTimestamp)
    : BPlusTreeIndex<int, T>(engine, name, hasEngineTimestamp), ISortedIntIndex<T> where T : notnull;

internal sealed class BPlusTreeUlongIndex<T>(BPlusTreeStorageEngine engine, string name, bool hasEngineTimestamp)
    : BPlusTreeIndex<ulong, T>(engine, name, hasEngineTimestamp), ISortedUlongIndex<T> where T : notnull;

internal sealed class BPlusTreeGuidIndex<T>(BPlusTreeStorageEngine engine, string name, bool hasEngineTimestamp)
    : BPlusTreeIndex<Guid, T>(engine, name, hasEngineTimestamp), ISortedGuidIndex<T> where T : notnull;
