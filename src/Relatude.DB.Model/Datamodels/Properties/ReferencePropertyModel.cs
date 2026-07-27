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
    public override string GetDefaultValueAsCode() =>
        DefaultValue == Guid.Empty ? "Guid.Empty" : "new Guid(\"" + DefaultValue + "\")";

}
