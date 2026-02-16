using Microsoft.CodeAnalysis;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.Query.Data;
internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource, IIncludeBranches {
    List<IncludeBranch>? _includeBranches;
    public void IncludeBranch(IncludeBranch relationPropertyIdBranch) {
        if (_includeBranches == null) _includeBranches = new();
        _includeBranches.Add(relationPropertyIdBranch);
    }
    // the purpose of this method is to both make lazy loading of node data as late as possible
    // for instance if the expression is followed by a filter, the node datas does not need to be loaded
    // if all the properties are indexed
    // but before it leved the read lock of the database it must be ensured that the node data is loaded
    // in case it is the last expression in the query
    // nodes may be added, deleted or changed between evaluateing the query and reading the nodes otherwise
    // the easy way out would be to load the node data in the constructor, but that would be a waste of resources
    public void EnsureRetrivalOfRelationNodesDataBeforeExitingReadLock(Metrics metrics) {
        if (_nodes == null) {
            if (_includeBranches != null) _includeBranches = IncludeUtil.JoinPathsToUniqueBranches(_includeBranches);
            _nodes = IncludeUtil.GetNodesWithIncludes(metrics, _ids, _db, _includeBranches, _ctx);
        }
    }
}

