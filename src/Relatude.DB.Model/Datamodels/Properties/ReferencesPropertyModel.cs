namespace Relatude.DB.Datamodels.Properties;

/// <summary>
/// An ordered, duplicate-preserving series of references to other nodes, stored as a Guid[] on the
/// node. Inherits the Guid[] value handling (binary format, coercion, indexing defaults) from
/// <see cref="GuidArrayPropertyModel"/> and adds the target-type metadata of
/// <see cref="ReferencePropertyModel"/>.
/// </summary>
public class ReferencesPropertyModel : GuidArrayPropertyModel {
    public override PropertyType PropertyType { get => PropertyType.References; }

    public List<Guid> NodeTypes { get; set; } = [];
    public List<string>? NodeTypesNames { get; set; }
    public IncludeTypeOptions IncludeTypes { get; set; } = IncludeTypeOptions.ThisTypeAndDescending;
    public ReferenceValueType ReferenceValueType { get; set; } = ReferenceValueType.Wrapper;

    // wrapper needs an initialized instance (the mapper calls Initialize on it); plain
    // collection shapes must stay null until preloaded: null means "leave stored value
    // unchanged" on save, while an empty collection clears the stored references
    public override string? GetDefaultDeclaration() => ReferenceValueType == ReferenceValueType.Wrapper ? "new()" : null;
}
