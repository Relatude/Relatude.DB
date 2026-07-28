using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Datamodels;

namespace Relatude.DB.GraphQL.Schema;

/// <summary>The standard scalar instances of a schema.</summary>
public sealed class GqlScalars {
    public required GqlScalarType Int { get; init; }
    public required GqlScalarType Float { get; init; }
    public required GqlScalarType String { get; init; }
    public required GqlScalarType Boolean { get; init; }
    public required GqlScalarType Id { get; init; }
    public required GqlScalarType DateTime { get; init; }
    public required GqlScalarType Long { get; init; }
    public required GqlScalarType Decimal { get; init; }
}

/// <summary>
/// An immutable GraphQL schema generated from a Relatude.DB datamodel.
/// Built once by <see cref="SchemaBuilder"/>; safe for concurrent reads afterwards.
/// </summary>
public sealed class GqlSchema {
    public required Datamodel Datamodel { get; init; }
    public required GqlObjectType QueryType { get; init; }
    public required GqlInterfaceType NodeInterface { get; init; }
    public required GqlScalars Scalars { get; init; }
    /// <summary>All named types by GraphQL name (excluding introspection meta types).</summary>
    public Dictionary<string, GqlNamedType> Types { get; } = new(StringComparer.Ordinal);
    /// <summary>Concrete GraphQL object type per exposed datamodel node type.</summary>
    public Dictionary<Guid, GqlObjectType> ObjectTypesByNodeTypeId { get; } = [];
    /// <summary>The type used when *referring* to a node type in field positions:
    /// its interface type (datamodel interfaces and classes with descendants) or its object type.</summary>
    public Dictionary<Guid, GqlNamedType> ReferenceTypesByNodeTypeId { get; } = [];

    public bool TryGetType(string name, [NotNullWhen(true)] out GqlNamedType? type) => Types.TryGetValue(name, out type);

    /// <summary>Resolves the GraphQL object type for a node instance's runtime datamodel type.</summary>
    public bool TryGetObjectType(Guid nodeTypeId, [NotNullWhen(true)] out GqlObjectType? type)
        => ObjectTypesByNodeTypeId.TryGetValue(nodeTypeId, out type);

    /// <summary>True if <paramref name="conditionName"/> (a fragment type condition) applies to the given runtime object type.</summary>
    public static bool TypeConditionMatches(GqlObjectType runtimeType, string conditionName) {
        if (runtimeType.Name == conditionName) return true;
        foreach (var i in runtimeType.Interfaces) if (i.Name == conditionName) return true;
        return false;
    }
}
