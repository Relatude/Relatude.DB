using Relatude.DB.Datamodels;
using Relatude.DB.Serialization;

namespace Relatude.DB.DataStores;

// Node versions: every write of a node appends the full node to the log together with the position
// of the node's previous version in the same file (the version chain), so older versions are found
// by following the chain backwards — one read per version — instead of scanning the log. The
// primary log holds the versions since the last log rewrite; the secondary backup log survives
// rewrites and extends the reach. Versions are read straight from the log files and never cached.
public sealed partial class DataStoreLocal : IDataStore {
    public NodeVersionData[] FindOlderVersions(Guid nodeId, int maxCount = 100, QueryContext? ctx = null) {
        validateDatabaseState();
        if (maxCount <= 0) return [];
        if (_wal.GetQueueActionCount() > 0) FlushToDisk(false, 0); // versions are read from the log files, so queued writes must reach them first
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            if (!_guids.TryGetId(nodeId, out var id)) return []; // unknown or deleted nodes have no reachable chain head
            if (!_nodes.TryGetSegment(id, out var currentSegment)) return [];
            var records = _wal.CollectOlderVersions(id, nodeId, currentSegment, maxCount);
            var ordered = records.OrderByDescending(r => r.Timestamp).ToList();
            // a log rewrite re-writes the then-current version under a fresh timestamp while the
            // secondary log keeps the original record; identical adjacent versions carry no
            // information, so collapse them to the oldest. Compared through one re-serialization,
            // never on stored bytes: deserializing materializes datamodel defaults, so stored bytes
            // differ between a record and its rewrite even when the content is identical:
            if (ordered.Count > 1) {
                var normalized = ordered.Select(r => {
                    var ms = new MemoryStream();
                    ToBytes.NodeData(r.Node, Datamodel, ms);
                    return ms.ToArray();
                }).ToList();
                for (var i = ordered.Count - 2; i >= 0; i--) {
                    if (normalized[i].AsSpan().SequenceEqual(normalized[i + 1])) {
                        ordered.RemoveAt(i);
                        normalized.RemoveAt(i);
                    }
                }
            }
            return ordered.Take(maxCount).Select(r => new NodeVersionData {
                Source = r.Source,
                Timestamp = r.Timestamp,
                Node = ToOuter(r.Node, ctx),
            }).ToArray();
        } finally {
            _lock.ExitReadLock();
        }
    }
}
