namespace SuperFastIndex;

/// <summary>
/// Memory-only comparison engine built on plain hash dictionaries:
/// <c>Dictionary&lt;id, value&gt;</c> and <c>Dictionary&lt;value, List&lt;id&gt;&gt;</c> (id lists kept sorted).
/// This is the upper bound for point operations — <see cref="ISortedIndex{T}.GetValue"/> and
/// <see cref="ISortedIndex{T}.GetIds"/> are single hash lookups — and the lower bound for ordered
/// ones: hash tables have no order, so <see cref="ISortedIndex{T}.GetIdsInRange"/> and
/// <see cref="ISortedIndex{T}.Entries"/> must filter/sort on every call. Nothing is ever written to
/// disk: the path argument is ignored, commits only publish in memory, and all data is lost
/// on dispose. Reads share the live structures under a reader/writer lock; cancel is an undo log.
/// </summary>
public sealed class HashDictionaryStorageEngine : IStorageEngine, IDisposable
{
    internal readonly ReaderWriterLockSlim Lock = new(LockRecursionPolicy.NoRecursion);

    private readonly Dictionary<string, object> _open = new();
    private long _timestamp;
    private bool _inTransaction;
    internal List<Action>? UndoLog;

    public HashDictionaryStorageEngine(string? path = null)
    {
        // path intentionally unused: this engine is memory-only by design.
    }

