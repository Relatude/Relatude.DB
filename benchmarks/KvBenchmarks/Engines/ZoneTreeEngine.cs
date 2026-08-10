using Relatude.DB.Datastores.Indexes.BTreeIndex;
using ZoneTree;
using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace KvBenchmarks.Engines;

/// <summary>
/// <see cref="IStorageEngine"/> on ZoneTree (LSM tree). Each index is two trees: id -> encoded
/// value for point lookups, and an ordered tree keyed by the composite (value bytes + id) for
/// range scans. Writes go straight to the trees (ZoneTree WAL = AsyncCompressed); the engine's
/// transactions only group work, rollback is not supported, and durable commits save metadata
/// (best effort â€” ZoneTree has no group-commit primitive).
/// </summary>
public sealed class ZoneTreeEngine : IStorageEngine, IBenchFlush, IDisposable
{
    private readonly string _folder;
    private readonly Dictionary<string, object> _openIndexes = new();
    private long _timestamp;
    private bool _inTxn;

    public ZoneTreeEngine(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        string tsFile = TsFile;
        _timestamp = File.Exists(tsFile) && long.TryParse(File.ReadAllText(tsFile), out long ts) ? ts : 0;
    }

    private string TsFile => Path.Combine(_folder, "timestamp.txt");

    public ISortedIntIndex<T> OpenOrCreateSortedIntIndex<T>(string name) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_openIndexes.TryGetValue(name, out object? open))
        {
            return open as ZoneTreeIndex<T>
                ?? throw new InvalidOperationException($"Index '{name}' is already open with a different value type or layout.");
        }
        string dir = Path.Combine(_folder, name);
        bool existed = Directory.Exists(dir);
        var index = new ZoneTreeIndex<T>(this, dir, hasEngineTimestamp: existed);
        _openIndexes[name] = index;
        return index;
    }

    /// <summary>
    /// The unordered layout: the id → value tree alone, without the composite (value, id) tree
    /// <see cref="OpenOrCreateSortedIntIndex{T}"/> maintains beside it. Every write then touches one LSM
    /// tree instead of two, and ordered queries are gone rather than slow.
    /// </summary>
    public IIntIndex<T> OpenOrCreateIntHashIndex<T>(string name) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_openIndexes.TryGetValue(name, out object? open))
        {
            if (open is ZoneTreeIndex<T>)
                throw new InvalidOperationException($"Index '{name}' is already open as a sorted index.");
            return open as ZoneTreeHashIndex<T>
                ?? throw new InvalidOperationException($"Index '{name}' is already open with a different value type.");
        }
        string dir = Path.Combine(_folder, name);
        bool existed = Directory.Exists(dir);
        var index = new ZoneTreeHashIndex<T>(this, dir, hasEngineTimestamp: existed);
        _openIndexes[name] = index;
        return index;
    }

    public ISortedUlongIndex<T> OpenOrCreateSortedUlongIndex<T>(string name) where T : notnull
        => throw new NotSupportedException("The benchmark engines only support int-keyed indexes.");

    public ISortedGuidIndex<T> OpenOrCreateSortedGuidIndex<T>(string name) where T : notnull
        => throw new NotSupportedException("The benchmark engines only support int-keyed indexes.");

    public IUlongIndex<T> OpenOrCreateUlongHashIndex<T>(string name) where T : notnull
        => throw new NotSupportedException("The benchmark engines only support int-keyed indexes.");

    public IGuidIndex<T> OpenOrCreateGuidHashIndex<T>(string name) where T : notnull
        => throw new NotSupportedException("The benchmark engines only support int-keyed indexes.");

    public bool IsInTransaction => _inTxn;

    public void BeginTransaction()
    {
        if (_inTxn) throw new InvalidOperationException("A transaction is already active.");
        _inTxn = true;
    }

    public void CommitTransaction(long timestamp, bool durable)
    {
        if (!_inTxn) throw new InvalidOperationException("No active transaction.");
        _inTxn = false;
        _timestamp = timestamp;
        if (durable)
        {
            foreach (object open in _openIndexes.Values)
                ((IZoneTreeIndexInternal)open).SaveMetaData();
            File.WriteAllText(TsFile, timestamp.ToString());
        }
        foreach (object open in _openIndexes.Values)
            ((IZoneTreeIndexInternal)open).AdoptEngineTimestamp();
    }

    public void RollbackTransaction()
        => throw new NotSupportedException("The ZoneTree engine applies writes immediately; rollback is not supported.");

    public long GetTimestamp() => _timestamp;

    public void SetTimestamp(long timestamp)
    {
        if (_inTxn) throw new InvalidOperationException("SetTimestamp cannot run while a transaction is active.");
        BeginTransaction();
        CommitTransaction(timestamp, durable: true);
    }

    public void FlushAllToDisk()
    {
        foreach (object open in _openIndexes.Values)
            ((IZoneTreeIndexInternal)open).FlushToDisk();
        File.WriteAllText(TsFile, _timestamp.ToString());
    }

    public long GetTotalDiskSpace() => DiskUsage.OfDirectory(_folder);

    public void DeleteAll()
    {
        if (_inTxn) throw new InvalidOperationException("DeleteAll cannot run while a transaction is active.");
        foreach (object open in _openIndexes.Values)
            ((IZoneTreeIndexInternal)open).ClearData();
        foreach (string dir in Directory.GetDirectories(_folder))
        {
            string name = Path.GetFileName(dir);
            if (!_openIndexes.ContainsKey(name)) Directory.Delete(dir, recursive: true);
        }
        _timestamp = 0;
        File.WriteAllText(TsFile, "0");
    }

    public void DeleteUnopenedIndexes()
    {
        if (_inTxn) throw new InvalidOperationException("DeleteUnopenedIndexes cannot run while a transaction is active.");
        foreach (string dir in Directory.GetDirectories(_folder))
        {
            string name = Path.GetFileName(dir);
            if (!_openIndexes.ContainsKey(name)) Directory.Delete(dir, recursive: true);
        }
    }

    public void Dispose()
    {
        foreach (object open in _openIndexes.Values)
            (open as IDisposable)?.Dispose();
        File.WriteAllText(TsFile, _timestamp.ToString());
    }
}

