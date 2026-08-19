using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Relatude.DB.Datamodels;

public partial class Datamodel {
    // Calculated:
    public Dictionary<Guid, PropertyModel> Properties = new();
    public Dictionary<string, PropertyModel> PropertiesByFullName = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _ambiguousPropertyNames = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, NodeTypeModel> NodeTypesByFullName = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, NodeTypeModel[]> NodeTypesByShortName = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Type, Guid> RelationIdByType = new();

    bool _hasInitialized = false;
    public bool HasInitialized() => _hasInitialized;
    object _lock = new();
    public void EnsureInitalization() {
        lock (_lock) {
            if (_hasInitialized) return;

            // register the synthetic "Code" source if any type or relation was added directly from code:
            if (!Sources.Any(s => s.Id == DatamodelSource.CodeSourceId)
                && (NodeTypes.Values.Any(t => t.DatamodelSourceId == DatamodelSource.CodeSourceId)
                    || Relations.Values.Any(r => r.DatamodelSourceId == DatamodelSource.CodeSourceId))) {
                Sources.Add(DatamodelSource.CreateCodeSource());
            }

            // validate all class refernces:
            foreach (var item in NodeTypes) {
                foreach (var parentId in item.Value.Parents) {
                    if (!NodeTypes.ContainsKey(parentId)) throw new Exception(
                        "The node type " + item.Value.FullName + " inherits from or implements a type with id " + parentId + " that is not part of the datamodel. "
                        + "All base classes and interfaces of a node type (except object) must be included in the datamodel. "
                        + "This typically happens when the base type is in a namespace or assembly that is not added to the datamodel, "
                        + "or when the base type is marked with [Exclude] while the derived type is not. "
                        + "Add the missing type to the datamodel, or exclude the derived type as well. ");
                }
            }

            // ensuring textindex if semantic index:            
            foreach (var n in NodeTypes.Values) if (n.SemanticIndex.HasValue && n.SemanticIndex.Value)
                n.TextIndex = true;

            // making sure every type inherits from INode
            foreach (var t in NodeTypes.Values) {
                if (!NodeTypesByFullName.TryAdd(t.FullName, t)) {
                    var other = NodeTypesByFullName[t.FullName];
                    throw new Exception("Two node types in the datamodel have the same full name \"" + t.FullName + "\" but different ids ("
                        + other.Id + " and " + t.Id + "). "
                        + "This usually means the same type is added twice with different ids, for example once from code and once from a JSON datamodel with another id. "
                        + "Make sure the type is only defined once, or that both definitions use the same id. ");
                }
                if (!NodeTypesByShortName.TryGetValue(t.CodeName, out var arr)) {
                    NodeTypesByShortName.Add(t.CodeName, [t]);
                } else {
                    NodeTypesByShortName[t.CodeName] = [.. arr, t];
                }
                if (t.Id != NodeConstants.BaseNodeTypeId) {
                    if (t.Parents.Count == 0) t.Parents.Add(NodeConstants.BaseNodeTypeId);
                }
            }

            foreach (var t in NodeTypes.Values) {
                foreach (var p in t.Properties.Values) {
                    p.NodeType = t.Id;
                    if (!Properties.TryAdd(p.Id, p)) {
                        var other = Properties[p.Id];
                        throw new Exception("The properties " + other.GetFullNameBaseType(this) + " and " + p.GetFullNameAnyType(t)
                            + " have the same property id " + p.Id + ". "
                            + "Property ids must be unique across the whole datamodel. "
                            + "This usually comes from a copy-pasted Id in a property attribute or JSON datamodel. Give one of them a new id. ");
                    }
                }
            }
            foreach (var t in NodeTypes.Values) findAllInherited(this, t, t.ThisAndAllInheritedTypes);
            foreach (var t in NodeTypes.Values) findAllDescendants(this, t);
            foreach (var t in NodeTypes.Values) findAllProperties(this, t);
            foreach (var t in NodeTypes.Values) {
                foreach (var p in t.Properties.Values) {
                    var fullName = p.GetFullNameAnyType(t);
                    if (!PropertiesByFullName.TryAdd(fullName, p)) {
                        var other = PropertiesByFullName[fullName];
                        if (other.NodeType == t.Id) {
                            throw new Exception("The node type " + t.FullName + " defines the property name \"" + p.CodeName + "\" more than once "
                                + "(names are compared case-insensitively). Property names must be unique within a node type. "
                                + "Rename one of the members, or exclude one of them with [Exclude]. ");
                        }
                        // two node types share the same short name: the name cannot be resolved
                        // unambiguously, but the datamodel itself is valid
                        _ambiguousPropertyNames.Add(fullName);
                    }
                }
            }
            foreach (var t in NodeTypes.Values) {
                foreach (var p in t.AllProperties.Values) {
                    if (p.DisplayName) t.DisplayProperties.Add(p);
                    if (!p.ExcludeFromTextIndex) t.TextIndexProperties.Add(p);
                }
            }
            identifyNameOfPropertyFromInheritance();
            initializeRelations();

            foreach (var r in Relations.Values) {
                if (r.RelationClassType != null) {
                    if (!RelationIdByType.ContainsKey(r.RelationClassType)) {
                        RelationIdByType.Add(r.RelationClassType, r.Id);
                    } else {
                        throw new Exception("The relation class " + r.RelationClassType.FullName + " is used by two different relations in the datamodel ("
                            + RelationIdByType[r.RelationClassType] + " and " + r.Id + "). "
                            + "A relation class can only define one relation. This usually means the same class was added twice with different ids. ");
                    }
                }
            }
            calculateEmbeddedNodeTypesAndPropertyKeys();
            verifyReferenceNodeTypes();
            _hasInitialized = true;
        }
    }
    void calculateEmbeddedNodeTypesAndPropertyKeys() {
        // looking up KeyPropertyId for EmbeddedPropertyModel:
        foreach (var p in Properties.Values.Where(p => p.PropertyType == PropertyType.Embedded)) {
            if (p is not EmbeddedPropertyModel inp) throw new Exception("Embedded property is not an EmbeddedPropertyModel");
            if (inp.InnerNodeTypesNames != null) {
                foreach (var typeName in inp.InnerNodeTypesNames) {
                    if (!NodeTypesByFullName.TryGetValue(typeName, out var nodeType)) {
                        throw new Exception("The embedded property " + p.GetFullNameBaseType(this) + " refers to an inner node type \"" + typeName + "\" that is not part of the datamodel. "
                            + "Add the inner type to the datamodel or correct the type name. ");
                    }
                    // the same model instance may be verified by more than one datamodel: never accumulate duplicates
                    if (!inp.InnerNodeTypes.Contains(nodeType.Id)) inp.InnerNodeTypes.Add(nodeType.Id);
                }
            }
            foreach (var typeId in inp.InnerNodeTypes) {
                if (!NodeTypes.TryGetValue(typeId, out var nodeType)) {
                    throw new Exception("The embedded property " + p.GetFullNameBaseType(this) + " refers to an inner node type with id " + typeId + " that is not part of the datamodel. "
                        + "Add the inner type to the datamodel or correct the id. ");
                }
            }
            switch (inp.EmbeddedValueType) {
                case EmbeddedValueType.InnerNodeList:
                    inp.KeyProperty = InnerNodeDataMap<object>.PropertyIdNodeGuidId;
                    inp.GetKeyTypeOfPropertyIfPossible(this); // caches the key type, required when deserializing inner nodes
                    break;
                case EmbeddedValueType.InnerNodeMap:
                    var bestCommonBase = FindFirstCommonBase(inp.InnerNodeTypes);
                    if (!string.IsNullOrWhiteSpace(inp.KeyPropertyName)) {
                        if (!bestCommonBase.AllPropertiesByName.TryGetValue(inp.KeyPropertyName, out var keyProp)) {
                            throw new Exception("The embedded map property " + p.GetFullNameBaseType(this) + " uses \"" + inp.KeyPropertyName + "\" as its key property, "
                                + "but no property with that name exists on " + bestCommonBase.FullName + ", the closest common base type of the inner node types. "
                                + "The key property must be defined on a type all inner node types share. ");
                        }
                        inp.KeyProperty = keyProp.Id;
                    }
                    Type keyPropType = inp.GetKeyTypeOfPropertyIfPossible(this);
                    if (inp._keyTypeInCodeModelForLaterChecks != null) {
                        var _valueTypeKey = inp._keyTypeInCodeModelForLaterChecks.GetGenericArguments()[0];
                        if (keyPropType != _valueTypeKey) throw new Exception("The embedded map property " + p.GetFullNameBaseType(this) + " is declared with key type "
                            + _valueTypeKey.Name + " in code, but its key property is of type " + keyPropType.Name + ". "
                            + "The first generic argument of EmbeddedMap<TKey, TValue> must match the type of the key property. ");
                    }
                    break;
                default:
                    throw new Exception("The embedded property " + p.GetFullNameBaseType(this) + " has an unsupported EmbeddedValueType: " + inp.EmbeddedValueType);
            }
        }
    }
    void verifyReferenceNodeTypes() {
        foreach (var p in Properties.Values.Where(p => p.PropertyType == PropertyType.Reference || p.PropertyType == PropertyType.References)) {
            // both property types carry the same target-type trio, but the models are unrelated classes:
            List<Guid> nodeTypes;
            List<string>? nodeTypesNames;
            if (p is ReferencePropertyModel refP) {
                nodeTypes = refP.NodeTypes;
                nodeTypesNames = refP.NodeTypesNames;
            } else if (p is ReferencesPropertyModel refsP) {
                nodeTypes = refsP.NodeTypes;
                nodeTypesNames = refsP.NodeTypesNames;
            } else {
                throw new Exception("Reference property is not a ReferencePropertyModel or ReferencesPropertyModel");
            }
            if (nodeTypesNames != null) {
                foreach (var typeName in nodeTypesNames) {
                    if (!NodeTypesByFullName.TryGetValue(typeName, out var nodeType)) {
                        throw new Exception("The reference property " + p.GetFullNameBaseType(this) + " refers to a node type \"" + typeName + "\" that is not part of the datamodel. "
                            + "Add the referenced type to the datamodel or correct the type name. ");
                    }
                    // the same model instance may be verified by more than one datamodel; never accumulate duplicates
                    if (!nodeTypes.Contains(nodeType.Id)) nodeTypes.Add(nodeType.Id);
                }
            }
            foreach (var typeId in nodeTypes) {
                if (!NodeTypes.TryGetValue(typeId, out var nodeType)) {
                    throw new Exception("The reference property " + p.GetFullNameBaseType(this) + " refers to a node type with id " + typeId + " that is not part of the datamodel. "
                        + "Add the referenced type to the datamodel or correct the id. ");
                }
            }
        }
    }

