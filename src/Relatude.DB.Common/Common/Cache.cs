using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Common {
    /// <summary>
    /// Threadsafe simple cache, with a upper memory size ( _maxSize )
    /// LRU cache, items are removed in order of last accessed
    /// If adding an item will exceed max size it removes items from cache until total size is half of max
    /// Reducing size is costly, and above logic reduce calls to this method
    /// Only items with size>0 is removed. So items with size 0 is reserved!!
    /// This is used to ensure items are kept in cache until segment is written to transaction log.
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="maxSize"></param>
    public class Cache<TKey, TValue>(long maxSize) where TKey : notnull {
        // Entries are linked into an intrusive least-recently-used list, so eviction only touches
        // the entries it actually removes. ( A sort of the whole cache is prohibitive at millions of entries. )
        class Entry(TKey key, TValue data, int size) {
            public readonly TKey Key = key;
            public TValue Data = data;
            public int Size = size;
            public Entry? Newer;
            public Entry? Older;
        }
        readonly object _lock = new();
        readonly long _maxSize = maxSize;
        readonly Dictionary<TKey, Entry> _cache = [];
        Entry? _mru;
        Entry? _lru;
        long _hits = 0;
        long _misses = 0;
        int _overflows = 0;
        long _size = 0;
        void unlink(Entry e) {
            if (e.Newer != null) e.Newer.Older = e.Older; else _mru = e.Older;
            if (e.Older != null) e.Older.Newer = e.Newer; else _lru = e.Newer;
            e.Newer = e.Older = null;
        }
        void linkAsMru(Entry e) {
            e.Older = _mru;
            e.Newer = null;
            if (_mru != null) _mru.Newer = e;
            _mru = e;
            _lru ??= e;
        }
        void touch(Entry e) {
            if (_mru == e) return;
            unlink(e);
            linkAsMru(e);
        }
        void remove(Entry e) {
            unlink(e);
            _cache.Remove(e.Key);
        }
        public bool TryUpdateSize(TKey key, int size) {
            lock (_lock) {
                if (_cache.TryGetValue(key, out var item)) {
                    if (_maxSize == 0 && size > 0) {
                        _size -= item.Size; // it means items was only in cache because it had size 0, ( and had not been written to log yet )
                        remove(item);
                    } else {
                        _size -= item.Size;
                        item.Size = size;
                        _size += size;
                        resizeIfNeeded();
                    }
                    return true;
                } else {
                    return false;
                }
            }
        }
        public void Set(TKey key, TValue data, int size) {
            if (_maxSize == 0 && size > 0) return;
            lock (_lock) {
                if (_cache.TryGetValue(key, out var item)) {
                    _size -= item.Size;
                    item.Data = data;
                    item.Size = size;
                    touch(item);
                } else {
                    item = new Entry(key, data, size);
                    _cache.Add(key, item);
                    linkAsMru(item);
                }
                _size += size;
                if (size > 0) resizeIfNeeded();
            }
        }
        void resizeIfNeeded() {
            // removes items from cache until total size is half of max, if size is above max
            if (_size < _maxSize) return;
            reduceToSize(_maxSize / 2);
        }
        void reduceToSize(long size) {
            // removes items in order of last accessed until total size is less than size
            if (_size < size) return;
            _overflows++;
            var e = _lru;
            while (e != null && _size >= size) {
                var newer = e.Newer;
                if (e.Size > 0) { // if size is 0, it indicates item should never be removed ( used by Nodestore while waiting for transaction log write)
                    _size -= e.Size;
                    remove(e);
                }
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
                    touch(item);
                } else {
                    item = new Entry(nodeId, data, 0);
                    _cache.Add(nodeId, item);
                    linkAsMru(item);
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
                while (e != null) {
                    var newer = e.Newer;
                    if (e.Size > 0) remove(e);
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
