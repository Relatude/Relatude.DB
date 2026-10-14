using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Common {
    /// <summary>
    /// Threadsafe simple cache, with a upper memory size ( _maxSize )
    /// LRU cache, items are removed in order of last accessed
    /// If adding an item will exceed max size it removes the least recently used items until the total
    /// size is below 90% of max, so a full cache sheds a little at a time and never holds its lock for long
    /// Only items with size>0 is removed. So items with size 0 is reserved!!
    /// This is used to ensure items are kept in cache until segment is written to transaction log.
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="maxSize"></param>
    public class Cache<TKey, TValue>(long maxSize) where TKey : notnull {
        // Entries with a size are linked into an intrusive least-recently-used list, so eviction only touches
        // the entries it actually removes. ( A sort of the whole cache is prohibitive at millions of entries. )
        // Reserved entries (size 0) stay out of the list: they can never be evicted, and an eviction walking
        // past every one of them - a bulk insert leaves many waiting for the log - would cost more each time.
        class Entry(TKey key, TValue data, int size) {
            public readonly TKey Key = key;
            public TValue Data = data;
            public int Size = size;
            public bool Linked;
            public Entry? Newer;
            public Entry? Older;
        }
        // a full cache evicts down to this share of its budget
        const double _evictTo = 0.9;
        readonly System.Threading.Lock _lock = new();
        long _maxSize = maxSize;
        readonly Dictionary<TKey, Entry> _cache = [];
        Entry? _mru;
        Entry? _lru;
        long _hits = 0;
        long _misses = 0;
        int _overflows = 0;
        long _size = 0;
        void unlink(Entry e) {
            if (!e.Linked) return;
            if (e.Newer != null) e.Newer.Older = e.Older; else _mru = e.Older;
            if (e.Older != null) e.Older.Newer = e.Newer; else _lru = e.Newer;
            e.Newer = e.Older = null;
            e.Linked = false;
        }
        void linkAsMru(Entry e) {
            e.Older = _mru;
            e.Newer = null;
            if (_mru != null) _mru.Newer = e;
            _mru = e;
            _lru ??= e;
            e.Linked = true;
        }
        void linkAsLru(Entry e) {
            e.Newer = _lru;
            e.Older = null;
            if (_lru != null) _lru.Older = e;
            _lru = e;
            _mru ??= e;
            e.Linked = true;
        }
        // places the entry where its size says it belongs: in the list when it has one, out of it when reserved
        void place(Entry e, bool cold) {
            unlink(e);
            if (e.Size > 0) {
                if (cold) linkAsLru(e);
                else linkAsMru(e);
            }
        }
        void touch(Entry e) {
            if (!e.Linked || _mru == e) return;
            unlink(e);
            linkAsMru(e);
        }
        void remove(Entry e) {
            unlink(e);
            _cache.Remove(e.Key);
        }
        /// <summary>Changes the budget; anything over the new one is evicted at once.</summary>
        public void SetMaxSize(long maxSize) {
            lock (_lock) {
                _maxSize = maxSize;
                if (_size >= _maxSize) reduceToSize((long)(_maxSize * _evictTo));
            }
        }
        public bool TryUpdateSize(TKey key, int size) {
            lock (_lock) {
                if (_cache.TryGetValue(key, out var item)) {
                    if (_maxSize == 0 && size > 0) {
                        _size -= item.Size; // it means items was only in cache because it had size 0, ( and had not been written to log yet )
                        remove(item);
                    } else {
                        var wasReserved = item.Size == 0;
                        _size -= item.Size;
                        item.Size = size;
                        _size += size;
                        if (wasReserved || size == 0) place(item, false); // a reserved entry joins the list as the most recent, and leaves it when reserved again
                        resizeIfNeeded();
                    }
                    return true;
                } else {
                    return false;
                }
            }
        }
        public void Set(TKey key, TValue data, int size) => Set(key, data, size, false);
        /// <summary>
        /// Adds or replaces an entry. A <paramref name="cold"/> entry goes in as the least recently used one,
        /// the first to be evicted: what a large scan adds then leaves the cache before what other readers use.
        /// A cold entry that is read again moves up like any other.
        /// </summary>
        public void Set(TKey key, TValue data, int size, bool cold) {
            if (_maxSize == 0 && size > 0) return;
            lock (_lock) {
                if (_cache.TryGetValue(key, out var item)) {
                    _size -= item.Size;
                    item.Data = data;
                    item.Size = size;
                } else {
                    item = new Entry(key, data, size);
                    _cache.Add(key, item);
                }
                place(item, cold);
                _size += size;
                if (size > 0) resizeIfNeeded();
            }
        }
        /// <summary>Adds the entry unless the key is cached already, which is left as it is. False when it was there.</summary>
        public bool TryAdd(TKey key, TValue data, int size, bool cold = false) {
            if (_maxSize == 0 && size > 0) return false;
            lock (_lock) {
                if (_cache.ContainsKey(key)) return false;
                var item = new Entry(key, data, size);
                _cache.Add(key, item);
                place(item, cold);
                _size += size;
                if (size > 0) resizeIfNeeded();
                return true;
            }
        }
        void resizeIfNeeded() {
            // removes the least recently used items until the total size is below 90% of max, if size is above max
            if (_size < _maxSize) return;
            reduceToSize((long)(_maxSize * _evictTo));
        }
        void reduceToSize(long size) {
            // removes items in order of last accessed until total size is less than size
            if (_size < size) return;
            _overflows++;
            var e = _lru;
            while (e != null && _size >= size) { // only entries with a size are linked, so every one can go
                var newer = e.Newer;
                _size -= e.Size;
                remove(e);
                e = newer;
            }
        }
        public bool Contains(TKey id) {
            lock (_lock) return _cache.ContainsKey(id);
        }
        public List<TKey> GetMissing(TKey[] ids) {
            List<TKey> missing = new();
            lock (_lock) {
                foreach (var id in ids) {
                    if (!_cache.ContainsKey(id)) {
                        missing.Add(id);
                        _misses++;
                    } else {
                        _hits++;
                    }
                }
            }
            return missing;
        }
        /// <summary>
        /// Looks up many keys: <paramref name="found"/>[i] gets the value of <paramref name="keys"/>[i], and
        /// the positions of the keys not in the cache are returned. The lock is taken per chunk of keys, so a
        /// large lookup does not hold up every other reader of the cache while it runs.
        /// </summary>
        public List<int> TryGetMany(TKey[] keys, TValue?[] found) {
            if (found.Length < keys.Length) throw new ArgumentException("The result array is shorter than the keys. ", nameof(found));
            const int chunk = 1024;
            var missing = new List<int>();
            for (var start = 0; start < keys.Length; start += chunk) {
                var end = Math.Min(start + chunk, keys.Length);
                lock (_lock) {
                    for (var i = start; i < end; i++) {
                        if (_cache.TryGetValue(keys[i], out var item)) {
                            found[i] = item.Data;
                            touch(item);
                            _hits++;
                        } else {
                            missing.Add(i);
                            _misses++;
                        }
                    }
                }
            }
            return missing;
        }
        /// <summary>
        /// The cached value, created and added when missing. The entry is added with size 0, which this
        /// cache reserves for entries it must keep: it is never evicted and not counted against the budget,
        /// until <see cref="TryUpdateSize"/> gives it a size. For an ordinary bounded cache use
        /// </summary>
        public TValue GetOrCreate(TKey nodeId, Func<TValue> create) {
            lock (_lock) {
                if (_cache.TryGetValue(nodeId, out var item)) {
                    touch(item);
                    _hits++;
                    return item.Data;
                }
            }
            var data = create();
            lock (_lock) {
                if (_cache.TryGetValue(nodeId, out var item)) {
                    _size -= item.Size;
                    item.Data = data;
                    item.Size = 0;
                    place(item, false);
                } else {
                    item = new Entry(nodeId, data, 0);
                    _cache.Add(nodeId, item);
                }
                _misses++;
                return data;
            }
        }
        public bool TryGet(TKey nodeId, [MaybeNullWhen(false)] out TValue data) {
            lock (_lock) {
                if (_cache.TryGetValue(nodeId, out var item)) {
                    data = item.Data;
                    touch(item);
                    _hits++;
                    return true;
                } else {
                    data = default;
                    _misses++;
                    return false;
                }
            }
        }
        public bool Clear_EvenIf0Size(TKey nodeId) {
            lock (_lock) {
                if (_cache.TryGetValue(nodeId, out var item)) {
                    _size -= item.Size;
                    remove(item);
                    return true;
                } else {
                    return false;
                }
            }
        }
        public void ClearAll_NotSize0() {
            lock (_lock) {
                var e = _lru;
                while (e != null) { // only entries with a size are linked; the reserved ones stay
                    var newer = e.Newer;
                    remove(e);
                    e = newer;
                }
                _misses = 0;
                _hits = 0;
                _size = 0;
            }
        }
        public IEnumerable<KeyValuePair<TKey, TValue>> AllNotThreadSafe() {
            foreach (var kv in _cache) {
                yield return new KeyValuePair<TKey, TValue>(kv.Key, kv.Value.Data);
            }
        }
        public void HalfSize() {
            lock (_lock) reduceToSize(_size / 2);
        }
        public long Size { get { lock (_lock) return _size; } }
        public int Count { get { lock (_lock) return _cache.Count; } }
        public int CountZeroSize { get { lock (_lock) return _cache.Count(i => i.Value.Size == 0); } }
        public long MaxSize { get { lock (_lock) return _maxSize; } }
        public long Hits { get { lock (_lock) return _hits; } }
        public long Misses { get { lock (_lock) return _misses; } }
        public long Overflows { get { lock (_lock) return _overflows; } }
    }
}
