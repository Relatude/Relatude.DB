using System.Text;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Transactions;


internal static class UtilsText {
    const int _callCountLimit = 1000; // max number of total contents per indexing
    const int _recursiveLevelLimit = 3; // max number recursive levels for relation properties

    public static string GetTextExtract(DataStoreLocal db, INodeData node) {
        return getTextExtract(db, db.Datamodel.NodeTypes[node.NodeType], node, db.Datamodel, _recursiveLevelLimit, _callCountLimit);
    }
    public static string GetSemanticExtract(DataStoreLocal db, INodeData node) {
        return getSemanticExtract(db, db.Datamodel.NodeTypes[node.NodeType], node, db.Datamodel, _recursiveLevelLimit, _callCountLimit);
    }
    static string getTextExtract(DataStoreLocal db, NodeTypeModel nodeType, INodeData node, Datamodel dm, int recursiveLevelLimit, int callCountLimit) {
        StringBuilder sb = new();
        foreach (var prop in nodeType.TextIndexProperties) {
            if (prop is not RelationPropertyModel rm) {
                if (node.TryGetValue(prop.Id, out var value)) {
                    sb.AppendLine(prop.GetTextIndex(value));
                    for (int i = 0; i < prop.IndexBoost; i++) sb.AppendLine(prop.GetTextIndex(value)); // temporary method for boosting a field
                }
            } else {
                if (rm.TextIndexRelatedContent) {
                    var levelNextCall = recursiveLevelLimit;
                    if (levelNextCall == int.MaxValue) levelNextCall = rm.TextIndexRecursiveLevelLimit;
                    if (levelNextCall < 0) continue;
                    var relatedIds = db._definition.Relations[rm.RelationId].GetRelated(node.__Id, rm.FromTargetToSource).ToArray();
                    var relatedNodes = db._nodes.Get(relatedIds);
                    foreach (var relatedNode in relatedNodes) {
                        sb.AppendLine(getTextExtract(db, nodeType, relatedNode, dm, recursiveLevelLimit - 1, --callCountLimit));
                        if (callCountLimit <= 0) {
                            sb.AppendLine("...");
                            break;
                        }
                    }
                    if (callCountLimit <= 0) break;
                } else if (rm.TextIndexRelatedDisplayName) {
                    var relatedIds = db._definition.Relations[rm.RelationId].GetRelated(node.__Id, rm.FromTargetToSource).ToArray();
                    var relatedNodes = db._nodes.Get(relatedIds);
                    foreach (var relatedNode in relatedNodes) {
                        nodeType.BuildDisplayName(relatedNode, sb);
                        sb.AppendLine();
                    }
                }
            }
        }
        return sb.ToString();
    }
    static string getSemanticExtract(DataStoreLocal db, NodeTypeModel nodeType, INodeData node, Datamodel dm, int recursiveLevelLimit, int callCountLimit) {
        StringBuilder sb = new();
        foreach (var prop in nodeType.TextIndexProperties) {
            if (prop is not RelationPropertyModel rm) {
                if (node.TryGetValue(prop.Id, out var value)) {
                    var str = prop.GetSemanticIndex(value);
                    if (string.IsNullOrEmpty(str)) continue;
                    sb.Append(prop.DisplayName + ": ");
                    sb.AppendLine(str);
                    sb.AppendLine();
                }
            } else {
                if (rm.TextIndexRelatedContent) {
                    var levelNextCall = recursiveLevelLimit;
                    if (levelNextCall == int.MaxValue) levelNextCall = rm.TextIndexRecursiveLevelLimit;
                    if (levelNextCall < 0) continue;
                    var relatedIds = db._definition.Relations[rm.RelationId].GetRelated(node.__Id, rm.FromTargetToSource).ToArray();
                    var relatedNodes = db._nodes.Get(relatedIds);
                    if (relatedNodes.Length == 0) continue;
                    sb.AppendLine(rm.DisplayName + ":");
                    foreach (var relatedNode in relatedNodes) {
                        sb.AppendLine(getSemanticExtract(db, nodeType, relatedNode, dm, recursiveLevelLimit - 1, --callCountLimit));
                        if (callCountLimit <= 0) {
                            sb.AppendLine("...");
                            break;
                        }
                    }
                    sb.AppendLine();
                    if (callCountLimit <= 0) break;
                } else if (rm.TextIndexRelatedDisplayName) {
                    var relatedIds = db._definition.Relations[rm.RelationId].GetRelated(node.__Id, rm.FromTargetToSource).ToArray();
                    var relatedNodes = db._nodes.Get(relatedIds);
                    if (relatedNodes.Length == 0) continue;
                    sb.AppendLine(rm.DisplayName + ":");
                    foreach (var relatedNode in relatedNodes) {
                        nodeType.BuildDisplayName(relatedNode, sb);
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString();
    }
}
