using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores;

/// <summary>One node as the log file has it: the type it is, where its bytes start and how many there are.</summary>
public readonly record struct NodeSizeEntry(int Id, Guid NodeTypeId, long Position, int Length);
/// <summary>Every node written to the log, with the size of the log file they are in.</summary>
public sealed class NodeSizeSnapshot {
    public NodeSizeEntry[] Nodes { get; init; } = [];
    /// <summary>Nodes still waiting for the log writer: they have no position, so no size, yet.</summary>
    public int NotYetWritten { get; init; }
    public long LogFileSize { get; init; }
    public string LogFileKey { get; init; } = "";
}
/// <summary>A node's stored bytes, or only where they are when the read was over its budget.</summary>
public readonly record struct StoredNodeBytes(int Id, Guid NodeId, Guid NodeTypeId, long Position, int Length, byte[]? Bytes);

// How big each node is where it is stored. Every node the database holds has a segment in the log
// file - the position and length of its current bytes - which the node store keeps for reading it
// back. Those lengths are the binary size of every node, known without reading a single one, so the
// whole database can be measured in the time it takes to walk the segment map.
public sealed partial class DataStoreLocal : IDataStore {
    /// <summary>
    /// The segment of every node written to the log, with its type. Nodes still waiting for the log
    /// writer are counted but not listed: their position is not known yet. The type of each node is
    /// looked up in batches, so writers are only held off for a batch at a time on a large database.
    /// </summary>
    public NodeSizeSnapshot GetNodeSizes() {
        validateDatabaseState();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying, "Measuring node sizes");
        try {
            var written = _nodes.WrittenSegments();
            var all = _nodes.Count;
            var nodes = new List<NodeSizeEntry>(written.Count);
            const int batchSize = 100_000;
            for (var offset = 0; offset < written.Count; offset += batchSize) {
                var end = Math.Min(offset + batchSize, written.Count);
                _lock.EnterReadLock();
                try {
                    validateDatabaseState();
                    for (var i = offset; i < end; i++) {
                        var (id, segment) = written[i];
                        // deleted since the segments were listed: not a node any more
                        if (_definition.TryGetTypeOfNode(id, out var typeId)) nodes.Add(new(id, typeId, segment.AbsolutePosition, segment.Length));
                    }
                } finally {
                    _lock.ExitReadLock();
                }
            }
            long logFileSize = 0;
            try { logFileSize = _wal.FileSize; } catch { } // the file may be in the middle of a swap
            return new NodeSizeSnapshot {
                Nodes = [.. nodes],
                NotYetWritten = Math.Max(0, all - written.Count),
                LogFileSize = logFileSize,
                LogFileKey = _wal.FileKey.AsKeyString(),
            };
        } finally {
            DeRegisterActivity(activityId);
        }
    }
    /// <summary>
    /// The stored bytes of these nodes, read straight from the log file rather than through the node
    /// cache: the nodes asked for here are usually the largest there are, and a look at them should
    /// not push what queries need out of the cache. Read in the order given until <paramref
    /// name="maxBytes"/> is spent; a node that does not fit is still returned, with its position and
    /// length but no bytes. Nodes that are gone, or not written to the log yet, are left out.
    /// </summary>
    public StoredNodeBytes[] ReadStoredNodes(IReadOnlyList<int> ids, long maxBytes) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying, "Reading stored nodes");
        try {
            validateDatabaseState();
            var found = new List<(int Id, Guid Guid, Guid TypeId, NodeSegment Segment)>(ids.Count);
            foreach (var id in ids) {
                if (!_nodes.TryGetSegment(id, out var segment) || segment.Length == 0) continue;
                if (!_definition.TryGetTypeOfNode(id, out var typeId)) continue;
                _guids.TryGetId(id, out Guid guid);
                found.Add((id, guid, typeId, segment));
            }
            var budget = maxBytes;
            var toRead = new List<int>();
            for (var i = 0; i < found.Count; i++) {
                if (found[i].Segment.Length > budget) continue;
                budget -= found[i].Segment.Length;
                toRead.Add(i);
            }
            // one call for all of them: the log reader orders and batches the reads by position
            var read = toRead.Count == 0 ? [] : _wal.ReadNodeSegments([.. toRead.Select(i => found[i].Segment)], out _);
            var bytes = new byte[]?[found.Count];
            for (var i = 0; i < toRead.Count; i++) bytes[toRead[i]] = read[i];
            var result = new StoredNodeBytes[found.Count];
            for (var i = 0; i < found.Count; i++) {
                var f = found[i];
                result[i] = new(f.Id, f.Guid, f.TypeId, f.Segment.AbsolutePosition, f.Segment.Length, bytes[i]);
            }
            return result;
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
}