internal interface IZoneTreeIndexInternal
{
    void AdoptEngineTimestamp();
    void SaveMetaData();
    void FlushToDisk();
    void ClearData();
}

/// <summary>
/// The unordered layout: a single ZoneTree keyed by id. <see cref="ZoneTreeIndex{T}"/> derives from
/// it and adds the composite (value, id) tree that ordered queries need, so the two rows of the
/// benchmark differ by exactly that tree and the writes that maintain it.
/// </summary>
public class ZoneTreeHashIndex<T> : IIntIndex<T>, IZoneTreeIndexInternal, IDisposable where T : notnull
{
    private readonly ZoneTreeEngine _engine;
    protected readonly IOrderedCodec<T> Codec = OrderedCodec.Get<T>();
    protected readonly IZoneTree<int, Memory<byte>> ById;         // id -> encoded value
    private int _count;
    private bool _hasEngineTimestamp;

    internal ZoneTreeHashIndex(ZoneTreeEngine engine, string dir, bool hasEngineTimestamp)
    {
        _engine = engine;
        _hasEngineTimestamp = hasEngineTimestamp;

        ById = new ZoneTreeFactory<int, Memory<byte>>()
            .SetDataDirectory(Path.Combine(dir, "byid"))
            .SetComparer(new Int32ComparerAscending())
            .SetKeySerializer(new Int32Serializer())
            .SetValueSerializer(new ByteArraySerializer())
            .SetIsDeletedDelegate((in int _, in Memory<byte> v) => v.Length == 0)
            .SetMarkValueDeletedDelegate((ref Memory<byte> v) => v = Memory<byte>.Empty)
            .ConfigureWriteAheadLogOptions(o => o.WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed)
            .OpenOrCreate();

        if (hasEngineTimestamp)
            _count = checked((int)ById.Count());
    }

    public int Count => _count;

    /// <summary>Hook for the ordered layout: the id tree has just taken <paramref name="newValue"/>, replacing <paramref name="old"/> when <paramref name="existed"/>.</summary>
    protected virtual void OnSet(int id, Memory<byte> old, bool existed, byte[] newValue) { }

    /// <summary>Hook for the ordered layout: (<paramref name="old"/>, <paramref name="id"/>) is about to leave the id tree.</summary>
    protected virtual void OnRemoved(int id, Memory<byte> old) { }

