using System.Text;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Transactions;


public static class UtilsText {
    const int _callCountLimit = 1000; // max number of total contents per indexing
    const int _recursiveLevelLimit = 3; // max number recursive levels for relation properties

    // the node is one holding values: a node without revisions or one revision, never a revision container
    public static string GetTextExtract(DataStoreLocal db, INodeData node) {
        return getTextExtract(db, db.Datamodel.NodeTypes[node.NodeType], node, db.Datamodel, cultureOf(node), _recursiveLevelLimit, _callCountLimit);
    }
    public static string GetSemanticExtract(DataStoreLocal db, INodeData node) {
        return getSemanticExtract(db, db.Datamodel.NodeTypes[node.NodeType], node, db.Datamodel, cultureOf(node), _recursiveLevelLimit, _callCountLimit);
    }
    static Guid cultureOf(INodeData node) => node.Meta?.CultureId ?? Guid.Empty;
    /// <summary>
    /// What a related node is indexed by: the node, or, for one with revisions, whose container has no values, the
    /// published revision a reader in the culture of the node indexed reads: the culture's own, else the first in
    /// fallback order (the neutral one first). Null when none is published.
    /// </summary>
    static INodeData? valuesOf(DataStoreLocal db, INodeData related, Guid cultureId) {
        if (related is not NodeDataRevisions revisions) return related;
        var published = revisions.Revisions.Where(r => r.RevisionType == RevisionType.Published).ToArray();
        return published.FirstOrDefault(r => r.CultureId == cultureId)
            ?? published.MinBy(r => db._nativeModelStore.GetCulturePriority(r.CultureId));
    }
    static string getTextExtract(DataStoreLocal db, NodeTypeModel nodeType, INodeData node, Datamodel dm, Guid cultureId, int recursiveLevelLimit, int callCountLimit) {
        StringBuilder sb = new();
        foreach (var prop in nodeType.TextIndexProperties) {
            if (prop is not RelationPropertyModel rm) {
                if (node.TryGetValue(prop.Id, out var value)) {
                    sb.AppendLine(prop.GetTextIndex(value));
                    // temporary method for boosting a field (the boost is the node type's: it may override the property's):
                    var boost = nodeType.GetIndexBoost(prop);
                    for (int i = 0; i < boost; i++) sb.AppendLine(prop.GetTextIndex(value));
                }
            } else {
                if (rm.TextIndexRelatedContent) {
                    var levelNextCall = recursiveLevelLimit;
                    if (levelNextCall == int.MaxValue) levelNextCall = rm.TextIndexRecursiveLevelLimit;
                    if (levelNextCall < 0) continue;
                    var relatedIds = db._definition.Relations[rm.RelationId].GetRelated(node.__Id, rm.FromTargetToSource).ToArray();
                    var relatedNodes = db._nodes.Get(relatedIds);
                    foreach (var relatedNode in relatedNodes) {
                        if (valuesOf(db, relatedNode, cultureId) is not { } related) continue; // nothing published
                        var relatedNodeType = dm.NodeTypes[related.NodeType]; // use the related node's own type, not the parent's
                        sb.AppendLine(getTextExtract(db, relatedNodeType, related, dm, cultureId, recursiveLevelLimit - 1, --callCountLimit));
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
                        if (valuesOf(db, relatedNode, cultureId) is not { } related) continue;
                        dm.NodeTypes[related.NodeType].BuildDisplayName(related, sb); // use the related node's own type, not the parent's
                        sb.AppendLine();
                    }
                }
            }
        }
        return sb.ToString();
    }
    static string getSemanticExtract(DataStoreLocal db, NodeTypeModel nodeType, INodeData node, Datamodel dm, Guid cultureId, int recursiveLevelLimit, int callCountLimit) {
        StringBuilder sb = new();
        foreach (var prop in nodeType.TextIndexProperties) {
            if (prop is not RelationPropertyModel rm) {
                if (node.TryGetValue(prop.Id, out var value)) {
                    var str = prop.GetSemanticIndex(value);
                    if (string.IsNullOrEmpty(str)) continue;
                    // labelled by name: DisplayName is the flag marking the display name property
                    sb.Append(prop.CodeName + ": ");
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
                    sb.AppendLine(rm.CodeName + ":");
                    foreach (var relatedNode in relatedNodes) {
                        if (valuesOf(db, relatedNode, cultureId) is not { } related) continue;
                        var relatedNodeType = dm.NodeTypes[related.NodeType]; // use the related node's own type, not the parent's
                        sb.AppendLine(getSemanticExtract(db, relatedNodeType, related, dm, cultureId, recursiveLevelLimit - 1, --callCountLimit));
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
                    sb.AppendLine(rm.CodeName + ":");
                    foreach (var relatedNode in relatedNodes) {
                        if (valuesOf(db, relatedNode, cultureId) is not { } related) continue;
                        dm.NodeTypes[related.NodeType].BuildDisplayName(related, sb); // use the related node's own type, not the parent's
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString();
    }
}
