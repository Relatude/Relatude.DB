namespace Relatude.DB.DataStores.Indexes.TextIndexing;

internal readonly record struct CacheKey(int Owner, byte Kind, long A, long B, string? Word);

/// <summary>
/// Byte-budget LRU cache shared by every word index of one engine, holding decoded term dictionary
/// blocks and merged postings lists. This is the single knob that bounds how much of the on-disk
/// index is allowed to live in memory (<see cref="TextIndexOptions.MaxCacheBytes"/>). Thread safe:
/// searches run concurrently under the data store's read lock and all mutate the LRU order.
/// </summary>
internal sealed class TextIndexCache(long maxBytes) {
    internal sealed class Entry {
        public CacheKey Key;
        public object Value = null!;
        public long Size;
    }
    readonly object _lock = new();
    readonly Dictionary<CacheKey, LinkedListNode<Entry>> _map = [];
    readonly LinkedList<Entry> _lru = new(); // front = most recently used
    long _used;
    public long MaxBytes { get; } = maxBytes;
    public bool TryGet(CacheKey key, out object value) {
        lock (_lock) {
            if (_map.TryGetValue(key, out var node)) {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = null!;
        return false;
    }
    public void Set(CacheKey key, object value, long size) {
        lock (_lock) {
            if (_map.TryGetValue(key, out var existing)) {
                _used -= existing.Value.Size;
                existing.Value.Value = value;
                existing.Value.Size = size;
                _used += size;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
            } else {
                var node = _lru.AddFirst(new Entry { Key = key, Value = value, Size = size });
                _map.Add(key, node);
                _used += size;
            }
            while (_used > MaxBytes && _lru.Count > 0) {
                var tail = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(tail.Value.Key);
                _used -= tail.Value.Size;
            }
        }
    }
    /// <summary>Drop every entry belonging to one index (owner), optionally only one kind.</summary>
    public void Evict(int owner, byte? kind = null) {
        lock (_lock) {
            var node = _lru.First;
            while (node != null) {
                var next = node.Next;
                if (node.Value.Key.Owner == owner && (kind == null || node.Value.Key.Kind == kind)) {
                    _lru.Remove(node);
                    _map.Remove(node.Value.Key);
                    _used -= node.Value.Size;
                }
                node = next;
            }
        }
    }
}
