namespace Relatude.DB.Datamodels.Properties;
public class BooleanPropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.Boolean; }
    public bool DefaultValue { get; set; }
    public override object GetDefaultValue() => DefaultValue;
    public static bool ForceValueType(object value, out bool changed) {
        if (value is bool v) {
            changed = false;
            return v;
        }
        changed = true;
        if (value is int i) return i != 0;
        if (value is long l) return l != 0;
        if (value is float f) return f != 0;
        if (value is decimal dec) return dec != 0;
        if (value is double d) return d != 0;
        if (value is byte bt) return bt != 0;
        if (value is string s) return s.Equals("true", StringComparison.CurrentCultureIgnoreCase) || s == "1" || s == "-1";
        return default;
    }
    public override string GetDefaultValueAsCode() => DefaultValue.ToString().ToLower();
}