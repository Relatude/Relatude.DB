using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Relatude.DB.Datamodels;
public partial class Datamodel {
    // Calculated:
    public Dictionary<Guid, PropertyModel> Properties = new();
    public Dictionary<string, PropertyModel> PropertiesByFullName = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, NodeTypeModel> NodeTypesByFullName = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, NodeTypeModel[]> NodeTypesByShortName = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Type, Guid> RelationIdByType = new();

    bool _hasInitialized = false;
    public bool HasInitialized() => _hasInitialized;
    object _lock = new();
    public void EnsureInitalization() {
        lock (_lock) {
            if (_hasInitialized) return;

            // ensuring textindex if semantic index:            
            foreach (var n in NodeTypes.Values) if (n.SemanticIndex.HasValue && n.SemanticIndex.Value)
                    n.TextIndex = true;

            // making sure every type inherits from INode
            foreach (var t in NodeTypes.Values) {
                NodeTypesByFullName.Add(t.FullName, t);
                if (!NodeTypesByShortName.TryGetValue(t.CodeName, out var arr)) {
                    NodeTypesByShortName.Add(t.CodeName, [t]);
                } else {
                    NodeTypesByShortName[t.CodeName] = [.. arr, t];
                }
                if (t.Id != NodeConstants.BaseNodeTypeId) {
                    if (t.Parents.Count == 0) t.Parents.Add(NodeConstants.BaseNodeTypeId);
                }
            }

            foreach (var t in NodeTypes.Values) findAllInherited(this, t, t.ThisAndAllInheritedTypes);
            foreach (var t in NodeTypes.Values) findAllDescendants(this, t);
            foreach (var t in NodeTypes.Values) findAllProperties(this, t);
            foreach (var t in NodeTypes.Values) {
                foreach (var p in t.Properties.Values) {
                    p.NodeType = t.Id;
                    Properties.Add(p.Id, p);
                }
            }
            foreach (var t in NodeTypes.Values) {
                foreach (var p in t.Properties.Values) PropertiesByFullName.Add(p.GetFullNameAnyType(t), p);
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
                        throw new Exception("Relation class type already exists in RelationIdByType: " + r.RelationClassType.FullName);
                    }
                }
            }

