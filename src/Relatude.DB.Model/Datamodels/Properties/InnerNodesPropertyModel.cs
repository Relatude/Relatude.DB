using Relatude.DB.Common;
using System.Text.Json.Serialization;
namespace Relatude.DB.Datamodels.Properties;

public enum InnerNodesValueType {
    //Array,
    //List,
    InnerNodeList,
    InnerNodeMap,
}
public enum IncludeTypeOptions {
    ThisTypeAndDescending,
    ThisTypeOnly,
    DescendingTypesOnly
}

public class InnerNodesPropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; } = false;
    public InnerNodesValueType InnerNodesValueType { get; set; }
    public List<Guid> InnerNodeTypes { get; set; } = [];
    public List<string>? InnerNodeTypesNames { get; set; }
    public IncludeTypeOptions IncludeTypes { get; set; } = IncludeTypeOptions.ThisTypeAndDescending;

    public Guid KeyProperty { get; set; } = Guid.Empty;
    public string? KeyPropertyName { get; set; }
    [JsonIgnore]
    public Type? _keyTypeInCodeModelForLaterChecks;


    public bool PrependOnAdd { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.InnerNodes; }

    public override string? GetDefaultDeclaration() => "[]";
    public override object? GetDefaultValue() => null;
    public override bool IsReferenceTypeAndMustCopy() => true;
    public static IInnerNodeDataMap ForceValueType(object value, out bool changed) {
        if (value is IInnerNodeDataMap ndm) {
            changed = false;
            return ndm;
        }
        changed = true;
        return null!;
    }
    public override string GetDefaultValueAsCode() => typeof(FileValue).FullName + ".Empty";
    public override string? GetTextIndex(object value) => "";

    public IInnerNodeDataMap CreateInnerNodeDataMap(ICollection<NodeData> nodes) {
        if (_cachedKeyPropType == null) throw new Exception("Key property type is not calculated. ");
        return _cachedKeyPropType switch {
            PropertyType.Boolean => new InnerNodeDataMap<bool>(KeyProperty, nodes),
            PropertyType.Integer => new InnerNodeDataMap<int>(KeyProperty, nodes),
            PropertyType.String => new InnerNodeDataMap<string>(KeyProperty, nodes),
            PropertyType.Double => new InnerNodeDataMap<double>(KeyProperty, nodes),
            PropertyType.Float => new InnerNodeDataMap<float>(KeyProperty, nodes),
            PropertyType.Decimal => new InnerNodeDataMap<decimal>(KeyProperty, nodes),
            PropertyType.DateTime => new InnerNodeDataMap<DateTime>(KeyProperty, nodes),
            PropertyType.TimeSpan => new InnerNodeDataMap<TimeSpan>(KeyProperty, nodes),
            PropertyType.Guid => new InnerNodeDataMap<Guid>(KeyProperty, nodes),
            PropertyType.Long => new InnerNodeDataMap<long>(KeyProperty, nodes),
            PropertyType.DateTimeOffset => new InnerNodeDataMap<DateTimeOffset>(KeyProperty, nodes),
            _ => throw new Exception("Key property " + KeyProperty + " for inner nodes property " + CodeName + " has unsupported type " + _cachedKeyPropType)
        };
    }
    PropertyType? _cachedKeyPropType;
    public Type GetKeyTypeOfPropertyIfPossible(Datamodel dm) {
        if (KeyProperty == InnerNodeDataMap<object>.PropertyIdNodeGuidId) {
            _cachedKeyPropType = PropertyType.Guid;
            return typeof(Guid);
        } else if (KeyProperty == InnerNodeDataMap<object>.PropertyIdNodeIntId) {
            _cachedKeyPropType = PropertyType.Integer;
            return typeof(int);
        } else {
            var bestCommonBase = dm.FindFirstCommonBase(InnerNodeTypes);
            if (!bestCommonBase.AllProperties.TryGetValue(KeyProperty, out var keyPropDef))
                throw new Exception("InnerNodes property " + GetFullNameBaseType(dm) + " refers to a key property that is not found in the common base type of the inner node types: " + KeyProperty);
            var keyType = keyPropDef.PropertyType;
            _cachedKeyPropType = keyType;
            return keyType switch {
                PropertyType.Boolean => typeof(bool),
                PropertyType.Integer => typeof(int),
                PropertyType.String => typeof(string),
                PropertyType.Double => typeof(double),
                PropertyType.Float => typeof(float),
                PropertyType.Decimal => typeof(decimal),
                PropertyType.DateTime => typeof(DateTime),
                PropertyType.TimeSpan => typeof(TimeSpan),
                PropertyType.Guid => typeof(Guid),
                PropertyType.Long => typeof(long),
                PropertyType.DateTimeOffset => typeof(DateTimeOffset),
                _ => throw new Exception("Key property " + KeyProperty + " for inner nodes property " + GetFullNameBaseType(dm) + " has unsupported type " + keyType)
            };
        }
    }

}
