namespace WAF.Datamodels.Properties;
public class FloatPropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.Float; }
    public float DefaultValue { get; set; }
    public float MinValue { get; set; } = float.MinValue;
    public float MaxValue { get; set; } = float.MaxValue;
    public override object GetDefaultValue() => DefaultValue;
    public static float ForceValueType(object value, out bool changed) {
        if (value is float f) {
            changed = false;
            return f;
        }
        changed = true;
        if (value is null) return default;
        if (value is byte) return (float)value;
        if (value is int i) return (float)i;
        if (value is long) return (float)value;
        if (value is byte) return (float)value;
        if (value is decimal) return (float)value;
        if (value is float) return (float)value;
        if (value is string && float.TryParse((string)value, out var v)) return v;
        return default;
    }
    public override string GetDefaultValueAsCode() => DefaultValue.ToString();
    public override string? GetTextIndex(object value) => value.ToString();
}
