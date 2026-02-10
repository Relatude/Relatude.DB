using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Relatude.DB.Datamodels.Properties;

public interface IPropertyModelUniqueContraints {
    public bool UniqueValues { get; set; }
}
public interface IScalarProperty {
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
}
public enum IndexStorageType {
    Default,
    Memory,
    Persisted,
}
public abstract class PropertyModel {
    public Guid Id { get; set; }
    public Guid NodeType { get; set; }
    public bool Indexed { get; set; }
    public bool CultureSensitive { get; set; }
    public IndexStorageType IndexType { get; set; }
    public bool DisplayName { get; set; }
    public bool UniqueValues { get; set; }
    public abstract bool ExcludeFromTextIndex { get; set; }
    public string CodeName { get; set; } = string.Empty;
    public abstract string GetDefaultValueAsCode();
    public abstract PropertyType PropertyType { get; }
    public int IndexBoost { get; set; } = 0;
    public Guid ReadAccess { get; set; }
    public Guid WriteAccess { get; set; }
    public bool CultureSpecific { get; set; }
    public abstract object GetDefaultValue();
    public virtual string? GetDefaultDeclaration() => string.Empty;
    public bool Private { get; set; }
    public virtual bool IsReferenceTypeAndMustCopy() => false;
    public bool TryParse(object anyFormat, [MaybeNullWhen(false)] out object value) {
        switch (PropertyType) {
            case PropertyType.Any:
                break;
            case PropertyType.Boolean:
                break;
            case PropertyType.Double:
                if (double.TryParse(anyFormat.ToString(), CultureInfo.InvariantCulture, out var valueDouble)) {
                    value = valueDouble;
                    return true;
                }
                break;
            case PropertyType.Integer:
                if (int.TryParse(anyFormat.ToString(), out var valueInt)) {
                    value = valueInt;
                    return true;
                }
                break;
            case PropertyType.String:
                var valueString = anyFormat.ToString();
                if (valueString != null) {
                    value = valueString;
                    return true;
                }
                break;
            case PropertyType.StringArray:
                break;
            case PropertyType.Relation:
                break;
            default:
                break;
        }
        value = null;
        return false;
    }

    internal string GetFullNameBaseType(Datamodel m) {
        if (m.NodeTypes.TryGetValue(NodeType, out var t)) return GetFullNameAnyType(t);
        if (NodeType == Guid.Empty) throw new Exception("Nodetype of property " + CodeName + " is empty. ");
        throw new Exception("Nodetype of property " + CodeName + " is empty. NodeType ID is " + NodeType);
    }
    internal string GetFullNameAnyType(NodeTypeModel t) {
        return t.CodeName + "." + CodeName;
    }
    public override string ToString() {
        return CodeName;
    }

    internal string GetFullName(Datamodel m) {
        if (m.NodeTypes.TryGetValue(NodeType, out var t)) return $"{t.FullName}.{CodeName}";
        if (NodeType == Guid.Empty) throw new Exception("Nodetype of property " + CodeName + " is empty. ");
        throw new Exception("Nodetype of property " + CodeName + " is empty. NodeType ID is " + NodeType);
    }

    public virtual string? GetTextIndex(object value) => null;
    public virtual string? GetSemanticIndex(object value) => GetTextIndex(value);

    public static T ForceValueAnyType<T>(object value, PropertyType propertyType, out bool changed) where T : notnull {
        switch (propertyType) {
            case PropertyType.Boolean: return (T)(object)BooleanPropertyModel.ForceValueType(value, out changed);
            case PropertyType.Integer: return (T)(object)IntegerPropertyModel.ForceValueType(value, out changed);
            case PropertyType.String: return (T)(object)StringPropertyModel.ForceValueType(value, out changed);
            case PropertyType.StringArray: return (T)(object)StringArrayPropertyModel.ForceValueType(value, out changed);
            case PropertyType.Double: return (T)(object)DoublePropertyModel.ForceValueType(value, out changed);
            case PropertyType.Float: return (T)(object)FloatPropertyModel.ForceValueType(value, out changed);
            case PropertyType.Decimal: return (T)(object)DecimalPropertyModel.ForceValueType(value, out changed);
            case PropertyType.DateTime: return (T)(object)DateTimePropertyModel.ForceValueType(value, out changed);
            case PropertyType.TimeSpan: return (T)(object)TimeSpanPropertyModel.ForceValueType(value, out changed);
            case PropertyType.Guid: return (T)(object)GuidPropertyModel.ForceValueType(value, out changed);
            case PropertyType.Long: return (T)(object)LongPropertyModel.ForceValueType(value, out changed);
            case PropertyType.ByteArray: return (T)(object)ByteArrayPropertyModel.ForceValueType(value, out changed);
            case PropertyType.File: return (T)(object)FilePropertyModel.ForceValueType(value, out changed);
            case PropertyType.FloatArray: return (T)(object)FloatArrayPropertyModel.ForceValueType(value, out changed);
            case PropertyType.DateTimeOffset: return (T)(object)DateTimeOffsetPropertyModel.ForceValueType(value, out changed);
            case PropertyType.Relation:
            case PropertyType.Any:
            default:
                throw new NotImplementedException("ForceValueType is not implemented for property type " + propertyType);
        }

    }
}
