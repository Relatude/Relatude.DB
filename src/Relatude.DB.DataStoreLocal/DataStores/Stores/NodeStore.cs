using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
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
    object _lock = new();
    static Guid _marker = new Guid("993d32a7-f608-43d7-a800-0be4208f723a");
    readonly ReadSegmentsFunc _read;
    readonly Cache<int, INodeData> _cache; // threadsafe
    readonly Dictionary<int, NodeSegment> _segments;  // NOT threadsafe, main store of all valid nodes
    Definition _definition;
    public NodeStore(Definition definition, SettingsLocal config, ReadSegmentsFunc read) {
        _read = read;
        _segments = [];
        _definition = definition;
        _cache = new((long)(config.NodeCacheSizeGb * Math.Pow(1024, 3)));
    }
    public (int nodeId, NodeSegment segment)[] Snapshot() {
        lock (_lock) {
            var result = _segments.Select(kv => (nodeId: kv.Key, segment: kv.Value)).ToArray();
            if (result.Where(s => s.segment.Length == 0).Any()) throw new Exception("Snapshot not ready");
            return result;
        }
    }
    public INodeData Get(int id) {
        if (TryGet(id, out var node, out _)) return node;
        throw new Exception("Node not found");
    }
    public INodeData Get(int id, out bool didReadDisk) {
        if (TryGet(id, out var node, out didReadDisk)) return node;
        throw new Exception("Node not found");
    }
    public INodeData[] Get(int[] ids) {
        int diskReads = 0;
        int nodesFromDisk = 0;
        return Get(ids, ref diskReads, ref nodesFromDisk);
    }
    public INodeData[] Get(int[] ids, ref int diskReads, ref int nodesFromDisk) {
        // first check cache to estimate missing
        var missing = _cache.GetMissing(ids);
        if (missing.Any()) { // load to cache
            // load all missing from cache from log in one batch
            nodesFromDisk += missing.Count;
            var segments = missing.Select(id => _segments[id]).ToArray();
            var bytes = _read(segments, out diskReads);  // takes time...
            // there is no read lock, so we need to check again if the item is in cache
            // there is a change both threads will read same item from log, but that is ok
            // better than locking the whole cache and block other threads
            var i = 0;
            foreach (var id in missing) {
                var b = bytes[i++];
                if (!_cache.Contains(id)) { // additional check in case other thread have added it to cache
                    var ms = new MemoryStream(b, false);
                    var node = FromBytes.NodeData(_definition.Datamodel, ms);
                    if (node.__Id != id) throw new Exception("Internal error");
                    node.EnsureReadOnly();
                    _cache.Set(node.__Id, node, b.Length);
                }
            }
        }
        var nodes = new INodeData[ids.Length];
        for (var i = 0; i < ids.Length; i++) {
            nodes[i] = Get(ids[i], out var didReadDisk); // will read from log if not in cache            
            if (didReadDisk) {
                nodesFromDisk++;
                diskReads++;
            }
        }
        return nodes;
    }
    public bool TryGet(int id, [MaybeNullWhen(false)] out INodeData node, out bool diskRead) {
        diskRead = false;
        if (_cache.TryGet(id, out node)) return true;
        // if not in cache, it must be in log as items are kept in cache until written to log ( size ==0 )
        NodeSegment segment;
        lock (_lock) {
            if (!_segments.TryGetValue(id, out segment)) {
                node = null;
                return false;
            }
        }
        var ms = new MemoryStream(_read([segment], out _).First(), false);
        diskRead = true;
        node = FromBytes.NodeData(_definition.Datamodel, ms);
        if (node.__Id != id) throw new Exception("Internal error");
        node.EnsureReadOnly(); // Making it immutable, as it is shared through the cache for other queries
        _cache.Set(node.__Id, node, estimateSize(segment.Length));
        return true;
    }
    public void ClearCache() {
        lock (_lock) _cache.ClearAll_NotSize0();
    }
    public bool Contains(int id) {
        lock (_lock) return _segments.ContainsKey(id);
    }
    public void Add(INodeData node, NodeSegment? segment) {
        lock (_lock) {
            node.EnsureReadOnly();
            _segments.Add(node.__Id, segment ?? (new()));
            _cache.Set(node.__Id, node, 0);
        }
    }
    public void Remove(INodeData node, out NodeSegment segmentInfoRemoved) {
        lock (_lock) {
            segmentInfoRemoved = _segments[node.__Id];
            _segments.Remove(node.__Id);
            _cache.Clear_EvenIf0Size(node.__Id); // if zero size in cache, item will never be written to log, so it can be removed
        }
    }
    public void UpdateNodeDataPositionInLogFile(int id, NodeSegment segment) {
        lock (_lock) {
            if (!_segments.ContainsKey(id)) return;
            _segments[id] = segment;
            _cache.TryUpdateSize(id, estimateSize(segment.Length));
        }
    }
    public void RegisterAction_NotThreadsafe(PrimitiveNodeAction action) { // not threadsafe, must be called from log writer thread only
        switch (action.Operation) {
            case PrimitiveOperation.Add:
                if (action.Segment == null) throw new Exception("Internal error. ");
                _segments.Add(action.Node.__Id, action.Segment.Value);
                break;
            case PrimitiveOperation.Remove:
                _segments.Remove(action.Node.__Id);
                break;
            default: throw new NotImplementedException();
        }
    }
    internal void ReadState(IReadStream stream, Action<string?, int?> progress) {
        stream.ValidateMarker(_marker);
        stream.RecordChecksum();
        var count = stream.ReadVerifiedInt();
        for (var i = 0; i < count; i++) {
            if (i % 79190 == 0) progress("Reading node index " + (i + 1) + " of " + count, (i * 100 / count)); // just a prime number to "avoid" patterns
            var nodeId = (int)stream.ReadUInt();
            var pos = stream.ReadLong();
            var len = stream.ReadVerifiedInt();
            _segments.Add(nodeId, new NodeSegment(pos, len));
        }
        stream.ValidateChecksum();
        stream.ValidateMarker(_marker);
    }
    internal void SaveState(IAppendStream stream) {
        stream.WriteGuid(_marker);
        stream.RecordChecksum();
        stream.WriteVerifiedInt(_segments.Count);
        foreach (var kv in _segments) {
            stream.WriteUInt((uint)kv.Key); // node id
            stream.WriteLong(kv.Value.AbsolutePosition); // position in log file
            stream.WriteVerifiedInt(kv.Value.Length);  // length
        }
        stream.WriteChecksum();
        stream.WriteGuid(_marker);
    }
    int estimateSize(int segmentLength) {
        return segmentLength + INodeData.BaseSize;
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
    internal int Count { get { lock (_lock) { return _segments.Count; } } }
    internal void HalfCacheSize() { lock (_lock) _cache.HalfSize(); }
}