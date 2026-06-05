using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Common {
    /// <summary>
    /// Threadsafe LRU cache backed by a linked list and dictionary (O(1) access and eviction).
    /// Items are evicted least-recently-used first when total size reaches max.
    /// Trims just enough entries to stay below max — no half-size reduction.
    /// Only items with size > 0 are evicted; size-0 items are pinned.
    /// </summary>
    public class LiloCache<TKey, TValue>(long maxSize) where TKey : notnull {
        class Entry(TKey key, TValue data, int size) {
            public TKey Key = key;
            public TValue Data = data;
            public int Size = size;
        }

        readonly object _lock = new();
        readonly long _maxSize = maxSize;
        readonly Dictionary<TKey, LinkedListNode<Entry>> _map = [];
        readonly LinkedList<Entry> _list = new();
        long _hits, _misses, _size;

        // Front = MRU, Back = LRU
        void moveToFront(LinkedListNode<Entry> node) {
            if (_list.First == node) return;
            _list.Remove(node);
            _list.AddFirst(node);
        }

        void TrimToMax() {
            var node = _list.Last;
            while (_size >= _maxSize && node != null) {
                var prev = node.Previous;
                if (node.Value.Size > 0) {
                    _size -= node.Value.Size;
                    _map.Remove(node.Value.Key);
                    _list.Remove(node);
                }
                node = prev;
            }
        }

        public bool TryUpdateSize(TKey key, int size) {
            lock (_lock) {
                if (!_map.TryGetValue(key, out var node)) return false;
                if (_maxSize == 0 && size > 0) {
                    _size -= node.Value.Size;
                    _list.Remove(node);
                    _map.Remove(key);
                } else {
                    _size = _size - node.Value.Size + size;
                    node.Value.Size = size;
                    if (size > 0) TrimToMax();
                }
                return true;
            }
        }

        public void Set(TKey key, TValue data, int size) {
            if (_maxSize == 0 && size > 0) return;
            lock (_lock) {
                if (_map.TryGetValue(key, out var node)) {
                    _size -= node.Value.Size;
                    node.Value.Data = data;
                    node.Value.Size = size;
                    moveToFront(node);
                } else {
                    _map[key] = _list.AddFirst(new Entry(key, data, size));
                }
                _size += size;
                if (size > 0) TrimToMax();
            }
        }

        public bool Contains(TKey id) { lock (_lock) return _map.ContainsKey(id); }

        public List<TKey> GetMissing(TKey[] ids) {
            List<TKey> missing = new();
            lock (_lock) {
                foreach (var id in ids) {
                    if (!_map.ContainsKey(id)) { missing.Add(id); _misses++; }
                    else _hits++;
                }
            }
            return missing;
        }

        public TValue GetOrCreate(TKey nodeId, Func<TValue> create) {
            lock (_lock) {
                if (_map.TryGetValue(nodeId, out var node)) { moveToFront(node); _hits++; return node.Value.Data; }
            }
            var data = create();
            lock (_lock) {
                _map[nodeId] = _list.AddFirst(new Entry(nodeId, data, 0));
                _misses++;
                return data;
            }
        }

        public bool TryGet(TKey nodeId, [MaybeNullWhen(false)] out TValue data) {
            lock (_lock) {
                if (_map.TryGetValue(nodeId, out var node)) {
                    data = node.Value.Data;
                    moveToFront(node);
                    _hits++;
                    return true;
                }
                data = default;
                _misses++;
                return false;
            }
        }

        public bool Clear_EvenIf0Size(TKey nodeId) {
            lock (_lock) {
                if (!_map.TryGetValue(nodeId, out var node)) return false;
                _size -= node.Value.Size;
                _list.Remove(node);
                _map.Remove(nodeId);
                return true;
            }
        }

        public void ClearAll_NotSize0() {
            lock (_lock) {
                foreach (var kv in _map.Where(kv => kv.Value.Value.Size > 0).ToList()) {
                    _list.Remove(kv.Value);
                    _map.Remove(kv.Key);
                }
                _hits = _misses = _size = 0;
            }
        }

        public IEnumerable<KeyValuePair<TKey, TValue>> AllNotThreadSafe() {
            foreach (var e in _list) yield return new KeyValuePair<TKey, TValue>(e.Key, e.Data);
        }

        public void HalfSize() {
            lock (_lock) {
                var target = _size / 2;
                var node = _list.Last;
                while (_size > target && node != null) {
                    var prev = node.Previous;
                    if (node.Value.Size > 0) { _size -= node.Value.Size; _map.Remove(node.Value.Key); _list.Remove(node); }
                    node = prev;
                }
            }
        }

        public long Size { get { lock (_lock) return _size; } }
        public int Count { get { lock (_lock) return _map.Count; } }
        public int CountZeroSize { get { lock (_lock) return _map.Count(i => i.Value.Value.Size == 0); } }
        public long MaxSize => _maxSize;
        public long Hits { get { lock (_lock) return _hits; } }
        public long Misses { get { lock (_lock) return _misses; } }
    }
}
