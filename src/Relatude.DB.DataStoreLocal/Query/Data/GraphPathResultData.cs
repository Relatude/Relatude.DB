using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.Query.Data;
/// <summary>
/// Result of a ShortestPath query: the node ids of one shortest path in order, from -> to inclusive.
/// Node data is hydrated in one batch before the store read lock is released.
/// </summary>
internal class GraphPathResultData : IGraphPathResultData {
    readonly DataStoreLocal _db;
    readonly QueryContext _ctx;
    readonly int[] _pathIds; // empty when no path was found
    List<IncludeBranch>? _includeBranches;
    List<INodeDataExternal>? _nodes;
    List<Guid>? _nodeGuids;
    public GraphPathResultData(DataStoreLocal db, QueryContext ctx, int[] pathIds) {
        _db = db;
        _ctx = ctx;
        _pathIds = pathIds;
    }
    public bool Found => _pathIds.Length > 0;
    public int Length => _pathIds.Length > 0 ? _pathIds.Length - 1 : 0;
    public int Count => _pathIds.Length;
    public int TotalCount => _pathIds.Length;
    public double DurationMs { get; set; }
    public List<Guid> NodeIds {
        get {
            if (_nodeGuids == null) {
                _nodeGuids = new List<Guid>(_pathIds.Length);
                foreach (var id in _pathIds) _nodeGuids.Add(_db._guids.GetGuid(id));
            }
            return _nodeGuids;
        }
    }
    public List<INodeDataExternal> Nodes {
        get {
            if (_nodes == null) throw new Exception("Path nodes are not materialized. ");
            return _nodes;
        }
    }
    public void IncludeBranch(IncludeBranch relationPropertyIdBranch) {
        if (_includeBranches == null) _includeBranches = new();
        _includeBranches.Add(relationPropertyIdBranch);
    }
    public void EnsureRetrivalOfRelationNodesDataBeforeExitingReadLock(Metrics metrics) {
        if (_nodes != null) return;
        _ = NodeIds; // resolve guids while inside the read lock
        if (_includeBranches != null) _includeBranches = IncludeUtil.JoinPathsToUniqueBranches(_includeBranches);
        var ids = IdSet.UncachableSet(new FixedOrderedSet(_pathIds, _pathIds.Length)); // preserve path order
        var nodes = IncludeUtil.GetNodesWithIncludes(metrics, ids, _db, _includeBranches, _ctx);
        _nodes = [.. nodes];
    }
}
