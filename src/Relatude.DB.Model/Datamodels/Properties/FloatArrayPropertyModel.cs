namespace Relatude.DB.Datamodels.Properties;

public class FloatArrayPropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.FloatArray; }
    public override object GetDefaultValue() => Array.Empty<float>();
    public override string? GetDefaultDeclaration() => "[]";
    public static float[] ForceValueType(object value, out bool changed) {
        if (value is float[]) {
            changed = false;
            return (float[])value;
        }
        if (value is IEnumerable<float> enm) {
            changed = true;
            return enm.ToArray();
        }
        changed = true;
        return [];
    }
    public override string GetDefaultValueAsCode() => "Array.Empty<byte>()";

    public static byte[] GetBytes(float[] value) {
        if (value == null || value.Length == 0) return Array.Empty<byte>();
        var bytes = new byte[value.Length * sizeof(float)];
        Buffer.BlockCopy(value, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] GetValue(byte[] bytes) {
        if (bytes == null || bytes.Length == 0) return Array.Empty<float>();
        if (bytes.Length % sizeof(float) != 0) throw new ArgumentException("Byte array length is not a multiple of float size.");
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
