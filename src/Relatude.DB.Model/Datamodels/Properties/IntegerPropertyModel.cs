namespace Relatude.DB.Datamodels.Properties;
public class IntegerPropertyModel : PropertyModel, IPropertyModelUniqueContraints, IScalarProperty {
    public override bool ExcludeFromTextIndex { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.Integer; }
    public int DefaultValue { get; set; }
    public bool IsEnum { get; set; }
    public string? FullEnumTypeName { get; set; }
    public int[]? LegalValues { get; set; }
    public int MinValue { get; set; } = int.MinValue;
    public int MaxValue { get; set; } = int.MaxValue;
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }

    public override object GetDefaultValue() => DefaultValue;
    public static int ForceValueType(object value, out bool changed) {
        if (value is int i) {
            changed = false;
            return i;
        }
        changed = true;
        if (value is Enum e) return Convert.ToInt32(e);
        if (value is null) return default;
        if (value is byte bt) return bt;
        if (value is long l) return (int)l;
        if (value is decimal dec) return (int)dec;
        if (value is double d) return (int)d;
        if (value is float f) return (int)f;
        if (value is string s && int.TryParse(s, out var v)) return v;
        return default;
    }
    public override string GetDefaultValueAsCode() => DefaultValue.ToString();
    public override string? GetTextIndex(object value) => value.ToString();
}
