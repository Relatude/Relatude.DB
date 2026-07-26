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

    public override string? GetDefaultDeclaration() => "new()";
}
