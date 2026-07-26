namespace Relatude.DB.Datamodels.Properties;
public class GuidArrayPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.GuidArray; }
    public override string? GetDefaultDeclaration() => "[]";
    public override object GetDefaultValue() => Array.Empty<Guid>();
    public static Guid[] ForceValueType(object? value, out bool changed) {
        if (value is Guid[] vs) {
            changed = false;
            return vs;
        }
        changed = true;
        if (value is null) return Array.Empty<Guid>();
        if (value is Guid g) return new Guid[] { g };
        if (value is string s) return Guid.TryParse(s, out var g1) ? new Guid[] { g1 } : Array.Empty<Guid>();
        if (value is string[] strings) {
            var values = new List<Guid>(strings.Length);
            foreach (var str in strings) if (Guid.TryParse(str, out var g2)) values.Add(g2);
            return values.ToArray();
        }
        if (value is IEnumerable<Guid> enm) return enm.ToArray();
        return Array.Empty<Guid>();
    }
    // fixed 16 bytes per element, no count prefix (element count = bytes.Length / 16, like FloatArrayPropertyModel)
    public static byte[] GetBytes(Guid[] value) {
        if (value == null || value.Length == 0) return Array.Empty<byte>();
        var bytes = new byte[value.Length * 16];
        for (int i = 0; i < value.Length; i++) value[i].TryWriteBytes(bytes.AsSpan(i * 16, 16));
        return bytes;
    }
    public static Guid[] GetValue(byte[] bytes) {
        if (bytes == null || bytes.Length == 0) return Array.Empty<Guid>();
        if (bytes.Length % 16 != 0) throw new ArgumentException("Byte array length is not a multiple of Guid size.");
        var values = new Guid[bytes.Length / 16];
        for (int i = 0; i < values.Length; i++) values[i] = new Guid(bytes.AsSpan(i * 16, 16));
        return values;
    }
    public override string GetDefaultValueAsCode() => "Array.Empty<Guid>()";
}
