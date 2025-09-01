using System.Text;
namespace Relatude.DB.Datamodels.Properties;
public class StringArrayPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.StringArray; }
    public override string? GetDefaultDeclaration() => "[]";
    public override object GetDefaultValue() => Array.Empty<string>();
    public static string[] ForceValueType(object value, out bool changed) {
        if (value is string[] vs) {
            changed = false;
            return vs;
        }
        changed = true;
        if (value is null) return Array.Empty<string>();
        if (value is string s) return new string[] { s };
        return Array.Empty<string>();
    }
    public static string[] GetValue(byte[] bytes) {
        MemoryStream s = new MemoryStream(bytes);
        var count = readInt(s);
        var values = new string[count];
        for (int i = 0; i < count; i++) values[i] = readString(s);
        return values;
    }
    public static byte[] GetBytes(string[] v) {
        var s = new MemoryStream();
        writeInt(s, v.Length);
        foreach (var str in v) writeString(s, str);
        return s.ToArray();
    }
    static int readInt(Stream s) {
        byte[] b = new byte[4];
        s.Read(b, 0, 4);
        return BitConverter.ToInt32(b, 0);
    }
    static string readString(Stream s) {
        var length = readInt(s);
        var bs = new byte[length];
        s.Read(bs, 0, length);
        return RelatudeDBGlobals.Encoding.GetString(bs);
    }
    static void writeInt(Stream s, int v) {
        s.Write(BitConverter.GetBytes(v), 0, 4);
    }
    static void writeString(Stream s, string v) {
        var bytes = RelatudeDBGlobals.Encoding.GetBytes(v);
        writeInt(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }
    public override string GetDefaultValueAsCode() => "Array.Empty<string>()";
    public override string? GetTextIndex(object value) {
        var values = ForceValueType(value, out _);
        if (values.Length == 0) return null;
        StringBuilder sb = new StringBuilder();
        foreach (var v in values) {
            if (sb.Length > 0) sb.Append(" ");
            sb.Append(v);
        }
        return sb.ToString();
    }
}
