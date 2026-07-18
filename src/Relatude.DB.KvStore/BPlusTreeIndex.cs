using System.Buffers;
using SuperFastIndex.Internal;

namespace SuperFastIndex;

/// <summary>
/// Bidirectional disk-based index backed by two B+Trees:
/// an id tree (encoded id → encoded value) serving <see cref="GetValue"/> and <see cref="Entries"/>,
/// and a value tree keyed by the composite (encoded value + encoded id) with empty payloads,
/// serving <see cref="GetIds"/> and <see cref="GetIdsInRange"/> via prefix/range scans.
/// Value encodings are order-preserving and prefix-free, so byte-wise key order equals
/// logical (value, id) order and prefix scans can never bleed into a different value.
/// Mutations cost one descent per tree: the previous value and duplicate-value presence
/// (for <see cref="DistinctValueCount"/>) are resolved inside the tree operations themselves via
/// <see cref="WriteExtras"/>, with a fallback lookup only when a leaf boundary is inconclusive.
/// </summary>
internal sealed class BPlusTreeIndex<T>(BPlusTreeStorageEngine engine, string name, bool hasEngineTimestamp) : ISortedIndex<T>, IValueCacheOwner, IIndexTimestamp where T : notnull
{
    private const int StackBufferSize = 512;

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
    private readonly ValueCache<T>? _valueCache =
        engine.ValueCacheEntries > 0 ? new ValueCache<T>(engine.ValueCacheEntries) : null;

    public int Count
    {
        get
        {
            using var read = engine.BeginRead();
            return read.Txn is not null
                ? engine.GetTxnState(read.Txn, name).IdCount
                : engine.GetCommittedState(read.Snapshot!, name).IdCount;
        }
    }

    public int DistinctValueCount
    {
        get
        {
            using var read = engine.BeginRead();
            return read.Txn is not null
                ? engine.GetTxnState(read.Txn, name).ValueCount
                : engine.GetCommittedState(read.Snapshot!, name).ValueCount;
        }
    }

