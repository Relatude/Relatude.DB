using System.Buffers.Binary;
using System.Text;
using SuperFastIndex.Internal;

namespace SuperFastIndex;

/// <summary>
/// Alternative engine used to validate the B+Tree design in practice: all data lives in
/// memory (dictionary + red-black tree) and durability comes from an append-only
/// operation log replayed on open. Writes are just log appends; reads are pure memory.
/// Trade-offs vs <see cref="BPlusTreeStorageEngine"/>: RAM usage grows with the data set,
/// open time grows with total history (no compaction), and readers share the live
/// structures under a reader/writer lock instead of getting isolated snapshots.
/// </summary>
public sealed class AppendLogStorageEngine : IStorageEngine, IDisposable
{
    private const ulong FrameMagicSeed = 14695981039346656037ul;
    private const byte OpAdd = 1;
    private const byte OpRemove = 2;
    private const byte OpDefineIndex = 3;

    internal readonly ReaderWriterLockSlim Lock = new(LockRecursionPolicy.NoRecursion);

    private readonly FileStream _log;
    private readonly Dictionary<string, (ushort NameId, byte TypeId)> _names = new();
    private readonly Dictionary<ushort, string> _namesById = new();
    private readonly HashSet<string> _persistedNames = new();   // definition already durable in the log
    private readonly HashSet<string> _txnDefinedNames = new();  // definition emitted by the active transaction
    private readonly Dictionary<string, List<(byte Op, int Id, byte[] Value)>> _pendingReplay = new();
    private readonly Dictionary<string, IReplayTarget> _openIndexes = new();

    private long _timestamp;
    private bool _inTransaction;
    private MemoryStream? _txnPayload;
    internal List<Action>? UndoLog;

    private interface IReplayTarget
    {
        void Apply(byte op, int id, ReadOnlySpan<byte> value);
        void ClearAll();
        byte TypeId { get; }
    }

    public AppendLogStorageEngine(string path)
    {
        _log = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        Replay();
        _log.Seek(0, SeekOrigin.End);
    }

    // ---- IStorageEngine ----