            _hasInitialized = true;
        }
    }
    void identifyNameOfPropertyFromInheritance() {
        foreach (var nodeType in NodeTypes.Values) {

            nodeType.DataTypeOfInternalId = getBestInternalIdPropTypeInParents(nodeType);
            nodeType.DataTypeOfPublicId = getBestPublicIdPropTypeInParents(nodeType);
            nodeType.NameOfPublicIdProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfPublicIdProperty);
            nodeType.NameOfInternalIdProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfInternalIdProperty);
            nodeType.NameOfChangedUtcProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfChangedUtcProperty);
            nodeType.NameOfCollectionProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfCollectionProperty);
            nodeType.NameOfDerivedFromLCID = getBestSystemPropNameInParents(nodeType, n => n.NameOfDerivedFromLCID);
            nodeType.NameOfIsDerivedProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfIsDerivedProperty);
            nodeType.NameOfLCIDProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfLCIDProperty);
            nodeType.NameOfReadAccessProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfReadAccessProperty);
            nodeType.NameOfWriteAccessProperty = getBestSystemPropNameInParents(nodeType, n => n.NameOfWriteAccessProperty);
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
        if (types.Count > 1) throw new Exception("Multiple types of internal id property found in parents of " + nodeType.CodeName);
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
        if (types.Count > 1) throw new Exception("Multiple types of public id property found in parents of " + nodeType.CodeName);
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
        if (names.Count > 1) throw new Exception("Multiple names of system property found in parents of " + nodeType.CodeName);
        return null;
    }
    void initializeRelations() {
        foreach (var p in Properties.Values.Where(p => p.PropertyType == PropertyType.Relation)) {
            if (p is not RelationPropertyModel rp) throw new Exception("Relation property is not a RelationPropertyModel");
            if (!Relations.TryGetValue(rp.RelationId, out var relation)) {
                if (rp.RelationId != Guid.Empty) throw new Exception("Relation of property " + p.GetFullNameBaseType(this) + " not part of the datamodel, relation id: " + rp.RelationId);
                if (!tryFindMatchingOneToManyRelation(rp, out relation)) {
                    if (tryToAutoCreateOneToManyRelations(rp, out relation, out var reasonForNotCreating)) {
                        Relations.Add(relation.Id, relation);
                    } else {
                        throw new Exception("Unable to infer a relation of member \"" + p.GetFullNameBaseType(this) + "\". The type is either not supported or not part of the datamodel. If it is a relation property please specify it using an attribute or define it explicitly in the datamodel. " + reasonForNotCreating);
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
            throw new Exception("Unable to automatically identify relation. More than one relation fit property " + thisProperty.GetFullNameBaseType(this) + ". "
                + "Automatic matching is ambiguous, please specify relation specifically. Relations matching are: "
                + string.Join(", ", Relations.Values.Select(r => r.CodeName))
                );
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
            Id = relName.GenerateGuid(),
            AutoGenerated = true,
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
            Private = true,
            MinWordLength = 2,
        };
        props.Add(textIndex);
        var vectorIndex = new FloatArrayPropertyModel() {
            Id = NodeConstants.SystemVectorIndexPropertyId,
            CodeName = NodeConstants.SystemVectorIndexPropertyName,
            ExcludeFromTextIndex = true,
            Indexed = true,
            Private = true,
        };
        props.Add(vectorIndex);
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
                    ct.AllProperties.Add(p.Id, p);
                    ct.AllPropertiesByName.Add(p.CodeName, p);
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
            return property.Id;
        } else {
            throw new Exception("Unknown property: " + idString);
        }
    }
    public Dictionary<Guid, object> CreateDefaultValues(Guid nodeTypeId) {
        var values = new Dictionary<Guid, object>();
        foreach (var p in NodeTypes[nodeTypeId].AllProperties.Values) {
            values.Add(p.Id, p.GetDefaultValue());
        }
        return values;
    }

    // Helper Functions
    public NodeTypeModel FindFirstCommonBase(IEnumerable<Guid> nodeTypes) {
        if (nodeTypes == null || nodeTypes.Count() == 0) return NodeTypes[NodeConstants.BaseNodeTypeId];
        if (nodeTypes.Count() == 1) return NodeTypes[nodeTypes.First()];
        var candidates = new HashSet<Guid>();
        foreach (var nt in NodeTypes.Values) {
            bool implementsAllTypes = true;
            foreach (var id in nodeTypes) {
                if (!nt.ThisAndAllInheritedTypes.ContainsKey(id)) {
                    implementsAllTypes = false;
                    break;
                }
            }
            if (implementsAllTypes) candidates.Add(nt.Id);
        }
        if (candidates.Count() == 0) return NodeTypes[NodeConstants.BaseNodeTypeId];
        if (candidates.Count() == 1) return NodeTypes[candidates.First()];
        // if multiple possible, select one with the fewest inherited types:
        var bestId = candidates.OrderBy(c => NodeTypes[c].ThisAndAllInheritedTypes.Count()).First();
        return NodeTypes[bestId];

    }

    public void AddDatamodel(Datamodel dm) {
        if (dm == null) return;
        if (dm.HasInitialized()) throw new Exception("Cannot add initialized datamodel to another datamodel");
        foreach (var nt in dm.NodeTypes.Values) {
            if (NodeTypes.ContainsKey(nt.Id)) throw new Exception("Node type already exists in datamodel: " + nt.CodeName);
            NodeTypes.Add(nt.Id, nt);
        }
        foreach (var r in dm.Relations.Values) {
            if (Relations.ContainsKey(r.Id)) throw new Exception("Relation already exists in datamodel: " + r.CodeName);
            Relations.Add(r.Id, r);
        }
        foreach (var p in dm.Properties.Values) {
            if (Properties.ContainsKey(p.Id)) throw new Exception("Property already exists in datamodel: " + p.CodeName);
            Properties.Add(p.Id, p);
        }
    }
}