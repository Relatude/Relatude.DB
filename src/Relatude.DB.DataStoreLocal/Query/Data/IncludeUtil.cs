using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.Query.Data;

/// <summary>
/// A query result whose node data is read after the store's read lock is let go. The query evaluates and
/// begins the retrieval under the lock, lets it go, and then completes it - so a result that has to read a
/// lot of nodes from the log does not keep writers, and every reader queued behind them, waiting for the disk.
/// </summary>
internal interface IDeferredNodeRetrieval {
    /// <summary>Under the read lock: everything but reading the node bytes. Null when there is nothing left to read.</summary>
    PendingRetrieval? BeginRetrievalUnderReadLock(Metrics metrics);
}
/// <summary>A retrieval begun under the read lock. Complete it after the lock is let go; dispose it in any case.</summary>
internal sealed class PendingRetrieval(PendingNodeData nodes, Action<INodeDataExternal[]> onRead) : IDisposable {
    public void Complete(CancellationToken cancel) => onRead(nodes.Complete(cancel));
    public void Dispose() => nodes.Dispose();
}
/// <summary>
/// Node data begun under the store's read lock (see <see cref="IncludeUtil.BeginNodesWithIncludes"/>):
/// the read of the nodes and, with includes, the relation tree they are put into.
/// </summary>
internal sealed class PendingNodeData(DataStores.Stores.NodeStore.NodeRead read, OuterConverter outer, NodeDataWithRelations[]? tree, Metrics metrics) : IDisposable {
    /// <summary>Reads what the cache did not hold. Takes no store lock, and must not.</summary>
    public INodeDataExternal[] Complete(CancellationToken cancel) {
        INodeDataInternal?[] inner;
        try {
            inner = read.Complete(cancel, ref metrics.DiskReads, ref metrics.NodesReadFromDisk);
        } finally {
            read.Dispose();
        }
        var nOuter = new INodeDataExternal[inner.Length];
        for (var i = 0; i < inner.Length; i++) nOuter[i] = outer.ToOuter(inner[i]!); // read with throwIfMissing, so none is null
        if (tree == null) return nOuter;
        var dic = nOuter.ToDictionary(n => n.__Id); // get all nodes in one go
        metrics.UniqueNodeCount += dic.Count;
        foreach (var node in tree) node.SwapNodeData(dic); // recursive for entire relation tree
        return tree;
    }
    public void Dispose() => read.Dispose();
}

