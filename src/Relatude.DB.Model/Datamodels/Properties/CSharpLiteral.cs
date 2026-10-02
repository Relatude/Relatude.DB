using System.Globalization;
using System.Text;

namespace Relatude.DB.Datamodels.Properties;

/// <summary>
/// C# literals for values written into generated code (the mapper's defaults). Invariant culture and the
/// suffix each type needs, because a default typed into the admin UI ends up compiled: "1,5" or an
/// unsuffixed 1.5 for a decimal would stop the database from opening.
/// </summary>
internal static class CSharpLiteral {
    public static string String(string? value) {
        if (value == null) return "\"\"";
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value) {
            switch (c) {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\0': sb.Append("\\0"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || char.IsSurrogate(c) || c == (char)0x2028 || c == (char)0x2029 || c == '\u0085') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
    public static string Double(double value) {
        if (double.IsNaN(value)) return "double.NaN";
        if (double.IsPositiveInfinity(value)) return "double.PositiveInfinity";
        if (double.IsNegativeInfinity(value)) return "double.NegativeInfinity";
        if (value == double.MinValue) return "double.MinValue";
        if (value == double.MaxValue) return "double.MaxValue";
        return value.ToString("R", CultureInfo.InvariantCulture) + "d";
    }
    public static string Float(float value) {
        if (float.IsNaN(value)) return "float.NaN";
        if (float.IsPositiveInfinity(value)) return "float.PositiveInfinity";
        if (float.IsNegativeInfinity(value)) return "float.NegativeInfinity";
        if (value == float.MinValue) return "float.MinValue";
        if (value == float.MaxValue) return "float.MaxValue";
        return value.ToString("R", CultureInfo.InvariantCulture) + "f";
    }
    public static string Decimal(decimal value) {
        if (value == decimal.MinValue) return "decimal.MinValue";
        if (value == decimal.MaxValue) return "decimal.MaxValue";
        return value.ToString(CultureInfo.InvariantCulture) + "m";
    }
    public static string Long(long value) => value == long.MinValue ? "long.MinValue" : value.ToString(CultureInfo.InvariantCulture) + "L";
    public static string Int(int value) => value == int.MinValue ? "int.MinValue" : value.ToString(CultureInfo.InvariantCulture);
}
