using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

/// <summary>
/// An ordered multi-reference stored as a Guid[] on the node. Inherits the guid-array index and
/// facet machinery from <see cref="GuidArrayProperty"/> and adds the reference semantics of
/// <see cref="ReferenceProperty"/>: per-element target-type validation and facet buckets decorated
/// with the referenced node's display name.
/// </summary>
internal class ReferencesProperty : GuidArrayProperty {
    List<Guid> _nodeTypes;
    Dictionary<Guid, bool> _isTypeValidCache = new(); // pure memo; mutated only under the store's write lock
    IncludeTypeOptions _includeTypeOptions;
    public ReferencesProperty(ReferencesPropertyModel pm, Definition def) : base(pm, def) {
        _nodeTypes = pm.NodeTypes;
        _includeTypeOptions = pm.IncludeTypes;
    }
    public override PropertyType PropertyType => PropertyType.References;
    public override void ValidateValue(object value, INodeData node) {
        if (value is not Guid[] guids) throw new ArgumentException("Value must be a Guid array.");
        foreach (var guid in guids) {
            if (guid == Guid.Empty) continue; // tolerated, like the scalar reference's unset value
            validateElement(guid, node);
        }
    }
    void validateElement(Guid guid, INodeData node) {
        Guid suggestedTypeId;
        if (node.Id == guid) {
            suggestedTypeId = node.NodeType; // self-reference: node not yet in store
        } else {
            if (Definition.Store.TryGetNodeType(guid, out var typeId)) {
                suggestedTypeId = typeId;
            } else {
                throw new ArgumentException($"Property '{CodeName}' expects references to nodes of type '{string.Join(", ", _nodeTypes.Select(t => Definition.NodeTypes[t].CodeName))}', but the value '{guid}' is not a valid node.");
            }
        }
        if (_isTypeValidCache.TryGetValue(suggestedTypeId, out var isValid)) {
            if (isValid) return;
        } else {
            var suggestedType = Definition.NodeTypes[suggestedTypeId];
            foreach (var allowedTypeId in _nodeTypes) {
                switch (_includeTypeOptions) {
                    case IncludeTypeOptions.ThisTypeAndDescending:
                        if (suggestedType.Model.ThisAndAllInheritedTypes.ContainsKey(allowedTypeId)) {
                            _isTypeValidCache[suggestedTypeId] = true;
                            return;
                        }
                        break;
                    case IncludeTypeOptions.ThisTypeOnly:
                        if (allowedTypeId == suggestedTypeId) {
                            _isTypeValidCache[suggestedTypeId] = true;
                            return;
                        }
                        break;
                    case IncludeTypeOptions.DescendingTypesOnly:
                        if (allowedTypeId != suggestedTypeId && suggestedType.Model.ThisAndAllInheritedTypes.ContainsKey(allowedTypeId)) {
                            _isTypeValidCache[suggestedTypeId] = true;
                            return;
                        }
                        break;
                    default:
                        break;
                };
            }
            _isTypeValidCache[suggestedTypeId] = false;
        }
        throw new ArgumentException($"Property '{CodeName}' expects references to nodes of type '{string.Join(", ", _nodeTypes.Select(t => Definition.NodeTypes[t].CodeName))}', but the value '{guid}' is of type '{Definition.NodeTypes[suggestedTypeId].CodeName}'.");
    }
    public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
        var facets = base.GetDefaultFacets(given, ctx); // buckets are the referenced node guids
        foreach (var v in facets.Values) {
            if (v.ExplicitDisplayName != null || v.Value is not Guid guid) continue;
            v.DisplayName = displayNameOfNode(guid);
        }
        return facets;
    }
    string displayNameOfNode(Guid guid) {
        if (guid == Guid.Empty) return "(none)";
        var db = Definition.Store;
        if (db._guids.TryGetId(guid, out var id) && db._nodes.TryGet(id, out var node, out _)) {
            var sb = new System.Text.StringBuilder();
            Definition.Datamodel.NodeTypes[node.NodeType].BuildDisplayName(node, sb);
            if (sb.Length > 0) return sb.ToString();
        }
        return guid.ToString(); // referenced node gone (stale index value): fall back to the raw guid
    }
}
