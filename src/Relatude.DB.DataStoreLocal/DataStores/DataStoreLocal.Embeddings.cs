using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Tasks.TextIndexing;
using Relatude.DB.Transactions;
using System.Diagnostics;
namespace Relatude.DB.DataStores;

// Another model's vectors cannot be compared with this one's, and may be of another length. Opened with another model
// than the index record names, the store empties every stored vector and embeds every node again. Emptied through the
// log, not just in the indexes, so a replay empties them before the new ones come, and an empty index takes their length.
public sealed partial class DataStoreLocal {
    string? _embeddingModelOfVectors; // as the index record has it
    /// <summary>At the open, under the write lock once the store is open: before anything can search with the new model.</summary>
    void embedAgainIfTheModelChanged(long activityId) {
        if (_ai == null) return;
        var current = _ai.ModelKey;
        if (_embeddingModelOfVectors == current) return;
        // a record from before the model was kept: the vectors are the model's in force, as the embedding cache takes
        // its first entries to be, unless the cache was first used by another model
        if (_embeddingModelOfVectors == null && !_ai.CacheFirstUsedByAnotherModel) {
            _embeddingModelOfVectors = current;
            writeIndexRecord();
            return;
        }
        try {
            var sw = Stopwatch.StartNew();
            UpdateActivity(activityId, "Emptying the vectors of another embedding model");
            var ids = _definition.Datamodel.NodeTypes.Values
                .Where(t => t.SemanticIndex == true)
                .SelectMany(t => _definition.GetAllIdsForTypeNoAccessControl(t.Id, false).ToArray())
                .Where(_nodes.Contains).ToArray();
            List<(int nodeId, Guid? revisionId)> stored = [];
            foreach (var batch in ids.Chunk(1000)) {
                foreach (var node in _nodes.Get(batch)) {
                    // every revision's, as a draft made from a published one carries its vector and may be published
                    if (node is NodeData nd) {
                        if (hasVector(nd)) stored.Add((nd.__Id, null));
                    } else if (node is NodeDataRevisions nr) {
                        foreach (var r in nr.Revisions) if (hasVector(r)) stored.Add((nr.__Id, r.RevisionId));
                    }
                }
            }
            foreach (var batch in stored.Chunk(1000)) {
                var t = new TransactionData { SystemWrite = true };
                foreach (var (nodeId, revisionId) in batch) t.ForceUpdateProperty(nodeId, NodeConstants.SystemVectorIndexPropertyId, revisionId, Array.Empty<float>());
                Execute(t, flushToDisk: false);
            }
            foreach (var id in ids) EnqueueTask(new SemanticIndexTask(id));
            FlushToDisk(Settings.DeepFlushDisk, activityId); // the emptied vectors are in the log before the record says so
            LogInfo("The vectors were made by another embedding model (" + (_embeddingModelOfVectors ?? "not recorded") + "): emptied "
                + stored.Count.To1000N() + " and queued " + ids.Length.To1000N() + " nodes to be embedded by " + current + " in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
            _embeddingModelOfVectors = current;
            writeIndexRecord();
        } catch (Exception err) {
            // the record still names the old model, so the next open tries again
            LogError("Could not empty the vectors of another embedding model. ", err);
        }
    }
    static bool hasVector(INodeData node) => node.TryGetValue(NodeConstants.SystemVectorIndexPropertyId, out var v) && v is float[] { Length: > 0 };
}
