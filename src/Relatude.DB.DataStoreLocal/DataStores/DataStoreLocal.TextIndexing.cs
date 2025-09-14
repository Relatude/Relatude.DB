using Relatude.DB.DataStores.Transactions;
namespace Relatude.DB.DataStores;
public sealed partial class DataStoreLocal : IDataStore {
    public (int NodeId, string Text)[] GetTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            var matchingIds = ids.Where(_nodes.Contains).ToArray();
            var nodes = _nodes.Get(matchingIds);
            Interlocked.Add(ref _noNodeGetsSinceClearCache, nodes.Length);
            return nodes.Select(n => (NodeId: n.__Id, Text: UtilsText.GetTextExtract(this, n))).ToArray();
        } finally {
            _lock.ExitReadLock();
        }
    }
    public (int NodeId, string Text)[] GetSemanticTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            var matchingIds = ids.Where(_nodes.Contains).ToArray();
            var nodes = _nodes.Get(matchingIds);
            Interlocked.Add(ref _noNodeGetsSinceClearCache, nodes.Length);
            return nodes.Select(n => (NodeId: n.__Id, Text: UtilsText.GetSemanticExtract(this, n))).ToArray();
        } finally {
            _lock.ExitReadLock();
        }
    }
}
