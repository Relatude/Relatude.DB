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

    public ISortedIndex<T> OpenOrCreateIndex<T>(string name) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_openIndexes.TryGetValue(name, out object? open))
        {
            return open as ISortedIndex<T>
                ?? throw new InvalidOperationException($"Index '{name}' is already open with a different value type.");
        }
        string dir = Path.Combine(_folder, name);
        bool existed = Directory.Exists(dir);
        var index = new ZoneTreeIndex<T>(this, dir, hasEngineTimestamp: existed);
        _openIndexes[name] = index;
        return index;
    }

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

public sealed class ZoneTreeIndex<T> : ISortedIndex<T>, IZoneTreeIndexInternal, IDisposable where T : notnull
{
    private readonly ZoneTreeEngine _engine;
    private readonly IOrderedCodec<T> _codec = OrderedCodec.Get<T>();
    private readonly IZoneTree<int, Memory<byte>> _byId;          // id -> encoded value
    private readonly IZoneTree<Memory<byte>, byte> _byValue;      // composite (value, id) -> 0
    private int _count;
    private bool _hasEngineTimestamp;

    internal ZoneTreeIndex(ZoneTreeEngine engine, string dir, bool hasEngineTimestamp)
    {
        _engine = engine;
        _hasEngineTimestamp = hasEngineTimestamp;

        _byId = new ZoneTreeFactory<int, Memory<byte>>()
            .SetDataDirectory(Path.Combine(dir, "byid"))
            .SetComparer(new Int32ComparerAscending())
            .SetKeySerializer(new Int32Serializer())
            .SetValueSerializer(new ByteArraySerializer())
            .SetIsDeletedDelegate((in int _, in Memory<byte> v) => v.Length == 0)
            .SetMarkValueDeletedDelegate((ref Memory<byte> v) => v = Memory<byte>.Empty)
            .ConfigureWriteAheadLogOptions(o => o.WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed)
            .OpenOrCreate();

        _byValue = new ZoneTreeFactory<Memory<byte>, byte>()
            .SetDataDirectory(Path.Combine(dir, "byval"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteSerializer())
            .SetIsDeletedDelegate((in Memory<byte> _, in byte v) => v != 0)
            .SetMarkValueDeletedDelegate((ref byte v) => v = 1)
            .ConfigureWriteAheadLogOptions(o => o.WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed)
            .OpenOrCreate();

        if (hasEngineTimestamp)
            _count = checked((int)_byId.Count());
    }

    public int Count => _count;

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

    private byte[] EncodeValue(T value)
    {
        byte[] tmp = new byte[_codec.GetMaxSize(value)];
        int n = _codec.Encode(tmp, value);
        return n == tmp.Length ? tmp : tmp[..n];
    }

    private static byte[] Composite(ReadOnlySpan<byte> valueBytes, int id)
    {
        byte[] c = new byte[valueBytes.Length + OrderedCodec.IdSize];
        valueBytes.CopyTo(c);
        OrderedCodec.WriteId(c.AsSpan(valueBytes.Length), id);
        return c;
    }

    public void Set(int id, T value)
    {
        RequireTxn();
        Memory<byte> old = default;
        bool existed = _byId.TryGet(in id, out old);
        if (existed)
        {
            Memory<byte> oldComposite = Composite(old.Span, id);
            _byValue.ForceDelete(in oldComposite);
        }
        byte[] valueBytes = EncodeValue(value);
        Memory<byte> valueMem = valueBytes;
        _byId.Upsert(in id, in valueMem);
        Memory<byte> composite = Composite(valueBytes, id);
        _byValue.Upsert(in composite, 0);
        if (!existed) _count++;
    }

    public bool Remove(int id)
    {
        RequireTxn();
        Memory<byte> old = default;
        if (!_byId.TryGet(in id, out old)) return false;
        Memory<byte> oldComposite = Composite(old.Span, id);
        _byValue.ForceDelete(in oldComposite);
        _byId.ForceDelete(in id);
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
        Memory<byte> bytes = default;
        if (_byId.TryGet(in id, out bytes))
        {
            value = _codec.Decode(bytes.Span);
            return true;
        }
        value = default!;
        return false;
    }

    public bool ContainsKey(int id) => _byId.ContainsKey(in id);

    public bool ContainsValue(T value)
    {
        foreach (int _ in GetIds(value)) return true;
        return false;
    }

    public IEnumerable<int> GetIds(T value)
    {
        byte[] valueBytes = EncodeValue(value);
        return ScanAscending(valueBytes, true, valueBytes, true, hasFrom: true, hasTo: true)
            .Select(k => OrderedCodec.IdOfComposite(k.Span));
    }

    public IEnumerable<KeyValuePair<int, T>> Entries
    {
        get
        {
            using var it = _byId.CreateIterator(IteratorType.NoRefresh, false, false);
            while (it.Next())
                yield return new(it.CurrentKey, _codec.Decode(it.CurrentValue.Span));
        }
    }

    public IEnumerable<int> Keys
    {
        get
        {
            using var it = _byId.CreateIterator(IteratorType.NoRefresh, false, false);
            while (it.Next())
                yield return it.CurrentKey;
        }
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
                    yield return _codec.Decode(val);
                }
            }
        }
    }

    public T GetMinValue()
    {
        using var it = _byValue.CreateIterator(IteratorType.NoRefresh, false, false);
        if (!it.Next()) throw new InvalidOperationException("The index is empty.");
        return _codec.Decode(OrderedCodec.ValueOfComposite(it.CurrentKey.Span));
    }

    public T GetMaxValue()
    {
        using var it = _byValue.CreateReverseIterator(IteratorType.NoRefresh, false, false);
        if (!it.Next()) throw new InvalidOperationException("The index is empty.");
        return _codec.Decode(OrderedCodec.ValueOfComposite(it.CurrentKey.Span));
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
            .Select(k => new KeyValuePair<int, T>(OrderedCodec.IdOfComposite(k.Span), _codec.Decode(OrderedCodec.ValueOfComposite(k.Span))));

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

    public long GetTimestamp() => _hasEngineTimestamp ? _engine.GetTimestamp() : 0;

    public void SetTimestamp(long timestamp)
    {
        if (timestamp == 0) { _hasEngineTimestamp = false; return; }
        if (timestamp != _engine.GetTimestamp())
            throw new InvalidOperationException("An index timestamp is always 0 or the engine's current timestamp.");
        _hasEngineTimestamp = true;
    }

    void IZoneTreeIndexInternal.AdoptEngineTimestamp() => _hasEngineTimestamp = true;

    void IZoneTreeIndexInternal.SaveMetaData()
    {
        _byId.Maintenance.SaveMetaData();
        _byValue.Maintenance.SaveMetaData();
    }

    void IZoneTreeIndexInternal.FlushToDisk()
    {
        Flush(_byId);
        Flush(_byValue);

        static void Flush<TKey, TValue>(IZoneTree<TKey, TValue> tree)
        {
            tree.Maintenance.MoveMutableSegmentForward();
            tree.Maintenance.StartMergeOperation()?.Join();
            tree.Maintenance.SaveMetaData();
        }
    }

    void IZoneTreeIndexInternal.ClearData()
    {
        foreach (int id in Keys.ToArray())
        {
            Memory<byte> old = default;
            if (_byId.TryGet(in id, out old))
            {
                Memory<byte> composite = Composite(old.Span, id);
                _byValue.ForceDelete(in composite);
                _byId.ForceDelete(in id);
            }
        }
        _count = 0;
    }

    public void Dispose()
    {
        _byId.Maintenance.SaveMetaData();
        _byValue.Maintenance.SaveMetaData();
        _byId.Dispose();
        _byValue.Dispose();
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