    public ISortedIndex<T> OpenOrCreateIndex<T>(string name) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Lock.EnterWriteLock();
        try
        {
            if (_openIndexes.TryGetValue(name, out var open))
            {
                return open as AppendLogIndex<T>
                    ?? throw new InvalidOperationException($"Index '{name}' is already open with a different value type.");
            }

            byte typeId = KeyCodec.GetTypeId<T>();
            if (_names.TryGetValue(name, out var known) && known.TypeId != typeId)
                throw new InvalidOperationException($"Index '{name}' exists with a different value type.");

            var index = new AppendLogIndex<T>(this, name);
            if (_pendingReplay.Remove(name, out var ops))
            {
                foreach (var (op, id, value) in ops)
                    ((IReplayTarget)index).Apply(op, id, value);
            }
            _openIndexes[name] = index;
            return index;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    public bool IsInTransaction => _inTransaction;

    public void BeginTransaction()
    {
        if (_inTransaction)
            throw new InvalidOperationException("A transaction is already active; this engine supports a single writer.");
        _inTransaction = true;
        _txnPayload = new MemoryStream();
        UndoLog = new List<Action>();
    }

    public void CommitTransaction(long timestamp, bool durable)
    {
        if (!_inTransaction)
            throw new InvalidOperationException("No active transaction.");
        byte[] payload = _txnPayload!.ToArray();
        WriteFrame(timestamp, payload, durable);
        _persistedNames.UnionWith(_txnDefinedNames);
        _txnDefinedNames.Clear();
        Lock.EnterWriteLock();
        _timestamp = timestamp;
        Lock.ExitWriteLock();
        _inTransaction = false;
        _txnPayload = null;
        UndoLog = null;
    }

    public void RollbackTransaction()
    {
        if (!_inTransaction)
            throw new InvalidOperationException("No active transaction.");
        Lock.EnterWriteLock();
        try
        {
            for (int i = UndoLog!.Count - 1; i >= 0; i--)
                UndoLog[i]();
        }
        finally
        {
            Lock.ExitWriteLock();
        }
        _txnDefinedNames.Clear();
        _inTransaction = false;
        _txnPayload = null;
        UndoLog = null;
    }

    public long GetTimestamp()
    {
        Lock.EnterReadLock();
        long ts = _timestamp;
        Lock.ExitReadLock();
        return ts;
    }

    public void SetTimestamp(long timestamp)
    {
        if (_inTransaction)
            throw new InvalidOperationException("SetTimestamp cannot run while a transaction is active; pass the timestamp to CommitTransaction instead.");
        WriteFrame(timestamp, [], deepDiskFlush: true);
        Lock.EnterWriteLock();
        _timestamp = timestamp;
        Lock.ExitWriteLock();
    }

    public long GetTotalDiskSpace() => _log.Length;

    public void DeleteAll()
    {
        if (_inTransaction)
            throw new InvalidOperationException("DeleteAll cannot run while a transaction is active.");
        Lock.EnterWriteLock();
        try
        {
            foreach (var index in _openIndexes.Values)
                index.ClearAll();
            _pendingReplay.Clear();
            _persistedNames.Clear(); // definitions must re-emit into the fresh log
            _log.SetLength(0);
            _log.Flush(true);
            _timestamp = 0;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        _log.Dispose();
        Lock.Dispose();
    }

    // ---- log ----

    internal void RequireTransaction()
    {
        if (!_inTransaction)
            throw new InvalidOperationException("Mutations require an active transaction (call BeginTransaction first).");
    }

    internal void LogOperation<T>(string indexName, byte typeId, byte op, int id, T? value, IKeyCodec<T>? codec)
        where T : notnull
    {
        var w = _txnPayload!;
        if (!_names.TryGetValue(indexName, out var entry))
        {
            entry = ((ushort)_names.Count, typeId);
            _names[indexName] = entry;
            _namesById[entry.NameId] = indexName;
        }
        if (!_persistedNames.Contains(indexName) && _txnDefinedNames.Add(indexName))
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(indexName);
            Span<byte> def = stackalloc byte[6];
            def[0] = OpDefineIndex;
            BinaryPrimitives.WriteUInt16LittleEndian(def[1..], entry.NameId);
            def[3] = typeId;
            BinaryPrimitives.WriteUInt16LittleEndian(def[4..], (ushort)nameBytes.Length);
            w.Write(def);
            w.Write(nameBytes);
        }

        Span<byte> head = stackalloc byte[9];
        head[0] = op;
        BinaryPrimitives.WriteUInt16LittleEndian(head[1..], entry.NameId);
        BinaryPrimitives.WriteInt32LittleEndian(head[3..], id);
        if (op == OpAdd)
        {
            byte[] valueBytes = new byte[codec!.GetMaxSize(value!)];
            int len = codec.Encode(valueBytes, value!);
            BinaryPrimitives.WriteUInt16LittleEndian(head[7..], (ushort)len);
            w.Write(head);
            w.Write(valueBytes, 0, len);
        }
        else
        {
            w.Write(head[..7]);
        }
    }

    private void WriteFrame(long timestamp, byte[] payload, bool deepDiskFlush)
    {
        Span<byte> head = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(head, payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(head[4..], timestamp);
        ulong h = FrameMagicSeed;
        foreach (byte b in head)
            h = (h ^ b) * 1099511628211ul;
        foreach (byte b in payload)
            h = (h ^ b) * 1099511628211ul;
        Span<byte> tail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tail, h);

        _log.Write(head);
        _log.Write(payload);
        _log.Write(tail);
        _log.Flush(deepDiskFlush);
    }

    private void Replay()
    {
        using var reader = new BinaryReader(_log, Encoding.UTF8, leaveOpen: true);
        long validEnd = 0;
        while (true)
        {
            byte[] head;
            byte[] payload;
            byte[] tail;
            try
            {
                head = reader.ReadBytes(12);
                if (head.Length < 12)
                    break;
                int len = BinaryPrimitives.ReadInt32LittleEndian(head);
                if (len < 0 || len > 1 << 30)
                    break;
                payload = reader.ReadBytes(len);
                tail = reader.ReadBytes(8);
                if (payload.Length < len || tail.Length < 8)
                    break;
            }
            catch (EndOfStreamException)
            {
                break;
            }

            ulong h = FrameMagicSeed;
            foreach (byte b in head)
                h = (h ^ b) * 1099511628211ul;
            foreach (byte b in payload)
                h = (h ^ b) * 1099511628211ul;
            if (BinaryPrimitives.ReadUInt64LittleEndian(tail) != h)
                break;

            ApplyPayload(payload);
            _timestamp = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(4));
            validEnd = _log.Position;
        }
        _log.SetLength(validEnd); // drop a torn tail from a crash mid-append
    }

