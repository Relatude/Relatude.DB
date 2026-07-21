using System.Buffers.Binary;
using FASTER.core;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace KvBenchmarks.Engines;

/// <summary>
/// <see cref="IStorageEngine"/> on Microsoft FASTER. FASTER is a hash key-value store with no
/// ordered scans, so each index pairs a FasterKV (id -> encoded value; persistence and point
/// lookups) with an in-memory SortedSet of composite (value, id) keys for every ordered
/// operation — the classic "FASTER + secondary index" architecture. The memory column of the
/// benchmark shows the price of that ordered side. Durable commits take a FoldOver checkpoint;
/// rollback is not supported.
/// </summary>
public sealed class FasterEngine : IStorageEngine, IDisposable
{
    private readonly string _folder;
    private readonly Dictionary<string, object> _openIndexes = new();
    private long _timestamp;
    private bool _inTxn;

    public FasterEngine(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        _timestamp = File.Exists(TsFile) && long.TryParse(File.ReadAllText(TsFile), out long ts) ? ts : 0;
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
        var index = new FasterIndex<T>(this, dir, hasEngineTimestamp: existed);
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
                ((IFasterIndexInternal)open).Checkpoint();
            File.WriteAllText(TsFile, timestamp.ToString());
        }
        foreach (object open in _openIndexes.Values)
            ((IFasterIndexInternal)open).AdoptEngineTimestamp();
    }

    public void RollbackTransaction()
        => throw new NotSupportedException("The FASTER engine applies writes immediately; rollback is not supported.");

    public long GetTimestamp() => _timestamp;

    public void SetTimestamp(long timestamp)
    {
        if (_inTxn) throw new InvalidOperationException("SetTimestamp cannot run while a transaction is active.");
        BeginTransaction();
        CommitTransaction(timestamp, durable: true);
    }

    public long GetTotalDiskSpace() => DiskUsage.OfDirectory(_folder);

    public void DeleteAll()
    {
        if (_inTxn) throw new InvalidOperationException("DeleteAll cannot run while a transaction is active.");
        foreach (object open in _openIndexes.Values)
            ((IFasterIndexInternal)open).ClearData();
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

internal interface IFasterIndexInternal
{
    void AdoptEngineTimestamp();
    void Checkpoint();
    void ClearData();
}

public sealed class FasterIndex<T> : ISortedIndex<T>, IFasterIndexInternal, IDisposable where T : notnull
{
    private readonly FasterEngine _engine;
    private readonly IOrderedCodec<T> _codec = OrderedCodec.Get<T>();
    private readonly FasterKVSettings<SpanByte, SpanByte> _settings;
    private readonly FasterKV<SpanByte, SpanByte> _store;
    private readonly ClientSession<SpanByte, SpanByte, SpanByte, byte[], Empty, SpanByteFunctions_ByteArrayOutput<Empty>> _session;
    private readonly SortedSet<byte[]> _ordered = new(ByteArrayMemCmp.Instance); // composite (value, id)
    private readonly byte[] _keyBuf = GC.AllocateArray<byte>(4, pinned: true);
    private readonly byte[] _valBuf = GC.AllocateArray<byte>(64 * 1024, pinned: true);
    private bool _hasEngineTimestamp;

    internal FasterIndex(FasterEngine engine, string dir, bool hasEngineTimestamp)
    {
        _engine = engine;
        _hasEngineTimestamp = hasEngineTimestamp;
        _settings = new FasterKVSettings<SpanByte, SpanByte>(dir)
        {
            IndexSize = 1L << 24,      // 16 MB hash index
            MemorySize = 1L << 28,     // 256 MB hybrid log memory
            PageSize = 1L << 22,       // 4 MB pages
            SegmentSize = 1L << 24,    // 16 MB log segments (keeps reported disk size honest)
            TryRecoverLatest = hasEngineTimestamp,
        };
        _store = new FasterKV<SpanByte, SpanByte>(_settings);
        _session = _store.For(new SpanByteFunctions_ByteArrayOutput<Empty>())
            .NewSession<SpanByteFunctions_ByteArrayOutput<Empty>>();
        if (hasEngineTimestamp)
            RebuildOrderedIndex();
    }

    private void RebuildOrderedIndex()
    {
        using var it = _session.Iterate();
        while (it.GetNext(out RecordInfo info))
        {
            if (info.Tombstone) continue;
            int id = BinaryPrimitives.ReadInt32LittleEndian(it.GetKey().AsReadOnlySpan());
            _ordered.Add(Composite(it.GetValue().AsReadOnlySpan(), id));
        }
    }

    public int Count => _ordered.Count;

    public int DistinctValueCount
    {
        get
        {
            int distinct = 0;
            byte[]? prev = null;
            foreach (byte[] c in _ordered)
            {
                var val = OrderedCodec.ValueOfComposite(c);
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

    private unsafe SpanByte StageKey(int id)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_keyBuf, id);
        return SpanByte.FromFixedSpan(_keyBuf.AsSpan());
    }

    private bool TryReadRaw(int id, out byte[] bytes)
    {
        SpanByte key = StageKey(id);
        byte[] output = null!;
        Status status = _session.Read(ref key, ref output);
        if (status.IsPending)
        {
            _session.CompletePendingWithOutputs(out var completed, wait: true);
            while (completed.Next())
            {
                status = completed.Current.Status;
                output = completed.Current.Output;
            }
            completed.Dispose();
        }
        bytes = output!;
        return status.Found;
    }

    public void Set(int id, T value)
    {
        RequireTxn();
        if (TryReadRaw(id, out byte[] old))
            _ordered.Remove(Composite(old, id));
        int n = _codec.Encode(_valBuf, value);
        SpanByte key = StageKey(id);
        SpanByte val = SpanByte.FromFixedSpan(_valBuf.AsSpan(0, n));
        _session.Upsert(ref key, ref val);
        _ordered.Add(Composite(_valBuf.AsSpan(0, n), id));
    }

    public bool Remove(int id)
    {
        RequireTxn();
        if (!TryReadRaw(id, out byte[] old)) return false;
        _ordered.Remove(Composite(old, id));
        SpanByte key = StageKey(id);
        _session.Delete(ref key);
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
        if (TryReadRaw(id, out byte[] bytes))
        {
            value = _codec.Decode(bytes);
            return true;
        }
        value = default!;
        return false;
    }

    public bool ContainsKey(int id) => TryReadRaw(id, out _);

    public bool ContainsValue(T value)
    {
        foreach (byte[] _ in ScanComposites(EncodeValue(value), true, EncodeValue(value), true, true, true, descending: false))
            return true;
        return false;
    }

    private byte[] EncodeValue(T value)
    {
        byte[] tmp = new byte[_codec.GetMaxSize(value)];
        int n = _codec.Encode(tmp, value);
        return n == tmp.Length ? tmp : tmp[..n];
    }

    public IEnumerable<int> GetIds(T value)
    {
        byte[] v = EncodeValue(value);
        return ScanComposites(v, true, v, true, true, true, descending: false)
            .Select(c => OrderedCodec.IdOfComposite(c));
    }

    public IEnumerable<KeyValuePair<int, T>> Entries
        => _ordered
            .Select(c => new KeyValuePair<int, T>(OrderedCodec.IdOfComposite(c), _codec.Decode(OrderedCodec.ValueOfComposite(c))))
            .OrderBy(e => e.Key);

    public IEnumerable<int> Keys
        => _ordered.Select(c => OrderedCodec.IdOfComposite(c)).Order();

    public IEnumerable<T> DistinctValues
    {
        get
        {
            byte[]? prev = null;
            foreach (byte[] c in _ordered)
            {
                var val = OrderedCodec.ValueOfComposite(c);
                if (prev is null || !val.SequenceEqual(prev))
                {
                    prev = val.ToArray();
                    yield return _codec.Decode(prev);
                }
            }
        }
    }

    public T GetMinValue()
    {
        if (_ordered.Count == 0) throw new InvalidOperationException("The index is empty.");
        return _codec.Decode(OrderedCodec.ValueOfComposite(_ordered.Min!));
    }

    public T GetMaxValue()
    {
        if (_ordered.Count == 0) throw new InvalidOperationException("The index is empty.");
        return _codec.Decode(OrderedCodec.ValueOfComposite(_ordered.Max!));
    }

    /// <summary>Ordered scan over the in-memory composite index; null bounds are unbounded.</summary>
    private IEnumerable<byte[]> ScanComposites(byte[]? fromValue, bool includeFrom, byte[]? toValue, bool includeTo, bool hasFrom, bool hasTo, bool descending)
    {
        if (_ordered.Count == 0) yield break;
        byte[] lower = hasFrom ? Composite(fromValue, int.MinValue) : _ordered.Min!;
        byte[] upper = hasTo ? Composite(toValue, int.MaxValue) : _ordered.Max!;
        if (ByteArrayMemCmp.Instance.Compare(lower, upper) > 0) yield break;
        var view = _ordered.GetViewBetween(lower, upper);
        IEnumerable<byte[]> ordered = descending ? view.Reverse() : view;
        foreach (byte[] c in ordered)
        {
            if (hasFrom && !includeFrom && OrderedCodec.ValueOfComposite(c).SequenceEqual(fromValue))
            {
                if (descending) yield break; // equal-from run is the tail in descending order
                continue;
            }
            if (hasTo && !includeTo && OrderedCodec.ValueOfComposite(c).SequenceEqual(toValue))
            {
                if (descending) continue;    // equal-to run is the head in descending order
                yield break;
            }
            yield return c;
        }
    }

    public IEnumerable<int> GetIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
        => ScanComposites(EncodeValue(from), includeFrom, EncodeValue(to), includeTo, true, true, descending)
            .Select(c => OrderedCodec.IdOfComposite(c));

    public IEnumerable<KeyValuePair<int, T>> GetEntriesInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
        => ScanComposites(EncodeValue(from), includeFrom, EncodeValue(to), includeTo, true, true, descending)
            .Select(c => new KeyValuePair<int, T>(OrderedCodec.IdOfComposite(c), _codec.Decode(OrderedCodec.ValueOfComposite(c))));

    public IEnumerable<int> GetIdsGreaterThan(T value, bool includeValue = true, bool descending = false)
        => ScanComposites(EncodeValue(value), includeValue, null, true, true, false, descending)
            .Select(c => OrderedCodec.IdOfComposite(c));

    public IEnumerable<int> GetIdsSmallerThan(T value, bool includeValue = true, bool descending = false)
        => ScanComposites(null, true, EncodeValue(value), includeValue, false, true, descending)
            .Select(c => OrderedCodec.IdOfComposite(c));

    public int CountIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true)
        => ScanComposites(EncodeValue(from), includeFrom, EncodeValue(to), includeTo, true, true, false).Count();

    public int CountIdsGreaterThan(T value, bool includeValue = true)
        => ScanComposites(EncodeValue(value), includeValue, null, true, true, false, false).Count();

    public int CountIdsSmallerThan(T value, bool includeValue = true)
        => ScanComposites(null, true, EncodeValue(value), includeValue, false, true, false).Count();

    public long GetTimestamp() => _hasEngineTimestamp ? _engine.GetTimestamp() : 0;

    public void SetTimestamp(long timestamp)
    {
        if (timestamp == 0) { _hasEngineTimestamp = false; return; }
        if (timestamp != _engine.GetTimestamp())
            throw new InvalidOperationException("An index timestamp is always 0 or the engine's current timestamp.");
        _hasEngineTimestamp = true;
    }

    void IFasterIndexInternal.AdoptEngineTimestamp() => _hasEngineTimestamp = true;

    void IFasterIndexInternal.Checkpoint()
    {
        _session.CompletePending(wait: true);
        if (!_store.TryInitiateHybridLogCheckpoint(out _, CheckpointType.FoldOver))
        {
            // A checkpoint is already in flight; wait for it and take ours.
            _store.CompleteCheckpointAsync().AsTask().GetAwaiter().GetResult();
            _store.TryInitiateHybridLogCheckpoint(out _, CheckpointType.FoldOver);
        }
        _store.CompleteCheckpointAsync().AsTask().GetAwaiter().GetResult();
    }

    void IFasterIndexInternal.ClearData()
    {
        foreach (byte[] c in _ordered.ToArray())
        {
            SpanByte key = StageKey(OrderedCodec.IdOfComposite(c));
            _session.Delete(ref key);
        }
        _ordered.Clear();
    }

    public void Dispose()
    {
        _session.Dispose();
        _store.Dispose();
        _settings.Dispose();
    }
}
