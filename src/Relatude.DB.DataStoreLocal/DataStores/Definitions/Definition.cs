using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.Meta;
using Relatude.DB.DataStores.Sets;
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
        Store = store;
        foreach (var cm in datamodel.NodeTypes.Values) {
            var c = new NodeType(cm);
            NodeTypes.Add(c.Id, c);
            var properties = cm.Properties.Values.Select(p => Property.Create(p, this));
            foreach (var property in properties) Properties.Add(property.Id, property);
        }
        _nodeDateProperties = [.. Properties.Values.OfType<SystemDateTimeProperty>()];
        foreach (var cm in datamodel.NodeTypes.Values) {
            var c = NodeTypes[cm.Id];
            foreach (var p in cm.AllProperties.Values) {
                c.AllPropertiesByName.Add(p.CodeName, Properties[p.Id]);
                c.AllProperties.Add(p.Id, Properties[p.Id]);
            }
            addNodeDateAliases(c, cm);
        }
        foreach (var cm in datamodel.Relations.Values) {
            var c = new Relation(cm, store);
            Relations.Add(c.Id, c);
        }
        PropertyGuidBy__Id = Properties.Values.ToDictionary(p => p.__Id_transient, p => p.Id);
        _nodeTypeIndex = new(this, store._nativeModelStore, store.Settings);
        foreach (var cm in datamodel.NodeTypes.Values) {
            var limited = cm.ThisAndAllInheritedTypes.Values.Where(hasInstanceLimits).Select(t => t.Id).ToArray();
            if (limited.Length > 0) _instanceLimitedTypes.Add(cm.Id, limited);
        }
    }
    static bool hasInstanceLimits(NodeTypeModel t) => t.MinNoInstances > 0 || t.MaxNoInstances != int.MaxValue;
    readonly Dictionary<Guid, Guid[]> _instanceLimitedTypes = [];
    /// <summary>Whether any node type has a MinNoInstances or MaxNoInstances, so a transaction has counts to check.</summary>
    public bool AnyInstanceLimits => _instanceLimitedTypes.Count > 0;
    /// <summary>The types whose instance limits a node of this type counts towards: the type itself and the
    /// types it inherits from, those of them with a limit.</summary>
    public Guid[] InstanceLimitedTypesOf(Guid nodeTypeId) => _instanceLimitedTypes.TryGetValue(nodeTypeId, out var types) ? types : [];
    // A class can map the node's own dates to members of its own - [CreatedUtcProperty] members, or the
    // members of its NodeMeta - and a query over it names them by those: x.Created, x.Meta.CreatedUtc.
    // They are the system properties under other names, so they resolve to them.
    void addNodeDateAliases(NodeType c, NodeTypeModel cm) {
        void alias(string? name, Guid propertyId) {
            if (string.IsNullOrEmpty(name) || !Properties.TryGetValue(propertyId, out var p)) return;
            c.AllPropertiesByName.TryAdd(name, p);
        }
        alias(cm.NameOfCreatedUtcProperty, NodeConstants.SystemCreatedUtcPropertyId);
        alias(cm.NameOfChangedUtcProperty, NodeConstants.SystemChangedUtcPropertyId);
        if (!string.IsNullOrEmpty(cm.NameOfMetaProperty)) {
            alias(cm.NameOfMetaProperty + "." + nameof(NodeMeta.CreatedUtc), NodeConstants.SystemCreatedUtcPropertyId);
            alias(cm.NameOfMetaProperty + "." + nameof(NodeMeta.ChangedUtc), NodeConstants.SystemChangedUtcPropertyId);
        }
    }
    readonly SystemDateTimeProperty[] _nodeDateProperties;
    public DataStoreLocal Store { get; }
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
    // replaced whole when a culture's indexes come or go (SyncCultureIndexes), under the store's write lock, as
    // the background warm-up lists them without it
    public IEnumerable<IIndex> GetAllIndexes() { return _indexes.Values; }
    /// <summary>
    /// The cultures the culture-sensitive properties have indexes for, besides no culture. The indexes are made
    /// before the store has read its cultures, so these are the ones the store had when it last had them in line
    /// (DataStoreLocal's index record), and <see cref="SyncCultureIndexes"/> brings them in line again.
    /// </summary>
    internal Guid[] IndexedCultures { get; set; } = [];
    bool _anyCultureSensitive;
    internal void Initialize(DataStoreLocal store, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        foreach (var p in Properties.Values) {
            p.Initalize(store, this, config, io, ai);
        }
        foreach (var t in Relations.Values) t.Initialize(this);
        _indexes = Properties.Values.SelectMany(p => p.AllIndexes).ToDictionary(k => k.UniqueKey, k => k);
        _anyCultureSensitive = Properties.Values.Any(p => p.CultureIndexes.Count > 0);
    }
    /// <summary>The culture whose indexes a query reads: its culture, by id or code, or no culture (<see cref="Guid.Empty"/>).</summary>
    internal Guid IndexCultureOf(QueryContext ctx) {
        if (ctx.CultureId is { } id && id != Guid.Empty) return id;
        if (!string.IsNullOrEmpty(ctx.CultureCode) && Store._nativeModelStore.TryGetCultureId(ctx.CultureCode, out var byCode)) return byCode;
        return Guid.Empty;
    }
    /// <summary>
    /// Makes the indexes of the cultures the culture-sensitive properties have none for, and takes away those of
    /// cultures that are gone. The ones made are empty: the caller fills them.
    /// </summary>
    internal (List<IIndex> made, List<IIndex> removed) SyncCultureIndexes(Guid[] cultures) {
        var made = new List<IIndex>();
        var removed = new List<IIndex>();
        if (_anyCultureSensitive) {
            foreach (var gone in IndexedCultures.Except(cultures)) foreach (var p in Properties.Values) removed.AddRange(p.RemoveCultureIndexes(gone));
            foreach (var culture in cultures) foreach (var p in Properties.Values) made.AddRange(p.AddCultureIndexes(culture));
            if (made.Count > 0 || removed.Count > 0) {
                var indexes = new Dictionary<string, IIndex>(_indexes);
                foreach (var index in removed) indexes.Remove(index.UniqueKey);
                foreach (var index in made) indexes.Add(index.UniqueKey, index);
                _indexes = indexes;
            }
        }
        IndexedCultures = cultures;
        return (made, removed);
    }
    /// <summary>
    /// What the node puts in which index. A node with no revisions gives its values to every index. A node with
    /// revisions gives what its readers read, of its published revisions (release dates are not looked at): to
    /// every index but a culture's own, the values of the revision a reader of no culture reads, the neutral one,
    /// else the one of the culture first in fallback order (<see cref="GetCulturePriority"/>); to the index of a
    /// culture it has a published revision in, that revision's values.
    /// <para>The same node must give the same entries when it leaves the indexes as when it went in, so what it
    /// gives depends on nothing but the node and the order of the cultures it has revisions in. That order never
    /// changes: a culture is made last in it, and one that revisions are in cannot be removed (the store's
    /// validateCultureRemovals), nor can a revision be put in a culture that does not exist yet (ActionConverter).</para>
    /// </summary>
    public void IteratePropertyIndexes(INodeData node, Action<INodeData, IIndex, object> action) {
        if (node is NodeData nd) {
            foreach (var kv in nd.Values) {
                var propDef = Properties[kv.PropertyId];
                if (propDef is SystemDateTimeProperty) continue; // indexed from the node record below, never from a value
                if (!propDef.ShouldIndexValue(kv.Value)) continue;
                foreach (var index in propDef.AllIndexes) {
                    if (propDef.IsNodeRelevantForIndex(nd.NodeType, index)) action(nd, index, kv.Value);
                }
            }
        } else if (node is NodeDataRevisions ndr) {
            iterateRevisionIndexes(ndr, action);
        } else {
            throw new Exception("Unknown node data type");
        }
        // every node is in the indexes of its own dates, whatever its type and whatever it maps
        foreach (var p in _nodeDateProperties) {
            object value = p.ValueOf(node);
            foreach (var index in p.AllIndexes) action(node, index, value);
        }
    }
    void iterateRevisionIndexes(NodeDataRevisions ndr, Action<INodeData, IIndex, object> action) {
        NodeDataRevision? read = null; // what a reader of no culture reads
        var readPriority = int.MaxValue;
        Dictionary<Guid, NodeDataRevision>? own = null; // a culture's own published revision, for its indexes
        foreach (var rev in ndr.Revisions) {
            if (rev.RevisionType != RevisionType.Published) continue;
            var priority = GetCulturePriority(rev.CultureId, Guid.Empty);
            if (read == null || priority < readPriority) {
                read = rev;
                readPriority = priority;
            }
            if (_anyCultureSensitive && rev.CultureId != Guid.Empty) (own ??= []).TryAdd(rev.CultureId, rev);
        }
        if (read == null) return; // nothing published
        foreach (var kv in read.Values) {
            var propDef = Properties[kv.PropertyId];
            if (propDef is SystemDateTimeProperty) continue;
            if (!propDef.ShouldIndexValue(kv.Value)) continue;
            foreach (var index in propDef.AllIndexes) {
                if (own != null && propDef.CultureIndexes.Count > 0 && own.ContainsKey(propDef.CultureOf(index))) continue; // the culture's own goes there
                if (propDef.IsNodeRelevantForIndex(read.NodeType, index)) action(read, index, kv.Value);
            }
        }
        if (own == null) return;
        foreach (var (culture, rev) in own) {
            foreach (var kv in rev.Values) {
                var propDef = Properties[kv.PropertyId];
                if (propDef.CultureIndexes.Count == 0) continue;
                if (propDef is SystemDateTimeProperty) continue;
                if (!propDef.ShouldIndexValue(kv.Value)) continue;
                foreach (var index in propDef.AllIndexes) {
                    if (propDef.CultureOf(index) == culture && propDef.IsNodeRelevantForIndex(rev.NodeType, index)) action(rev, index, kv.Value);
                }
            }
        }
    }
    internal void IndexNode(INodeData node) {
        IteratePropertyIndexes(node, (n, index, value) => index.Add(n.__Id, value));
        _nodeTypeIndex.Index(node);
    }
    internal void DeIndexNode(INodeData node) {
        IteratePropertyIndexes(node, (n, index, value) => index.Remove(n.__Id, value));
        _nodeTypeIndex.DeIndex(node);
    }
    public bool TryGetIndex(string indexUniqueKey, [MaybeNullWhen(false)] out IIndex index) {
        return _indexes.TryGetValue(indexUniqueKey, out index);
    }
    public void RegisterActionDuringStateLoad(long transactionTimestamp, PrimitiveNodeAction action, bool throwOnErrors, Action<string, Exception> log) {
        IteratePropertyIndexes(action.Node, (n, index, value) => {
            if (transactionTimestamp <= index.PersistedTimestamp) return;
            try {
                switch (action.Operation) {
                    case PrimitiveOperation.Add: index.RegisterAddDuringStateLoad(n.__Id, value); break;
                    case PrimitiveOperation.Remove: index.RegisterRemoveDuringStateLoad(n.__Id, value); break;
                    default: throw new NotImplementedException();
                }
            } catch (Exception err) {
                var msg = "Error during state load. " + err.Message;
                log(msg, err);
                if (throwOnErrors) throw new Exception(msg, err);
            }
        });
        //if (action.Node is NodeDataRevisions ndr) {
        //    HashSet<Guid> indexedProps = new(); // kind of a waste, could be optimized....
        //    foreach (var rev in ndr.Revisions) {
        //        foreach (var kv in rev.Values) {
        //            if (Properties.TryGetValue(kv.PropertyId, out var property)) {
        //                bool shouldIndex = true;
        //                if (!property.Model.CultureSensitive) {  // only once for all revisions
        //                    if (!indexedProps.Contains(property.Id)) {
        //                        indexedProps.Add(property.Id);
        //                    } else {
        //                        shouldIndex = false;
        //                    }
        //                }
        //                if (shouldIndex) {
        //                    foreach (var index in property.AllIndexes) {
        //                        if (transactionTimestamp <= index.PersistedTimestamp) continue;
        //                        try {
        //                            if (property.IsNodeRelevantForIndex(rev.NodeType, index)) {
        //                                switch (action.Operation) {
        //                                    case PrimitiveOperation.Add: index.RegisterAddDuringStateLoad(rev.__Id, kv.Value); break;
        //                                    case PrimitiveOperation.Remove: index.RegisterRemoveDuringStateLoad(rev.__Id, kv.Value); break;
        //                                    default: throw new NotImplementedException();
        //                                }
        //                            }
        //                        } catch (Exception err) {
        //                            var msg = "Error during state load. " + err.Message;
        //                            log(msg, err);
        //                            if (throwOnErrors) throw new Exception(msg, err);
        //                        }
        //                    }
        //                }
        //            } else {
        //                // just ignore if unknown property in log. indicates property has been removed from datamodel, but log still contain value
        //            }
        //        }
        //    }
        //} else if (action.Node is NodeData nd) {
        //    foreach (var kv in nd.Values) {
        //        if (Properties.TryGetValue(kv.PropertyId, out var property)) {
        //            foreach (var index in property.AllIndexes) {
        //                if (transactionTimestamp <= index.PersistedTimestamp) continue;
        //                try {
        //                    if (property.IsNodeRelevantForIndex(nd.NodeType, index)) {
        //                        switch (action.Operation) {
        //                            case PrimitiveOperation.Add: index.RegisterAddDuringStateLoad(nd.__Id, kv.Value); break;
        //                            case PrimitiveOperation.Remove: index.RegisterRemoveDuringStateLoad(nd.__Id, kv.Value); break;
        //                            default: throw new NotImplementedException();
        //                        }
        //                    }
        //                } catch (Exception err) {
        //                    var msg = "Error during state load. " + err.Message;
        //                    log(msg, err);
        //                    if (throwOnErrors) throw new Exception(msg, err);
        //                }
        //            }
        //        } else {
        //            // just ignore if unknown property in log. indicates property has been removed from datamodel, but log still contain value
        //        }
        //    }
        //}
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
        if (nodeIds.Count > 2000) {
            // for large sets it is cheaper to probe each node type's (cached) id set than to
            // resolve the type of every single node in the set:
            foreach (var typeId in Datamodel.NodeTypes.Keys) {
                if (Sets.CountIntersection(GetAllIdsForTypeNoAccessControl(typeId, false), nodeIds) > 0) relevantNodeTypes.Add(typeId);
            }
        } else {
            foreach (var id in nodeIds.Enumerate()) {
                relevantNodeTypes.Add(_nodeTypeIndex.GetType(id));
            }
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

    internal IEnumerable<Property> GetFacetPropertiesForType(Guid typeId, bool includeDescendants) {
        var types = Datamodel.NodeTypes.Values.Where(t => includeDescendants ? t.ThisAndAllInheritedTypes.ContainsKey(typeId) : t.Id == typeId);
        return types.SelectMany(t => t.AllProperties.Keys).Distinct().Select(id => Properties[id]).Where(p => p.CanBeFacet());
    }

    /// <summary>Where a revision's culture comes when a reader falls back to another culture (see NativeModelStore).</summary>
    internal int GetCulturePriority(Guid cultureId, Guid collectionId) => Store._nativeModelStore.GetCulturePriority(cultureId);
}