    protected byte[] EncodeValue(T value)
    {
        byte[] tmp = new byte[Codec.GetMaxSize(value)];
        int n = Codec.Encode(tmp, value);
        return n == tmp.Length ? tmp : tmp[..n];
    }

    public void Set(int id, T value)
    {
        RequireTxn();
        bool existed = ById.TryGet(in id, out Memory<byte> old);
        byte[] valueBytes = EncodeValue(value);
        Memory<byte> valueMem = valueBytes;
        ById.Upsert(in id, in valueMem);
        OnSet(id, old, existed, valueBytes);
        if (!existed) _count++;
    }

    public bool Remove(int id)
    {
        RequireTxn();
        if (!ById.TryGet(in id, out Memory<byte> old)) return false;
        OnRemoved(id, old);
        ById.ForceDelete(in id);
        _count--;
        return true;
    }

    private void RequireTxn()
    {
        if (!_engine.IsInTransaction)
            throw new InvalidOperationException("Mutations require an active transaction (call BeginTransaction first).");
    }

    public T GetValue(int id)
        => TryGetValue(id, out T value) ? value : throw new KeyNotFoundException($"Id {id} not found.");

    public bool TryGetValue(int id, out T value)
    {
        if (ById.TryGet(in id, out Memory<byte> bytes))
        {
            value = Codec.Decode(bytes.Span);
            return true;
        }
        value = default!;
        return false;
    }

    public bool ContainsKey(int id) => ById.ContainsKey(in id);

    /// <summary>Without a value tree this is a full scan of the id tree, comparing encoded bytes.</summary>
    public virtual IEnumerable<int> GetIds(T value)
    {
        byte[] valueBytes = EncodeValue(value);
        using var it = ById.CreateIterator(IteratorType.NoRefresh, false, false);
        while (it.Next())
            if (it.CurrentValue.Span.SequenceEqual(valueBytes))
                yield return it.CurrentKey;
    }

    public IEnumerable<KeyValuePair<int, T>> Entries
    {
        get
        {
            using var it = ById.CreateIterator(IteratorType.NoRefresh, false, false);
            while (it.Next())
                yield return new(it.CurrentKey, Codec.Decode(it.CurrentValue.Span));
        }
    }

    public IEnumerable<int> Keys
    {
        get
        {
            using var it = ById.CreateIterator(IteratorType.NoRefresh, false, false);
            while (it.Next())
                yield return it.CurrentKey;
        }
    }

    public long GetTimestamp() => _hasEngineTimestamp ? _engine.GetTimestamp() : 0;

    public void SetTimestamp(long timestamp)
    {
        if (timestamp == 0) { _hasEngineTimestamp = false; return; }
        if (timestamp != _engine.GetTimestamp())
            throw new InvalidOperationException("An index timestamp is always 0 or the engine's current timestamp.");
        _hasEngineTimestamp = true;
    }

    void IZoneTreeIndexInternal.AdoptEngineTimestamp() => _hasEngineTimestamp = true;

    public virtual void SaveMetaData() => ById.Maintenance.SaveMetaData();

    public virtual void FlushToDisk() => Flush(ById);

    protected static void Flush<TKey, TValue>(IZoneTree<TKey, TValue> tree)
    {
        tree.Maintenance.MoveMutableSegmentForward();
        tree.Maintenance.StartMergeOperation()?.Join();
        tree.Maintenance.SaveMetaData();
    }

    public void ClearData()
    {
        foreach (int id in Keys.ToArray())
        {
            if (!ById.TryGet(in id, out Memory<byte> old)) continue;
            OnRemoved(id, old);
            ById.ForceDelete(in id);
        }
        _count = 0;
    }

    public virtual void Dispose()
    {
        ById.Maintenance.SaveMetaData();
        ById.Dispose();
    }
}

public sealed class ZoneTreeIndex<T> : ZoneTreeHashIndex<T>, ISortedIntIndex<T> where T : notnull
{
    private readonly IZoneTree<Memory<byte>, byte> _byValue;      // composite (value, id) -> 0

