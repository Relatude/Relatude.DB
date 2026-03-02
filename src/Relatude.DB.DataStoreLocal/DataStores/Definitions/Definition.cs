using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores.Definitions;

internal sealed class Definition {
    internal Definition(SetRegister sets, Datamodel datamodel, DataStoreLocal store) {
        Datamodel = datamodel;
        NodeTypes = new();
        Relations = new();
        Properties = new();
        Sets = sets;
        _indexes = new();
        foreach (var cm in datamodel.NodeTypes.Values) {
            var c = new NodeType(cm);
            NodeTypes.Add(c.Id, c);
            var properties = cm.Properties.Values.Select(p => Property.Create(p, this));
            foreach (var property in properties) Properties.Add(property.Id, property);
        }
        foreach (var cm in datamodel.NodeTypes.Values) {
            var c = NodeTypes[cm.Id];
            foreach (var p in cm.AllProperties.Values) {
                c.AllPropertiesByName.Add(p.CodeName, Properties[p.Id]);
                c.AllProperties.Add(p.Id, Properties[p.Id]);
            }
        }
        foreach (var cm in datamodel.Relations.Values) {
            var c = new Relation(cm, store);
            Relations.Add(c.Id, c);
        }
        PropertyGuidBy__Id = Properties.Values.ToDictionary(p => p.__Id_transient, p => p.Id);
        _nodeTypeIndex = new(this, store._nativeModelStore);
    }
    public Datamodel Datamodel { get; }
    public SetRegister Sets { get; }
    internal Dictionary<Guid, NodeType> NodeTypes { get; }
    internal Dictionary<Guid, Property> Properties { get; }
    internal Dictionary<int, Guid> PropertyGuidBy__Id { get; }
    internal Dictionary<Guid, Relation> Relations { get; }
    readonly Cache<long, Property[]> _facetPropCache = new(400);
    Dictionary<string, IIndex> _indexes { get; set; }
    NodeTypesByIds _nodeTypeIndex;
    public NodeTypesByIds NodeTypeIndex => _nodeTypeIndex;
    public IdSet GetAllIdsForType(Guid typeId, QueryContext ctx) => _nodeTypeIndex.GetAllNodeIdsForTypeFilteredByContext(typeId, ctx);
    public IdSet GetAllIdsForTypeNoAccessControl(Guid typeId, bool includeDescendants) => _nodeTypeIndex.GetAllNodeIdsForTypeNoFilter(typeId, includeDescendants);
    public int GetCountForTypeForStatusInfo(Guid typeId) => _nodeTypeIndex.GetCountForTypeForStatusInfo(typeId);
    public Guid GetTypeOfNode(int id) => _nodeTypeIndex.GetType(id);
    public bool TryGetTypeOfNode(int id, [MaybeNullWhen(false)] out Guid typeId) => _nodeTypeIndex.TryGetType(id, out typeId);
    public IEnumerable<IIndex> GetAllIndexes() { return _indexes.Values; }
    internal void Initialize(DataStoreLocal store, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        foreach (var p in Properties.Values) {
            p.Initalize(store, this, config, io, ai);
        }
        foreach (var t in Relations.Values) t.Initialize(this);
        _indexes = Properties.Values.SelectMany(p => p.AllIndexes).ToDictionary(k => k.UniqueKey, k => k);
    }
    internal void IndexNode(INodeData node) {
        if (node is NodeDataRevisions ndr) {
            HashSet<Guid> indexedProps = new(); // kind of a waste, could be optimized....
            foreach (var rev in ndr.Revisions) {
                if (rev.RevisionType == RevisionType.Published) {
                    foreach (var kv in rev.Values) {
                        var propDef = Properties[kv.PropertyId];
                        bool shouldIndex = true;
                        if (!propDef.Model.CultureSensitive) {  // only once for all revisions
                            if (!indexedProps.Contains(propDef.Id)) {
                                indexedProps.Add(propDef.Id);
                            } else {
                                shouldIndex = false;
                            }
                        }
                        if (shouldIndex) {
                            foreach (var index in propDef.AllIndexes) {
                                if (propDef.IsNodeRelevantForIndex(rev, index)) index.Add(rev.__Id, kv.Value);
                            }
                        }
                    }
                }
                _nodeTypeIndex.Index(rev);
            }
        } else if (node is NodeData nd) {
            foreach (var kv in nd.Values) {
                var propDef = Properties[kv.PropertyId];
                foreach (var index in propDef.AllIndexes) {
                    if (propDef.IsNodeRelevantForIndex(nd, index)) index.Add(nd.__Id, kv.Value);
                }
            }
            _nodeTypeIndex.Index(nd);
        } else {
            throw new Exception("Unknown node data type");
        }
    }
    internal void DeIndexNode(INodeData node) {
        if (node is NodeDataRevisions ndr) {
            HashSet<Guid> indexedProps = new(); // kind of a waste, could be optimized....
            foreach (var rev in ndr.Revisions) {
                if (rev.RevisionType == RevisionType.Published) {
                    foreach (var kv in rev.Values) {
                        var propDef = Properties[kv.PropertyId];
                        bool shouldIndex = true;
                        if (!propDef.Model.CultureSensitive) {  // only once for all revisions
                            if (!indexedProps.Contains(propDef.Id)) {
                                indexedProps.Add(propDef.Id);
                            } else {
                                shouldIndex = false;
                            }
                        }
                        if (shouldIndex) {
                            foreach (var index in propDef.AllIndexes) {
                                if (propDef.IsNodeRelevantForIndex(rev, index)) index.Remove(rev.__Id, kv.Value);
                            }
                        }
                    }
                }
                _nodeTypeIndex.DeIndex(rev);
            }
        } else if (node is NodeData nd) {
            foreach (var kv in nd.Values) {
                var propDef = Properties[kv.PropertyId];
                foreach (var index in propDef.AllIndexes) {
                    if (propDef.IsNodeRelevantForIndex(nd, index)) index.Remove(nd.__Id, kv.Value);
                }
            }
            _nodeTypeIndex.DeIndex(nd);
        } else {
            throw new Exception("Unknown node data type");
        }
    }
    public bool TryGetIndex(string indexUniqueKey, [MaybeNullWhen(false)] out IIndex index) {
        return _indexes.TryGetValue(indexUniqueKey, out index);
    }
    public void RegisterActionDuringStateLoad(long transactionTimestamp, PrimitiveNodeAction action, bool throwOnErrors, Action<string, Exception> log) {
        if (action.Node is NodeDataRevisions ndr) {
            HashSet<Guid> indexedProps = new(); // kind of a waste, could be optimized....
            foreach (var rev in ndr.Revisions) {
                foreach (var kv in rev.Values) {
                    if (Properties.TryGetValue(kv.PropertyId, out var property)) {
                        bool shouldIndex = true;
                        if (!property.Model.CultureSensitive) {  // only once for all revisions
                            if (!indexedProps.Contains(property.Id)) {
                                indexedProps.Add(property.Id);
                            } else {
                                shouldIndex = false;
                            }
                        }
                        if (shouldIndex) {
                            foreach (var index in property.AllIndexes) {
                                if (transactionTimestamp <= index.PersistedTimestamp) continue;
                                try {
                                    if (property.IsNodeRelevantForIndex(rev, index)) {
                                        switch (action.Operation) {
                                            case PrimitiveOperation.Add: index.RegisterAddDuringStateLoad(rev.__Id, kv.Value); break;
                                            case PrimitiveOperation.Remove: index.RegisterRemoveDuringStateLoad(rev.__Id, kv.Value); break;
                                            default: throw new NotImplementedException();
                                        }
                                    }
                                } catch (Exception err) {
                                    var msg = "Error during state load. " + err.Message;
                                    log(msg, err);
                                    if (throwOnErrors) throw new Exception(msg, err);
                                }
                            }
                        }
                    } else {
                        // just ignore if unknown property in log. indicates property has been removed from datamodel, but log still contain value
                    }
                }
            }
        } else if (action.Node is NodeData nd) {
            foreach (var kv in nd.Values) {
                if (Properties.TryGetValue(kv.PropertyId, out var property)) {
                    foreach (var index in property.AllIndexes) {
                        if (transactionTimestamp <= index.PersistedTimestamp) continue;
                        try {
                            if (property.IsNodeRelevantForIndex(nd, index)) {
                                switch (action.Operation) {
                                    case PrimitiveOperation.Add: index.RegisterAddDuringStateLoad(nd.__Id, kv.Value); break;
                                    case PrimitiveOperation.Remove: index.RegisterRemoveDuringStateLoad(nd.__Id, kv.Value); break;
                                    default: throw new NotImplementedException();
                                }
                            }
                        } catch (Exception err) {
                            var msg = "Error during state load. " + err.Message;
                            log(msg, err);
                            if (throwOnErrors) throw new Exception(msg, err);
                        }
                    }
                } else {
                    // just ignore if unknown property in log. indicates property has been removed from datamodel, but log still contain value
                }
            }
        }
    }
    internal void AddInfo(DataStoreInfo info) {
        info.DatamodelNodeTypeCount = NodeTypes.Count - 1; // -1 due to base type
        info.DatamodelPropertyCount = Properties.Count - 1; // -1 due to textindex
        info.DatamodelRelationCount = Relations.Count;
        info.DatamodelIndexCount = _indexes.Count - 2; // due to nodeTypeIndex and textindex
    }
    internal Property[] GetFacetPropertiesForSet(IdSet nodeIds) {
        if (_facetPropCache.TryGet(nodeIds.StateId, out var result)) return result;
        // look at every relevant property:
        var relevantNodeTypes = new HashSet<Guid>();
        foreach (var id in nodeIds.Enumerate()) { // timeconsuming for large dataset, but no easy way around this...
            relevantNodeTypes.Add(_nodeTypeIndex.GetType(id));
        }
        var allPossibleProps = new HashSet<Guid>();
        // next part could be cached..... but should not be very time consuming
        foreach (var typeId in relevantNodeTypes) {
            foreach (var prop in Datamodel.NodeTypes[typeId].AllProperties.Values) allPossibleProps.Add(prop.Id);
        }
        result = allPossibleProps.Select(pId => Properties[pId]).Where(p => p.CanBeFacet()).ToArray();
        _facetPropCache.Set(nodeIds.StateId, result, 1);
        return result;
    }

    internal object GetCulturePriority(Guid cultureId, Guid collectionId) {
        throw new NotImplementedException();
    }
}
