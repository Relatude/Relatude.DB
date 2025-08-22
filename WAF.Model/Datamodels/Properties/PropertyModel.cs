using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace WAF.Datamodels.Properties;
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
            //case PropertyType.Collection:
            //    break;
            //case PropertyType.DataObject:
            //    break;
            //case PropertyType.FacetCollection:
            //    break;
            //case PropertyType.FacetNumberRange:
            //    break;
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

}
