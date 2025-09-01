using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.Query.Data;
namespace Relatude.DB.Query.Expressions {
    public enum RelQuestion {
        Relates,
    }
    public class RelationExpression : IExpression {
        public readonly VariableReferenceExpression SourceObject;
        public readonly string[] PropertyPath;
        Guid? _to;
        string _toArgument;
        public readonly RelQuestion Method;
        public RelationExpression(string propertyPath, string argument, string method) {
            var parts = propertyPath.Split('.');
            SourceObject = new VariableReferenceExpression(parts[0]);
            PropertyPath = parts.Skip(1).ToArray();
            _toArgument = argument;
            Method = method switch {
                "is" => RelQuestion.Relates,
                "has" => RelQuestion.Relates,
                _ => throw new NotSupportedException(),
            };
        }
        public Guid GetTo(IDataStore db) {
            if (_to.HasValue) return _to.Value;
            if (int.TryParse(_toArgument, out var id)) {
                if (db.TryGetGuid(id, out var to)) {
                    _to = to;
                } else {
                    _to = Guid.Empty;
                }
            } else if (Guid.TryParse(_toArgument, out var guid)) {
                _to = guid;
            } else {
                throw new Exception("Expected a Guid or ID.");
            }
            return _to.Value;
        }
        public Tuple<bool[], Guid[]> GetRelationInfo(Guid nodeTypeId, Datamodel dm) {
            var nodeType = dm.NodeTypes[nodeTypeId];
            var directions = new bool[PropertyPath.Length];
            var relations = new Guid[directions.Length];
            for (var i = 0; i < PropertyPath.Length; i++) {
                var codeName = PropertyPath[i];
                var property = nodeType.AllPropertiesByName[codeName];
                if (property is not RelationPropertyModel relProp) throw new NotSupportedException();
                directions[i] = relProp.FromTargetToSource;
                relations[i] = relProp.RelationId;
                nodeType = dm.NodeTypes[relProp.NodeTypeOfRelated];
            }
            return Tuple.Create(directions, relations);
        }
        public object Evaluate(IVariables vars) {
            var r = SourceObject.Evaluate(vars);
            if (r is not IStoreNodeData node) throw new Exception("RelationExpression must be evaluated on a node object.");
            var nodeTypeId = node.NodeData.NodeType;
            (var directions, var relations) = GetRelationInfo(nodeTypeId, node.Store.Datamodel);
            var nodeId = node.NodeData.Id;
            return Method switch {
                RelQuestion.Relates => contains(0, nodeId, node.Store, directions, relations),
                _ => throw new NotSupportedException(),
            };
        }
        bool contains(int level, Guid from, IDataStore db, bool[] dirs, Guid[] rels) {
            if (dirs.Length - level == 1) // last level
                return db.ContainsRelation(rels[level], from, GetTo(db), dirs[level]);
            foreach (var target in db.GetRelatedNodeIdsFromRelationId(rels[level], from, dirs[level])) { // recursive
                if (contains(level + 1, target, db, dirs, rels)) return true;
            }
            return false;
        }
        public override string ToString() => SourceObject + "." + string.Join('.', PropertyPath) + Method + "(" + _toArgument + ")";
    }
}
