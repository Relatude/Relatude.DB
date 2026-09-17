using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.StateStores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Serialization;
using Relatude.DB.Transactions;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores.Stores;
// new nodes are added to cache first, and as empty
// they are kept here ( due to size set to 0 ) until they are written to log
// once written to log, the log update the cache size and updates the segment in _segements
// the item can now be removed from cache later if cache needs to save memory
// if a get is issued for node not in cache, then it is loaded from log ( using segment info )

// threadsafe, excect when loading and using method "_NotThreadsafe"
internal sealed class NodeStore {
    readonly System.Threading.Lock _lock = new();
    readonly static Guid _marker = new Guid("993d32a7-f608-43d7-a800-0be4208f723a");
    readonly ReadSegmentsFunc _read;
    readonly Cache<int, INodeDataInternal> _cache; // threadsafe
    readonly ISegmentMap _segments; // main store of all valid nodes; an engine backed map is written from the transaction thread only
    readonly Dictionary<int, NodeSegment> _pending = []; // log positions confirmed by the log writer, waiting for DrainPendingSegments when the map is engine backed
    readonly HashSet<int> _dropWhenWritten = []; // nodes from bulk transactions, evicted the moment the log write makes them readable from disk
    Definition _definition;
    public NodeStore(Definition definition, SettingsLocal config, ReadSegmentsFunc read, ISegmentMap segments) {
        _read = read;
        _segments = segments;
        _definition = definition;
        _cache = new((long)(config.NodeCacheSizeGb * Math.Pow(1024, 3)));
    }
    bool tryGetSegment(int id, out NodeSegment segment) => _pending.TryGetValue(id, out segment) || _segments.TryGet(id, out segment);
    public (int nodeId, NodeSegment segment)[] Snapshot() {
        lock (_lock) {
            var result = new List<(int nodeId, NodeSegment segment)>(_segments.Count);
            foreach (var kv in _segments.Entries) result.Add((kv.Key, _pending.TryGetValue(kv.Key, out var p) ? p : kv.Value));
            foreach (var s in result) if (s.segment.Length == 0) throw new Exception("Snapshot not ready");
            return [.. result];
        }
    }
    // for the open sequence, before anything else runs: no lock, nothing pending
    internal IEnumerable<KeyValuePair<int, NodeSegment>> EnumerateSegments_NotThreadsafe() => _segments.Entries;
    /// <summary>The nodes already written to the log, with their positions.</summary>
    internal List<(int id, NodeSegment segment)> WrittenSegments() {
        lock (_lock) {
            var result = new List<(int, NodeSegment)>(_segments.Count);
            foreach (var kv in _segments.Entries) {
                var segment = _pending.TryGetValue(kv.Key, out var p) ? p : kv.Value;
                if (segment.Length > 0) result.Add((kv.Key, segment));
            }
            return result;
        }
    }

    public INodeDataInternal Get(int id, out bool didReadDisk) {
        if (TryGet(id, out var node, out didReadDisk)) return node;
        throw new Exception("Node not found");
    }
    public INodeDataInternal[] Get(int[] ids) {
        int diskReads = 0;
        int nodesFromDisk = 0;
        return Get(ids, ref diskReads, ref nodesFromDisk);
    }
    public INodeDataInternal[] Get(int[] ids, ref int diskReads, ref int nodesFromDisk) {
        // first check cache to estimate missing
        var missing = _cache.GetMissing(ids);
        if (missing.Any()) { // load to cache
            // load all missing from cache from log in one batch
            nodesFromDisk += missing.Count;
            var segments = new NodeSegment[missing.Count];
            lock (_lock) { // _segments is not threadsafe
                for (var n = 0; n < segments.Length; n++) {
                    if (!tryGetSegment(missing[n], out segments[n])) throw new KeyNotFoundException("Node not found: " + missing[n]);
                }
            }
            var bytes = _read(segments, out var diskReadsInBatch);  // takes time...
            diskReads += diskReadsInBatch; // accumulate, callers pass a running counter
            // there is no read lock, so we need to check again if the item is in cache
            // there is a change both threads will read same item from log, but that is ok
            // better than locking the whole cache and block other threads
            var i = 0;
            foreach (var id in missing) {
                var b = bytes[i++];
                if (!_cache.Contains(id)) { // additional check in case other thread have added it to cache
                    var ms = new MemoryStream(b, false);
                    var node = FromBytes.NodeData(_definition.Datamodel, ms, null);
                    if (node.__Id != id) throw new Exception("Internal error");
                    node.EnsureReadOnly();
                    _cache.Set(node.__Id, node, b.Length);
                }
            }
        }
        var nodes = new INodeDataInternal[ids.Length];
        for (var i = 0; i < ids.Length; i++) {
            nodes[i] = Get(ids[i], out var didReadDisk); // will read from log if not in cache
            if (didReadDisk) {
                nodesFromDisk++;
                diskReads++;
            }
        }
        return nodes;
    }
    public bool TryGet(int id, [MaybeNullWhen(false)] out INodeDataInternal node, out bool diskRead) {
        diskRead = false;
        if (_cache.TryGet(id, out node)) return true;
        // if not in cache, it must be in log as items are kept in cache until written to log ( size ==0 )
        NodeSegment segment;
        lock (_lock) {
            if (!tryGetSegment(id, out segment)) {
                node = null;
                return false;
            }
        }
        var ms = new MemoryStream(_read([segment], out _).First(), false);
        diskRead = true;
        node = FromBytes.NodeData(_definition.Datamodel, ms, null);
        if (node.__Id != id) throw new Exception("Internal error");
        node.EnsureReadOnly(); // Making it immutable, as it is shared through the cache for other queries
        _cache.Set(node.__Id, node, estimateSize(segment.Length));
        return true;
    }

