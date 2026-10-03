using System.Text;
using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Endpoints;

public sealed class GraphQLExample {
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Query { get; init; }
    /// <summary>Variables as json text, or null when the query takes none.</summary>
    public string? Variables { get; init; }
}

public sealed class GraphQLExampleGroup {
    /// <summary>The GraphQL type the examples are about; empty for views and introspection.</summary>
    public required string Type { get; init; }
    public required string Label { get; init; }
    public List<GraphQLExample> Examples { get; } = [];
}

/// <summary>
/// Ready-to-run queries for an endpoint's playground: a few per exposed type, one per view, and introspection.
/// A sampler may hand over one stored node per type, so ids and filter values in the examples are real.
/// </summary>
public static class ExampleQueries {
    const string IdPlaceholder = "paste a node id here";

    public static List<GraphQLExampleGroup> Build(GqlSchema schema, Func<NodeTypeModel, INodeData?>? sample) {
        var groups = new List<GraphQLExampleGroup>();
        foreach (var list in schema.QueryType.Fields.Where(f => f.Source == FieldSource.RootList).OrderBy(f => f.Name, StringComparer.Ordinal)) {
            var type = list.TargetNodeType!;
            if (!schema.ReferenceTypesByNodeTypeId.TryGetValue(type.Id, out var refType) || refType is not IGqlCompositeType composite) continue;
            INodeData? node = null;
            try { node = sample?.Invoke(type); } catch { /* a sample is a convenience */ }
            // a class with subtypes is referred to through its synthesized interface; the group still carries the type's own name
            var typeName = schema.ObjectTypesByNodeTypeId.TryGetValue(type.Id, out var objectType) && objectType.NodeType?.Id == type.Id ? objectType.Name : refType.Name;
            var group = new GraphQLExampleGroup { Type = typeName, Label = typeName };
            addTypeExamples(schema, group, list, composite, node);
            if (group.Examples.Count > 0) groups.Add(group);
        }
        var views = new GraphQLExampleGroup { Type = "", Label = "Views" };
        foreach (var view in schema.QueryType.Fields.Where(f => f.Source == FieldSource.RootView)) {
            var itemType = view.Type.UnwrapNamed() is GqlObjectType w && w.TryGetField("items", out var items) ? items.Type.UnwrapNamed() as IGqlCompositeType : null;
            views.Examples.Add(new GraphQLExample {
                Id = "view-" + view.Name, Title = $"View {view.Name}",
                Query = $"{{\n  {view.Name}(page: 0, pageSize: 5) {{\n    totalCount\n    items {{ {string.Join(" ", TypeScriptWriter.SampleFields(itemType))} }}\n  }}\n}}\n",
            });
        }
        if (views.Examples.Count > 0) groups.Add(views);

        var general = new GraphQLExampleGroup { Type = "", Label = "Schema" };
        if (schema.Definition.EnableIntrospection) {
            general.Examples.Add(new GraphQLExample {
                Id = "schema-roots", Title = "Root fields",
                Query = "{\n  __schema {\n    queryType { fields { name description } }\n    mutationType { fields { name description } }\n  }\n}\n",
            });
            general.Examples.Add(new GraphQLExample {
                Id = "schema-types", Title = "All type names",
                Query = "{\n  __schema {\n    types { name kind }\n  }\n}\n",
            });
            var first = groups.FirstOrDefault(g => g.Type.Length > 0);
            if (first != null) {
                general.Examples.Add(new GraphQLExample {
                    Id = "schema-type", Title = $"Fields of {first.Type}",
                    Query = $"{{\n  __type(name: \"{first.Type}\") {{\n    name\n    fields {{ name type {{ name kind ofType {{ name }} }} }}\n  }}\n}}\n",
                });
            }
        } else {
            general.Examples.Add(new GraphQLExample { Id = "typename", Title = "Root type name", Query = "{ __typename }\n" });
        }
        groups.Add(general);
        return groups;
    }

