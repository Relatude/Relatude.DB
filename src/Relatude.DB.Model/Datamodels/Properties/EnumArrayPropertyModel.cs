namespace Relatude.DB.Datamodels.Properties;

/// <summary>
/// Multiple enum values per node, stored as an int[]. The user model declares a plain enum array
/// (e.g. Sizes[]); the CLR treats enum arrays and int[] as cast-identical, so no conversion or
/// copying is needed between the model surface and the stored value. Enum metadata (type name,
/// legal values and their names) is captured at model build time for code generation and facet
/// display names.
/// </summary>
public class EnumArrayPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.EnumArray; }
    public string? FullEnumTypeName { get; set; }
    public int[]? LegalValues { get; set; }
    public string[]? LegalValueNames { get; set; } // parallel to LegalValues
    public override string? GetDefaultDeclaration() => "[]";
    public override object GetDefaultValue() => Array.Empty<int>();
    public static int[] ForceValueType(object? value, out bool changed) {
        if (value is int[] vs) { // also matches enum arrays: the CLR treats them as int[]
            if (vs.GetType() == typeof(int[])) {
                changed = false;
                return vs;
            }
            // enum-typed array: reads fine as int[] but is rejected by primitive-only APIs like
            // Buffer.BlockCopy, so materialize a true int[] once here at the coercion boundary
            changed = true;
            var copy = new int[vs.Length];
            for (var n = 0; n < vs.Length; n++) copy[n] = vs[n];
            return copy;
        }
        changed = true;
        if (value is null) return Array.Empty<int>();
        if (value is int i) return new int[] { i };
        if (value is Enum e) return new int[] { Convert.ToInt32(e) };
        if (value is string s) return int.TryParse(s, out var i1) ? new int[] { i1 } : Array.Empty<int>();
        if (value is string[] strings) {
            var values = new List<int>(strings.Length);
            foreach (var str in strings) if (int.TryParse(str, out var i2)) values.Add(i2);
            return values.ToArray();
        }
        if (value is IEnumerable<int> enm) return enm.ToArray();
        return Array.Empty<int>();
    }
    // fixed 4 bytes per element, no count prefix (element count = bytes.Length / 4, like FloatArrayPropertyModel)
    public static byte[] GetBytes(int[] value) {
        if (value == null || value.Length == 0) return Array.Empty<byte>();
        if (value.GetType() != typeof(int[])) value = ForceValueType(value, out _); // enum-typed array: BlockCopy needs a true int[]
        var bytes = new byte[value.Length * 4];
        Buffer.BlockCopy(value, 0, bytes, 0, bytes.Length);
        return bytes;
    }
    public static int[] GetValue(byte[] bytes) {
        if (bytes == null || bytes.Length == 0) return Array.Empty<int>();
        if (bytes.Length % 4 != 0) throw new ArgumentException("Byte array length is not a multiple of int size.");
        var values = new int[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
    // emitted into generated model code, so it must be typed as the enum array, not int[]
    public override string GetDefaultValueAsCode() => "Array.Empty<" + FullEnumTypeName + ">()";
}
