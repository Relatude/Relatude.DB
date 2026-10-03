using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL.Schema;
using Relatude.DB.Query.Data;

namespace Relatude.DB.GraphQL.Endpoints;

/// <summary>A stored node the explorer can fill ids with, and its display name.</summary>
public sealed record GraphQLSample(string Id, string? Name);

/// <summary>
/// What the explorer needs to show an endpoint: the schema described type by type, one stored node per type (so ids
/// in the builder and the guide are real), the guide's examples, and what the schema builder had to leave out.
/// </summary>
public sealed record GraphQLExplorerData(SchemaDescription.Info Schema, Dictionary<string, GraphQLSample> Samples, GraphQLGuide Guide, IReadOnlyList<string> Warnings);

public static class ExplorerData {

    public static GraphQLExplorerData Build(GqlSchema schema, Func<NodeTypeModel, INodeData?>? sample) {
        var cache = new Dictionary<Guid, INodeData?>();
        Func<NodeTypeModel, INodeData?>? sampler = sample == null ? null : t => {
            if (cache.TryGetValue(t.Id, out var node)) return node;
            try { node = sample(t); } catch { node = null; /* a sample is a convenience */ }
            cache[t.Id] = node;
            return node;
        };
        var samples = new Dictionary<string, GraphQLSample>(StringComparer.Ordinal);
        if (sampler != null) {
            foreach (var list in schema.QueryType.Fields.Where(f => f.Source == FieldSource.RootList && f.TargetNodeType != null)) {
                var type = list.TargetNodeType!;
                var node = sampler(type);
                if (node == null) continue;
                var entry = new GraphQLSample(node.Id.ToString(), ExplorerGuide.DisplayName(type, node));
                // the builder looks a sample up by the type a field returns: the interface a class is referred to by, or the class
                if (schema.ReferenceTypesByNodeTypeId.TryGetValue(type.Id, out var reference)) samples.TryAdd(reference.Name, entry);
                if (schema.ObjectTypesByNodeTypeId.TryGetValue(type.Id, out var objectType)) samples.TryAdd(objectType.Name, entry);
            }
        }
        return new GraphQLExplorerData(SchemaDescription.Describe(schema), samples, ExplorerGuide.Build(schema, sampler), schema.Warnings);
    }

    /// <summary>The first node of a type as the store gives it in the query context, the same one the endpoint's queries run in.</summary>
    public static Func<NodeTypeModel, INodeData?> StoreSampler(IDataStore store, QueryContext? queryContext)
        => t => (store.Query(t.CodeName + ".Page(0, 1)", Array.Empty<Relatude.DB.Query.Parameter>(), queryContext) as IStoreNodeDataCollection)?.NodeValues.FirstOrDefault();
}
