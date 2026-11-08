namespace Relatude.DB.Datamodels.Properties;

/// <summary>
/// The CLR shape backing a Reference/References property. Wrapper is the Reference&lt;T&gt; /
/// References&lt;T&gt; helper class; the other values describe plain node-typed members
/// (a single node object, or an array/list/collection/enumerable of node objects).
/// </summary>
public enum ReferenceValueType {
    Wrapper = 0, // Reference<T> or References<T>
    Object,      // plain node-typed member (single reference)
    Array,
    List,
    Collection,
    Enumerable,
}
public class ReferencePropertyModel : PropertyModel {

    public override bool ExcludeFromTextIndex { get; set; }

    public List<Guid> NodeTypes { get; set; } = [];
    public List<string>? NodeTypesNames { get; set; }
    public IncludeTypeOptions IncludeTypes { get; set; } = IncludeTypeOptions.ThisTypeAndDescending;
    public ReferenceValueType ReferenceValueType { get; set; } = ReferenceValueType.Wrapper;
    /// <summary>How an editing interface shows the node referred to; see <see cref="RelationPropertyModel.Display"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public DisplayMode Display { get; set; }
    /// <summary>
    /// Whether an editing interface may show the node referred to as a form that opens in place
    /// (<see cref="DisplayMode.Inline"/>), where it is edited and saved with the node holding the reference. Off, as it
    /// is when nothing is said, the node is edited in an editor of its own. Presentation only, as Display is.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowInlineEditing { get; set; }

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
    // existing instance and would NRE on an uninitialized property. Plain object-shaped
    // references stay null until preloaded, so they get no initializer.
    public override string? GetDefaultDeclaration() => ReferenceValueType == ReferenceValueType.Wrapper ? "new()" : null;
    public override string GetDefaultValueAsCode() => GetValueAsCode(DefaultValue);
    public override string GetValueAsCode(object? value) =>
        value is not Guid g || g == Guid.Empty ? "Guid.Empty" : "new Guid(\"" + g + "\")";

}
