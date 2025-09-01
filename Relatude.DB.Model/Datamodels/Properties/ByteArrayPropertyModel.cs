namespace Relatude.DB.Datamodels.Properties;

public class ByteArrayPropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.ByteArray; }
    public override object GetDefaultValue() => Array.Empty<byte>();
    public override string? GetDefaultDeclaration() => "[]";
    public static byte[] ForceValueType(object value, out bool changed) {
        if (value is byte[]) {
            changed = false;
            return (byte[])value;
        }
        changed = true;
        return [];
    }
    public override string GetDefaultValueAsCode() => "Array.Empty<byte>()";
}
