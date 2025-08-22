namespace WAF.Datamodels.Properties;
public class DateTimePropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.DateTime; }
    public DateTime DefaultValue { get; set; }
    public DateTime MinValue { get; set; } = DateTime.MinValue;
    public DateTime MaxValue { get; set; } = DateTime.MaxValue;
    public override object GetDefaultValue() => DefaultValue;
    public static DateTime ForceValueType(object value, out bool changed) {
        if (value is DateTime dt) {
            changed = false;
            if (dt.Kind != DateTimeKind.Utc)
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return dt;
        }
        changed = true;
        if (value is null) return default;
        //if (value is byte) return (decimal)value;
        if (value is long l) return new DateTime(l, DateTimeKind.Utc);
        //if (value is byte) return (decimal)value;
        //if (value is decimal) return (int)value;
        //if (value is double) return (int)value;
        //if (value is float) return (int)value;
        if (value is string sv) {
            if (DateTime.TryParse(sv, out var v)) {
                if (v.Kind != DateTimeKind.Utc) return DateTime.SpecifyKind(v, DateTimeKind.Utc);
                return v;
            } else if (long.TryParse(sv, out var lv)) {
                return new DateTime(lv, DateTimeKind.Utc);
            }
        }
        return default;
    }
    public override string GetDefaultValueAsCode() => $"new DateTime({DefaultValue.Ticks}, DateTimeKind.Utc)";
}
