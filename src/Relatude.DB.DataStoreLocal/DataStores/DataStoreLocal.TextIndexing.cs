using Relatude.DB.Common;
using Relatude.DB.DataStores.Transactions;
namespace Relatude.DB.DataStores;
public sealed partial class DataStoreLocal : IDataStore {
    public TextExtractInfo[] GetTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids) {
        throw new NotImplementedException();
        //_lock.EnterReadLock();
        //try {
        //    validateDatabaseState();
        //    var matchingIds = ids.Where(_nodes.Contains).ToArray();
        //    var nodes = _nodes.Get(matchingIds);
        //    Interlocked.Add(ref _noNodeGetsSinceClearCache, nodes.Length);
        //    return nodes.Select(n => (NodeId: n.__Id, Text: UtilsText.GetTextExtract(this, n))).ToArray();
        //} finally {
        //    _lock.ExitReadLock();
        //}
    }
    public TextExtractInfo[] GetSemanticTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids) {
        throw new NotImplementedException();
        //_lock.EnterReadLock();
        //try {
        //    validateDatabaseState();
        //    var matchingIds = ids.Where(_nodes.Contains).ToArray();
        //    var nodes = _nodes.Get(matchingIds);
        //    Interlocked.Add(ref _noNodeGetsSinceClearCache, nodes.Length);
        //    return nodes.Select(n => (NodeId: n.__Id, Text: UtilsText.GetSemanticExtract(this, n))).ToArray();
        //} finally {
        //    _lock.ExitReadLock();
        //}
    }
}
