using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class ReferenceProperty : ValueProperty<Guid> {
    List<Guid> _nodeTypes;
    Dictionary<Guid, bool> _isTypeValidCache = new();
    IncludeTypeOptions _includeTypeOptions;
    public ReferenceProperty(ReferencePropertyModel pm, Definition def) : base(pm, def) {
        DefaultValue = pm.DefaultValue;
        _nodeTypes = pm.NodeTypes;
        _includeTypeOptions = pm.IncludeTypes;
    }
    protected override void WriteValue(Guid v, IAppendStream stream) => stream.WriteGuid(v);
    protected override Guid ReadValue(IReadStream stream) => stream.ReadGuid();
    public Guid DefaultValue;
    public override PropertyType PropertyType => PropertyType.Reference;
    public override void ValidateValue(object value, INodeData node) {
        if (value is not Guid guid) throw new ArgumentException("Value must be a Guid.");
        if (guid == Guid.Empty) return;
        // validate type:
        var id = Definition.Store.GetId(guid);
        Guid suggestedTypeId;
        if (node.Id == guid) {
            suggestedTypeId = node.NodeType;
        } else {
            if(Definition.Store.TryGetNodeType(guid, out var typeId)) {
                suggestedTypeId = typeId;
            } else {
                throw new ArgumentException($"Property '{CodeName}' expects a reference to a node of type '{string.Join(", ", _nodeTypes.Select(t => Definition.NodeTypes[t].CodeName))}', but the provided value is not a valid node.");
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
                        _nodeTypes.Contains(suggestedTypeId);
                        break;
                    case IncludeTypeOptions.DescendingTypesOnly:
                        if (suggestedType.Model.ThisAndAllInheritedTypes.ContainsKey(allowedTypeId)) {
                            _isTypeValidCache[suggestedTypeId] = allowedTypeId != suggestedTypeId;
                            return;
                        }
                        break;
                    default:
                        break;
                };
            }
            _isTypeValidCache[suggestedTypeId] = false;
        }
        throw new ArgumentException($"Property '{CodeName}' expects a reference to a node of type '{string.Join(", ", _nodeTypes.Select(t => Definition.NodeTypes[t].CodeName))}', but the provided value is of type '{Definition.NodeTypes[suggestedTypeId].CodeName}'.");
    }
    public static object GetValue(byte[] bytes) => new Guid(bytes);
    public override bool SatisfyValueRequirement(object? value1, object? value2, ValueRequirement requirement) {
        var v1 = (Guid)value1!;
        var v2 = (Guid)value2!;
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            _ => throw new NotSupportedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is Guid g1 && v2 is Guid g2) return g1 == g2;
        return false;
    }
}

