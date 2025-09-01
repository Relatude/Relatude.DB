namespace Relatude.DB.Datamodels.Properties;
public class DoublePropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.Double; }
    public double DefaultValue { get; set; }
    public double MinValue { get; set; } = double.MinValue;
    public double MaxValue { get; set; } = double.MaxValue;
    public override object GetDefaultValue() => DefaultValue;
    public static double ForceValueType(object value, out bool changed) {
        if (value is double d) {
            changed = false;
            return d;
        }
        changed = true;
        if (value is null) return default;
        if (value is byte) return (double)value;
        if (value is int) return (double)value;
        if (value is long) return (double)value;
        if (value is byte) return (double)value;
        if (value is decimal) return (double)value;
        if (value is float) return (double)value;
        if (value is string && double.TryParse((string)value, out var v)) return v;
        return default;
    }
    public override string GetDefaultValueAsCode() => DefaultValue.ToString();
    public override string? GetTextIndex(object value) => value.ToString();
}
