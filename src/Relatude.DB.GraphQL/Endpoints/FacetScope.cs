using System.Collections.Concurrent;
using Relatude.DB.Datamodels;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Endpoints;

/// <summary>
/// What an endpoint's facet search may look at: the node types the schema has a list root field for, and for each the
/// properties behind its fields - its exposed subtypes' fields too, since a search of a type finds their nodes as well.
/// It is read off the schema, so whatever keeps a type or a property out of the endpoint (the selection, a property
/// list, the system types, the type filter, a relation to a type that is not exposed) keeps it out of the facets.
/// </summary>
public sealed class GraphQLFacetScope {
    readonly GqlSchema _schema;
    readonly Dictionary<Guid, HashSet<Guid>> _byType = [];
    readonly ConcurrentDictionary<Guid, HashSet<Guid>> _shownBy = new();

    /// <summary>The searchable types: in the order they were picked in Selected mode, in the schema's order otherwise.</summary>
    public IReadOnlyList<NodeTypeModel> Types { get; }

    GraphQLFacetScope(GqlSchema schema) {
        _schema = schema;
        var types = schema.QueryType.Fields
            .Where(f => f.Source == FieldSource.RootList && f.TargetNodeType != null)
            .Select(f => f.TargetNodeType!)
            .DistinctBy(t => t.Id)
            .ToList();
        // picked types come in the order they were picked, so the endpoint's owner decides which one the page opens on
        if (schema.Definition.Mode == GraphQLEndpointMode.Selected) {
            var picked = schema.Definition.Types.Select((t, i) => (t.NodeTypeId, i)).GroupBy(p => p.NodeTypeId).ToDictionary(g => g.Key, g => g.First().i);
            types = [.. types.OrderBy(t => picked.TryGetValue(t.Id, out var at) ? at : int.MaxValue)];
        }
        Types = types;
        foreach (var type in types) {
            var properties = new HashSet<Guid>();
            foreach (var exposed in types) {
                if (!type.ThisAndDescendingTypes.ContainsKey(exposed.Id)) continue;
                if (schema.ReferenceTypesByNodeTypeId.TryGetValue(exposed.Id, out var named) && named is IGqlCompositeType composite) addFields(composite, properties);
                if (schema.ObjectTypesByNodeTypeId.TryGetValue(exposed.Id, out var objectType)) addFields(objectType, properties);
            }
            _byType[type.Id] = properties;
        }
    }

    static void addFields(IGqlCompositeType type, HashSet<Guid> into) {
        foreach (var f in type.Fields) if (f.Property != null) into.Add(f.Property.Id);
    }

    public static GraphQLFacetScope Build(GqlSchema schema) => new(schema);

    /// <summary>Whether the facet search may be run on this type.</summary>
    public bool Exposes(Guid typeId) => _byType.ContainsKey(typeId);

    /// <summary>The properties a search of the type may facet, colour, stack and sort by; empty for a type not exposed.</summary>
    public IReadOnlySet<Guid> PropertiesOf(Guid typeId) => _byType.TryGetValue(typeId, out var set) ? set : _none;
    static readonly HashSet<Guid> _none = [];

    /// <summary>
    /// Whether a node of the given type is shown with the property by the endpoint: the type it is seen as (itself, or
    /// its nearest exposed ancestor) has a field for it. What a node's picture is allowed to be taken from.
    /// </summary>
    public bool Shows(Guid nodeTypeId, Guid propertyId) => _shownBy.GetOrAdd(nodeTypeId, id => {
        var set = new HashSet<Guid>();
        if (_schema.ObjectTypesByNodeTypeId.TryGetValue(id, out var type)) addFields(type, set);
        return set;
    }).Contains(propertyId);

    /// <summary>Whether a node of the given type is seen through the endpoint at all.</summary>
    public bool ShowsType(Guid nodeTypeId) => _schema.ObjectTypesByNodeTypeId.ContainsKey(nodeTypeId);
}
