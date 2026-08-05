using System.Buffers;
using System.Runtime.CompilerServices;
using Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex;

/// <summary>
/// Unordered disk-based index: one extendible-hash table mapping id → value, generic over the id
/// type (int, ulong or Guid — see <see cref="IdCodec{TId}"/>; the sealed leaves below bind each id
/// type to its public interface). It lives in the same file, transactions and snapshots as
/// <see cref="BPlusTreeIndex{TId,T}"/> and is the same thing minus the ordering:
/// <list type="bullet">
/// <item>a lookup is one page read — the directory sits in memory, so the id's hash names the
/// bucket page directly, with no descent and no comparisons along the way;</item>
/// <item>a write copies one page instead of a whole root-to-leaf path, and touches one tree
/// instead of two, so it moves a fraction of the bytes a sorted write does;</item>
/// <item>in exchange there is no order and no value index: <see cref="GetIds"/> is a full scan,
/// and <see cref="Keys"/>/<see cref="Entries"/> come out in bucket order, which changes as the
/// table grows.</item>
/// </list>
/// Buckets split when they fill up and the directory doubles when a bucket reaches the global
/// depth (see <see cref="HashDirectory"/>), so both stay proportional to the data with no
/// reorganization pass and no rebalancing.
/// </summary>
[SkipLocalsInit] // Set/Remove stackalloc scratch per call; zeroing it is pure cost (every read is length-bounded to written bytes)
internal abstract class HashIndex<TId, T>(BPlusTreeStorageEngine engine, string name, bool hasEngineTimestamp)
    : IDictionaryIndex<TId, T>, IValueCacheOwner, IIndexTimestamp
    where TId : unmanaged
    where T : notnull
{
    private const int StackBufferSize = 512;
    private const int MaxIdSize = 16; // largest supported id encoding (Guid); stack buffers use this so their size stays a JIT constant

    // A property forwarding to IdCodec<TId>, not a static field on this class: TId is always a
    // value type, so IdCodec<TId> is an exact instantiation whose readonly Size JIT-folds to a
    // constant even inside this class's canonically shared code (reference-type T).
    private static int IdSize => IdCodec<TId>.Size;

    private readonly IKeyCodec<T> _codec = KeyCodec.Get<T>();

    private readonly ValueCache<TId, T>? _valueCache =
        engine.ValueCacheEntries > 0 ? new ValueCache<TId, T>(engine.ValueCacheEntries) : null;

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

    // ---- writes ----

    public void Set(TId id, T value)
    {
        var txn = engine.RequireTxn();
        var st = engine.GetTxnState(txn, name);
        MutableHashDir dir = st.Dir!;

        int maxSize = _codec.GetMaxSize(value);
        byte[]? rented = maxSize > StackBufferSize ? ArrayPool<byte>.Shared.Rent(maxSize) : null;
        Span<byte> buf = rented ?? stackalloc byte[StackBufferSize];
        try
        {
            int valueLen = _codec.Encode(buf, value);
            if (valueLen > HashPage.MaxValueSize)
                throw new ArgumentException($"Encoded value is {valueLen} bytes; the maximum is {HashPage.MaxValueSize}.");
            ReadOnlySpan<byte> valueBytes = buf[..valueLen];

            Span<byte> key = stackalloc byte[MaxIdSize];
            key = key[..IdSize];
            IdCodec<TId>.Encode(key, id);
            ulong hash = IdCodec<TId>.Hash(id);
            ushort tag = HashDirectory.TagOf(hash);
            int cellSize = HashPage.CellSize(IdSize, valueLen);

            // Each pass either writes the cell or makes room (a bucket split, preceded by a
            // directory doubling when the bucket is already at the global depth) and retries.
            while (true)
            {
                int slot = (int)(hash & dir.Mask);
                uint pageId = dir[slot];

                if (pageId == 0)
                {
                    // No bucket for this slot yet: a fresh one is addressed by the full depth, so
                    // it belongs to this slot alone.
                    var (freshId, freshPage) = txn.Allocate();
                    HashPage.Init(freshPage, dir.GlobalDepth);
                    dir.Set(slot, freshId);
                    Insert(freshPage, tag, key, valueBytes);
                    MarkAdded(st, id);
                    return;
                }

                byte[] page = txn.GetPage(pageId);
                int cell = HashPage.Find(page, tag, key);
                if (cell >= 0)
                {
                    if (HashPage.Value(page, cell, IdSize).SequenceEqual(valueBytes))
                        return; // same mapping already present: nothing was written
                    if (HashPage.CanFit(page, cellSize, freedCells: 1, freedBytes: HashPage.CellSizeAt(page, cell, IdSize)))
                    {
                        // A copy is byte-identical, so the cell keeps its index.
                        page = CowBucket(txn, dir, slot, pageId).Page;
                        HashPage.RemoveAt(page, cell, IdSize);
                        Insert(page, tag, key, valueBytes);
                        MarkWritten(st, id);
                        return;
                    }
                }
                else if (HashPage.CanFit(page, cellSize))
                {
                    page = CowBucket(txn, dir, slot, pageId).Page;
                    Insert(page, tag, key, valueBytes);
                    MarkAdded(st, id);
                    return;
                }

                Grow(txn, dir, slot, pageId);
                st.Dirty = true;
            }
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
        var st = engine.GetTxnState(txn, name);
        MutableHashDir dir = st.Dir!;

        Span<byte> key = stackalloc byte[MaxIdSize];
        key = key[..IdSize];
        IdCodec<TId>.Encode(key, id);
        ulong hash = IdCodec<TId>.Hash(id);

        int slot = (int)(hash & dir.Mask);
        uint pageId = dir[slot];
        if (pageId == 0)
            return false;

        byte[] page = txn.GetPage(pageId);
        int cell = HashPage.Find(page, HashDirectory.TagOf(hash), key);
        if (cell < 0)
            return false;

        int localDepth = HashPage.LocalDepth(page);
        (uint cowId, page) = CowBucket(txn, dir, slot, pageId);
        HashPage.RemoveAt(page, cell, IdSize);
        if (HashPage.Count(page) == 0)
        {
            // An emptied bucket gives its page back; its slots revert to "no bucket" and each
            // will create its own full-depth bucket if it is written to again.
            dir.Repoint(slot, localDepth, 0);
            txn.Free(cowId);
        }
        st.IdCount--;
        MarkWritten(st, id);
        return true;
    }

    private void MarkAdded(BPlusTreeStorageEngine.MutableIndexState st, TId id)
    {
        st.IdCount++;
        MarkWritten(st, id);
    }

    private void MarkWritten(BPlusTreeStorageEngine.MutableIndexState st, TId id)
    {
        st.Dirty = true;
        RecordTouched(st, id);
    }

    private static void Insert(byte[] page, ushort tag, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (!HashPage.TryInsert(page, tag, key, value, IdSize))
            throw new InvalidOperationException("Internal error: a cell that was measured to fit must always insert.");
    }

    /// <summary>Makes the bucket writable; a copy lands on a new page, which every slot naming that bucket has to follow.</summary>
    private static (uint Id, byte[] Page) CowBucket(BPlusTreeStorageEngine.WriteTxn txn, MutableHashDir dir, int slot, uint pageId)
    {
        var (cowId, page) = txn.Cow(pageId);
        if (cowId != pageId)
            dir.Repoint(slot, HashPage.LocalDepth(page), cowId);
        return (cowId, page);
    }

    /// <summary>Makes room for one more cell in the bucket at <paramref name="slot"/>: one split, or one doubling when the bucket already spans the full depth.</summary>
    private void Grow(BPlusTreeStorageEngine.WriteTxn txn, MutableHashDir dir, int slot, uint pageId)
    {
        byte[] old = txn.GetPage(pageId);
        int localDepth = HashPage.LocalDepth(old);
        if (localDepth == dir.GlobalDepth)
        {
            if (dir.GlobalDepth >= HashDirectory.MaxGlobalDepth)
                throw new InvalidOperationException(
                    $"Index '{name}' cannot grow past a directory depth of {HashDirectory.MaxGlobalDepth}: the ids sharing a full bucket also share too many hash bits to be separated.");
            dir.Double(); // the retry re-reads the deeper directory and splits on the next pass
            return;
        }

        // Split on bit `localDepth` of the hash — the first bit the bucket's entries are allowed
        // to disagree on. Both halves are built fresh, so the old page is pure input.
        var (lowId, lowPage) = txn.Allocate();
        var (highId, highPage) = txn.Allocate();
        HashPage.Init(lowPage, localDepth + 1);
        HashPage.Init(highPage, localDepth + 1);

        int count = HashPage.Count(old);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> key = HashPage.Key(old, i, IdSize);
            ulong hash = IdCodec<TId>.Hash(IdCodec<TId>.Decode(key));
            Insert(((hash >> localDepth) & 1) == 0 ? lowPage : highPage,
                HashDirectory.TagOf(hash), key, HashPage.Value(old, i, IdSize));
        }

        int step = 1 << localDepth;
        for (int j = slot & (step - 1); j < dir.Size; j += step)
            dir.Set(j, ((j >> localDepth) & 1) == 0 ? lowId : highId);
        txn.Free(pageId);
    }

    // ---- reads ----

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

        if (!TryFind(read, id, out byte[] page, out int cell))
        {
            value = default!;
            return false;
        }
        value = _codec.Decode(HashPage.Value(page, cell, IdSize));

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
        return TryFind(read, id, out _, out _);
    }

    /// <summary>The one page read a lookup costs: hash the id, index the directory, scan the bucket's tags.</summary>
    private bool TryFind(in BPlusTreeStorageEngine.ReadHandle read, TId id, out byte[] page, out int cell)
    {
        DirView dir = DirFor(read);
        ulong hash = IdCodec<TId>.Hash(id);
        uint pageId = dir[dir.SlotOf(hash)];
        if (pageId == 0)
        {
            page = [];
            cell = -1;
            return false;
        }
        Span<byte> key = stackalloc byte[MaxIdSize];
        key = key[..IdSize];
        IdCodec<TId>.Encode(key, id);
        page = read.Source.GetPage(pageId);
        cell = HashPage.Find(page, HashDirectory.TagOf(hash), key);
        return cell >= 0;
    }

    /// <summary>
    /// Every id mapped to <paramref name="value"/>, in bucket order. There is no value index, so
    /// this reads every bucket — but it compares encoded bytes, so nothing is decoded on the way.
    /// </summary>
    public IEnumerable<TId> GetIds(T value)
    {
        byte[] target = EncodeToArray(value);
        using var read = engine.BeginRead();
        foreach (byte[] page in Buckets(read.Source, DirFor(read)))
        {
            int count = HashPage.Count(page);
            for (int i = 0; i < count; i++)
            {
                if (HashPage.Value(page, i, IdSize).SequenceEqual(target))
                    yield return IdCodec<TId>.Decode(HashPage.Key(page, i, IdSize));
            }
        }
    }

    /// <summary>Every (id, value) entry, in bucket order — which is neither id order nor insertion order, and shifts as the table grows.</summary>
    public IEnumerable<KeyValuePair<TId, T>> Entries
    {
        get
        {
            using var read = engine.BeginRead();
            foreach (byte[] page in Buckets(read.Source, DirFor(read)))
            {
                int count = HashPage.Count(page);
                for (int i = 0; i < count; i++)
                {
                    yield return new KeyValuePair<TId, T>(
                        IdCodec<TId>.Decode(HashPage.Key(page, i, IdSize)),
                        _codec.Decode(HashPage.Value(page, i, IdSize)));
                }
            }
        }
    }

    /// <summary>Every id with an entry, in bucket order (see <see cref="Entries"/>).</summary>
    public IEnumerable<TId> Keys
    {
        get
        {
            using var read = engine.BeginRead();
            foreach (byte[] page in Buckets(read.Source, DirFor(read)))
            {
                int count = HashPage.Count(page);
                for (int i = 0; i < count; i++)
                    yield return IdCodec<TId>.Decode(HashPage.Key(page, i, IdSize));
            }
        }
    }

    /// <summary>Each bucket exactly once: a bucket of local depth d answers to every slot sharing its low d bits, and is visited at the lowest of them.</summary>
    private static IEnumerable<byte[]> Buckets(IPageSource src, DirView dir)
    {
        for (int slot = 0; slot < dir.Size; slot++)
        {
            uint pageId = dir[slot];
            if (pageId == 0)
                continue;
            byte[] page = src.GetPage(pageId);
            if ((slot & ((1 << HashPage.LocalDepth(page)) - 1)) != slot)
                continue;
            yield return page;
        }
    }

    private DirView DirFor(in BPlusTreeStorageEngine.ReadHandle read)
        => read.Txn is not null
            ? engine.GetTxnState(read.Txn, name).Dir!.View
            : engine.GetCommittedState(read.Snapshot!, name).Dir!.View;

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

    // ---- value cache ----

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
}

internal sealed class HashIntIndex<T>(BPlusTreeStorageEngine engine, string name, bool hasEngineTimestamp)
    : HashIndex<int, T>(engine, name, hasEngineTimestamp), IIntIndex<T> where T : notnull;

internal sealed class HashUlongIndex<T>(BPlusTreeStorageEngine engine, string name, bool hasEngineTimestamp)
    : HashIndex<ulong, T>(engine, name, hasEngineTimestamp), IUlongIndex<T> where T : notnull;

internal sealed class HashGuidIndex<T>(BPlusTreeStorageEngine engine, string name, bool hasEngineTimestamp)
    : HashIndex<Guid, T>(engine, name, hasEngineTimestamp), IGuidIndex<T> where T : notnull;
