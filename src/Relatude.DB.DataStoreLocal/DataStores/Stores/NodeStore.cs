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
//
// Reading in two steps (see NodeRead): what a read needs is looked up while the caller holds the store's
// read lock - cached nodes, and log positions for the rest - and the bytes are read once that lock is let
// go, under a read lease on the log file. Positions in the log never change while the lease is held, so
// the result is exactly what reading under the lock would have given, while writers no longer wait for
// the disk. Every insert into and removal from the cache happens under _lock, which is what lets a
// reader check, before caching what it read, that the node has not changed since it was looked up.
internal sealed class NodeStore {
    readonly System.Threading.Lock _lock = new();
    readonly static Guid _marker = new Guid("993d32a7-f608-43d7-a800-0be4208f723a");
    readonly WALFile _log;
    readonly Cache<int, INodeDataInternal> _cache; // threadsafe
    readonly ISegmentMap _segments; // main store of all valid nodes; an engine backed map is written from the transaction thread only
    readonly Dictionary<int, NodeSegment> _pending = []; // log positions confirmed by the log writer, waiting for DrainPendingSegments when the map is engine backed
    readonly HashSet<int> _dropWhenWritten = []; // nodes from bulk transactions, evicted the moment the log write makes them readable from disk
    Definition _definition;
    public NodeStore(Definition definition, SettingsLocal config, WALFile log, ISegmentMap segments) {
        _log = log;
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
    /// <summary>The ids of every node, those not yet written to the log included - which
    /// <see cref="Snapshot"/> refuses, as it hands out log positions.</summary>
    public int[] AllIds() {
        lock (_lock) {
            var ids = new List<int>(_segments.Count);
            foreach (var kv in _segments.Entries) ids.Add(kv.Key);
            return [.. ids];
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
    /// <summary>Reads the nodes in one go, while the caller still holds the store lock. Throws when one does not exist.</summary>
    public INodeDataInternal[] Get(int[] ids, ref int diskReads, ref int nodesFromDisk) {
        using var read = BeginRead(ids, throwIfMissing: true);
        return read.Complete(CancellationToken.None, ref diskReads, ref nodesFromDisk)!;
    }
    public bool TryGet(int id, [MaybeNullWhen(false)] out INodeDataInternal node, out bool diskRead) {
        diskRead = false;
        if (_cache.TryGet(id, out node)) return true;
        using var read = BeginRead([id], throwIfMissing: false);
        if (read.NodesToRead == 0) { // gone, or cached in the meantime
            node = read.Complete(CancellationToken.None)[0];
            return node != null;
        }
        diskRead = true;
        node = read.Complete(CancellationToken.None)[0]!;
        return true;
    }
    /// <summary>
    /// The first step of a read, taken while the caller holds the store's read (or write) lock: the cached
    /// nodes, and the log positions of the rest under a read lease on the log file. Complete the read after
    /// letting the store lock go - or still holding it - and dispose it either way. A node that does not
    /// exist throws here, or with <paramref name="throwIfMissing"/> off comes back as null.
    /// </summary>
    public NodeRead BeginRead(int[] ids, bool throwIfMissing) {
        var nodes = new INodeDataInternal?[ids.Length];
        var missing = _cache.TryGetMany(ids, nodes);
        if (missing.Count == 0) return new NodeRead(this, ids, nodes, [], [], null);
        var toRead = new List<int>(missing.Count);
        var segments = new List<NodeSegment>(missing.Count);
        lock (_lock) { // _segments is not threadsafe
            foreach (var pos in missing) {
                var id = ids[pos];
                if (!tryGetSegment(id, out var segment)) {
                    if (throwIfMissing) throw new KeyNotFoundException("Node not found: " + id);
                    continue;
                }
                if (segment.Length == 0) {
                    // not written to the log yet, so it is held in the cache: a writer on this thread may have
                    // replaced it since the lookup above (no other thread writes while the store lock is held)
                    if (_cache.TryGet(id, out var cached)) {
                        nodes[pos] = cached;
                        continue;
                    }
                    throw new Exception("Internal error: node " + id + " is neither cached nor written to the log. ");
                }
                toRead.Add(pos);
                segments.Add(segment);
            }
        }
        // the lease is taken while the caller still holds the store lock, so no swap of the log file can
        // come between looking the positions up and reading them
        var lease = toRead.Count > 0 ? _log.AcquireReadLease() : null;
        return new NodeRead(this, ids, nodes, [.. toRead], [.. segments], lease);
    }
    /// <summary>
    /// A read begun under the store lock (see <see cref="BeginRead"/>): the cached nodes, and the log
    /// positions of the rest under a read lease. <see cref="Complete(CancellationToken, ref int, ref int)"/>
    /// reads them and needs no store lock; it must not take one either, as a writer swapping the log file
    /// holds the store's write lock while it waits for this lease. Dispose releases the lease; disposing
    /// without completing is how a read is abandoned.
    /// </summary>
    internal sealed class NodeRead : IDisposable {
        readonly NodeStore _store;
        readonly int[] _ids;
        readonly INodeDataInternal?[] _nodes;
        readonly int[] _toRead; // positions in _ids still to be read from the log
        readonly NodeSegment[] _segments; // their log positions
        LogReadGate.Lease? _lease;
        internal NodeRead(NodeStore store, int[] ids, INodeDataInternal?[] nodes, int[] toRead, NodeSegment[] segments, LogReadGate.Lease? lease) {
            _store = store;
            _ids = ids;
            _nodes = nodes;
            _toRead = toRead;
            _segments = segments;
            _lease = lease;
        }
        public int NodesToRead => _toRead.Length;
        public INodeDataInternal?[] Complete(CancellationToken cancel) {
            int diskReads = 0, nodesFromDisk = 0;
            return Complete(cancel, ref diskReads, ref nodesFromDisk);
        }
        /// <summary>The nodes, in the order of the ids the read was begun with; null for one that did not exist.</summary>
        public INodeDataInternal?[] Complete(CancellationToken cancel, ref int diskReads, ref int nodesFromDisk) {
            if (_toRead.Length == 0) {
                Dispose();
                return _nodes;
            }
            if (_lease == null) throw new ObjectDisposedException(nameof(NodeRead), "The read was abandoned. ");
            INodeDataInternal[] read;
            try {
                read = _store.readFromLog(_ids, _toRead, _segments, cancel, out var reads);
                diskReads += reads;
                nodesFromDisk += _toRead.Length;
            } finally {
                Dispose(); // the bytes are in; caching what was read needs no lease
            }
            for (var i = 0; i < _toRead.Length; i++) _nodes[_toRead[i]] = read[i];
            _store.cacheIfCurrent(read, _segments);
            return _nodes;
        }
        public void Dispose() {
            var lease = _lease;
            _lease = null;
            lease?.Dispose();
        }
    }
    INodeDataInternal[] readFromLog(int[] ids, int[] toRead, NodeSegment[] segments, CancellationToken cancel, out int diskReads) {
        var datamodel = _definition.Datamodel;
        var read = new INodeDataInternal[segments.Length];
        // may run on several threads at once, each for its own index
        _log.ReadNodeSegments(segments, (index, buffer, offset, length) => {
            // checked per node as well as per batch: the batches of one read may all be in flight at once,
            // and a stop arriving while they are should not wait for every node to be deserialized too
            cancel.ThrowIfCancellationRequested();
            var node = FromBytes.NodeData(datamodel, new MemoryStream(buffer, offset, length, false), null);
            if (node.__Id != ids[toRead[index]]) throw new Exception("Internal error: the log holds node " + node.__Id + " where node " + ids[toRead[index]] + " was expected. ");
            node.EnsureReadOnly(); // Making it immutable, as it is shared through the cache for other queries
            read[index] = node;
        }, cancel, out diskReads);
        return read;
    }
    // Caches what a read got from the log, unless the node changed after its position was looked up - the
    // read may have run outside the store lock, beside writers. A node whose position is still the one it
    // was read from is the same version. A read big enough to push out a good part of the cache goes in
    // cold, as the first to be evicted, so a large scan does not flush what other readers use.
    void cacheIfCurrent(INodeDataInternal[] nodes, NodeSegment[] segments) {
        long bytes = 0;
        foreach (var s in segments) bytes += estimateSize(s.Length);
        var cold = bytes > _cache.MaxSize / 4;
        const int chunk = 1024; // the node store lock is taken per chunk, not held for the whole read
        for (var start = 0; start < nodes.Length; start += chunk) {
            var end = Math.Min(start + chunk, nodes.Length);
            lock (_lock) {
                for (var i = start; i < end; i++) {
                    var node = nodes[i];
                    if (!tryGetSegment(node.__Id, out var current) || current.AbsolutePosition != segments[i].AbsolutePosition || current.Length != segments[i].Length) continue;
                    _cache.TryAdd(node.__Id, node, estimateSize(segments[i].Length), cold); // another reader may have cached it already
                }
            }
        }
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
    internal void AddCacheCounters(StoreCounters counters) {
        lock (_lock) {
            counters.NodeCacheHits = _cache.Hits;
            counters.NodeCacheMisses = _cache.Misses;
            counters.NodeCacheOverflows = _cache.Overflows;
        }
    }
    internal long CacheSize { get { lock (_lock) { return _cache.Size; } } }
    internal int CacheCount { get { lock (_lock) { return _cache.Count; } } }
    internal int Count { get { lock (_lock) { return _segments.Count; } } }
    internal void HalfCacheSize() { lock (_lock) _cache.HalfSize(); }
    internal long CacheMaxSize => _cache.MaxSize;
    internal void SetCacheMaxSize(long bytes) => _cache.SetMaxSize(bytes);
}
