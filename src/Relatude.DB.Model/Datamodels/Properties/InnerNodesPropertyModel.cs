using Relatude.DB.Common;
namespace Relatude.DB.Datamodels.Properties;

public enum InnerNodesValueType {
    Array,
    List,
    InnerNodeList,
    InnerNodeMap,
}

public class InnerNodesPropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; } = false;

    public List<Guid>? InnerNodeTypes { get; set; }
    public List<string>? InnerNodeTypesNames { get; set; }
    public bool IncludeDescendingTypes { get; set; }

    public Guid KeyProperty { get; set; } = Guid.Empty;
    public string? KeyPropertyName { get; set; }

    public bool PrependOnAdd { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.InnerNodes; }
    public override string? GetDefaultDeclaration() => "[]";
    public override object? GetDefaultValue() => null;
    public override bool IsReferenceTypeAndMustCopy() => true;
    public static FileValue ForceValueType(object value, out bool changed) {
        if (value is FileValue fileValue) {
            changed = false;
            return fileValue;
        }
        changed = true;
        return FileValue.Empty;
    }
    public static FileValue GetValue(byte[] bytes) {
        return FileValue.FromBytes(bytes);
    }
    public static byte[] GetBytes(FileValue value) {
        return value.ToBytes();
    }
    public override string GetDefaultValueAsCode() => typeof(FileValue).FullName + ".Empty";
    public override string? GetTextIndex(object value) => ForceValueType(value, out _).Name;
}
