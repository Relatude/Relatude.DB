using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Indexes.Trie.TrieNet._Ukkonen;
using Relatude.DB.Tasks;
using Relatude.DB.Tasks.TextIndexing;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores.Transactions;
internal static class Utils {
    public static void ForceTypeValidateValuesAndCopyMissing(Definition definition, INodeData node, INodeData? oldNode, bool transformValues) {
        // should optimize this method, it is called for every node
        // 1 ready all relevant properties for the node type ( and exclude relations and text index)
        // 2 avoid looking up property for each value, use the property id as key
        // 3 do a better diff, combine both "forceValue" and "add if missing" in one loop
        if (!definition.NodeTypes.TryGetValue(node.NodeType, out var nodeType)) {
            throw new("Node with id " + node.Id + " is of unknown type: " + node.NodeType + ". ");
        }
        var allProps = nodeType.AllProperties;

        // forcing type and validating value on given properties in node
        foreach (var kv in node.Values) {
            if (allProps.TryGetValue(kv.PropertyId, out var prop)) {
                var value = prop.ForceValueType(kv.Value, out var changed);
                if (transformValues) value = prop.TransformFromOuterToInnerValue(value, oldNode);
                prop.ValidateValue(value);
                if (changed) node.AddOrUpdate(kv.PropertyId, value);
            } else {
                // just ignore properties that does not belong to class
            }
        }
        // copying missing properties from old node
        if (oldNode != null) { // copy missing properties from old node, only props in model and not relations or text index
            foreach (var prop in allProps.Values) {
                if (prop.PropertyType != PropertyType.Relation && prop.Id != NodeConstants.SystemTextIndexPropertyId && prop.Id != NodeConstants.SystemVectorIndexPropertyId) {
                    if (!node.Contains(prop.Id)) {
                        if (oldNode.TryGetValue(prop.Id, out var oldValue)) {
                            node.Add(prop.Id, oldValue);
                        } else {
                            node.Add(prop.Id, prop.GetDefaultValue());
                        }
                    }
                }
            }
        } else {
            // ignore values for missing properties
        }
    }

    static void evalIndexing(DataStoreLocal db, Guid[]? omit, Guid nodeTypeId, out bool textIndex, out bool vectorIndex, out bool instantTextIndexing) {
        var isUpdateOfAnyIndexProp = omit != null && (omit.Contains(NodeConstants.SystemTextIndexPropertyId) ||
            omit.Contains(NodeConstants.SystemVectorIndexPropertyId));
        if (isUpdateOfAnyIndexProp) {
            instantTextIndexing = false;
            textIndex = false;
            vectorIndex = false;
            return; // no indexing needed, as we are updating the index properties
        }
        var nodeType = db._definition.Datamodel.NodeTypes[nodeTypeId];
        textIndex = nodeType.TextIndex!.Value;
        vectorIndex = db._ai != null && nodeType.SemanticIndex!.Value;
        instantTextIndexing = textIndex && nodeType.InstantTextIndexing!.Value && !vectorIndex; // allow instant text indexing only if no vector indexing is enabled
    }
    public static void EnsureOrQueueIndex(DataStoreLocal db, INodeData node, Guid[]? omit, List<KeyValuePair<TaskData, string?>> newTasks) {
        evalIndexing(db, omit, node.NodeType, out var textIndex, out var vectorIndex, out var instantTextIndexing);
        if (!textIndex && !vectorIndex) return; // no indexing needed
        if (instantTextIndexing) {
            var text = UtilsText.GetTextExtract(db, node);
            node.AddOrUpdate(NodeConstants.SystemTextIndexPropertyId, text);
        } else {
            newTasks.Add(new(new IndexTask(node.__Id, textIndex, vectorIndex), null));
        }
    }
    public static void QueueIndexing(DataStoreLocal db, int nodeId, Guid nodeTypeId, Guid[]? omit, List<KeyValuePair<TaskData, string?>> newTasks) {
        evalIndexing(db, omit, nodeTypeId, out var textIndex, out var vectorIndex, out var instantTextIndexing);
        if (!textIndex && !vectorIndex) return; // no indexing needed
        newTasks.Add(new(new IndexTask(nodeId, textIndex, vectorIndex), null));
    }
    public static bool AreDifferentIgnoringGeneratedProps(INodeData node1, INodeData node2, Definition dm) {
        if (node1.Id != node2.Id || node1.__Id != node2.__Id) return true;
        //if (node1.CreatedUtc != node2.CreatedUtc) return true;
        //if (node1.ChangedUtc != node2.ChangedUtc) return true;
        if (node1.NodeType != node2.NodeType) return true;
        //var count1 = node1.Contains(NodeConstants.SystemTextIndexPropertyId) ? node1.ValueCount - 1 : node1.ValueCount;
        //var count2 = node2.Contains(NodeConstants.SystemTextIndexPropertyId) ? node2.ValueCount - 1 : node2.ValueCount;
        var count1 = node1.ValueCount;
        if (node1.Contains(NodeConstants.SystemTextIndexPropertyId)) count1--;
        if (node1.Contains(NodeConstants.SystemVectorIndexPropertyId)) count1--;
        var count2 = node2.ValueCount;
        if (node2.Contains(NodeConstants.SystemTextIndexPropertyId)) count2--;
        if (node2.Contains(NodeConstants.SystemVectorIndexPropertyId)) count2--;
        if (count1 != count2) return true; // different number of properties, so different node
        foreach (var kv in node1.Values) {
            if (kv.PropertyId == NodeConstants.SystemTextIndexPropertyId) continue; // ignore text index, it is always different
            if (kv.PropertyId == NodeConstants.SystemVectorIndexPropertyId) continue; // ignore vector index, it is always different
            if (!node2.TryGetValue(kv.PropertyId, out var oldValue)) return true; // property missing
            var propDef = dm.Properties[kv.PropertyId];
            if (!propDef.AreValuesEqual(kv.Value, oldValue)) return true; // value different            
        }
        return false;
    }
}
