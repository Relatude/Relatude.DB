using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.GraphQL.Schema;

/// <summary>
/// How a field's value is produced at execution time. Execution is data-driven off the datamodel,
/// so fields carry a source kind plus the relevant PropertyModel instead of resolver delegates.
/// </summary>
public enum FieldSource {
    // system fields available on every node (from INodeData)
    Id, DisplayName, CreatedUtc, ChangedUtc,
    // property-backed fields
    ScalarProperty,      // INodeData.TryGetValue + scalar conversion
    EnumProperty,        // int -> enum value name
    EnumArrayProperty,   // int[] -> enum value names
    FileProperty,        // FileValue -> FileInfo object (null when empty)
    RelationOne, RelationMany,     // IRelations.TryGetOneRelation / TryGetManyRelation
    ReferenceOne, ReferenceMany,   // IRelations.TryGetReference / TryGetReferences
    // root Query fields
    RootSingle, RootList,
    // fields of the generated <Type>Result wrapper
    WrapperItems, WrapperTotalCount, WrapperPageIndex, WrapperPageSize,
    // fields of the shared FileInfo type (source value is a FileValue)
    FileName, FileSize, FileWidth, FileHeight, FileContentType,
}

public sealed class GqlField {
    public required string Name { get; init; }
    public required GqlType Type { get; set; }
    public string? Description { get; set; }
    public FieldSource Source { get; init; }
    /// <summary>The datamodel property behind this field (property-backed sources only).</summary>
    public PropertyModel? Property { get; init; }
    /// <summary>Target node type for relation/reference/root fields.</summary>
    public NodeTypeModel? TargetNodeType { get; init; }
    public List<GqlArgument> Arguments { get; } = [];
    public GqlArgument? GetArgument(string name) {
        foreach (var a in Arguments) if (a.Name == name) return a;
        return null;
    }
}

public sealed class GqlArgument {
    public required string Name { get; init; }
    public required GqlType Type { get; init; }
    public string? Description { get; set; }
    public object? DefaultValue { get; init; }
    public bool HasDefaultValue { get; init; }
}