    static void addTypeExamples(GqlSchema schema, GraphQLExampleGroup group, GqlField list, IGqlCompositeType composite, INodeData? node) {
        var selection = string.Join(" ", TypeScriptWriter.SampleFields(composite));
        var typeName = composite.Name;
        var id = node?.Id.ToString() ?? IdPlaceholder;
        var hasRealId = node != null;

        group.Examples.Add(new GraphQLExample {
            Id = "page", Title = "A page",
            Query = $"query Page($page: Int!, $pageSize: Int!) {{\n  {list.Name}(page: $page, pageSize: $pageSize) {{\n    totalCount\n    pageIndex\n    items {{ {selection} }}\n  }}\n}}\n",
            Variables = json(new { page = 0, pageSize = 5 }),
        });

        var single = schema.QueryType.Fields.FirstOrDefault(f => f.Source == FieldSource.RootSingle && f.TargetNodeType == list.TargetNodeType);
        if (single != null) {
            group.Examples.Add(new GraphQLExample {
                Id = "one", Title = hasRealId ? "One by id" : "One by id (needs an id)",
                Query = $"query One($id: ID!) {{\n  {single.Name}(id: $id) {{ {selection} }}\n}}\n",
                Variables = json(new { id }),
            });
        }

        if (list.GetArgument("filter")?.Type.UnwrapNamed() is GqlInputObjectType filter) {
            var text = filter.InputFields.FirstOrDefault(f => f.Property != null && f.Type.UnwrapNamed().Name == "StringFilterInput");
            var number = filter.InputFields.FirstOrDefault(f => f.Property != null && f.Type.UnwrapNamed().Name is "IntFilterInput" or "LongFilterInput" or "FloatFilterInput" or "DecimalFilterInput");
            if (text != null) {
                var value = sampleString(node, text.Property!) ?? "example";
                group.Examples.Add(new GraphQLExample {
                    Id = "filter", Title = $"Filter on {text.Name}",
                    Query = $"query Filtered($value: String!) {{\n  {list.Name}(filter: {{ {text.Name}: {{ eq: $value }} }}, pageSize: 5) {{\n    totalCount\n    items {{ {selection} }}\n  }}\n}}\n",
                    Variables = json(new { value }),
                });
            } else if (number != null) {
                var scalar = number.Type.UnwrapNamed().Name switch { "IntFilterInput" => "Int", "LongFilterInput" => "Long", "FloatFilterInput" => "Float", _ => "Decimal" };
                group.Examples.Add(new GraphQLExample {
                    Id = "filter", Title = $"Filter on {number.Name}",
                    Query = $"query Filtered($min: {scalar}!) {{\n  {list.Name}(filter: {{ {number.Name}: {{ gte: $min }} }}, pageSize: 5) {{\n    totalCount\n    items {{ {selection} }}\n  }}\n}}\n",
                    Variables = json(new { min = 0 }),
                });
            }
            var relation = filter.InputFields.FirstOrDefault(f => f.Property != null && f.Type.UnwrapNamed().Name == "RelatedNodeFilterInput");
            if (relation != null) {
                group.Examples.Add(new GraphQLExample {
                    Id = "filter-related", Title = $"Related to a node through {relation.Name}",
                    Query = $"query Related($relatedId: ID!) {{\n  {list.Name}(filter: {{ {relation.Name}: {{ eq: $relatedId }} }}, pageSize: 5) {{\n    totalCount\n    items {{ {selection} }}\n  }}\n}}\n",
                    Variables = json(new { relatedId = IdPlaceholder.Replace("node", "related node") }),
                });
            }
        }

        var searchable = schema.Definition.Mode == GraphQLEndpointMode.WholeDatamodel || composite.Fields.Any(f => f.Property?.PropertyType == PropertyType.String);
        if (searchable) {
            var word = firstWord(sampleString(node, composite.Fields.FirstOrDefault(f => f.Property?.PropertyType == PropertyType.String)?.Property)) ?? "example";
            group.Examples.Add(new GraphQLExample {
                Id = "search", Title = "Free-text search",
                Query = $"query Search($search: String!) {{\n  {list.Name}(search: $search, pageSize: 5) {{\n    totalCount\n    items {{ {selection} }}\n  }}\n}}\n",
                Variables = json(new { search = word }),
            });
        }

        if (list.GetArgument("orderBy")?.Type.UnwrapNamed() is GqlEnumType orderBy && orderBy.Values.Count > 0) {
            var field = orderBy.Values[0].Name;
            group.Examples.Add(new GraphQLExample {
                Id = "ordered", Title = $"Ordered by {field}, newest first",
                Query = $"{{\n  {list.Name}(orderBy: {field}, descending: true, pageSize: 5) {{\n    items {{ {selection} }}\n  }}\n}}\n",
            });
        }

        var related = composite.Fields.FirstOrDefault(f => f.Source is FieldSource.RelationOne or FieldSource.ReferenceOne)
            ?? composite.Fields.FirstOrDefault(f => f.Source is FieldSource.RelationMany or FieldSource.ReferenceMany);
        if (related != null) {
            var many = related.Source is FieldSource.RelationMany or FieldSource.ReferenceMany;
            var relatedType = related.Type.UnwrapNamed() as IGqlCompositeType;
            var relatedSelection = string.Join(" ", TypeScriptWriter.SampleFields(relatedType).Where(s => !s.Contains('{')));
            group.Examples.Add(new GraphQLExample {
                Id = "related", Title = $"With {related.Name}",
                Query = $"{{\n  {list.Name}(pageSize: 3) {{\n    items {{\n      id displayName\n      {related.Name}{(many ? "(top: 3)" : "")} {{ {relatedSelection} }}\n    }}\n  }}\n}}\n",
            });
        }

        group.Examples.Add(new GraphQLExample {
            Id = "ids", Title = "Given ids only",
            Query = $"query ByIds($ids: [ID!]) {{\n  {list.Name}(ids: $ids) {{\n    totalCount\n    items {{ {selection} }}\n  }}\n}}\n",
            Variables = json(new { ids = new[] { id } }),
        });

        if (schema.MutationType != null) {
            var create = schema.MutationType.Fields.FirstOrDefault(f => f.Source == FieldSource.MutationCreate && f.TargetNodeType == list.TargetNodeType);
            var update = schema.MutationType.Fields.FirstOrDefault(f => f.Source == FieldSource.MutationUpdate && f.TargetNodeType == list.TargetNodeType);
            var delete = schema.MutationType.Fields.FirstOrDefault(f => f.Source == FieldSource.MutationDelete && f.TargetNodeType == list.TargetNodeType);
            if (create?.GetArgument("input")?.Type.UnwrapNamed() is GqlInputObjectType input) {
                var inputName = input.Name;
                group.Examples.Add(new GraphQLExample {
                    Id = "create", Title = $"Create a {typeName}",
                    Query = $"mutation Create($input: {inputName}!) {{\n  {create.Name}(input: $input) {{ {selection} }}\n}}\n",
                    Variables = json(new Dictionary<string, object?> { ["input"] = sampleInput(input) }),
                });
                if (update != null) {
                    var textField = input.InputFields.FirstOrDefault(f => f.Source == FieldSource.ScalarProperty && f.Property?.PropertyType == PropertyType.String);
                    var change = new Dictionary<string, object?>();
                    if (textField != null) change[textField.Name] = "Updated";
                    else if (input.InputFields.Count > 0) change[input.InputFields[0].Name] = sampleValue(input.InputFields[0]);
                    group.Examples.Add(new GraphQLExample {
                        Id = "update", Title = $"Update a {typeName} (needs an id)",
                        Query = $"mutation Update($id: ID!, $input: {inputName}!) {{\n  {update.Name}(id: $id, input: $input) {{ {selection} }}\n}}\n",
                        Variables = json(new Dictionary<string, object?> { ["id"] = IdPlaceholder.Replace("a node", "the node to change"), ["input"] = change }),
                    });
                }
                if (delete != null) {
                    group.Examples.Add(new GraphQLExample {
                        Id = "delete", Title = $"Delete a {typeName} (needs an id)",
                        Query = $"mutation Delete($id: ID!) {{\n  {delete.Name}(id: $id)\n}}\n",
                        Variables = json(new { id = IdPlaceholder.Replace("a node", "the node to delete") }),
                    });
                }
            }
        }
    }