    private void ApplyPayload(ReadOnlySpan<byte> p)
    {
        int off = 0;
        while (off < p.Length)
        {
            byte op = p[off];
            if (op == OpDefineIndex)
            {
                ushort nameId = BinaryPrimitives.ReadUInt16LittleEndian(p[(off + 1)..]);
                byte typeId = p[off + 3];
                int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(p[(off + 4)..]);
                string name = Encoding.UTF8.GetString(p.Slice(off + 6, nameLen));
                _names[name] = (nameId, typeId);
                _namesById[nameId] = name;
                _persistedNames.Add(name);
                off += 6 + nameLen;
                continue;
            }

            ushort nid = BinaryPrimitives.ReadUInt16LittleEndian(p[(off + 1)..]);
            int id = BinaryPrimitives.ReadInt32LittleEndian(p[(off + 3)..]);
            string indexName = _namesById[nid];
            byte[] value = [];
            if (op == OpAdd)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(p[(off + 7)..]);
                value = p.Slice(off + 9, len).ToArray();
                off += 9 + len;
            }
            else
            {
                off += 7;
            }

            if (_openIndexes.TryGetValue(indexName, out var target))
            {
                target.Apply(op, id, value);
            }
            else
            {
                if (!_pendingReplay.TryGetValue(indexName, out var list))
                    _pendingReplay[indexName] = list = new List<(byte, int, byte[])>();
                list.Add((op, id, value));
            }
        }
    }

    /// <summary>In-memory bidirectional index; see engine docs for the trade-offs.</summary>
    private sealed class AppendLogIndex<T> : ISortedIndex<T>, IReplayTarget where T : notnull
    {
        private readonly AppendLogStorageEngine _engine;
        private readonly string _name;
        private readonly IKeyCodec<T> _codec = KeyCodec.Get<T>();
        private readonly Dictionary<int, T> _byId = new();
        private readonly SortedSet<(T Value, int Id)> _sorted;
        private readonly Dictionary<T, int> _valueRefs = new();
        private readonly IComparer<T> _valueComparer;

        public AppendLogIndex(AppendLogStorageEngine engine, string name)
        {
            _engine = engine;
            _name = name;
            _valueComparer = typeof(T) == typeof(string)
                ? (IComparer<T>)(object)StringComparer.Ordinal
                : Comparer<T>.Default;
            _sorted = new SortedSet<(T, int)>(Comparer<(T, int)>.Create((a, b) =>
            {
                int c = _valueComparer.Compare(a.Item1, b.Item1);
                return c != 0 ? c : a.Item2.CompareTo(b.Item2);
            }));
        }

        byte IReplayTarget.TypeId => KeyCodec.GetTypeId<T>();

        void IReplayTarget.Apply(byte op, int id, ReadOnlySpan<byte> value)
        {
            if (op == OpAdd)
                ApplyAdd(id, _codec.Decode(value));
            else
                ApplyRemove(id);
        }

        void IReplayTarget.ClearAll()
        {
            _byId.Clear();
            _sorted.Clear();
            _valueRefs.Clear();
        }

        public int Count
        {
            get
            {
                _engine.Lock.EnterReadLock();
                int n = _byId.Count;
                _engine.Lock.ExitReadLock();
                return n;
            }
        }

        public int DistinctValueCount
        {
            get
            {
                _engine.Lock.EnterReadLock();
                int n = _valueRefs.Count;
                _engine.Lock.ExitReadLock();
                return n;
            }
        }

        public void Set(int id, T value)
        {
            _engine.RequireTransaction();
            _engine.Lock.EnterWriteLock();
            try
            {
                bool hadOld = _byId.TryGetValue(id, out T? old);
                if (hadOld && EqualityComparer<T>.Default.Equals(old, value))
                    return;
                ApplyAdd(id, value);
                _engine.LogOperation(_name, KeyCodec.GetTypeId<T>(), OpAdd, id, value, _codec);
                _engine.UndoLog!.Add(hadOld
                    ? () => ApplyAdd(id, old!)
                    : () => ApplyRemove(id));
            }
            finally
            {
                _engine.Lock.ExitWriteLock();
            }
        }

        public bool Remove(int id)
        {
            _engine.RequireTransaction();
            _engine.Lock.EnterWriteLock();
            try
            {
                if (!_byId.TryGetValue(id, out T? old))
                    return false;
                ApplyRemove(id);
                _engine.LogOperation<T>(_name, KeyCodec.GetTypeId<T>(), OpRemove, id, default, null);
                _engine.UndoLog!.Add(() => ApplyAdd(id, old));
                return true;
            }
            finally
            {
                _engine.Lock.ExitWriteLock();
            }
        }

        private void ApplyAdd(int id, T value)
        {
            if (_byId.TryGetValue(id, out T? old))
            {
                _sorted.Remove((old, id));
                DecRef(old);
            }
            _byId[id] = value;
            _sorted.Add((value, id));
            _valueRefs[value] = _valueRefs.GetValueOrDefault(value) + 1;
        }

        private void ApplyRemove(int id)
        {
            if (!_byId.Remove(id, out T? old))
                return;
            _sorted.Remove((old, id));
            DecRef(old);
        }

        private void DecRef(T value)
        {
            int n = _valueRefs[value] - 1;
            if (n == 0)
                _valueRefs.Remove(value);
            else
                _valueRefs[value] = n;
        }

        public T GetValue(int id)
            => TryGetValue(id, out T value)
                ? value
                : throw new KeyNotFoundException($"Id {id} is not present in index '{_name}'.");

        public bool TryGetValue(int id, out T value)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                return _byId.TryGetValue(id, out value!);
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public bool ContainsKey(int id)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                return _byId.ContainsKey(id);
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public bool ContainsValue(T value)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                return _valueRefs.ContainsKey(value);
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<int> GetIds(T value)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                var view = _sorted.GetViewBetween((value, int.MinValue), (value, int.MaxValue));
                var result = new List<int>(view.Count);
                foreach (var (_, id) in view)
                    result.Add(id);
                return result;
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<KeyValuePair<int, T>> Entries
        {
            get
            {
                _engine.Lock.EnterReadLock();
                try
                {
                    var result = new List<KeyValuePair<int, T>>(_byId.Count);
                    foreach (var kv in _byId)
                        result.Add(kv);
                    result.Sort(static (a, b) => a.Key.CompareTo(b.Key));
                    return result;
                }
                finally
                {
                    _engine.Lock.ExitReadLock();
                }
            }
        }

        public IEnumerable<int> Keys
        {
            get
            {
                _engine.Lock.EnterReadLock();
                try
                {
                    var result = new List<int>(_byId.Keys);
                    result.Sort(); // hash order -> id order
                    return result;
                }
                finally
                {
                    _engine.Lock.ExitReadLock();
                }
            }
        }

        public T GetMinValue()
        {
            _engine.Lock.EnterReadLock();
            try
            {
                if (_sorted.Count == 0)
                    throw new InvalidOperationException($"Index '{_name}' is empty.");
                return _sorted.Min.Value; // O(log n): leftmost node of the sorted set
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public T GetMaxValue()
        {
            _engine.Lock.EnterReadLock();
            try
            {
                if (_sorted.Count == 0)
                    throw new InvalidOperationException($"Index '{_name}' is empty.");
                return _sorted.Max.Value; // O(log n): rightmost node of the sorted set
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<T> DistinctValues
        {
            get
            {
                _engine.Lock.EnterReadLock();
                try
                {
                    var result = new List<T>(_valueRefs.Keys);
                    result.Sort(_valueComparer); // hash order -> value order
                    return result;
                }
                finally
                {
                    _engine.Lock.ExitReadLock();
                }
            }
        }

        public IEnumerable<int> GetIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                var cmp = _sorted.Comparer;
                if (cmp.Compare((from, 0), (to, 0)) > 0)
                    return [];
                var view = _sorted.GetViewBetween((from, int.MinValue), (to, int.MaxValue));
                var result = new List<int>();
                // SortedSet.Reverse() is a lazy reverse in-order walk — no materialization or re-sort.
                foreach (var (v, id) in descending ? view.Reverse() : view)
                {
                    if (!includeFrom && cmp.Compare((v, 0), (from, 0)) == 0)
                        continue;
                    if (!includeTo && cmp.Compare((v, 0), (to, 0)) == 0)
                        continue;
                    result.Add(id);
                }
                return result;
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<KeyValuePair<int, T>> GetEntriesInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                var cmp = _sorted.Comparer;
                if (cmp.Compare((from, 0), (to, 0)) > 0)
                    return [];
                var view = _sorted.GetViewBetween((from, int.MinValue), (to, int.MaxValue));
                var result = new List<KeyValuePair<int, T>>();
                foreach (var (v, id) in descending ? view.Reverse() : view)
                {
                    if (!includeFrom && cmp.Compare((v, 0), (from, 0)) == 0)
                        continue;
                    if (!includeTo && cmp.Compare((v, 0), (to, 0)) == 0)
                        continue;
                    result.Add(new KeyValuePair<int, T>(id, v)); // the value rides in the sorted entry
                }
                return result;
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<int> GetIdsGreaterThan(T value, bool includeValue = true, bool descending = false)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                if (_sorted.Count == 0 || _valueComparer.Compare(value, _sorted.Max.Value) > 0)
                    return [];
                var view = _sorted.GetViewBetween((value, int.MinValue), _sorted.Max);
                var result = new List<int>();
                foreach (var (v, id) in descending ? view.Reverse() : view)
                {
                    if (!includeValue && _valueComparer.Compare(v, value) == 0)
                        continue;
                    result.Add(id);
                }
                return result;
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<int> GetIdsSmallerThan(T value, bool includeValue = true, bool descending = false)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                if (_sorted.Count == 0 || _valueComparer.Compare(value, _sorted.Min.Value) < 0)
                    return [];
                var view = _sorted.GetViewBetween(_sorted.Min, (value, int.MaxValue));
                var result = new List<int>();
                foreach (var (v, id) in descending ? view.Reverse() : view)
                {
                    if (!includeValue && _valueComparer.Compare(v, value) == 0)
                        continue;
                    result.Add(id);
                }
                return result;
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public int CountIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                int c = _valueComparer.Compare(from, to);
                if (c > 0)
                    return 0;
                if (c == 0)
                    return includeFrom && includeTo ? _valueRefs.GetValueOrDefault(from) : 0;
                int n = _sorted.GetViewBetween((from, int.MinValue), (to, int.MaxValue)).Count;
                // Excluded bounds: subtract the boundary values' entry counts instead of walking the view.
                if (!includeFrom)
                    n -= _valueRefs.GetValueOrDefault(from);
                if (!includeTo)
                    n -= _valueRefs.GetValueOrDefault(to);
                return n;
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public int CountIdsGreaterThan(T value, bool includeValue = true)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                if (_sorted.Count == 0 || _valueComparer.Compare(value, _sorted.Max.Value) > 0)
                    return 0;
                int n = _sorted.GetViewBetween((value, int.MinValue), _sorted.Max).Count;
                if (!includeValue)
                    n -= _valueRefs.GetValueOrDefault(value);
                return n;
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }

        public int CountIdsSmallerThan(T value, bool includeValue = true)
        {
            _engine.Lock.EnterReadLock();
            try
            {
                if (_sorted.Count == 0 || _valueComparer.Compare(value, _sorted.Min.Value) < 0)
                    return 0;
                int n = _sorted.GetViewBetween(_sorted.Min, (value, int.MaxValue)).Count;
                if (!includeValue)
                    n -= _valueRefs.GetValueOrDefault(value);
                return n;
            }
            finally
            {
                _engine.Lock.ExitReadLock();
            }
        }
    }
}
