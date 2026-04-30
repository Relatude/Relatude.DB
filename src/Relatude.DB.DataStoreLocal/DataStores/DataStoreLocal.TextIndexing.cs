using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Transactions;
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
    string getExtract(INodeDataInternal node, TextIndexType indexType) {
        return indexType == TextIndexType.PlainTextSearch ? UtilsText.GetTextExtract(this, node) : UtilsText.GetSemanticExtract(this, node);
    }
}