    internal static Dictionary<string, object?> sampleInput(GqlInputObjectType input) {
        var values = new Dictionary<string, object?>();
        foreach (var f in input.InputFields) {
            if (values.Count >= 6) break;
            var v = sampleValue(f);
            if (v != null) values[f.Name] = v;
        }
        return values;
    }

    /// <summary>A plausible value for an input field, or null when only a real id would do.</summary>
    internal static object? sampleValue(GqlInputField f) {
        switch (f.Source) {
            case FieldSource.EnumProperty: return (f.Type.UnwrapNamed() as GqlEnumType)?.Values.FirstOrDefault()?.Name;
            case FieldSource.EnumArrayProperty: return (f.Type.UnwrapNamed() as GqlEnumType)?.Values.Take(1).Select(v => v.Name).ToArray();
            case FieldSource.GeoProperty: return new { latitude = 59.91, longitude = 10.75 };
            case FieldSource.RelationOne or FieldSource.RelationMany or FieldSource.ReferenceOne or FieldSource.ReferenceMany: return null;
        }
        return f.Property?.PropertyType switch {
            PropertyType.Boolean => true,
            PropertyType.Integer => 1,
            PropertyType.Long => 1L,
            PropertyType.Double or PropertyType.Float or PropertyType.Decimal => 1.5,
            PropertyType.String => "Example",
            PropertyType.StringArray => new[] { "one", "two" },
            PropertyType.DateTime or PropertyType.DateTimeOffset => "2026-01-01T12:00:00Z",
            PropertyType.TimeSpan => "01:30:00",
            _ => null,
        };
    }

    internal static string? sampleString(INodeData? node, PropertyModel? property) {
        if (node == null || property == null || !node.TryGetValue(property.Id, out var value) || value is not string s || s.Length == 0) return null;
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 60 ? s[..60] : s;
    }

    internal static string? firstWord(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var word = new StringBuilder();
        foreach (var c in text.TrimStart()) {
            if (char.IsLetterOrDigit(c)) word.Append(c);
            else if (word.Length > 0) break;
        }
        return word.Length >= 2 ? word.ToString() : null;
    }

    static readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    internal static string json(object value) => JsonSerializer.Serialize(value, _json).Replace("\r\n", "\n");
}
