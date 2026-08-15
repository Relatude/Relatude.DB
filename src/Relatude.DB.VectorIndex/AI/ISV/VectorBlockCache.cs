namespace Relatude.DB.AI.ISV;

/// <summary>
/// Byte-budgeted LRU cache over the packed vector blocks read from segment files. The budget is
/// adjustable at runtime. Disk reads happen outside the lock so concurrent searches never serialize
/// on IO, only on the (cheap) bookkeeping; a racing duplicate load of the same block is harmless.
/// </summary>
internal sealed class VectorBlockCache {
    readonly record struct BlockKey(long SegmentId, int Ordinal);
    sealed class Entry {
        public BlockKey Key;
        public float[] Data = [];
        public long Bytes;
    }
    const long entryOverhead = 128; // rough per-entry bookkeeping cost
    readonly object _lock = new();
    readonly Dictionary<BlockKey, LinkedListNode<Entry>> _map = [];
    readonly LinkedList<Entry> _lru = []; // most recently used first
    long _bytes;
    long _maxBytes;
    public VectorBlockCache(long maxBytes) => _maxBytes = maxBytes;
    public long MaxBytes {
        get { lock (_lock) return _maxBytes; }
        set { lock (_lock) { _maxBytes = value; evictOverBudget(); } }
    }
    public long Bytes { get { lock (_lock) return _bytes; } }
    public bool TryGet(long segmentId, int blockOrdinal, out float[] data) {
        lock (_lock) {
            if (_map.TryGetValue(new(segmentId, blockOrdinal), out var node)) {
                _lru.Remove(node);
                _lru.AddFirst(node);
                data = node.Value.Data;
                return true;
            }
        }
        data = [];
        return false;
    }
    public float[] GetOrLoad(long segmentId, int blockOrdinal, Func<float[]> load) {
        if (TryGet(segmentId, blockOrdinal, out var cached)) return cached;
        var data = load(); // outside the lock
        Set(segmentId, blockOrdinal, data);
        return data;
    }
    public void Set(long segmentId, int blockOrdinal, float[] data) {
        var bytes = (long)data.Length * 4 + entryOverhead;
        lock (_lock) {
            if (bytes > _maxBytes) return; // a block bigger than the whole budget would just evict everything
            var key = new BlockKey(segmentId, blockOrdinal);
            if (_map.ContainsKey(key)) return; // keep the first loaded copy, they are identical
            var node = _lru.AddFirst(new Entry { Key = key, Data = data, Bytes = bytes });
            _map.Add(key, node);
            _bytes += bytes;
            evictOverBudget();
        }
    }
    /// <summary>Drops every block of the given segments; used when segments are merged away so the
    /// budget is not held by data that can never be read again.</summary>
    public void RemoveSegments(IReadOnlyCollection<long> segmentIds) {
        if (segmentIds.Count == 0) return;
        var ids = segmentIds as ISet<long> ?? segmentIds.ToHashSet();
        lock (_lock) {
            var node = _lru.First;
            while (node != null) {
                var next = node.Next;
                if (ids.Contains(node.Value.Key.SegmentId)) {
                    _map.Remove(node.Value.Key);
                    _lru.Remove(node);
                    _bytes -= node.Value.Bytes;
                }
                node = next;
            }
        }
    }
    public void Clear() {
        lock (_lock) {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }
    void evictOverBudget() { // must hold _lock
        while (_bytes > _maxBytes && _lru.Last != null) {
            var last = _lru.Last;
            _map.Remove(last.Value.Key);
            _lru.RemoveLast();
            _bytes -= last.Value.Bytes;
        }
    }
}
