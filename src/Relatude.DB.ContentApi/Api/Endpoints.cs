using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;
using Relatude.DB.Transactions;

namespace Relatude.DB.ContentApi.Api;

public static class Endpoints {

    public static void MapContentApi(this WebApplication app) {
        var api = app.MapGroup("/api");

        // Datamodel metadata for building the UI.
        api.MapGet("/model", (NodeStore store) => ModelMapper.ToDto(store.Datastore.Datamodel));

        // Paged (and optionally text-filtered) list of nodes of a type, including subtypes.
        api.MapGet("/types/{typeId:guid}/nodes", (NodeStore store, Guid typeId, string? search, int page = 0, int pageSize = 30) => {
            if (!store.Datastore.Datamodel.NodeTypes.ContainsKey(typeId)) return Results.NotFound();
            pageSize = Math.Clamp(pageSize, 1, 200);
            var query = store.QueryType(typeId);
            if (!string.IsNullOrWhiteSpace(search)) query.WhereSearch(search);
            var ids = query.Page(page, pageSize).SelectId().Execute();
            var items = ids.Select(id => NodeMapper.ToSummary(store, store.Datastore.Get(id))).ToList();
            return Results.Ok(new NodeListDto(items, ids.TotalCount, page, pageSize));
        });

        // Full node for the editor blade.
        api.MapGet("/nodes/{id:guid}", (NodeStore store, Guid id) => {
            if (!store.Exists(id)) return Results.NotFound();
            return Results.Ok(NodeMapper.ToDto(store, store.Datastore.Get(id)));
        });

        // Create a node of any type from a property name/value map.
        api.MapPost("/nodes", (NodeStore store, CreateNodeRequest request) => {
            var dm = store.Datastore.Datamodel;
            if (!dm.NodeTypes.TryGetValue(request.TypeId, out var type)) {
                return Results.BadRequest(new { error = $"Unknown type id {request.TypeId}." });
            }
            if (type.IsInterface) {
                return Results.BadRequest(new { error = $"Type '{type.CodeName}' is abstract and cannot be instantiated." });
            }
            var (propertyIds, values) = NodeMapper.CoerceValues(type, request.Values ?? []);
            var properties = new Properties<object>(propertyIds.Length);
            for (var i = 0; i < propertyIds.Length; i++) properties.Add(propertyIds[i], values[i]);
            var now = DateTime.UtcNow;
            var node = new NodeData(Guid.NewGuid(), 0, type.Id, now, now, properties, null);
            var transaction = new TransactionData();
            transaction.InsertOrFail(node, null, null);
            store.Datastore.Execute(transaction);
            return Results.Ok(NodeMapper.ToDto(store, store.Datastore.Get(node.Id)));
        });

        // Update value properties of a node.
        api.MapPut("/nodes/{id:guid}", (NodeStore store, Guid id, UpdateNodeRequest request) => {
            if (!store.Exists(id)) return Results.NotFound();
            var node = store.Datastore.Get(id);
            var type = store.Datastore.Datamodel.NodeTypes[node.NodeType];
            var (propertyIds, values) = NodeMapper.CoerceValues(type, request.Values ?? []);
            if (propertyIds.Length > 0) {
                var transaction = new TransactionData();
                transaction.UpdateIfDifferentProperties(id, propertyIds, values);
                store.Datastore.Execute(transaction);
            }
            return Results.Ok(NodeMapper.ToDto(store, store.Datastore.Get(id)));
        });

        api.MapDelete("/nodes/{id:guid}", (NodeStore store, Guid id) => {
            if (!store.Exists(id)) return Results.NotFound();
            store.DeleteOrFail(id);
            return Results.NoContent();
        });

        // Replace the value of a relation property (works for both one- and many-relations).
        api.MapPut("/nodes/{id:guid}/relations/{propertyId:guid}", (NodeStore store, Guid id, Guid propertyId, SetRelationRequest request) => {
            if (!store.Exists(id)) return Results.NotFound();
            // Relate per id: the IEnumerable<Guid> overloads on NodeStore bind to the
            // object-based Transaction overloads and fail at runtime.
            var transaction = store.CreateTransaction();
            transaction.ClearRelations(id, propertyId);
            foreach (var toId in request.Ids.Distinct()) transaction.Relate(id, propertyId, toId);
            transaction.Execute();
            return Results.Ok(NodeMapper.ToDto(store, store.Datastore.Get(id)));
        });

        // Global ranked full text search across all node types.
        api.MapGet("/search", (NodeStore store, string q, int take = 20) => {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new SearchResultDto([], 0, 0));
            take = Math.Clamp(take, 1, 100);
            var result = store.QueryType(NodeConstants.BaseNodeTypeId).Search(q).Page(0, take).Execute();
            var hits = new List<SearchHitDto>();
            foreach (var hit in result) {
                if (hit.Node == null) continue;
                var nodeId = store.Mapper.GetIdGuid(hit.Node);
                var summary = NodeMapper.ToSummary(store, store.Datastore.Get(nodeId));
                hits.Add(new SearchHitDto(summary, hit.Score, hit.Sample.FormatSample("<mark>", "</mark>")));
            }
            return Results.Ok(new SearchResultDto(hits, result.TotalCount, result.DurationMs));
        });
    }
}
