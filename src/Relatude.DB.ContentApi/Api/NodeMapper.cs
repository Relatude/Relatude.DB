using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;

namespace Relatude.DB.ContentApi.Api;

// Maps between raw INodeData (the untyped node representation) and the JSON DTOs,
// using only the runtime datamodel - no compile time knowledge of node types.
public static class NodeMapper {

    public static NodeSummaryDto ToSummary(NodeStore store, INodeData node) {
        var type = store.Datastore.Datamodel.NodeTypes[node.NodeType];
        return new NodeSummaryDto(node.Id, GetDisplayName(store, node), type.Id, type.CodeName);
    }

    public static string GetDisplayName(NodeStore store, INodeData node) {
        if (!string.IsNullOrWhiteSpace(node.DisplayName)) return node.DisplayName!;
        var type = store.Datastore.Datamodel.NodeTypes[node.NodeType];
        var fromProps = type.GetDisplayName(node);
        if (!string.IsNullOrWhiteSpace(fromProps)) return fromProps;
        // fall back to the first non-empty string value, then to a shortened id
        foreach (var p in type.AllProperties.Values) {
            if (p.PropertyType != PropertyType.String || ModelMapper.IsSystemProperty(p)) continue;
            if (node.TryGetValue(p.Id, out var v) && v is string s && !string.IsNullOrWhiteSpace(s)) {
                return s.Length > 60 ? s[..60] + "…" : s;
            }
        }
        return type.CodeName + " " + node.Id.ToString()[..8];
    }

    public static NodeDto ToDto(NodeStore store, INodeData node, int maxRelatedItems = 50) {
        var dm = store.Datastore.Datamodel;
        var type = dm.NodeTypes[node.NodeType];
        var values = new Dictionary<string, object?>();
        var relations = new List<RelationValueDto>();
        foreach (var p in type.AllProperties.Values) {
            if (ModelMapper.IsSystemProperty(p)) continue;
            if (p is RelationPropertyModel rp) {
                relations.Add(readRelation(store, node, rp));
            } else if (ModelMapper.IsReadableValue(p)) {
                values[p.CodeName] = node.TryGetValue(p.Id, out var v) ? ToJsonFriendly(v) : null;
            }
        }
        return new NodeDto(
            node.Id, type.Id, type.CodeName,
            GetDisplayName(store, node),
            node.CreatedUtc, node.ChangedUtc,
            values, relations
        );

        RelationValueDto readRelation(NodeStore store, INodeData node, RelationPropertyModel rp) {
            var related = store.Datastore.GetRelatedNodesFromPropertyId(rp.Id, node.Id);
            var items = related.Take(maxRelatedItems).Select(r => ToSummary(store, r)).ToList();
            return new RelationValueDto(rp.Id, rp.CodeName, rp.IsMany, ModelMapper.GetRelatedTypeId(dm, rp), items, related.Length);
        }
    }

    static object? ToJsonFriendly(object? value) => value switch {
        null => null,
        TimeSpan ts => ts.ToString(),
        _ => value, // primitives, strings, dates and string arrays serialize cleanly as-is
    };

    // Parses incoming JSON values into the CLR types the store expects for each property.
    public static (Guid[] PropertyIds, object[] Values) CoerceValues(
        NodeTypeModel type, Dictionary<string, JsonElement> values) {
        var propertyIds = new List<Guid>();
        var coerced = new List<object>();
        foreach (var (name, json) in values) {
            if (!type.AllPropertiesByName.TryGetValue(name, out var p)) {
                throw new BadHttpRequestException($"Unknown property '{name}' on type '{type.CodeName}'.");
            }
            if (!ModelMapper.IsEditable(p)) {
                throw new BadHttpRequestException($"Property '{name}' is not editable through this API.");
            }
            if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            propertyIds.Add(p.Id);
            coerced.Add(coerce(p, json));
        }
        return (propertyIds.ToArray(), coerced.ToArray());
    }

    static object coerce(PropertyModel p, JsonElement json) {
        try {
            return p.PropertyType switch {
                PropertyType.String => json.GetString() ?? string.Empty,
                PropertyType.Integer => json.GetInt32(),
                PropertyType.Long => json.GetInt64(),
                PropertyType.Double => json.GetDouble(),
                PropertyType.Float => json.GetSingle(),
                PropertyType.Decimal => json.GetDecimal(),
                PropertyType.Boolean => json.GetBoolean(),
                PropertyType.DateTime => json.GetDateTime(),
                PropertyType.DateTimeOffset => json.GetDateTimeOffset(),
                PropertyType.TimeSpan => TimeSpan.Parse(json.GetString() ?? "0"),
                PropertyType.Guid => json.GetGuid(),
                PropertyType.StringArray => json.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
                _ => throw new BadHttpRequestException($"Property type {p.PropertyType} is not writable."),
            };
        } catch (Exception ex) when (ex is not BadHttpRequestException) {
            throw new BadHttpRequestException($"Invalid value for property '{p.CodeName}' ({p.PropertyType}): {ex.Message}");
        }
    }
}