    public void Set(int id, T value)
    {
        var txn = engine.RequireTxn();
        var st = engine.GetTxnState(txn, name);

        int maxSize = _codec.GetMaxSize(value) + KeyCodec.IdSize;
        byte[]? rented = maxSize > StackBufferSize ? ArrayPool<byte>.Shared.Rent(maxSize) : null;
        Span<byte> buf = rented ?? stackalloc byte[StackBufferSize];
        Span<byte> oldBuf = stackalloc byte[NodePage.MaxValueSize + KeyCodec.IdSize];
        try
        {
            int valueLen = _codec.Encode(buf, value);
            // Validate the composite up front: failing on the second tree would leave the two trees inconsistent.
            if (valueLen + KeyCodec.IdSize > NodePage.MaxKeySize)
                throw new ArgumentException($"Encoded value is {valueLen} bytes; the maximum is {NodePage.MaxKeySize - KeyCodec.IdSize}.");

            KeyCodec.EncodeId(buf[valueLen..], id);
            Span<byte> composite = buf[..(valueLen + KeyCodec.IdSize)];
            Span<byte> valueBytes = buf[..valueLen];
            Span<byte> idKey = buf.Slice(valueLen, KeyCodec.IdSize);

            var idExtras = new WriteExtras { OldValue = oldBuf };
            st.IdRoot = BTree.Insert(txn, st.IdRoot, idKey, valueBytes, ref idExtras);
            if (idExtras.Outcome == InsertOutcome.NoChange)
                return; // same mapping already present: nothing was written
            RecordTouched(st, id);

            if (idExtras.Outcome == InsertOutcome.Replaced)
            {
                // Unlink (oldValue, id) from the value tree.
                idKey.CopyTo(oldBuf[idExtras.OldValueLength..]);
                Span<byte> oldComposite = oldBuf[..(idExtras.OldValueLength + KeyCodec.IdSize)];
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

    public bool Remove(int id)
    {
        var txn = engine.RequireTxn();
        var st = engine.GetTxnState(txn, name);

        Span<byte> idKey = stackalloc byte[KeyCodec.IdSize];
        KeyCodec.EncodeId(idKey, id);
        Span<byte> oldBuf = stackalloc byte[NodePage.MaxValueSize + KeyCodec.IdSize];

        var idExtras = new WriteExtras { OldValue = oldBuf };
        st.IdRoot = BTree.Delete(txn, st.IdRoot, idKey, out bool removed, ref idExtras);
        if (!removed)
            return false;
        RecordTouched(st, id);

        idKey.CopyTo(oldBuf[idExtras.OldValueLength..]);
        Span<byte> composite = oldBuf[..(idExtras.OldValueLength + KeyCodec.IdSize)];
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
        if (!cursor.Key[^KeyCodec.IdSize..].SequenceEqual(idKey))
            return true;
        return cursor.MoveNext() && cursor.Key.StartsWith(valueBytes);
    }

    public T GetValue(int id)
        => TryGetValue(id, out T value)
            ? value
            : throw new KeyNotFoundException($"Id {id} is not present in index '{name}'.");

    public bool TryGetValue(int id, out T value)
    {
        using var read = engine.BeginRead();
        // The cache only ever serves committed snapshots; the writer inside its own
        // transaction (read.Snapshot is null) must see uncommitted data and bypasses it.
        bool cacheable = _valueCache is not null && read.Snapshot is not null;
        if (cacheable && _valueCache!.TryGet(id, read.Snapshot!.TxId, out value!))
            return true;

        Span<byte> idKey = stackalloc byte[KeyCodec.IdSize];
        KeyCodec.EncodeId(idKey, id);
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

    public bool ContainsKey(int id)
    {
        using var read = engine.BeginRead();
        if (_valueCache is not null && read.Snapshot is not null && _valueCache.TryGet(id, read.Snapshot.TxId, out _))
            return true;

        Span<byte> idKey = stackalloc byte[KeyCodec.IdSize];
        KeyCodec.EncodeId(idKey, id);
        return BTree.TryGet(read.Source, RootsFor(read).IdRoot, idKey, out _, out _);
    }

    public bool ContainsValue(T value)
    {
        byte[] prefix = EncodeToArray(value);
        using var read = engine.BeginRead();
        return HasValue(read.Source, RootsFor(read).ValueRoot, prefix);
    }

    void IValueCacheOwner.EvictCommittedIds(List<int>? touchedIds, bool overflow)
    {
        if (_valueCache is null)
            return;
        if (overflow || touchedIds is null)
        {
            _valueCache.Clear();
            return;
        }
        foreach (int id in touchedIds)
            _valueCache.Remove(id);
    }

    private void RecordTouched(BPlusTreeStorageEngine.MutableIndexState st, int id)
    {
        if (_valueCache is null || st.TouchedOverflow)
            return;
        var list = st.TouchedIds ??= new List<int>();
        if (list.Count >= engine.ValueCacheEntries)
        {
            // The txn touched more ids than the cache can hold: clearing at commit is cheaper.
            st.TouchedIds = null;
            st.TouchedOverflow = true;
            return;
        }
        list.Add(id);
    }

    public IEnumerable<int> GetIds(T value)
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
            int id = KeyCodec.DecodeId(key[^KeyCodec.IdSize..]);
            yield return id;
        } while (cursor.MoveNext());
    }

    public IEnumerable<KeyValuePair<int, T>> Entries
    {
        get
        {
            using var read = engine.BeginRead();
            var cursor = new BTreeCursor(read.Source);
            if (!cursor.SeekFirst(RootsFor(read).IdRoot))
                yield break;
            do
            {
                int id = KeyCodec.DecodeId(cursor.Key);
                T value = _codec.Decode(cursor.Value);
                yield return new KeyValuePair<int, T>(id, value);
            } while (cursor.MoveNext());
        }
    }

    public IEnumerable<int> Keys
    {
        get
        {
            using var read = engine.BeginRead();
            var cursor = new BTreeCursor(read.Source);
            if (!cursor.SeekFirst(RootsFor(read).IdRoot))
                yield break;
            do
            {
                yield return KeyCodec.DecodeId(cursor.Key);
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
        return _codec.Decode(cursor.Key[..^KeyCodec.IdSize]);
    }

    public T GetMaxValue()
    {
        using var read = engine.BeginRead();
        var cursor = new BTreeCursor(read.Source);
        if (!cursor.SeekLast(RootsFor(read).ValueRoot))
            throw new InvalidOperationException($"Index '{name}' is empty.");
        return _codec.Decode(cursor.Key[..^KeyCodec.IdSize]);
    }

    public IEnumerable<T> DistinctValues
    {
        get
        {
            // Same-value composites are contiguous in the value tree; decode once per prefix change.
            byte[]? lastValueBytes = null;
            using var read = engine.BeginRead();
            var cursor = new BTreeCursor(read.Source);
            if (!cursor.SeekFirst(RootsFor(read).ValueRoot))
                yield break;
            do
            {
                ReadOnlySpan<byte> valueBytes = cursor.Key[..^KeyCodec.IdSize];
                if (lastValueBytes is null || !valueBytes.SequenceEqual(lastValueBytes))
                {
                    lastValueBytes = valueBytes.ToArray(); // the key span dies when the cursor moves
                    yield return _codec.Decode(valueBytes);
                }
            } while (cursor.MoveNext());
        }
    }

    /// <summary>
    /// Half-open scan bounds [startKey, stopKey) over the (value, id) composite key space.
    /// Prefix-freedom guarantees neither bound can collide with a stored composite.
    /// </summary>
    private (byte[] StartKey, byte[] StopKey) BuildRangeKeys(T from, T to, bool includeFrom, bool includeTo)
        => (BuildStartKey(from, includeFrom), BuildStopKey(to, includeTo));

    // Composite keys are (value, id); id spans 0x00000000..0xFFFFFFFF after encoding.
    private byte[] BuildStartKey(T from, bool includeFrom)
    {
        byte[] encFrom = EncodeToArray(from);
        if (includeFrom)
            return encFrom; // sorts before every (from, id) composite
        byte[] startKey = new byte[encFrom.Length + KeyCodec.IdSize + 1];
        encFrom.CopyTo(startKey, 0);
        startKey.AsSpan(encFrom.Length, KeyCodec.IdSize).Fill(0xFF); // past the last (from, id) composite
        return startKey;
    }

    private byte[] BuildStopKey(T to, bool includeTo)
    {
        // The stop key is the first key NOT in range.
        byte[] encTo = EncodeToArray(to);
        if (!includeTo)
            return encTo; // prefix-freedom: every smaller value's composite compares below this
        byte[] stopKey = new byte[encTo.Length + KeyCodec.IdSize + 1];
        encTo.CopyTo(stopKey, 0);
        stopKey.AsSpan(encTo.Length, KeyCodec.IdSize).Fill(0xFF);
        return stopKey;
    }

    public IEnumerable<int> GetIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
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
                int id = KeyCodec.DecodeId(key[^KeyCodec.IdSize..]);
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
                int id = KeyCodec.DecodeId(key[^KeyCodec.IdSize..]);
                yield return id;
            } while (cursor.MoveNext());
        }
    }

    public IEnumerable<KeyValuePair<int, T>> GetEntriesInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
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

    public IEnumerable<int> GetIdsGreaterThan(T value, bool includeValue = true, bool descending = false)
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
                yield return KeyCodec.DecodeId(key[^KeyCodec.IdSize..]);
            } while (cursor.MovePrevious());
        }
        else
        {
            if (!cursor.Seek(RootsFor(read).ValueRoot, startKey))
                yield break;
            do
            {
                yield return KeyCodec.DecodeId(cursor.Key[^KeyCodec.IdSize..]);
            } while (cursor.MoveNext());
        }
    }

    public IEnumerable<int> GetIdsSmallerThan(T value, bool includeValue = true, bool descending = false)
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
                yield return KeyCodec.DecodeId(cursor.Key[^KeyCodec.IdSize..]);
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
                yield return KeyCodec.DecodeId(key[^KeyCodec.IdSize..]);
            } while (cursor.MoveNext());
        }
    }

    public int CountIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true)
    {
        var (startKey, stopKey) = BuildRangeKeys(from, to, includeFrom, includeTo);
        using var read = engine.BeginRead();
        return CountFrom(read, startKey, stopKey);
    }

    public int CountIdsGreaterThan(T value, bool includeValue = true)
    {
        byte[] startKey = BuildStartKey(value, includeValue);
        using var read = engine.BeginRead();
        return CountFrom(read, startKey, null);
    }

    public int CountIdsSmallerThan(T value, bool includeValue = true)
    {
        byte[] stopKey = BuildStopKey(value, includeValue);
        using var read = engine.BeginRead();
        var cursor = new BTreeCursor(read.Source);
        if (!cursor.SeekFirst(RootsFor(read).ValueRoot))
            return 0;
        int n = 0;
        do
        {
            if (cursor.Key.SequenceCompareTo(stopKey) >= 0)
                break;
            n++;
        } while (cursor.MoveNext());
        return n;
    }

    /// <summary>Counts value-tree entries from startKey up to stopKey (or the end when null), without decoding.</summary>
    private int CountFrom(in BPlusTreeStorageEngine.ReadHandle read, byte[] startKey, byte[]? stopKey)
    {
        var cursor = new BTreeCursor(read.Source);
        if (!cursor.Seek(RootsFor(read).ValueRoot, startKey))
            return 0;
        int n = 0;
        do
        {
            if (stopKey is not null && cursor.Key.SequenceCompareTo(stopKey) >= 0)
                break;
            n++;
        } while (cursor.MoveNext());
        return n;
    }

    /// <summary>Splits a composite key into (id, decoded value), reusing the last decode for repeated values.</summary>
    private KeyValuePair<int, T> DecodeEntry(ReadOnlySpan<byte> key, ref byte[]? lastValueBytes, ref T lastValue)
    {
        ReadOnlySpan<byte> valueBytes = key[..^KeyCodec.IdSize];
        if (lastValueBytes is null || !valueBytes.SequenceEqual(lastValueBytes))
        {
            lastValueBytes = valueBytes.ToArray(); // the key span dies when the cursor moves
            lastValue = _codec.Decode(valueBytes);
        }
        return new KeyValuePair<int, T>(KeyCodec.DecodeId(key[^KeyCodec.IdSize..]), lastValue);
    }

    private (uint ValueRoot, uint IdRoot) RootsFor(in BPlusTreeStorageEngine.ReadHandle read)
    {
        if (read.Txn is not null)
        {
            var st = engine.GetTxnState(read.Txn, name);
            return (st.ValueRoot, st.IdRoot);
        }
        var committed = engine.GetCommittedState(read.Snapshot!, name);
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
