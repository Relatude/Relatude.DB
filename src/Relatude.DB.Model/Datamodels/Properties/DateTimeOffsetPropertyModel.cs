namespace Relatude.DB.Datamodels.Properties;
public class DateTimeOffsetPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.DateTimeOffset; }
    public DateTimeOffset DefaultValue { get; set; }
    public DateTimeOffset MinValue { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset MaxValue { get; set; } = DateTimeOffset.MaxValue;
    public override object GetDefaultValue() => DefaultValue;
    public static DateTimeOffset ForceValueType(object value, out bool changed) {
        if (value is DateTimeOffset dt) {
            changed = false;
            return dt;
        }
        changed = true;
        if (value is null) return default;
        //if (value is byte) return (decimal)value;
        if (value is long l) return new DateTimeOffset(l, TimeSpan.Zero);
        //if (value is byte) return (decimal)value;
        //if (value is decimal) return (int)value;
        //if (value is double) return (int)value;
        //if (value is float) return (int)value;
        if (value is string sv) {
            if (DateTimeOffset.TryParse(sv, out var v)) {
                return v;
            }
            if (long.TryParse(sv, out var lv)) {
                return new DateTimeOffset(lv, TimeSpan.Zero);
            }
        }
        return default;
    }
    public override string GetDefaultValueAsCode() => 
        $"new DateTimeOffset({DefaultValue.Ticks}, new TimeSpan({DefaultValue.Offset.Ticks}))";
}
