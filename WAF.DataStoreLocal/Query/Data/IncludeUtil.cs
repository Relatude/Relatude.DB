using WAF.Datamodels;
using WAF.Datamodels.Properties;
using WAF.DataStores;
using WAF.DataStores.Sets;

namespace WAF.Query.Data;
internal static class IncludeUtil {
    public static INodeData[] GetNodesWithIncludes(Metrics metrics, IdSet _ids, DataStoreLocal _db, List<IncludeBranch>? _includeBranches) {
        metrics.NodeCount += _ids.Count;
        if (_includeBranches == null) {
            metrics.UniqueNodeCount += _ids.Count;
            return _db._nodes.Get(_ids.Enumerate().ToArray(), ref metrics.DiskReads, ref metrics.NodesReadFromDisk);
        } else {
            var idsToGet = new HashSet<int>(_ids.Enumerate());
            var nodes = new NodeDataWithRelations[_ids.Count];
            var i = 0;
            foreach (var id in _ids.Enumerate()) {
                nodes[i++] = new(new NodeDataOnlyTypeAndUId(id, _db._definition.GetTypeOfNode(id)));
            }
            foreach (var branch in _includeBranches) branch.Reset();
            foreach (var node in nodes) ensureIncludes(node, _includeBranches, idsToGet, 0, _db, ref metrics.NodeCount);
            var dic = _db._nodes.Get(idsToGet.ToArray(), ref metrics.DiskReads, ref metrics.NodesReadFromDisk).ToDictionary(n => n.__Id); // get all nodes in one go
            metrics.UniqueNodeCount += dic.Count;
            foreach (var node in nodes) node.SwapNodeData(dic); // recursive for entire relation tree
            return nodes;
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
    static void ensureIncludes(NodeDataWithRelations from, IEnumerable<IncludeBranch> branches, HashSet<int> idsToGet, int level, DataStoreLocal _db, ref int _totalNodeCount) {
        foreach (var branch in branches) {

            // TODO: NONE WORKING OPTIMZATION, LOOK INTO LATER
            //if (branch.EvaluatedIds.Contains(from.__Id)) continue; // already evaluated this node branch  
            //branch.EvaluatedIds.Add(from.__Id); // mark this node branch as evaluated


            ensureIncludes(from, branch, idsToGet, level, _db, ref _totalNodeCount);
        }
    }
    static void ensureIncludes(NodeDataWithRelations from, IncludeBranch branch, HashSet<int> idsToGet, int level, DataStoreLocal _db, ref int _totalNodeCount) {
        var propId = branch.PropertyId;
        var _def = _db._definition;
        var typeDef = _def.Datamodel.NodeTypes[from.NodeType];
        if (!typeDef.AllProperties.ContainsKey(propId)) return; // not relevant for this node type
        if (from.Relations.ContainsRelation(propId)) return; // already included
        int? top = branch.Top;
        var relProp = (RelationPropertyModel)_def.Datamodel.Properties[propId];
        var relation = _def.Relations[relProp.RelationId];
        var idsRel = relation.GetRelated(from.__Id, relProp.FromTargetToSource);
        _totalNodeCount += idsRel.Count;
        var ids = idsRel.Enumerate();
        int count; // faster count, avoiding Count() on the enumerable
        if (top.HasValue && idsRel.Count > top) {
            ids = ids.Take(top.Value);
            count = top.Value;
        } else {
            count = idsRel.Count;
        }
        var tos = new NodeDataWithRelations[count];
        var i = 0;
        foreach (var id in ids) {
            idsToGet.Add(id);
            tos[i++] = new(new NodeDataOnlyTypeAndUId(id, _db._definition.GetTypeOfNode(id)));
        }
        if (relProp.IsMany) {
            from.Relations.AddManyRelation(propId, tos);
        } else {
            if (tos.Length == 1) {
                from.Relations.AddOneRelation(propId, tos[0]);
            } else if (tos.Length == 0) {
                from.Relations.SetNoRelation(propId); // no relation
            } else if (tos.Length > 1) {
                throw new Exception("Multiple relations on property " + relProp.CodeName + " for node " + from.__Id + " is not allowed.");
            }
        }
        if (branch.HasChildren()) foreach (var to in tos) ensureIncludes(to, branch.Children, idsToGet, level + 1, _db, ref _totalNodeCount);
    }
}
