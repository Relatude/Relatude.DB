namespace Relatude.DB.Datamodels.Properties;

public class GuidPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.Guid; }
    public Guid DefaultValue { get; set; }
    /// <summary>
    /// What the Guid is the id of (a node type, a property, a relation), so an editing interface can offer them by name.
    /// Presentation only: out of the store's checksum, and out of the JSON when AnyGuid.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public GuidValueType GuidType { get; set; }
    public override object GetDefaultValue() => DefaultValue;
    public static Guid ForceValueType(object? value, out bool changed) {
        if (value is Guid v) {
            changed = false;
            return v;
        }
        changed = true;
        if (value is string && Guid.TryParse((string)value, out var g)) return g;
        return Guid.Empty;
    }
    public override string GetDefaultValueAsCode() => GetValueAsCode(DefaultValue);
    public override string GetValueAsCode(object? value) => $"new Guid(\"{(value is Guid g ? g : Guid.Empty)}\")";
}