    public ISortedIndex<T> OpenOrCreateIndex<T>(string name) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_open)
        {
            if (_open.TryGetValue(name, out object? open))
            {
                return open as HashDictionaryIndex<T>
                    ?? throw new InvalidOperationException($"Index '{name}' is already open with a different value type.");
            }
            var index = new HashDictionaryIndex<T>(this, name);
            _open[name] = index;
            return index;
        }
    }

    public bool IsInTransaction => _inTransaction;

    public void BeginTransaction()
    {
        if (_inTransaction)
            throw new InvalidOperationException("A transaction is already active; this engine supports a single writer.");
        _inTransaction = true;
        UndoLog = new List<Action>();
    }

    public void CommitTransaction(long timestamp, bool durable)
    {
        RequireTransaction();
        Lock.EnterWriteLock();
        _timestamp = timestamp;
        Lock.ExitWriteLock();
        _inTransaction = false;
        UndoLog = null;
    }

    public void RollbackTransaction()
    {
        RequireTransaction();
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
        _inTransaction = false;
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
        Lock.EnterWriteLock();
        _timestamp = timestamp;
        Lock.ExitWriteLock();
    }

    internal void RequireTransaction()
    {
        if (!_inTransaction)
            throw new InvalidOperationException("Mutations require an active transaction (call BeginTransaction first).");
    }

    public long GetTotalDiskSpace() => 0; // memory-only by design

    public void DeleteAll()
    {
        if (_inTransaction)
            throw new InvalidOperationException("DeleteAll cannot run while a transaction is active.");
        Lock.EnterWriteLock();
        try
        {
            lock (_open)
            {
                foreach (object index in _open.Values)
                    ((IClearable)index).ClearAll();
            }
            _timestamp = 0;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    private interface IClearable
    {
        void ClearAll();
    }

    public void Dispose() => Lock.Dispose();

    private sealed class HashDictionaryIndex<T>(HashDictionaryStorageEngine engine, string name) : ISortedIndex<T>, IClearable where T : notnull
    {
        void IClearable.ClearAll()
        {
            _byId.Clear();
            _byValue.Clear();
        }

        private readonly Dictionary<int, T> _byId = new();
        private readonly Dictionary<T, List<int>> _byValue = new();
        private readonly IComparer<T> _comparer = typeof(T) == typeof(string)
            ? (IComparer<T>)(object)StringComparer.Ordinal
            : Comparer<T>.Default;

        public int Count
        {
            get
            {
                engine.Lock.EnterReadLock();
                int n = _byId.Count;
                engine.Lock.ExitReadLock();
                return n;
            }
        }

        public int DistinctValueCount
        {
            get
            {
                engine.Lock.EnterReadLock();
                int n = _byValue.Count;
                engine.Lock.ExitReadLock();
                return n;
            }
        }

        public void Set(int id, T value)
        {
            engine.RequireTransaction();
            engine.Lock.EnterWriteLock();
            try
            {
                bool hadOld = _byId.TryGetValue(id, out T? old);
                if (hadOld && EqualityComparer<T>.Default.Equals(old, value))
                    return;
                ApplyAdd(id, value);
                engine.UndoLog!.Add(hadOld
                    ? () => ApplyAdd(id, old!)
                    : () => ApplyRemove(id));
            }
            finally
            {
                engine.Lock.ExitWriteLock();
            }
        }

        public bool Remove(int id)
        {
            engine.RequireTransaction();
            engine.Lock.EnterWriteLock();
            try
            {
                if (!_byId.TryGetValue(id, out T? old))
                    return false;
                ApplyRemove(id);
                engine.UndoLog!.Add(() => ApplyAdd(id, old));
                return true;
            }
            finally
            {
                engine.Lock.ExitWriteLock();
            }
        }

        private void ApplyAdd(int id, T value)
        {
            if (_byId.TryGetValue(id, out T? old))
                RemoveFromValueList(old, id);
            _byId[id] = value;
            if (!_byValue.TryGetValue(value, out var ids))
                _byValue[value] = ids = new List<int>();
            int pos = ids.BinarySearch(id);
            ids.Insert(pos < 0 ? ~pos : pos, id); // keep ascending so GetIds needs no sort
        }

        private void ApplyRemove(int id)
        {
            if (_byId.Remove(id, out T? old))
                RemoveFromValueList(old, id);
        }

        private void RemoveFromValueList(T value, int id)
        {
            var ids = _byValue[value];
            ids.RemoveAt(ids.BinarySearch(id));
            if (ids.Count == 0)
                _byValue.Remove(value);
        }

        public T GetValue(int id)
            => TryGetValue(id, out T value)
                ? value
                : throw new KeyNotFoundException($"Id {id} is not present in index '{name}'.");

        public bool TryGetValue(int id, out T value)
        {
            engine.Lock.EnterReadLock();
            try
            {
                return _byId.TryGetValue(id, out value!);
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public bool ContainsKey(int id)
        {
            engine.Lock.EnterReadLock();
            try
            {
                return _byId.ContainsKey(id);
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public bool ContainsValue(T value)
        {
            engine.Lock.EnterReadLock();
            try
            {
                return _byValue.ContainsKey(value);
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<int> GetIds(T value)
        {
            engine.Lock.EnterReadLock();
            try
            {
                return _byValue.TryGetValue(value, out var ids) ? ids.ToArray() : [];
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<KeyValuePair<int, T>> Entries
        {
            get
            {
                engine.Lock.EnterReadLock();
                try
                {
                    var result = new List<KeyValuePair<int, T>>(_byId.Count);
                    foreach (var kv in _byId)
                        result.Add(kv);
                    result.Sort(static (a, b) => a.Key.CompareTo(b.Key)); // hash order -> id order
                    return result;
                }
                finally
                {
                    engine.Lock.ExitReadLock();
                }
            }
        }

        public IEnumerable<int> Keys
        {
            get
            {
                engine.Lock.EnterReadLock();
                try
                {
                    var result = new List<int>(_byId.Keys);
                    result.Sort(); // hash order -> id order
                    return result;
                }
                finally
                {
                    engine.Lock.ExitReadLock();
                }
            }
        }

        public T GetMinValue() => ExtremeValue(max: false);

        public T GetMaxValue() => ExtremeValue(max: true);

        /// <summary>No order in a hash table: one pass over the distinct values (no sort, no id lists).</summary>
        private T ExtremeValue(bool max)
        {
            engine.Lock.EnterReadLock();
            try
            {
                if (_byValue.Count == 0)
                    throw new InvalidOperationException($"Index '{name}' is empty.");
                T best = default!;
                bool first = true;
                foreach (T v in _byValue.Keys)
                {
                    if (first || (max ? _comparer.Compare(v, best) > 0 : _comparer.Compare(v, best) < 0))
                    {
                        best = v;
                        first = false;
                    }
                }
                return best;
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<T> DistinctValues
        {
            get
            {
                engine.Lock.EnterReadLock();
                try
                {
                    var result = new List<T>(_byValue.Keys);
                    result.Sort(_comparer); // hash order -> value order
                    return result;
                }
                finally
                {
                    engine.Lock.ExitReadLock();
                }
            }
        }

        /// <summary>No order in a hash table: filter every distinct value, then sort the matches. Caller holds the lock.</summary>
        private List<T> MatchedValues(T from, T to, bool includeFrom, bool includeTo, bool descending)
        {
            var matched = new List<T>();
            if (_comparer.Compare(from, to) > 0)
                return matched;
            foreach (T value in _byValue.Keys)
            {
                int cf = _comparer.Compare(value, from);
                int ct = _comparer.Compare(value, to);
                if ((includeFrom ? cf >= 0 : cf > 0) && (includeTo ? ct <= 0 : ct < 0))
                    matched.Add(value);
            }
            matched.Sort(_comparer);
            if (descending)
                matched.Reverse();
            return matched;
        }

        /// <summary>Concatenates the matched values' id lists; per-value lists are ascending, reversed when descending. Caller holds the lock.</summary>
        private List<int> CollectIds(List<T> matchedValues, bool descending)
        {
            var result = new List<int>();
            foreach (T value in matchedValues)
            {
                var ids = _byValue[value];
                if (descending)
                {
                    for (int i = ids.Count - 1; i >= 0; i--)
                        result.Add(ids[i]);
                }
                else
                {
                    result.AddRange(ids);
                }
            }
            return result;
        }

        /// <summary>One-sided variant of <see cref="MatchedValues"/>. Caller holds the lock.</summary>
        private List<T> MatchedValuesWhere(Func<T, bool> inRange, bool descending)
        {
            var matched = new List<T>();
            foreach (T value in _byValue.Keys)
            {
                if (inRange(value))
                    matched.Add(value);
            }
            matched.Sort(_comparer);
            if (descending)
                matched.Reverse();
            return matched;
        }

        public IEnumerable<int> GetIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
        {
            engine.Lock.EnterReadLock();
            try
            {
                return CollectIds(MatchedValues(from, to, includeFrom, includeTo, descending), descending);
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<int> GetIdsGreaterThan(T value, bool includeValue = true, bool descending = false)
        {
            engine.Lock.EnterReadLock();
            try
            {
                var matched = MatchedValuesWhere(
                    v => includeValue ? _comparer.Compare(v, value) >= 0 : _comparer.Compare(v, value) > 0,
                    descending);
                return CollectIds(matched, descending);
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<int> GetIdsSmallerThan(T value, bool includeValue = true, bool descending = false)
        {
            engine.Lock.EnterReadLock();
            try
            {
                var matched = MatchedValuesWhere(
                    v => includeValue ? _comparer.Compare(v, value) <= 0 : _comparer.Compare(v, value) < 0,
                    descending);
                return CollectIds(matched, descending);
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public IEnumerable<KeyValuePair<int, T>> GetEntriesInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false)
        {
            engine.Lock.EnterReadLock();
            try
            {
                var result = new List<KeyValuePair<int, T>>();
                foreach (T value in MatchedValues(from, to, includeFrom, includeTo, descending))
                {
                    var ids = _byValue[value];
                    if (descending)
                    {
                        for (int i = ids.Count - 1; i >= 0; i--)
                            result.Add(new KeyValuePair<int, T>(ids[i], value));
                    }
                    else
                    {
                        foreach (int id in ids)
                            result.Add(new KeyValuePair<int, T>(id, value));
                    }
                }
                return result;
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public int CountIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true)
        {
            engine.Lock.EnterReadLock();
            try
            {
                if (_comparer.Compare(from, to) > 0)
                    return 0;
                int n = 0;
                foreach (var (value, ids) in _byValue)
                {
                    int cf = _comparer.Compare(value, from);
                    int ct = _comparer.Compare(value, to);
                    if ((includeFrom ? cf >= 0 : cf > 0) && (includeTo ? ct <= 0 : ct < 0))
                        n += ids.Count;
                }
                return n;
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public int CountIdsGreaterThan(T value, bool includeValue = true)
        {
            engine.Lock.EnterReadLock();
            try
            {
                int n = 0;
                foreach (var (v, ids) in _byValue)
                {
                    int c = _comparer.Compare(v, value);
                    if (includeValue ? c >= 0 : c > 0)
                        n += ids.Count;
                }
                return n;
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }

        public int CountIdsSmallerThan(T value, bool includeValue = true)
        {
            engine.Lock.EnterReadLock();
            try
            {
                int n = 0;
                foreach (var (v, ids) in _byValue)
                {
                    int c = _comparer.Compare(v, value);
                    if (includeValue ? c <= 0 : c < 0)
                        n += ids.Count;
                }
                return n;
            }
            finally
            {
                engine.Lock.ExitReadLock();
            }
        }
    }
}