internal static class IncludeUtil {
    // per query execution state of one include expansion: the store, context and
    // lazily created filter evaluators (one per filtered branch)
    sealed class IncludeWalk {
        public required DataStoreLocal Db;
        public required QueryContext Ctx;
        public required Metrics Metrics;
        Dictionary<IncludeBranch, IncludeFilterEvaluator?>? _evaluators;
        Dictionary<Guid, IdSet>? _visibleByType;
        public IncludeFilterEvaluator? GetEvaluator(IncludeBranch branch) {
            if (branch.Filter == null) return null;
            _evaluators ??= [];
            if (!_evaluators.TryGetValue(branch, out var evaluator)) {
                evaluator = IncludeFilterEvaluator.Create(branch, Db, Ctx, Metrics);
                _evaluators.Add(branch, evaluator);
            }
            return evaluator;
        }
        /// <summary>
        /// True when the context may see this node. Preloaded nodes do not come from a type set, so
        /// unlike the rest of the query pipeline they are not filtered by read access, deleted flag,
        /// release/expire window or culture unless it is done here. The set is looked up by the node's
        /// own type (the declared related type of the property is not always the real one), and is
        /// itself cached per type and context by the meta index.
        /// </summary>
        public bool IsVisible(int nodeId, Guid nodeTypeId) {
            _visibleByType ??= [];
            if (!_visibleByType.TryGetValue(nodeTypeId, out var visible)) {
                visible = Db._definition.GetAllIdsForType(nodeTypeId, Ctx);
                _visibleByType.Add(nodeTypeId, visible);
            }
            return visible.Has(nodeId);
        }
    }
    /// <summary>The node data of a result, with its includes, read while the caller holds the store's read lock.</summary>
    public static INodeDataExternal[] GetNodesWithIncludes(Metrics metrics, IdSet _ids, DataStoreLocal _db, List<IncludeBranch>? _includeBranches, QueryContext ctx) {
        using var pending = BeginNodesWithIncludes(metrics, _ids, _db, _includeBranches, ctx);
        return pending.Complete(ctx.CancellationToken);
    }
    /// <summary>
    /// The first step of reading the node data of a result, taken under the store's read lock: the include
    /// tree is walked, the nodes it needs are looked up (cached ones taken, the positions of the rest noted)
    /// and the context resolved. <see cref="PendingNodeData.Complete"/> reads the rest after the lock is let
    /// go, and must not take it again.
    /// </summary>
    public static PendingNodeData BeginNodesWithIncludes(Metrics metrics, IdSet _ids, DataStoreLocal _db, List<IncludeBranch>? _includeBranches, QueryContext ctx) {
        metrics.NodeCount += _ids.Count;
        var outer = _db.GetOuterConverter(ctx);
        if (_includeBranches == null) {
            metrics.UniqueNodeCount += _ids.Count;
            return new PendingNodeData(_db._nodes.BeginRead(_ids.Enumerate().ToArray(), throwIfMissing: true), outer, null, metrics);
        } else {
            var idsToGet = new HashSet<int>(_ids.Enumerate());
            var nodes = new NodeDataWithRelations[_ids.Count];
            var i = 0;
            foreach (var id in _ids.Enumerate()) {
                nodes[i++] = new(new NodeDataOnlyTypeAndId(id, _db._definition.GetTypeOfNode(id)));
            }
            var walk = new IncludeWalk { Db = _db, Ctx = ctx, Metrics = metrics };
            foreach (var branch in _includeBranches) branch.Reset();
            var cancel = ctx.CancellationToken;
            for (var n = 0; n < nodes.Length; n++) {
                if ((n & 255) == 0) cancel.ThrowIfCancellationRequested(); // the walk reads the values it follows
                ensureIncludes(nodes[n], _includeBranches, idsToGet, 0, walk, ref metrics.NodeCount);
            }
            return new PendingNodeData(_db._nodes.BeginRead(idsToGet.ToArray(), throwIfMissing: true), outer, nodes, metrics);
        }
    }
    public static List<IncludeBranch>? JoinPathsToUniqueBranches(List<IncludeBranch>? branches) {
        if (branches == null) return null;
        var root = new IncludeBranch(Guid.Empty, null);
        foreach (var branch in branches) {
            root.AddBranch(branch);
        }
        return root.Children.ToList();
    }
    static void ensureIncludes(NodeDataWithRelations from, IEnumerable<IncludeBranch> branches, HashSet<int> idsToGet, int level, IncludeWalk walk, ref int _totalNodeCount) {
        foreach (var branch in branches) {

            // TODO: NONE WORKING OPTIMZATION, LOOK INTO LATER
            //if (branch.EvaluatedIds.Contains(from.__Id)) continue; // already evaluated this node branch
            //branch.EvaluatedIds.Add(from.__Id); // mark this node branch as evaluated


            ensureIncludes(from, branch, idsToGet, level, walk, ref _totalNodeCount);
        }
    }
    static void ensureIncludes(NodeDataWithRelations from, IncludeBranch branch, HashSet<int> idsToGet, int level, IncludeWalk walk, ref int _totalNodeCount) {
        var propId = branch.PropertyId;
        var _db = walk.Db;
        var _def = _db._definition;
        var typeDef = _def.Datamodel.NodeTypes[from.NodeType];
        if (!typeDef.AllProperties.ContainsKey(propId)) return; // not relevant for this node type
        if (from.Relations.ContainsRelation(propId)) return; // already included
        int? top = branch.Top;
        var filter = walk.GetEvaluator(branch); // null when the branch has no filter
        var propDef = _def.Datamodel.Properties[propId];
        if (propDef is ReferencePropertyModel refProp) {
            if (_db.TryGetValue<Guid>(new PropertyPath(from.__Id, propId), out var guid)) {
                if (guid != Guid.Empty && _db.Exists(guid)) {
                    // has value, is set, and exists in the database:
                    if (!_db._guids.TryGetId(guid, out var id)) return;
                    var typeId = _db._definition.GetTypeOfNode(id);
                    if (!walk.IsVisible(id, typeId)) return; // not readable in this context: same result as an unset reference
                    if (filter != null && !filter.Keep(id)) return; // filtered out: same result as an unset reference
                    idsToGet.Add(id);
                    var idNodePlaceholder = new NodeDataOnlyTypeAndId(id, typeId);
                    var to = new NodeDataWithRelations(idNodePlaceholder);
                    from.Relations.AddReference(propId, to);
                    if (branch.HasChildren()) ensureIncludes(to, branch.Children, idsToGet, level + 1, walk, ref _totalNodeCount);
                }
            }
        } else if (propDef is ReferencesPropertyModel refsProp) {
            if (_db.TryGetValue<Guid[]>(new PropertyPath(from.__Id, propId), out var guids)) {
                List<NodeDataWithRelations> tos = new(guids.Length);
                foreach (var guid in guids) { // preserve stored order
                    if (top.HasValue && tos.Count >= top.Value) break;
                    if (guid == Guid.Empty || !_db.Exists(guid)) continue; // unset or stale entry (target deleted): skip, not truncate
                    if (!_db._guids.TryGetId(guid, out var id)) continue;
                    var typeId = _db._definition.GetTypeOfNode(id);
                    if (!walk.IsVisible(id, typeId)) continue; // not readable in this context, before filter and top
                    if (filter != null && !filter.Keep(id)) continue; // filter before top
                    idsToGet.Add(id);
                    tos.Add(new(new NodeDataOnlyTypeAndId(id, typeId)));
                }
                _totalNodeCount += tos.Count;
                from.Relations.AddReferences(propId, tos.ToArray());
                if (branch.HasChildren()) foreach (var to in tos) ensureIncludes(to, branch.Children, idsToGet, level + 1, walk, ref _totalNodeCount);
            }
        } else if (propDef is RelationPropertyModel relProp) {
            var relation = _def.Relations[relProp.RelationId];
            var idsRel = relation.GetRelated(from.__Id, relProp.FromTargetToSource);
            // context visibility and the branch filter are both applied before top, so a related node
            // the reader may not see does not use up one of the requested slots:
            List<NodeDataWithRelations> kept = new(top.HasValue && idsRel.Count > top.Value ? top.Value : idsRel.Count);
            foreach (var id in idsRel.Enumerate()) {
                if (top.HasValue && kept.Count >= top.Value) break;
                var typeId = _db._definition.GetTypeOfNode(id);
                if (!walk.IsVisible(id, typeId)) continue;
                if (filter != null && !filter.Keep(id)) continue;
                idsToGet.Add(id);
                kept.Add(new(new NodeDataOnlyTypeAndId(id, typeId)));
            }
            _totalNodeCount += kept.Count;
            var tos = kept.ToArray();
            if (relProp.IsMany) {
                from.Relations.AddManyRelation(propId, tos);
            } else {
                if (tos.Length == 1) {
                    from.Relations.AddOneRelation(propId, tos[0]);
                } else if (tos.Length == 0) {
                    from.Relations.SetNoRelation(propId); // no relation, or the single related node was filtered out
                } else if (tos.Length > 1) {
                    throw new Exception("Multiple relations on property " + relProp.CodeName + " for node " + from.__Id + " is not allowed.");
                }
            }
            if (branch.HasChildren()) foreach (var to in tos) ensureIncludes(to, branch.Children, idsToGet, level + 1, walk, ref _totalNodeCount);
        } else {
            throw new Exception("Property " + propDef.CodeName + " is not a reference or relation property, cannot preload.");
        }
    }
}