    public void ClearCache() {
        lock (_lock) _cache.ClearAll_NotSize0();
    }
    public bool Contains(int id) {
        lock (_lock) return _segments.Contains(id);
    }
    /// <summary>Both lookups under one lock, for the two ends of a relation.</summary>
    public void Contains(int id1, int id2, out bool contains1, out bool contains2) {
        lock (_lock) {
            contains1 = _segments.Contains(id1);
            contains2 = _segments.Contains(id2);
        }
    }
    public bool TryGetSegment(int id, out NodeSegment segment) {
        lock (_lock) return tryGetSegment(id, out segment);
    }
    public void Add(INodeDataInternal node, NodeSegment? segment, bool keepInCache = true) {
        lock (_lock) {
            node.EnsureReadOnly();
            _segments.Set(node.__Id, segment ?? (new()));
            _pending.Remove(node.__Id);
            _cache.Set(node.__Id, node, 0);
            if (!keepInCache) _dropWhenWritten.Add(node.__Id);
        }
    }
    public void Remove(INodeDataInternal node, out NodeSegment segmentInfoRemoved) {
        lock (_lock) {
            if (!tryGetSegment(node.__Id, out segmentInfoRemoved)) throw new KeyNotFoundException("Node not found: " + node.__Id);
            _segments.Remove(node.__Id);
            _pending.Remove(node.__Id);
            _dropWhenWritten.Remove(node.__Id);
            _cache.Clear_EvenIf0Size(node.__Id); // if zero size in cache, item will never be written to log, so it can be removed
        }
    }
    public void UpdateNodeDataPositionInLogFile(int id, NodeSegment segment) {
        lock (_lock) updateNodeDataPositionInLogFile(id, segment);
    }
    /// <summary>The log writer confirms positions a batch at a time: one lock per batch instead of one per node, so readers see far fewer lock handovers during a flush.</summary>
    public void UpdateNodeDataPositionsInLogFile(ReadOnlySpan<(int id, NodeSegment segment)> segments) {
        lock (_lock) {
            foreach (var (id, segment) in segments) updateNodeDataPositionInLogFile(id, segment);
        }
    }
    void updateNodeDataPositionInLogFile(int id, NodeSegment segment) {
        if (!_segments.Contains(id)) return;
        if (_segments.PersistedByEngine) _pending[id] = segment; // the engine takes writes from the transaction thread only
        else _segments.Set(id, segment);
        if (_dropWhenWritten.Remove(id)) _cache.Clear_EvenIf0Size(id); // now readable from the log, so no reason to keep the bulk inserted node
        else _cache.TryUpdateSize(id, estimateSize(segment.Length));
    }
    internal bool HasPendingSegments { get { lock (_lock) return _pending.Count > 0; } }
    /// <summary>Writes the confirmed log positions into an engine backed map. Single writer: call inside the engine transaction.</summary>
    internal void DrainPendingSegments() {
        lock (_lock) {
            foreach (var kv in _pending) if (_segments.Contains(kv.Key)) _segments.Set(kv.Key, kv.Value);
            _pending.Clear();
        }
    }
    /// <summary>After a log rewrite hot swap: every node's position in the new log file. Single writer, under the store's write lock.</summary>
    internal void ReplaceAllSegments(IEnumerable<KeyValuePair<int, NodeSegment>> segments) {
        lock (_lock) {
            _pending.Clear(); // positions in the old file
            foreach (var kv in segments) {
                if (!_segments.Contains(kv.Key)) continue;
                _segments.Set(kv.Key, kv.Value);
                if (_dropWhenWritten.Remove(kv.Key)) _cache.Clear_EvenIf0Size(kv.Key);
                else _cache.TryUpdateSize(kv.Key, estimateSize(kv.Value.Length));
            }
        }
    }
    public void RegisterAction_NotThreadsafe(PrimitiveNodeAction action) { // not threadsafe, must be called from log writer thread only
        switch (action.Operation) {
            case PrimitiveOperation.Add:
                if (action.Segment == null) throw new Exception("Internal error. ");
                _segments.Set(action.Node.__Id, action.Segment.Value);
                break;
            case PrimitiveOperation.Remove:
                _segments.Remove(action.Node.__Id);
                break;
            default: throw new NotImplementedException();
        }
    }
    // an engine backed map writes -1 instead of a count, so a state file written by the other kind of store is detected
    internal void ReadState(BufferReader stream, Action<string?, int?> progress) {
        stream.ValidateMarker(_marker);
        stream.RecordChecksum();
        var count = stream.ReadVerifiedInt();
        if (_segments.PersistedByEngine != (count < 0)) throw new Exception("The state file was written by another kind of state store. ");
        if (count > 0) _segments.EnsureCapacity(count);
        for (var i = 0; i < count; i++) {
            if (i % 79190 == 0) progress("Reading node index " + (i + 1) + " of " + count, (i * 100 / count)); // just a prime number to "avoid" patterns
            var nodeId = (int)stream.ReadUInt();
            var pos = stream.ReadLong();
            var len = stream.ReadVerifiedInt();
            _segments.Set(nodeId, new NodeSegment(pos, len));
        }
        stream.ValidateChecksum();
        stream.ValidateMarker(_marker);
    }
    internal void SaveState(IAppendStream stream) {
        stream.WriteGuid(_marker);
        stream.RecordChecksum();
        var count = _segments.PersistedByEngine ? -1 : _segments.Count;
        stream.WriteVerifiedInt(count);
        if (count > 0) {
            foreach (var kv in _segments.Entries) {
                stream.WriteUInt((uint)kv.Key); // node id
                stream.WriteLong(kv.Value.AbsolutePosition); // position in log file
                stream.WriteVerifiedInt(kv.Value.Length);  // length
            }
        }
        stream.WriteChecksum();
        stream.WriteGuid(_marker);
    }
    const int _nodeDataBaseSize = 1000;  // approximate min size of node data without properties for cache size estimation
    int estimateSize(int segmentLength) {
        return segmentLength + _nodeDataBaseSize;
    }
    internal void AddInfo(DataStoreInfo s) {
        lock (_lock) {
            s.NodeCount = _segments.Count;
            s.NodeCacheCount = _cache.Count;
            s.NodeCacheCountOfUnsaved = _cache.CountZeroSize;
            s.NodeCacheSize = _cache.Size;
            if (_cache.MaxSize > 0) s.NodeCacheSizePercentage = 100d * _cache.Size / _cache.MaxSize;
            s.NodeCacheHits = _cache.Hits;
            s.NodeCacheMisses = _cache.Misses;
            s.NodeCacheOverflows = _cache.Overflows;
        }
    }
    internal long CacheSize { get { lock (_lock) { return _cache.Size; } } }
    internal int CacheCount { get { lock (_lock) { return _cache.Count; } } }
    internal int Count { get { lock (_lock) { return _segments.Count; } } }
    internal void HalfCacheSize() { lock (_lock) _cache.HalfSize(); }
    internal long CacheMaxSize => _cache.MaxSize;
    internal void SetCacheMaxSize(long bytes) => _cache.SetMaxSize(bytes);
}
