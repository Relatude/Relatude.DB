using System.Globalization;
using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Endpoints;

/// <summary>A runnable example for one topic of the explorer's guide, written for a particular endpoint.</summary>
public sealed class GraphQLGuideExample {
    /// <summary>The guide topic the example belongs to, such as "filter" or "fragments".</summary>
    public required string Id { get; init; }
    public required string Query { get; init; }
    /// <summary>Variables as json text, or null when the query takes none.</summary>
    public string? Variables { get; init; }
    /// <summary>What the example uses on this endpoint and where its values came from, in one or two sentences.</summary>
    public string? Note { get; init; }
    /// <summary>True when running the example changes data.</summary>
    public bool IsMutation { get; init; }
}

/// <summary>A guide topic the endpoint cannot show, and why.</summary>
public sealed class GraphQLGuideGap {
    public required string Id { get; init; }
    public required string Reason { get; init; }
}

public sealed class GraphQLGuide {
    public List<GraphQLGuideExample> Examples { get; } = [];
    public List<GraphQLGuideGap> Unavailable { get; } = [];
}

/// <summary>
/// The examples behind the explorer's guide: one per GraphQL feature (paging, filters, search, ordering,
/// relations, variables, aliases, fragments, directives, introspection, mutations...), written with the names
/// of the endpoint at hand. A sampler may hand over one stored node per type, so ids and filter values are
/// real and the examples return something. Update and delete never get a real id.
/// </summary>
public static class ExplorerGuide {
    const string IdPlaceholder = "paste a node id here";

    sealed class Root {
        public required GqlField List;
        public GqlField? Single;
        public required IGqlCompositeType Type;
        public required NodeTypeModel NodeType;
        public GqlInputObjectType? Filter;
        public GqlEnumType? OrderBy;
        public INodeData? Node;
    }

    static readonly string[] _typeTopics = [
        "first", "paging", "one", "filter", "filter-range", "filter-in", "filter-logic", "enums", "search", "order", "relations",
        "relation-filter", "ids", "files", "geo", "variables", "aliases", "fragments", "inline-fragments", "directives",
        "typename", "multiple-roots", "operation-name",
    ];