    internal ZoneTreeIndex(ZoneTreeEngine engine, string dir, bool hasEngineTimestamp)
        : base(engine, dir, hasEngineTimestamp)
    {
        _byValue = new ZoneTreeFactory<Memory<byte>, byte>()
            .SetDataDirectory(Path.Combine(dir, "byval"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteSerializer())
            .SetIsDeletedDelegate((in Memory<byte> _, in byte v) => v != 0)
            .SetMarkValueDeletedDelegate((ref byte v) => v = 1)
            .ConfigureWriteAheadLogOptions(o => o.WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed)
            .OpenOrCreate();
    }

    protected override void OnSet(int id, Memory<byte> old, bool existed, byte[] newValue)
    {
        if (existed)
        {
            Memory<byte> oldComposite = Composite(old.Span, id);
            _byValue.ForceDelete(in oldComposite);
        }
        Memory<byte> composite = Composite(newValue, id);
        _byValue.Upsert(in composite, 0);
    }

    protected override void OnRemoved(int id, Memory<byte> old)
    {
        Memory<byte> oldComposite = Composite(old.Span, id);
        _byValue.ForceDelete(in oldComposite);
    }

    public int DistinctValueCount
    {
        get
        {
            int distinct = 0;
            byte[]? prev = null;
            using var it = _byValue.CreateIterator(IteratorType.NoRefresh, false, false);
            while (it.Next())
            {
                var key = it.CurrentKey;
                var val = OrderedCodec.ValueOfComposite(key.Span);
                if (prev is null || !val.SequenceEqual(prev))
                {
                    distinct++;
                    prev = val.ToArray();
                }
            }
            return distinct;
        }
    }

    private static byte[] Composite(ReadOnlySpan<byte> valueBytes, int id)
    {
        byte[] c = new byte[valueBytes.Length + OrderedCodec.IdSize];
        valueBytes.CopyTo(c);
        OrderedCodec.WriteId(c.AsSpan(valueBytes.Length), id);
        return c;
    }

    public bool ContainsValue(T value)
    {
        foreach (int _ in GetIds(value)) return true;
        return false;
    }

    /// <summary>A seek into the value tree here, rather than the base class's scan of the id tree.</summary>
    public override IEnumerable<int> GetIds(T value)
    {
        byte[] valueBytes = EncodeValue(value);
        return ScanAscending(valueBytes, true, valueBytes, true, hasFrom: true, hasTo: true)
            .Select(k => OrderedCodec.IdOfComposite(k.Span));
    }

    public IEnumerable<T> DistinctValues
    {
        get
        {
            byte[]? prev = null;
            using var it = _byValue.CreateIterator(IteratorType.NoRefresh, false, false);
            while (it.Next())
            {
                byte[] val = OrderedCodec.ValueOfComposite(it.CurrentKey.Span).ToArray();
                if (prev is null || !val.AsSpan().SequenceEqual(prev))
                {
                    prev = val;
                    yield return Codec.Decode(val);
                }
            }
        }
    }

    public T GetMinValue()
    {
        using var it = _byValue.CreateIterator(IteratorType.NoRefresh, false, false);
        if (!it.Next()) throw new InvalidOperationException("The index is empty.");
        return Codec.Decode(OrderedCodec.ValueOfComposite(it.CurrentKey.Span));
    }

    public T GetMaxValue()
    {
        using var it = _byValue.CreateReverseIterator(IteratorType.NoRefresh, false, false);
        if (!it.Next()) throw new InvalidOperationException("The index is empty.");
        return Codec.Decode(OrderedCodec.ValueOfComposite(it.CurrentKey.Span));
    }

    // ---- ordered scans over the composite tree ----

    /// <summary>Ascending composite scan; null bounds mean unbounded on that side.</summary>
    private IEnumerable<Memory<byte>> ScanAscending(byte[]? fromValue, bool includeFrom, byte[]? toValue, bool includeTo, bool hasFrom, bool hasTo)
    {
        using var it = _byValue.CreateIterator(IteratorType.NoRefresh, false, false);
        if (hasFrom)
        {
            Memory<byte> seek = Composite(fromValue, int.MinValue);
            it.Seek(in seek);
        }
        while (it.Next())
        {
            Memory<byte> key = it.CurrentKey;
            if (hasFrom && !includeFrom && ValuePartEquals(key, fromValue!)) continue;
            if (hasTo)
            {
                int cmp = OrderedCodec.Compare(OrderedCodec.ValueOfComposite(key.Span), toValue);
                if (cmp > 0 || (cmp == 0 && !includeTo)) yield break;
            }
            yield return key;
        }
    }

    /// <summary>Descending composite scan; null bounds mean unbounded on that side.</summary>
    private IEnumerable<Memory<byte>> ScanDescending(byte[]? fromValue, bool includeFrom, byte[]? toValue, bool includeTo, bool hasFrom, bool hasTo)
    {
        using var it = _byValue.CreateReverseIterator(IteratorType.NoRefresh, false, false);
        if (hasTo)
        {
            Memory<byte> seek = Composite(toValue, int.MaxValue);
            it.Seek(in seek);
        }
        while (it.Next())
        {
            Memory<byte> key = it.CurrentKey;
            if (hasTo && !includeTo && ValuePartEquals(key, toValue!)) continue;
            if (hasFrom)
            {
                int cmp = OrderedCodec.Compare(OrderedCodec.ValueOfComposite(key.Span), fromValue);
                if (cmp < 0 || (cmp == 0 && !includeFrom)) yield break;
            }
            yield return key;
        }
    }

    private static bool ValuePartEquals(Memory<byte> composite, byte[] valueBytes)
        => OrderedCodec.ValueOfComposite(composite.Span).SequenceEqual(valueBytes);

    private IEnumerable<Memory<byte>> Scan(byte[]? fromValue, bool includeFrom, byte[]? toValue, bool includeTo, bool hasFrom, bool hasTo, bool descending)
        => descending
            ? ScanDescending(fromValue, includeFrom, toValue, includeTo, hasFrom, hasTo)
            : ScanAscending(fromValue, includeFrom, toValue, includeTo, hasFrom, hasTo);

    public IEnumerable<int> GetIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
        => Scan(EncodeValue(from), includeFrom, EncodeValue(to), includeTo, true, true, descending)
            .Select(k => OrderedCodec.IdOfComposite(k.Span));

    public IEnumerable<KeyValuePair<int, T>> GetEntriesInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
        => Scan(EncodeValue(from), includeFrom, EncodeValue(to), includeTo, true, true, descending)
            .Select(k => new KeyValuePair<int, T>(OrderedCodec.IdOfComposite(k.Span), Codec.Decode(OrderedCodec.ValueOfComposite(k.Span))));

    public IEnumerable<int> GetIdsGreaterThan(T value, bool includeValue = true, bool descending = false)
        => Scan(EncodeValue(value), includeValue, null, true, true, false, descending)
            .Select(k => OrderedCodec.IdOfComposite(k.Span));

    public IEnumerable<int> GetIdsSmallerThan(T value, bool includeValue = true, bool descending = false)
        => Scan(null, true, EncodeValue(value), includeValue, false, true, descending)
            .Select(k => OrderedCodec.IdOfComposite(k.Span));

    public int CountIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true)
        => ScanAscending(EncodeValue(from), includeFrom, EncodeValue(to), includeTo, true, true).Count();

    public int CountIdsGreaterThan(T value, bool includeValue = true)
        => ScanAscending(EncodeValue(value), includeValue, null, true, true, false).Count();

    public int CountIdsSmallerThan(T value, bool includeValue = true)
        => ScanAscending(null, true, EncodeValue(value), includeValue, false, true).Count();

    public override void SaveMetaData()
    {
        base.SaveMetaData();
        _byValue.Maintenance.SaveMetaData();
    }

    public override void FlushToDisk()
    {
        base.FlushToDisk();
        Flush(_byValue);
    }

    public override void Dispose()
    {
        _byValue.Maintenance.SaveMetaData();
        _byValue.Dispose();
        base.Dispose();
    }
}

internal static class DiskUsage
{
    public static long OfDirectory(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            total += LengthOf(fi);
        return total;
    }

    /// <summary>
    /// File size via an opened handle: for files another component holds open with unbuffered
    /// I/O (FASTER's log), the directory-entry size Windows reports can lag far behind EOF.
    /// </summary>
    private static long LengthOf(FileInfo fi)
    {
        try
        {
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return fs.Length;
        }
        catch
        {
            return fi.Length;
        }
    }
}

