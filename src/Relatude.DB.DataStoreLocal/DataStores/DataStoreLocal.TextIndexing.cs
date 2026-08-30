using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.Tasks.TextIndexing;
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
    string getExtract(INodeDataInternal node, TextIndexType indexType) {
        return indexType == TextIndexType.PlainTextSearch ? UtilsText.GetTextExtract(this, node) : UtilsText.GetSemanticExtract(this, node);
    }
}
