namespace WAF.Datamodels.Properties;

public class LongPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.Long; }
    public long DefaultValue { get; set; }
    public long MinValue { get; set; } = int.MinValue;
    public long MaxValue { get; set; } = int.MaxValue;
    public override object GetDefaultValue() => DefaultValue;
    public static long ForceValueType(object value, out bool changed) {
        if (value is long l) {
            changed = false;
            return l;
        }
        changed = true;
        if (value is null) return default;
        if (value is byte bt) return bt;
        if (value is int i) return i;
        if (value is decimal dec) return (long)dec;
        if (value is double d) return (long)d;
        if (value is float f) return (long)f;
        if (value is string s && int.TryParse(s, out var v)) return v;
        return default;
    }
    public override string GetDefaultValueAsCode() => DefaultValue.ToString();
    public override string? GetTextIndex(object value) => value.ToString();
}