    void identifyNameOfPropertyFromInheritance() {
        foreach (var nodeType in NodeTypes.Values) {

            nodeType.DataTypeOfInternalId = getBestInternalIdPropTypeInParents(nodeType);
            nodeType.DataTypeOfPublicId = getBestPublicIdPropTypeInParents(nodeType);
            nodeType.NameOfPublicIdProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfPublicIdProperty);
            nodeType.NameOfInternalIdProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfInternalIdProperty);
            nodeType.NameOfChangedUtcProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfChangedUtcProperty);
            nodeType.NameOfMetaProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfMetaProperty);
            nodeType.NameOfDisplayNameProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfDisplayNameProperty);
            nodeType.NameOfAddressProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfAddressProperty);
        }
    }
    DataTypeInternalId? getBestInternalIdPropTypeInParents(NodeTypeModel nodeType) {
        var current = nodeType.DataTypeOfInternalId;
        if (current != null) return current;
        var types = nodeType.Parents
            .Select(id => NodeTypes[id])
            .Select(t => t.DataTypeOfInternalId ?? getBestInternalIdPropTypeInParents(t))
            .Where(n => n != null)
            .ToHashSet();
        if (types.Count == 1) return types.First();
        if (types.Count > 1) throw new Exception("The node type " + nodeType.FullName + " inherits internal id properties of different data types ("
            + string.Join(", ", types) + ") from its base types. "
            + "All base types must agree on the data type of the internal id property (int, long or string). ");
        return null;
    }
    DataTypePublicId? getBestPublicIdPropTypeInParents(NodeTypeModel nodeType) {
        var current = nodeType.DataTypeOfPublicId;
        if (current != null) return current;
        var types = nodeType.Parents
            .Select(id => NodeTypes[id])
            .Select(t => t.DataTypeOfPublicId ?? getBestPublicIdPropTypeInParents(t))
            .Where(n => n != null)
            .ToHashSet();
        if (types.Count == 1) return types.First();
        if (types.Count > 1) throw new Exception("The node type " + nodeType.FullName + " inherits public id properties of different data types ("
            + string.Join(", ", types) + ") from its base types. "
            + "All base types must agree on the data type of the public id property (Guid or string). ");
        return null;
    }
    string? getBestSystemPropNameInParents(NodeTypeModel nodeType, Func<NodeTypeModel, string?> getProp) {
        var currentName = getProp(nodeType);
        if (currentName != null) return currentName;
        var names = nodeType.Parents
            .Select(id => NodeTypes[id])
            .Select(t => getProp(t) ?? getBestSystemPropNameInParents(t, getProp))
            .Where(n => n != null)
            .ToHashSet();
        if (names.Count == 1) return names.First();
        if (names.Count > 1) throw new Exception("The node type " + nodeType.FullName + " inherits the same system property (id, meta, display name, address or changed date) "
            + "under different names from its base types: " + string.Join(", ", names) + ". "
            + "All base types must use the same member name for a given system property. ");
        return null;
    }
    void initializeRelations() {
        foreach (var p in Properties.Values.Where(p => p.PropertyType == PropertyType.Relation)) {
            if (p is not RelationPropertyModel rp) throw new Exception("Relation property is not a RelationPropertyModel");
            if (!Relations.TryGetValue(rp.RelationId, out var relation)) {
                if (rp.RelationId != Guid.Empty) throw new Exception(
                    "The property " + p.GetFullNameBaseType(this) + " refers to a relation with id " + rp.RelationId + " that is not part of the datamodel. "
                    + "If the property uses a relation class, make sure that class is included in the datamodel (it is picked up automatically "
                    + "when it is in an added namespace or referenced by an added type). If the relation is defined in JSON, check that the id matches. ");
                if (!tryFindMatchingOneToManyRelation(rp, out relation)) {
                    if (tryToAutoCreateOneToManyRelations(rp, out relation, out var reasonForNotCreating)) {
                        Relations.Add(relation.Id, relation);
                    } else {
                        throw new Exception("Unable to infer a relation for the member \"" + p.GetFullNameBaseType(this) + "\". "
                            + "The member type is either not supported as a property value, or it points to a type that is not part of the datamodel. "
                            + "If the member is meant to be a relation, reference a relation class with [RelationProperty<TRelation>], "
                            + "or define the relation explicitly in the datamodel. If it is not meant to be stored, mark it with [Exclude]. "
                            + reasonForNotCreating);
                    }
                }
            }
            if (Relations.TryGetValue(rp.RelationId, out var r) && r.RelationType == RelationType.OneToMany) {
                rp.FromTargetToSource = !rp.IsMany; // har coded default for relation properties
            }
        }
    }
    bool tryFindMatchingOneToManyRelation(RelationPropertyModel thisProperty, [MaybeNullWhen(false)] out RelationModel relation) {
        // relations of one to many type, with matching source and target types
        relation = null;
        var possibleMatches = Relations.Values.Where(r => {
            if (r.SourceTypes.Count != 1 || r.TargetTypes.Count != 1) return false; // only simple relations are considered
            if (r.RelationType != RelationType.OneToMany) return false; // only one to many relations are considered
            var fromType = r.SourceTypes.First();
            var toType = r.TargetTypes.First();
            if (thisProperty.IsMany) {
                return fromType == thisProperty.NodeType && toType == thisProperty.NodeTypeOfRelated;
            } else {
                return fromType == thisProperty.NodeTypeOfRelated && toType == thisProperty.NodeType;
            }
        });
        if (possibleMatches.Count() == 0) return false; // no match
        if (possibleMatches.Count() > 1)
            throw new Exception("Unable to automatically match the property " + thisProperty.GetFullNameBaseType(this) + " to a relation: "
                + "more than one relation in the datamodel fits its source and target types ("
                + string.Join(", ", possibleMatches.Select(r => r.CodeName)) + "). "
                + "Specify which relation the property belongs to, for example with [RelationProperty<TRelation>]. ");
        relation = possibleMatches.First();
        var relationId = relation.Id;
        var allRelationProperies = Properties.Values.Where(p => p.PropertyType == PropertyType.Relation).Cast<RelationPropertyModel>();
        var propertiesAlreadyReferingToRelation = allRelationProperies.Where(p => p.RelationId == relationId);
        var propetiesInSameDirection = propertiesAlreadyReferingToRelation.Where(p => p.IsMany == thisProperty.IsMany);
        if (propetiesInSameDirection.Count() > 0) return false; // relation is not available
                                                                // ok, relation is a match:
        thisProperty.RelationId = relationId;
        thisProperty.AutoAssigned = true;
        thisProperty.FromTargetToSource = !thisProperty.IsMany;
        return true;
    }
    bool tryToAutoCreateOneToManyRelations(RelationPropertyModel thisProperty, [MaybeNullWhen(false)] out RelationModel relation, [MaybeNullWhen(true)] out string reasonForNotCreating) {
        relation = null;
        // look for matching oposite property of relation:
        var possiblePropertiesForOpositeSideOfRelation = Properties.Values
            .Where(p => p.PropertyType == PropertyType.Relation).Cast<RelationPropertyModel>() // relation properties
            .Where(p => p.Id != thisProperty.Id && p.RelationId == Guid.Empty) // not this property and not assigned to a relation
            .Where(p => p.IsMany != thisProperty.IsMany) // oposite direction
            .Where(p => p.NodeType == thisProperty.NodeTypeOfRelated && p.NodeTypeOfRelated == thisProperty.NodeType); // correct type
        ;
        if (possiblePropertiesForOpositeSideOfRelation.Count() > 1) {
            reasonForNotCreating = "Multiple properties found match oposite side of the relation for property \"" + thisProperty.GetFullNameBaseType(this) + "\"";
            return false;
        }
        var otherProp = possiblePropertiesForOpositeSideOfRelation.FirstOrDefault();
        var thisNodeType = NodeTypes[thisProperty.NodeType];
        if (!NodeTypes.ContainsKey(thisProperty.NodeTypeOfRelated)) {
            reasonForNotCreating = "Property relates to a type that is not part of the datamodel: \"" + thisProperty.GetFullNameBaseType(this) + "\"";
            return false;
        }
        var otherNodeType = NodeTypes[thisProperty.NodeTypeOfRelated];
        var relName = thisNodeType.CodeName + thisProperty.CodeName + "_" + otherNodeType.CodeName + otherProp?.CodeName;
        relation = new RelationModel() {
            Id = relName.GenerateHashGuid(),
            AutoGenerated = true,
            // auto-created relations inherit the provenance of the type that owns the property:
            DatamodelSourceId = thisNodeType.DatamodelSourceId,
            DatamodelSourceFilename = thisNodeType.DatamodelSourceFilename,
            Namespace = thisNodeType.Namespace,
            CodeName = relName,
            SourceTypes = new() { thisProperty.IsMany ? thisProperty.NodeType : thisProperty.NodeTypeOfRelated },
            TargetTypes = new() { thisProperty.IsMany ? thisProperty.NodeTypeOfRelated : thisProperty.NodeType },
            RelationType = RelationType.OneToMany,
        };
        thisProperty.FromTargetToSource = !thisProperty.IsMany;
        thisProperty.AutoAssigned = true;
        thisProperty.RelationId = relation.Id;
        if (otherProp != null) {
            otherProp.FromTargetToSource = !thisProperty.FromTargetToSource;
            otherProp.RelationId = relation.Id;
        }
        reasonForNotCreating = null;
        return true;
    }
    List<PropertyModel> getBaseProperties() {
        List<PropertyModel> props = new();
        var textIndex = new StringPropertyModel() {
            Id = NodeConstants.SystemTextIndexPropertyId,
            CodeName = NodeConstants.SystemTextIndexPropertyName,
            ExcludeFromTextIndex = true,
            Indexed = false,
            IndexedByWords = true,
            IndexedBySemantic = true,
            InfixSearch = false,
            PropertyIdForEmbeddings = NodeConstants.SystemVectorIndexPropertyId,
            Internal = true,
            MinWordLength = 2,
        };
        props.Add(textIndex);
        var vectorIndex = new FloatArrayPropertyModel() {
            Id = NodeConstants.SystemVectorIndexPropertyId,
            CodeName = NodeConstants.SystemVectorIndexPropertyName,
            ExcludeFromTextIndex = true,
            Indexed = true,
            Internal = true,
        };
        props.Add(vectorIndex);
        var address = new StringPropertyModel() {
            Id = NodeConstants.SystemAddressPropertyId,
            DefaultValue = null,
            CodeName = NodeConstants.SystemAddressPropertyName,
            ExcludeFromTextIndex = true,
            Indexed = false,
            Internal = true,
        };
        props.Add(address);
        var autoAddress = new BooleanPropertyModel() {
            Id = NodeConstants.SystemAutoAddressPropertyId,
            CodeName = NodeConstants.SystemAutoAddressPropertyName,
            ExcludeFromTextIndex = true,
            Indexed = false,
            Internal = true,
        };
        props.Add(autoAddress);
        var displayName = new StringPropertyModel() {
            Id = NodeConstants.SystemDisplayNamePropertyId,
            CodeName = NodeConstants.SystemDisplayNamePropertyName,
            ExcludeFromTextIndex = false,
            Indexed = false,
            DisplayName = true,
            Internal = true,
        };
        props.Add(displayName);
        return props;
    }
    static void findAllInherited(Datamodel datamodel, NodeTypeModel ct, Dictionary<Guid, NodeTypeModel> allInherited) {
        if (allInherited.ContainsKey(ct.Id)) return;
        allInherited.Add(ct.Id, ct);
        foreach (var id in ct.Parents) {
            if (datamodel.NodeTypes.TryGetValue(id, out var parent)) {
                findAllInherited(datamodel, parent, allInherited);
            }
        }
    }
    static void findAllDescendants(Datamodel datamodel, NodeTypeModel ct) {
        if (ct.ThisAndDescendingTypes.ContainsKey(ct.Id)) return;
        ct.ThisAndDescendingTypes.Add(ct.Id, ct);
        foreach (var t in datamodel.NodeTypes.Values) {
            if (t.ThisAndAllInheritedTypes.ContainsKey(ct.Id)) {
                if (!ct.ThisAndDescendingTypes.ContainsKey(t.Id)) {
                    ct.ThisAndDescendingTypes.Add(t.Id, t);
                }
            }
        }
    }
    static void findAllProperties(Datamodel datamodel, NodeTypeModel ct) {
        foreach (var t in ct.ThisAndAllInheritedTypes) {
            if (datamodel.NodeTypes.TryGetValue(t.Key, out var parent)) {
                foreach (var p in parent.Properties.Values) {
                    if (!ct.AllPropertiesByName.TryAdd(p.CodeName, p)) {
                        var other = ct.AllPropertiesByName[p.CodeName];
                        var otherOwner = datamodel.NodeTypes.TryGetValue(other.NodeType, out var o) ? o.FullName : other.NodeType.ToString();
                        throw new Exception("The node type " + ct.FullName + " gets the property name \"" + p.CodeName + "\" from two different types: "
                            + otherOwner + " and " + parent.FullName + " (names are compared case-insensitively). "
                            + "A property name can only be declared once in a type hierarchy. "
                            + "Declare it once on a shared base type, or rename or exclude one of the members. ");
                    }
                    ct.AllProperties.Add(p.Id, p);
                    ct.AllPropertyIdsByName.Add(p.CodeName, p.Id);
                }
            }
        }
    }

    public Guid GetPropertyGuid(string idString) {
        var value = idString.Split('|')[0]; // only first part matter
        if (Guid.TryParse(value, out var propertyId)) {
            return propertyId;
        } else if (PropertiesByFullName.TryGetValue(idString, out var property)) {
            if (_ambiguousPropertyNames.Contains(idString)) {
                throw new Exception("The property name \"" + idString + "\" is ambiguous: more than one node type has the short name \""
                    + idString.Split('.')[0] + "\" with a property of this name. Use the property id instead. ");
            }
            return property.Id;
        } else {
            throw new Exception("Unknown property \"" + idString + "\". Expected a property id or a name in the form \"TypeName.PropertyName\". ");
        }
    }

    // Helper Functions
    public NodeTypeModel FindFirstCommonBase(IEnumerable<Guid> nodeTypes) {
        if (nodeTypes == null || nodeTypes.Count() == 0) return NodeTypes[NodeConstants.BaseNodeTypeId];
        if (nodeTypes.Count() == 1) return NodeTypes[nodeTypes.First()];
        // the closest common ancestor: a type that every given type inherits or implements.
        // (every type includes the base node type in its inheritance closure, so the
        // intersection is never empty - disjoint types resolve to the base node type)
        HashSet<Guid>? common = null;
        foreach (var id in nodeTypes) {
            var ancestors = NodeTypes[id].ThisAndAllInheritedTypes.Keys;
            if (common == null) common = [.. ancestors];
            else common.IntersectWith(ancestors);
        }
        if (common == null || common.Count == 0) return NodeTypes[NodeConstants.BaseNodeTypeId];
        // prefer the most derived common ancestor (largest inheritance closure), with a
        // deterministic tie-break:
        var bestId = common.OrderByDescending(c => NodeTypes[c].ThisAndAllInheritedTypes.Count).ThenBy(c => c).First();
        return NodeTypes[bestId];
    }

    /// <summary>
    /// Merges another (not yet initialized) datamodel into this one. When sourceId is given, all
    /// imported node types and relations are re-tagged as coming from that datamodel source (any
    /// provenance stored inside the added model, including its Sources list, is discarded), and
    /// sourceFilename (the file the model was read from, when file-based) is stamped on them.
    /// </summary>
    public void AddDatamodel(Datamodel dm, Guid? sourceId = null, string? sourceFilename = null) {
        if (dm == null) return;
        if (dm.HasInitialized()) throw new Exception("Cannot add an already initialized datamodel to another datamodel. Add datamodels together before the store is created. ");
        foreach (var nt in dm.NodeTypes.Values) {
            if (nt.Id == NodeConstants.BaseNodeTypeId) continue; // both models contain the built-in base type
            if (NodeTypes.TryGetValue(nt.Id, out var existing)) throw new Exception(
                "Cannot combine datamodels: the node type " + nt.FullName + " has the same id as " + existing.FullName + " (" + nt.Id + "). "
                + "The same type is probably included by more than one datamodel source. ");
            if (sourceId.HasValue) {
                nt.DatamodelSourceId = sourceId.Value;
                nt.DatamodelSourceFilename = sourceFilename;
            }
            NodeTypes.Add(nt.Id, nt);
        }
        foreach (var r in dm.Relations.Values) {
            if (Relations.TryGetValue(r.Id, out var existing)) throw new Exception(
                "Cannot combine datamodels: the relation " + r.CodeName + " has the same id as " + existing.CodeName + " (" + r.Id + "). "
                + "The same relation is probably included by more than one datamodel source. ");
            if (sourceId.HasValue) {
                r.DatamodelSourceId = sourceId.Value;
                r.DatamodelSourceFilename = sourceFilename;
            }
            Relations.Add(r.Id, r);
        }
        foreach (var p in dm.Properties.Values) {
            if (Properties.TryGetValue(p.Id, out var existing)) throw new Exception(
                "Cannot combine datamodels: the property " + p.CodeName + " has the same id as " + existing.CodeName + " (" + p.Id + "). "
                + "The same property is probably included by more than one datamodel source. ");
            Properties.Add(p.Id, p);
        }
    }

    Dictionary<Guid, Guid[]> _innerNodePropsByTypeId = [];
    public Guid[] GetEmbeddedProps(Guid nodeType) {
        if (!_innerNodePropsByTypeId.TryGetValue(nodeType, out var props)) {
            props = [.. NodeTypes[nodeType].AllProperties.Values.Where(p => p.PropertyType == PropertyType.Embedded).Select(p => p.Id)];
            _innerNodePropsByTypeId[nodeType] = props;
        }
        return props;
    }

}