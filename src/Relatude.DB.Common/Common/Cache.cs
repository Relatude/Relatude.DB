using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Linq;

namespace Relatude.DB.Common {
    class Entry<T> {
        public Entry(T data, ulong timestamp, int size) {
            Data = data;
            Timestamp = timestamp;
            Size = size;
        }
        public T Data;
        public ulong Timestamp;
        public int Size;
    }
    // Threadsafe
    // Simple cache, with a upper memory size ( _maxSize )
    // LRU cache, items are removed in order of last accessed
    // If adding an item will exceed max size it removes items from cache until total size is half of max
    // Reducing size is costly, and above logic reduce calls to this method
    // Only items with size>0 is removed. So items with size 0 is reserved!!
    // This is used to ensure items are kept in cache until segment is written to transaction log.
    // Only after item is written to transaction log, its size is updated.
    public class Cache<TKey, TValue>(long maxSize) where TKey : notnull {
        readonly object _lock = new();
        readonly long _maxSize = maxSize;
        readonly Dictionary<TKey, Entry<TValue>> _cache = [];
        long _hits = 0;
        long _misses = 0;
        int _overflows = 0;
        long _size = 0;
        ulong _timestamp = 0;
        public bool TryUpdateSize(TKey key, int size) {
            lock (_lock) {
                if (_cache.TryGetValue(key, out var item)) {
                    if (_maxSize == 0 && size > 0) {
                        _cache.Remove(key); // it means items was only in cache because it had size 0, ( and had not been written to log yet )
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
                    item.Timestamp = ++_timestamp;
                    item.Size = size;
                } else {
                    _cache.Add(key, new Entry<TValue>(data, ++_timestamp, size));
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
            var items = _cache.OrderBy(i => i.Value.Timestamp);
            if (_size < size) return;
            _overflows++;
            foreach (var i in items) {
                if (i.Value.Size > 0) { // if size is 0, it indicates item should never be removed ( used by Nodestore while waiting for transaction log write)
                    if (_size < size) break;
                    _size -= i.Value.Size;
                    _cache.Remove(i.Key);
                }
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
        public bool TryGet(TKey nodeId, [MaybeNullWhen(false)] out TValue data) {
            lock (_lock) {
                if (_cache.TryGetValue(nodeId, out var item)) {
                    data = item.Data;
                    item.Timestamp = ++_timestamp;
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
                    _cache.Remove(nodeId);
                    return true;
                } else {
                    return false;
                }
            }
        }
        public void ClearAll_NotSize0() {
            lock (_lock) {
                _overflows++;
                var toRemove = _cache.Where(kv => kv.Value.Size > 0).ToArray();
                foreach (var kv in toRemove) _cache.Remove(kv.Key);
                _misses = 0;
                _hits = 0;
                _size = 0;
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
