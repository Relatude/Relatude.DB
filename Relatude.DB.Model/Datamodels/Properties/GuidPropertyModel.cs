namespace Relatude.DB.Datamodels.Properties;

public class GuidPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.Guid; }
    public Guid DefaultValue { get; set; }
    public override object GetDefaultValue() => DefaultValue;
    public static Guid ForceValueType(object value, out bool changed) {
        if (value is Guid v) {
            changed = false;
            return v;
        }
        changed = true;
        if (value is string && Guid.TryParse((string)value, out var g)) return g;
        return Guid.Empty;
    }
    public override string GetDefaultValueAsCode() => $"new Guid(\"{DefaultValue}\")";
}
