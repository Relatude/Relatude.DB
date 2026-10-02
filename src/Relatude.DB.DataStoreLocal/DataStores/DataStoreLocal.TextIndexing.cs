using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.Tasks.TextIndexing;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    public TextExtract[] GetTextExtract(IEnumerable<int> ids, TextIndexType indexType) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            var matchingIds = ids.Where(_nodes.Contains).ToArray();
            var nodes = _nodes.Get(matchingIds);
            Interlocked.Add(ref _noNodeGetsSinceClearCache, nodes.Length);
            List<TextExtract> extracts = [];
            foreach (var node in nodes) {
                if (node is NodeData nd) {
                    extracts.Add(new(nd.__Id, getExtract(nd, indexType), null));
                } else if (node is NodeDataRevisions nr) {
                    foreach (var revision in nr.Revisions) {
                        if (revision.RevisionType == RevisionType.Published) {
                            extracts.Add(new(nr.__Id, getExtract(nr, indexType), revision.RevisionId));
                        }
                    }
                }
            }
            return extracts.ToArray();
        } finally {
            _lock.ExitReadLock();
        }
    }
    public int ReIndexAllText() {
        _lock.EnterReadLock();
        int[][] idsPerType;
        try {
            validateDatabaseState();
            // read the ids under the lock, queue outside it: queueing writes batches to the task
            // queue store, which is not work to hold a read lock over
            idsPerType = [.. _definition.Datamodel.NodeTypes.Values
                .Where(nodeType => nodeType.TextIndex == true)
                // the exact type only: every node is reached through its own type, and a type that
                // is not text indexed must not be pulled in by an indexed ancestor - the same rule
                // the transaction path applies when it decides what to index
                .Select(nodeType => _definition.GetAllIdsForTypeNoAccessControl(nodeType.Id, false).ToArray())];
        } finally {
            _lock.ExitReadLock();
        }
        var count = 0;
        foreach (var ids in idsPerType) {
            foreach (var id in ids) {
                EnqueueTask(new TextIndexTask(id));
                count++;
            }
        }
        LogInfo("Queued " + count.To1000N() + " nodes for text indexing. ");
        return count;
    }
    public int ReIndexText(IEnumerable<Guid> nodeTypeIds) {
        var wanted = nodeTypeIds.ToHashSet();
        _lock.EnterReadLock();
        List<(int[] ids, bool text, bool semantic)> perType;
        try {
            validateDatabaseState();
            // the exact types only, as in ReIndexAllText; a type indexed neither way has nothing to queue
            perType = [.. _definition.Datamodel.NodeTypes.Values
                .Where(nodeType => wanted.Contains(nodeType.Id))
                .Select(nodeType => (text: nodeType.TextIndex == true, semantic: _ai != null && nodeType.SemanticIndex == true, nodeType.Id))
                .Where(x => x.text || x.semantic)
                .Select(x => (_definition.GetAllIdsForTypeNoAccessControl(x.Id, false).ToArray(), x.text, x.semantic))];
        } finally {
            _lock.ExitReadLock();
        }
        var count = 0;
        foreach (var (ids, text, semantic) in perType) {
            foreach (var id in ids) {
                if (text) { EnqueueTask(new TextIndexTask(id)); count++; }
                if (semantic) { EnqueueTask(new SemanticIndexTask(id)); count++; }
            }
        }
        LogInfo("Queued " + count.To1000N() + " text and semantic indexing tasks for " + perType.Count + " node type" + (perType.Count == 1 ? "" : "s") + ". ");
        return count;
    }
    public int ClearIndexedText(IEnumerable<Guid> nodeTypeIds) {
        var wanted = nodeTypeIds.ToHashSet();
        List<(int nodeId, Guid? revisionId)> targets = [];
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            var ids = _definition.Datamodel.NodeTypes.Values.Where(t => wanted.Contains(t.Id))
                .SelectMany(t => _definition.GetAllIdsForTypeNoAccessControl(t.Id, false).ToArray()).ToArray();
            // the text of a node with revisions is indexed per published revision, as in GetTextExtract
            foreach (var node in _nodes.Get(ids.Where(_nodes.Contains).ToArray())) {
                if (node is NodeData nd) targets.Add((nd.__Id, null));
                else if (node is NodeDataRevisions nr) foreach (var r in nr.Revisions) if (r.RevisionType == RevisionType.Published) targets.Add((nr.__Id, r.RevisionId));
            }
        } finally {
            _lock.ExitReadLock();
        }
        foreach (var batch in targets.Chunk(1000)) {
            var t = new TransactionData();
            foreach (var (nodeId, revisionId) in batch) t.ForceUpdateProperty(nodeId, NodeConstants.SystemTextIndexPropertyId, revisionId, string.Empty);
            Execute(t);
        }
        if (targets.Count > 0) LogInfo("Cleared the indexed text of " + targets.Count.To1000N() + " nodes. ");
        return targets.Count;
    }
    string getExtract(INodeDataInternal node, TextIndexType indexType) {
        return indexType == TextIndexType.PlainTextSearch ? UtilsText.GetTextExtract(this, node) : UtilsText.GetSemanticExtract(this, node);
    }
}
