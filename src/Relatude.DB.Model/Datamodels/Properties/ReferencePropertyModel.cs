namespace Relatude.DB.Datamodels.Properties;

public class ReferencePropertyModel : PropertyModel {

    public override bool ExcludeFromTextIndex { get; set; }

    public List<Guid> NodeTypes { get; set; } = [];
    public List<string>? NodeTypesNames { get; set; }
    public IncludeTypeOptions IncludeTypes { get; set; } = IncludeTypeOptions.ThisTypeAndDescending;

    public Guid DefaultValue { get; set; }
    public override PropertyType PropertyType { get => PropertyType.Reference; }

    public static Guid ForceValueType(object? value, out bool changed) {
        if (value is Guid v) {
            changed = false;
            return v;
        }
        changed = true;
        if (value is string && Guid.TryParse((string)value, out var g)) return g;
        return Guid.Empty;
    }

    public override object GetDefaultValue() => DefaultValue;
    // generated model declarations must initialize the wrapper: the mapper calls Initialize on the
    // existing instance and would NRE on an uninitialized property
    public override string? GetDefaultDeclaration() => "new()";
    public override string GetDefaultValueAsCode() =>
        DefaultValue == Guid.Empty ? "Guid.Empty" : "new Guid(\"" + DefaultValue + "\")";

}
