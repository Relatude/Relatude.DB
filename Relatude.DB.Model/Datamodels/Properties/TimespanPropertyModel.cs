namespace Relatude.DB.Datamodels.Properties {
    public class TimeSpanPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
        public override bool ExcludeFromTextIndex { get; set; } = true;
        public override PropertyType PropertyType { get => PropertyType.TimeSpan; }
        public TimeSpan DefaultValue { get; set; }
        public TimeSpan MinValue { get; set; } = TimeSpan.MinValue;
        public TimeSpan MaxValue { get; set; } = TimeSpan.MaxValue;
        public override object GetDefaultValue() => DefaultValue;
        public static TimeSpan ForceValueType(object value, out bool changed) {
            if (value is TimeSpan t) {
                changed = false;
                return t;
            }
            changed = true;
            if (value is null) return default;
            if (value is long l) return new TimeSpan(l);
            if (value is string s && TimeSpan.TryParse(s, out var v)) return v;
            return default;
        }
        public override string GetDefaultValueAsCode() => $"new TimeSpan({DefaultValue.Ticks})";
    }
}