    public static GraphQLGuide Build(GqlSchema schema, Func<NodeTypeModel, INodeData?>? sample) {
        var guide = new GraphQLGuide();
        var samples = new Dictionary<Guid, INodeData?>();
        INodeData? sampleOf(NodeTypeModel t) {
            if (samples.TryGetValue(t.Id, out var n)) return n;
            try { n = sample?.Invoke(t); } catch { n = null; /* a sample is a convenience */ }
            samples[t.Id] = n;
            return n;
        }
        void add(string id, string query, object? variables = null, string? note = null, bool mutation = false)
            => guide.Examples.Add(new GraphQLGuideExample { Id = id, Query = text(query), Variables = variables == null ? null : ExampleQueries.json(variables), Note = note, IsMutation = mutation });
        void gap(string id, string reason) => guide.Unavailable.Add(new GraphQLGuideGap { Id = id, Reason = reason });

        var roots = new List<Root>();
        foreach (var list in schema.QueryType.Fields.Where(f => f.Source == FieldSource.RootList && f.TargetNodeType != null).OrderBy(f => f.Name, StringComparer.Ordinal)) {
            var t = list.TargetNodeType!;
            if (!schema.ReferenceTypesByNodeTypeId.TryGetValue(t.Id, out var refType) || refType is not IGqlCompositeType composite) continue;
            roots.Add(new Root {
                List = list, Type = composite, NodeType = t,
                Single = schema.QueryType.Fields.FirstOrDefault(f => f.Source == FieldSource.RootSingle && f.TargetNodeType == t),
                Filter = list.GetArgument("filter")?.Type.UnwrapNamed() as GqlInputObjectType,
                OrderBy = list.GetArgument("orderBy")?.Type.UnwrapNamed() as GqlEnumType,
                Node = sampleOf(t),
            });
        }
        var def = schema.Definition;

        // the endpoint's owner may name the type the explorer opens on; otherwise the one with the most to show
        var main = roots.FirstOrDefault(r => r.NodeType.Id == def.DefaultNodeTypeId)
            ?? roots.OrderByDescending(score).ThenBy(r => r.List.Name, StringComparer.Ordinal).FirstOrDefault();
        if (main == null) {
            foreach (var id in _typeTopics) gap(id, "The endpoint exposes no node types yet.");
        } else try {
            var L = main.List.Name;
            var T = main.Type.Name;

            add("first", $$"""
                {
                  {{L}}(pageSize: 5) {
                    totalCount
                    items { id displayName }
                  }
                }
                """, note: $"{L} returns a page of {T} nodes; totalCount counts every match, not only the page.");

            add("paging", $$"""
                query Paging($page: Int!, $pageSize: Int!) {
                  {{L}}(page: $page, pageSize: $pageSize) {
                    totalCount
                    pageIndex
                    pageSize
                    durationMs
                    items { id displayName }
                  }
                }
                """, new { page = 1, pageSize = 3 },
                $"Pages count from 0. Without pageSize a page holds {def.DefaultPageSize} items, and pageSize is capped at {def.MaxPageSize}.");

            var oneRoot = new[] { main }.Concat(roots).FirstOrDefault(r => r.Single != null && r.Node != null) ?? roots.FirstOrDefault(r => r.Single != null);
            if (oneRoot?.Single != null) {
                add("one", $$"""
                    query One($id: ID!) {
                      {{oneRoot.Single.Name}}(id: $id) {
                        {{lines(TypeScriptWriter.SampleFields(oneRoot.Type), 4)}}
                      }
                    }
                    """, new { id = oneRoot.Node?.Id.ToString() ?? IdPlaceholder },
                    oneRoot.Node != null ? $"The id belongs to a {oneRoot.Type.Name} in the database{named(oneRoot.NodeType, oneRoot.Node)}." : $"No {oneRoot.Type.Name} is stored yet, so paste an id into the variables.");
            } else gap("one", "No type has a single-node root field.");

            // text filters: a stored value when there is one, so the example finds something
            var textFilter = best(filterCandidates(main, roots, isText));
            if (textFilter != null) {
                var (r, f, value) = textFilter;
                add("filter", $$"""
                    query Filter($value: String!) {
                      {{r.List.Name}}(filter: { {{f.Name}}: { eq: $value } }, pageSize: 5) {
                        totalCount
                        items { id {{leaf(r.Type, f.Name)}} }
                      }
                    }
                    """, new { value = value as string ?? "example" },
                    value != null ? $"The value is the {f.Name} of a stored {r.Type.Name}, so at least one node matches." : $"No stored {r.Type.Name} has a {f.Name} yet; change the value in the variables.");
                add("filter-in", $$"""
                    {
                      {{r.List.Name}}(filter: { {{f.Name}}: { in: [{{literal(value as string ?? "example")}}, "something else"] } }, pageSize: 5) {
                        totalCount
                        items { id {{leaf(r.Type, f.Name)}} }
                      }
                    }
                    """, note: $"Any {r.Type.Name} whose {f.Name} is one of the listed values; nin is the opposite.");
            } else {
                gap("filter", "No exposed type has a text property to filter on.");
                var numberIn = best(filterCandidates(main, roots, isNumber));
                if (numberIn != null) {
                    add("filter-in", $$"""
                        {
                          {{numberIn.Root.List.Name}}(filter: { {{numberIn.Field.Name}}: { in: [{{literal(numberIn.Value ?? 1)}}, 2, 3] } }, pageSize: 5) {
                            totalCount
                            items { id {{leaf(numberIn.Root.Type, numberIn.Field.Name)}} }
                          }
                        }
                        """, note: $"Any {numberIn.Root.Type.Name} whose {numberIn.Field.Name} is one of the listed values; nin is the opposite.");
                } else gap("filter-in", "No exposed type has a property that takes a list of values in a filter.");
            }

            var range = best(filterCandidates(main, roots, f => isNumber(f) || isDate(f)));
            if (range != null) {
                var (lo, hi) = rangeAround(range.Field, range.Value);
                add("filter-range", $$"""
                    {
                      {{range.Root.List.Name}}(filter: { {{range.Field.Name}}: { gte: {{lo}}, lte: {{hi}} } }, pageSize: 5) {
                        totalCount
                        items { id displayName {{leaf(range.Root.Type, range.Field.Name)}} }
                      }
                    }
                    """, note: range.Value != null ? $"The range is drawn around the {range.Field.Name} of a stored {range.Root.Type.Name}." : null);
            } else gap("filter-range", "No exposed type has a number or date property to filter on.");

            var logicRoot = new[] { main }.Concat(roots).FirstOrDefault(r => r.Filter != null && r.Filter.InputFields.Any(f => f.Property != null && (isText(f) || isNumber(f))));
            if (logicRoot != null) {
                var fields = logicRoot.Filter!.InputFields.Where(f => f.Property != null).OrderByDescending(indexed).ToList();
                var a = fields.FirstOrDefault(isText) ?? fields.First(isNumber);
                var b = fields.FirstOrDefault(f => f != a && isNumber(f));
                var av = literal(valueOf(logicRoot.Node, a) ?? (isText(a) ? (object)"example" : 1));
                var second = b != null ? $"{{ {b.Name}: {{ gte: {literal(valueOf(logicRoot.Node, b) ?? 0)} }} }}" : $"{{ {a.Name}: {{ eq: {(isText(a) ? "\"something else\"" : "2")} }} }}";
                add("filter-logic", $$"""
                    {
                      either: {{logicRoot.List.Name}}(filter: { or: [{ {{a.Name}}: { eq: {{av}} } }, {{second}}] }, pageSize: 5) {
                        totalCount
                        items { id displayName }
                      }
                      allBut: {{logicRoot.List.Name}}(filter: { not: { {{a.Name}}: { eq: {{av}} } } }, pageSize: 5) {
                        totalCount
                      }
                    }
                    """, note: "Conditions side by side in one filter must all match; or takes a list of alternatives and not turns a condition around.");
            } else gap("filter-logic", "No exposed type has properties to combine in a filter.");

            var enumFilter = best(filterCandidates(main, roots, isEnum));
            if (enumFilter != null) {
                var et = (GqlEnumType)((GqlInputObjectType)enumFilter.Field.Type.UnwrapNamed()).InputFields.First(f => f.Name == "eq").Type.UnwrapNamed();
                var name = enumFilter.Value is int iv && et.TryGetByInt(iv, out var ev) ? ev.Name : et.Values.FirstOrDefault()?.Name;
                if (name != null) {
                    add("enums", $$"""
                        {
                          {{enumFilter.Root.List.Name}}(filter: { {{enumFilter.Field.Name}}: { eq: {{name}} } }, pageSize: 5) {
                            totalCount
                            items { id displayName {{leaf(enumFilter.Root.Type, enumFilter.Field.Name)}} }
                          }
                        }
                        """, note: $"{et.Name} has the values {string.Join(", ", et.Values.Take(8).Select(v => v.Name))}{(et.Values.Count > 8 ? "…" : "")}.");
                } else gap("enums", "The enum has no values.");
            } else gap("enums", "No exposed type has an enum property.");

            var searchRoot = new[] { main }.Concat(roots).FirstOrDefault(r => r.Node != null && wordOf(r) != null) ?? main;
            add("search", $$"""
                query Search($text: String!) {
                  {{searchRoot.List.Name}}(search: $text, pageSize: 5) {
                    totalCount
                    items { id displayName }
                  }
                }
                """, new { text = wordOf(searchRoot) ?? "example" },
                wordOf(searchRoot) != null ? $"The word comes from a stored {searchRoot.Type.Name}. Search can be combined with filter, orderBy and paging." : "Search can be combined with filter, orderBy and paging.");

            var orderRoot = new[] { main }.Concat(roots).FirstOrDefault(r => r.OrderBy != null && r.OrderBy.Values.Count > 0);
            if (orderRoot != null) {
                // an indexed property sorts without reading every node; a date or a number shows the order best
                var by = orderRoot.OrderBy!.Values
                    .OrderByDescending(v => v.Property?.Indexed == true)
                    .ThenByDescending(v => v.Property?.PropertyType switch {
                        PropertyType.DateTime or PropertyType.DateTimeOffset => 2,
                        PropertyType.Integer or PropertyType.Long or PropertyType.Double or PropertyType.Float or PropertyType.Decimal => 1,
                        _ => 0,
                    })
                    .First();
                add("order", $$"""
                    {
                      {{orderRoot.List.Name}}(orderBy: {{by.Name}}, descending: true, pageSize: 5) {
                        items { id displayName {{leaf(orderRoot.Type, by.Name)}} }
                      }
                    }
                    """, note: $"{orderRoot.OrderBy.Name} lists what {orderRoot.List.Name} can be ordered by: {string.Join(", ", orderRoot.OrderBy.Values.Take(8).Select(v => v.Name))}{(orderRoot.OrderBy.Values.Count > 8 ? "…" : "")}.");
            } else gap("order", "No exposed type has a property to order by.");

            var relRoot = new[] { main }.Concat(roots).Where(r => r.Node != null).Concat(roots).FirstOrDefault(r => relationOf(r.Type) != null);
            if (relRoot != null) {
                var rel = relationOf(relRoot.Type)!;
                var target = rel.Type.UnwrapNamed() as IGqlCompositeType;
                var deeper = target == null ? null : relationOf(target);
                var inner = new List<string> { "id", "displayName" };
                if (deeper != null) inner.Add($"{deeper.Name}{top(deeper)} {{ id displayName }}");
                add("relations", $$"""
                    {
                      {{relRoot.List.Name}}(pageSize: 3) {
                        items {
                          id
                          displayName
                          {{rel.Name}}{{top(rel)}} {
                            {{lines(inner, 8)}}
                          }
                        }
                      }
                    }
                    """, note: $"{rel.Name} leads from {relRoot.Type.Name} to {target?.Name}" + (deeper != null ? $", and {deeper.Name} one step further" : "")
                        + $". Everything comes back from one store query; relations can be followed {def.MaxIncludeDepth} levels deep.");
            } else gap("relations", "No exposed type has a relation or reference to another exposed type.");

            var relFilter = new[] { main }.Concat(roots).Select(r => (Root: r, Field: r.Filter?.InputFields.FirstOrDefault(f => f.Property != null && f.Type.UnwrapNamed().Name == "RelatedNodeFilterInput")))
                .FirstOrDefault(x => x.Field != null);
            if (relFilter.Field != null) {
                var (r, f) = (relFilter.Root, relFilter.Field!);
                var output = r.Type.TryGetField(f.Name, out var of) ? of : null;
                var targetType = output?.TargetNodeType;
                var targetNode = targetType == null ? null : sampleOf(targetType);
                add("relation-filter", $$"""
                    query RelatedTo($id: ID!) {
                      {{r.List.Name}}(filter: { {{f.Name}}: { eq: $id } }, pageSize: 5) {
                        totalCount
                        items { id displayName{{(output != null ? " " + f.Name + " { id displayName }" : "")}} }
                      }
                    }
                    """, new { id = targetNode?.Id.ToString() ?? IdPlaceholder.Replace("a node", "a related node") },
                    targetNode != null ? $"The id is a stored {targetType!.CodeName}{named(targetType, targetNode)}; the result lists the {r.Type.Name} nodes related to it, which may be none. in takes several ids." : "Paste the id of a related node into the variables; in takes several ids.");
            } else gap("relation-filter", "No exposed type has a relation to filter on.");

            add("ids", $$"""
                query ByIds($ids: [ID!]) {
                  {{L}}(ids: $ids) {
                    totalCount
                    items { id displayName }
                  }
                }
                """, new { ids = new[] { main.Node?.Id.ToString() ?? IdPlaceholder } },
                "ids restricts any list or view to the given nodes, and can be combined with filter, search and ordering.");

            var fileRoot = roots.Select(r => (Root: r, Field: r.Type.Fields.FirstOrDefault(f => f.Source == FieldSource.FileProperty))).OrderByDescending(x => x.Root.Node != null).FirstOrDefault(x => x.Field != null);
            if (fileRoot.Field != null) {
                add("files", $$"""
                    {
                      {{fileRoot.Root.List.Name}}(pageSize: 5) {
                        items {
                          id
                          displayName
                          {{fileRoot.Field.Name}} { name size contentType width height }
                        }
                      }
                    }
                    """, note: $"{fileRoot.Field.Name} is null on a {fileRoot.Root.Type.Name} without a file.");
            } else gap("files", "No exposed type has a file property.");

            var geoRoot = roots.Select(r => (Root: r, Field: r.Type.Fields.FirstOrDefault(f => f.Source == FieldSource.GeoProperty))).OrderByDescending(x => x.Root.Node != null).FirstOrDefault(x => x.Field != null);
            if (geoRoot.Field != null) {
                add("geo", $$"""
                    {
                      {{geoRoot.Root.List.Name}}(pageSize: 5) {
                        items {
                          id
                          displayName
                          {{geoRoot.Field.Name}} { latitude longitude }
                        }
                      }
                    }
                    """, note: $"{geoRoot.Field.Name} is null on a {geoRoot.Root.Type.Name} without a position.");
            } else gap("geo", "No exposed type has a position (GeoCoordinate) property.");

            add("variables", $$"""
                query Recent($page: Int = 0, $pageSize: Int = 3) {
                  {{L}}(page: $page, pageSize: $pageSize) {
                    pageIndex
                    pageSize
                    items { id displayName }
                  }
                }
                """, new { pageSize = 2 }, "pageSize comes from the variables; page is not given there, so its default 0 is used.");

            var by2 = main.OrderBy?.Values.OrderByDescending(v => v.Property?.Indexed == true).FirstOrDefault();
            add("aliases", by2 != null ? $$"""
                {
                  lowest: {{L}}(orderBy: {{by2.Name}}, pageSize: 3) {
                    items { id displayName {{leaf(main.Type, by2.Name)}} }
                  }
                  highest: {{L}}(orderBy: {{by2.Name}}, descending: true, pageSize: 3) {
                    items { id displayName {{leaf(main.Type, by2.Name)}} }
                  }
                }
                """ : $$"""
                {
                  firstPage: {{L}}(pageSize: 3) {
                    items { id displayName }
                  }
                  secondPage: {{L}}(page: 1, pageSize: 3) {
                    items { id displayName }
                  }
                }
                """, note: "An alias names a field in the result, so the same field can be asked for twice with different arguments.");

            add("fragments", $$"""
                {
                  firstPage: {{L}}(pageSize: 2) {
                    items { ...{{T}}Fields }
                  }
                  secondPage: {{L}}(page: 1, pageSize: 2) {
                    items { ...{{T}}Fields }
                  }
                }

                fragment {{T}}Fields on {{T}} {
                  {{lines(TypeScriptWriter.SampleFields(main.Type), 2)}}
                }
                """, note: $"The fragment {T}Fields is written once and used twice.");

            var poly = roots.Select(r => (Root: r, Sub: subtypeWithOwnField(r.Type))).OrderByDescending(x => x.Root.Node != null).FirstOrDefault(x => x.Sub.Type != null);
            if (poly.Sub.Type != null) {
                var (sub, own) = poly.Sub;
                add("inline-fragments", $$"""
                    {
                      {{poly.Root.List.Name}}(pageSize: 10) {
                        items {
                          __typename
                          id
                          displayName
                          ... on {{sub!.Name}} {
                            {{own!.Name}}{{subselection(own)}}
                          }
                        }
                      }
                    }
                    """, note: $"{poly.Root.Type.Name} items can be of type {sub.Name}; only those carry {own.Name}. __typename tells each item's type.");
            } else gap("inline-fragments", "No exposed type has subtypes with fields of their own, so every result has one shape.");

            add("directives", $$"""
                query Details($withDetails: Boolean = false) {
                  {{L}}(pageSize: 3) {
                    totalCount @skip(if: $withDetails)
                    items {
                      id
                      displayName
                      createdUtc @include(if: $withDetails)
                      changedUtc @include(if: $withDetails)
                    }
                  }
                }
                """, new { withDetails = true }, "Set withDetails to false in the variables and run again: the dates go and totalCount comes back.");

            add("typename", $$"""
                {
                  __typename
                  {{L}}(pageSize: 2) {
                    __typename
                    items { __typename id displayName }
                  }
                }
                """, note: "__typename can be asked for on any object, the root included.");

            var other = roots.FirstOrDefault(r => r != main && r.Node != null) ?? roots.FirstOrDefault(r => r != main);
            add("multiple-roots", other != null ? $$"""
                {
                  {{L}}(pageSize: 2) {
                    totalCount
                    items { id displayName }
                  }
                  {{other.List.Name}}(pageSize: 2) {
                    totalCount
                    items { id displayName }
                  }
                }
                """ : $$"""
                {
                  {{L}}(pageSize: 2) {
                    totalCount
                    items { id displayName }
                  }
                  __typename
                }
                """, note: "One request, one round trip, any number of root fields.");

            add("operation-name", $$"""
                query Count {
                  {{L}} {
                    totalCount
                  }
                }

                query FirstThree {
                  {{L}}(pageSize: 3) {
                    items { id displayName createdUtc }
                  }
                }
                """, note: "A request with several operations names the one to run in operationName.");
        } catch (Exception ex) {
            // an example that cannot be written for this data does not take the rest of the guide with it
            foreach (var id in _typeTopics) {
                if (!guide.Examples.Any(e => e.Id == id) && !guide.Unavailable.Any(u => u.Id == id)) gap(id, "The example could not be written: " + ex.Message);
            }
        }

        var view = schema.QueryType.Fields.FirstOrDefault(f => f.Source == FieldSource.RootView);
        if (view != null) {
            add("views", $$"""
                {
                  {{view.Name}}(pageSize: 5) {
                    totalCount
                    items { id displayName }
                  }
                }
                """, note: $"{view.Name} starts from the query {view.ViewQuery}; filter, search, orderBy and paging work on top of it.");
        } else gap("views", "The endpoint defines no views; add one under Views.");

        if (def.EnableIntrospection) {
            var typeName = main?.Type.Name ?? schema.QueryType.Name;
            add("introspection-type", $$"""
                {
                  __type(name: "{{typeName}}") {
                    name
                    kind
                    description
                    fields {
                      name
                      description
                      type { name kind ofType { name kind } }
                    }
                  }
                }
                """, note: "This is how tools such as code generators learn the schema.");
            add("introspection-schema", """
                {
                  __schema {
                    queryType { fields { name description } }
                    mutationType { fields { name } }
                    types { name kind }
                  }
                }
                """);
        } else {
            gap("introspection-type", "Introspection is switched off for this endpoint (Settings); the explorer does not need it.");
            gap("introspection-schema", "Introspection is switched off for this endpoint (Settings); the explorer does not need it.");
        }

        if (schema.MutationType == null) {
            var reason = def.AllowMutations ? "No exposed type can be written." : "Mutations are not allowed on this endpoint (Settings).";
            gap("create", reason);
            gap("update", reason);
            gap("delete", reason);
        } else {
            var writable = new[] { main }.Concat(roots).Where(r => r != null).Select(r => (Root: r!, Create: schema.MutationType.Fields.FirstOrDefault(f => f.Source == FieldSource.MutationCreate && f.TargetNodeType == r!.NodeType)))
                .FirstOrDefault(x => x.Create != null);
            if (writable.Create?.GetArgument("input")?.Type.UnwrapNamed() is GqlInputObjectType input) {
                var r = writable.Root;
                var sel = "id displayName";
                add("create", $$"""
                    mutation Create($input: {{input.Name}}!) {
                      {{writable.Create.Name}}(input: $input) { {{sel}} }
                    }
                    """, new Dictionary<string, object?> { ["input"] = ExampleQueries.sampleInput(input) },
                    $"Running it adds a {r.Type.Name} to the database. The answer is the new node, selected like any other.", mutation: true);
                var update = schema.MutationType.Fields.FirstOrDefault(f => f.Source == FieldSource.MutationUpdate && f.TargetNodeType == r.NodeType);
                if (update != null) {
                    var textField = input.InputFields.FirstOrDefault(f => f.Source == FieldSource.ScalarProperty && f.Property?.PropertyType == PropertyType.String);
                    var change = new Dictionary<string, object?>();
                    if (textField != null) change[textField.Name] = "Updated";
                    else if (input.InputFields.Count > 0) change[input.InputFields[0].Name] = ExampleQueries.sampleValue(input.InputFields[0]);
                    add("update", $$"""
                        mutation Update($id: ID!, $input: {{input.Name}}!) {
                          {{update.Name}}(id: $id, input: $input) { {{sel}} }
                        }
                        """, new Dictionary<string, object?> { ["id"] = IdPlaceholder.Replace("a node", "the node to change"), ["input"] = change },
                        "Only the fields given in input change; a null clears a value.", mutation: true);
                } else gap("update", "The type cannot be updated.");
                var delete = schema.MutationType.Fields.FirstOrDefault(f => f.Source == FieldSource.MutationDelete && f.TargetNodeType == r.NodeType);
                if (delete != null) {
                    add("delete", $$"""
                        mutation Delete($id: ID!) {
                          {{delete.Name}}(id: $id)
                        }
                        """, new { id = IdPlaceholder.Replace("a node", "the node to delete") }, "Answers true when the node was deleted.", mutation: true);
                } else gap("delete", "The type cannot be deleted.");
            } else {
                gap("create", "No exposed type can be written.");
                gap("update", "No exposed type can be written.");
                gap("delete", "No exposed type can be written.");
            }
        }
        return guide;
    }

    // ---- choosing what to show ----

    static int score(Root r) {
        var s = 0;
        if (r.Node != null) s += 8;
        if (r.Filter?.InputFields.Any(f => f.Property != null && isText(f)) == true) s += 4;
        if (relationOf(r.Type) != null) s += 2;
        // filters and orderings on indexed properties answer at once, on any size of database
        if (r.Filter?.InputFields.Any(indexed) == true) s += 3;
        if (r.OrderBy != null) s += 1;
        if (r.NodeType.Namespace == "Relatude.DB.Native.Models") s -= 10;
        return s;
    }

    sealed record FilterCandidate(Root Root, GqlInputField Field, object? Value);

    static IEnumerable<FilterCandidate> filterCandidates(Root main, List<Root> roots, Func<GqlInputField, bool> accept) {
        foreach (var r in new[] { main }.Concat(roots.Where(x => x != main))) {
            if (r.Filter == null) continue;
            foreach (var f in r.Filter.InputFields) {
                if (f.Property == null || !accept(f)) continue;
                yield return new FilterCandidate(r, f, valueOf(r.Node, f));
            }
        }
    }

    /// <summary>The best candidate: one with a stored value (so it finds something), on an indexed property (so it is quick).</summary>
    static FilterCandidate? best(IEnumerable<FilterCandidate> candidates)
        => candidates.OrderByDescending(c => (c.Value != null ? 2 : 0) + (indexed(c.Field) ? 1 : 0)).FirstOrDefault();

    static bool indexed(GqlInputField f) => f.Property?.Indexed == true;

    static bool isText(GqlInputField f) => f.Type.UnwrapNamed().Name == "StringFilterInput";
    static bool isNumber(GqlInputField f) => f.Type.UnwrapNamed().Name is "IntFilterInput" or "LongFilterInput" or "FloatFilterInput" or "DecimalFilterInput";
    static bool isDate(GqlInputField f) => f.Type.UnwrapNamed().Name == "DateTimeFilterInput";
    static bool isEnum(GqlInputField f) => f.Type.UnwrapNamed() is GqlInputObjectType io && io.TryGetInputField("eq", out var eq) && eq.Type.UnwrapNamed() is GqlEnumType;

    static object? valueOf(INodeData? node, GqlInputField f) {
        if (node == null || f.Property == null || !node.TryGetValue(f.Property.Id, out var value)) return null;
        // an exact match needs the whole value: a long text, or one over several lines, makes a poor example
        if (value is string s) return s.Length == 0 || s.Length > 60 || s.Contains('\n') || s.Contains('\r') ? null : s;
        return value;
    }

    static string? wordOf(Root r) {
        var text = r.Type.Fields.Where(f => f.Property?.PropertyType == PropertyType.String && r.Node != null)
            .Select(f => ExampleQueries.sampleString(r.Node, f.Property)).FirstOrDefault(s => ExampleQueries.firstWord(s) != null);
        return ExampleQueries.firstWord(text);
    }

    static GqlField? relationOf(IGqlCompositeType type)
        => type.Fields.FirstOrDefault(f => f.Source is FieldSource.RelationMany or FieldSource.ReferenceMany)
        ?? type.Fields.FirstOrDefault(f => f.Source is FieldSource.RelationOne or FieldSource.ReferenceOne);

    static string top(GqlField relation) => relation.GetArgument("top") != null ? "(top: 3)" : "";

    static (GqlObjectType? Type, GqlField? Field) subtypeWithOwnField(IGqlCompositeType type) {
        if (type is not GqlInterfaceType iface) return (null, null);
        foreach (var sub in iface.PossibleTypes) {
            var own = sub.Fields.Where(f => !iface.TryGetField(f.Name, out _)).OrderBy(f => f.Source is FieldSource.ScalarProperty or FieldSource.EnumProperty ? 0 : 1).FirstOrDefault();
            if (own != null) return (sub, own);
        }
        return (null, null);
    }

    static string subselection(GqlField f) => f.Type.UnwrapNamed() switch {
        IGqlCompositeType c when c.Fields.Any(x => x.Name == "displayName") => " { id displayName }",
        IGqlCompositeType c => " { " + string.Join(" ", c.Fields.Where(x => x.Type.UnwrapNamed() is not IGqlCompositeType).Take(3).Select(x => x.Name)) + " }",
        _ => "",
    };

    /// <summary>The field to show next to a filter or order: the field itself, or nothing when the item type lacks it.</summary>
    static string leaf(IGqlCompositeType type, string name) => type.TryGetField(name, out var f) ? name + subselection(f) : "";

    static (string Lo, string Hi) rangeAround(GqlInputField f, object? value) {
        switch (value) {
            case int i: return (literal(i), literal(i + Math.Max(10, Math.Abs(i))));
            case long l: return (literal(l), literal(l + Math.Max(10L, Math.Abs(l))));
            case double d: return (literal(Math.Floor(d)), literal(Math.Ceiling(d + Math.Max(10, Math.Abs(d)))));
            case float fl: return (literal(Math.Floor(fl)), literal(Math.Ceiling(fl + Math.Max(10, Math.Abs(fl)))));
            case decimal m: return (literal(Math.Floor(m)), literal(Math.Ceiling(m + Math.Max(10m, Math.Abs(m)))));
            case DateTime dt when dt > DateTime.MinValue.AddDays(31) && dt < DateTime.MaxValue.AddDays(-31): return (literal(dt.ToUniversalTime().AddDays(-30)), literal(dt.ToUniversalTime().AddDays(30)));
            case DateTimeOffset dto when dto.UtcDateTime > DateTime.MinValue.AddDays(31) && dto.UtcDateTime < DateTime.MaxValue.AddDays(-31): return (literal(dto.UtcDateTime.AddDays(-30)), literal(dto.UtcDateTime.AddDays(30)));
        }
        return isDate(f) ? ("\"2000-01-01T00:00:00Z\"", "\"2100-01-01T00:00:00Z\"") : ("0", "100");
    }

    // ---- writing ----

    static string literal(object value) => value switch {
        string s => JsonSerializer.Serialize(s),
        bool b => b ? "true" : "false",
        DateTime dt => "\"" + dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\"",
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value.ToString()),
    };

    static string named(NodeTypeModel type, INodeData node) {
        var name = DisplayName(type, node);
        return string.IsNullOrWhiteSpace(name) ? "" : $" ({name})";
    }

    /// <summary>The node's display name, as the endpoint's displayName field gives it.</summary>
    public static string? DisplayName(NodeTypeModel type, INodeData node) {
        string? name;
        if (!string.IsNullOrWhiteSpace(node.DisplayName)) name = node.DisplayName;
        else {
            try { name = type.GetDisplayName(node); } catch { name = null; }
        }
        name = name?.Trim();
        return string.IsNullOrEmpty(name) ? null : name.Length > 60 ? name[..60] + "…" : name;
    }

    /// <summary>Selection lines for a raw string: the first line sits where the placeholder is, the rest get the indentation.</summary>
    static string lines(List<string> fields, int indent) => string.Join("\n" + new string(' ', indent), fields.Select(f => f.Trim()));

    static string text(string query) => query.Replace("\r\n", "\n").TrimEnd() + "\n";
}
