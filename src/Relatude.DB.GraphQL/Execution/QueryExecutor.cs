using System.Diagnostics;
using System.Text;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL.Language;
using Relatude.DB.GraphQL.Schema;
using Relatude.DB.Query.Data;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>
/// Orchestrates a GraphQL request: parse → operation selection → variables → validation →
/// per-root-field translation into one Relatude query (filter/search/orderBy/paging + merged includes) → projection.
/// Mutation root fields run serially through <see cref="MutationExecutor"/>.
/// </summary>
internal static class QueryExecutor {

    public static GraphQLResult Execute(RelatudeGraphQL host, IDataStore store, GraphQLRequest request, QueryContext? queryContext) {
        var requestTimer = Stopwatch.StartNew();
        try {
            if (string.IsNullOrWhiteSpace(request.Query)) {
                throw new GraphQLRequestException(new GraphQLError { Message = "No query provided." });
            }
            Document document;
            try {
                document = Parser.Parse(request.Query);
            } catch (GraphQLSyntaxException ex) {
                throw new GraphQLRequestException(new GraphQLError { Message = ex.Message, Locations = [new ErrorLocation { Line = ex.Line, Column = ex.Column }] });
            }
            var fragments = DocumentWalker.CollectFragments(document);
            var op = selectOperation(document, request.OperationName);
            if (op.Operation == OperationKind.Subscription) {
                throw new GraphQLRequestException(new GraphQLError { Message = "Subscriptions are not supported by this endpoint." });
            }
            var isMutation = op.Operation == OperationKind.Mutation;
            if (isMutation && host.Schema.MutationType == null) {
                throw new GraphQLRequestException(new GraphQLError { Message = "Mutations are not supported: this endpoint is read-only." });
            }
            var ctx = new ExecutionContext {
                Host = host, Schema = host.Schema, Endpoint = host.Definition, Store = store,
                Document = document, Fragments = fragments, QueryContext = queryContext, Origin = request.Origin,
            };
            DocumentWalker.EnsureNoFragmentCycles(ctx);
            var depth = DocumentWalker.MaxDepth(ctx, op.SelectionSet);
            if (depth > host.Definition.MaxQueryDepth) {
                throw ctx.RequestError($"Query depth {depth} exceeds the maximum of {host.Definition.MaxQueryDepth}.");
            }
            ctx.Variables = VariableCoercer.Coerce(ctx, op, request.Variables);
            var root = isMutation ? host.Schema.MutationType! : host.Schema.QueryType;
            DocumentWalker.Validate(ctx, op, root);

            var data = new Dictionary<string, object?>();
            var rootFields = DocumentWalker.CollectFields(ctx, name => name == root.Name, [op.SelectionSet]);
            foreach (var cf in rootFields) {
                var name = cf.First.Name;
                var path = new List<object> { cf.Key };
                try {
                    if (name == "__typename") {
                        data[cf.Key] = root.Name;
                    } else if (!isMutation && name == "__schema") {
                        data[cf.Key] = TreeProjector.Project(ctx, host.Introspection.SchemaData, cf.SelectionSets);
                    } else if (!isMutation && name == "__type") {
                        data[cf.Key] = resolveTypeIntrospection(ctx, host, cf);
                    } else if (root.TryGetField(name, out var fieldDef)) {
                        data[cf.Key] = isMutation
                            ? MutationExecutor.Execute(ctx, fieldDef, cf, path)
                            : executeRootField(ctx, fieldDef, cf, path);
                    } else {
                        data[cf.Key] = null; // unreachable after validation
                    }
                } catch (GraphQLRequestException) {
                    throw;
                } catch (GraphQLFieldException fe) {
                    data[cf.Key] = null;
                    ctx.AddError(fe.Message, fe.Node ?? cf.First, path);
                } catch (Exception ex) {
                    data[cf.Key] = null;
                    ctx.AddError(ex.Message, cf.First, path);
                }
            }
            return new GraphQLResult {
                Data = data,
                Errors = ctx.Errors.Count > 0 ? ctx.Errors : null,
                Extensions = timing(requestTimer),
            };
        } catch (GraphQLRequestException rex) {
            return new GraphQLResult { Errors = [rex.Error], Extensions = timing(requestTimer) };
        }
    }

    static Dictionary<string, object?> timing(Stopwatch sw) => new(StringComparer.Ordinal) {
        ["durationMs"] = Round(sw.Elapsed.TotalMilliseconds),
    };

    /// <summary>Microsecond resolution is plenty and keeps float noise out of the response.</summary>
    static double Round(double ms) => Math.Round(ms, 3);

    static OperationDefinition selectOperation(Document document, string? operationName) {
        var operations = document.Definitions.OfType<OperationDefinition>().ToList();
        if (operations.Count == 0) {
            throw new GraphQLRequestException(new GraphQLError { Message = "The document contains no operations." });
        }
        if (!string.IsNullOrEmpty(operationName)) {
            var named = operations.FirstOrDefault(o => o.Name == operationName);
            return named ?? throw new GraphQLRequestException(new GraphQLError { Message = $"Operation \"{operationName}\" was not found in the document." });
        }
        if (operations.Count > 1) {
            throw new GraphQLRequestException(new GraphQLError { Message = "The document contains multiple operations; specify operationName." });
        }
        return operations[0];
    }

    static object? resolveTypeIntrospection(ExecutionContext ctx, RelatudeGraphQL host, CollectedField cf) {
        string? typeName = null;
        foreach (var a in cf.First.Arguments) {
            if (a.Name == "name") {
                typeName = ValueResolver.Resolve(ctx, a.Value, new GqlNonNullType(ctx.Schema.Scalars.String)) as string;
                break;
            }
        }
        if (typeName == null) return null;
        return host.Introspection.TypesByName.TryGetValue(typeName, out var typeData)
            ? TreeProjector.Project(ctx, typeData, cf.SelectionSets)
            : null;
    }

    static object? executeRootField(ExecutionContext ctx, GqlField field, CollectedField cf, List<object> path) {
        var nodeType = field.TargetNodeType!;
        var args = Arguments.Resolve(ctx, field, cf.First);
        if (field.Source == FieldSource.RootSingle) {
            var idText = Arguments.GetString(args, "id");
            if (idText == null || !Guid.TryParse(idText, out var id)) {
                throw new GraphQLFieldException($"\"{idText}\" is not a valid node id.");
            }
            return FetchAndProjectNode(ctx, nodeType, id, (GqlNamedType)field.Type.UnwrapNamed(), cf.SelectionSets, path);
        }
        return executeList(ctx, field, cf, nodeType, args, path);
    }

    /// <summary>Loads one node with the relations the selection asks for and projects it; null when it does not exist (or is not visible).</summary>
    public static object? FetchAndProjectNode(ExecutionContext ctx, NodeTypeModel nodeType, Guid id, GqlNamedType declaredType, IEnumerable<SelectionSet> sets, List<object> path) {
        var parameters = new ParameterBag();
        var sb = new StringBuilder(nodeType.CodeName);
        sb.Append($".WhereInIds({parameters.Add(new[] { id })})");
        var selections = sets.ToList();
        appendIncludes(ctx, sb, declaredType, selections);
        var collection = RunQuery(ctx, sb.ToString(), parameters);
        var node = collection.NodeValues.FirstOrDefault();
        return node == null ? null : Projector.ProjectNode(ctx, node, selections, path);
    }

    static object executeList(ExecutionContext ctx, GqlField field, CollectedField cf, NodeTypeModel nodeType, Dictionary<string, object?> args, List<object> path) {
        var wrapper = (GqlObjectType)field.Type.UnwrapNamed();
        var wrapperFields = DocumentWalker.CollectFields(ctx, name => name == wrapper.Name, cf.SelectionSets);

        var parameters = new ParameterBag();
        // a view starts from the query its definition gives it; a plain list from the type itself
        var sb = new StringBuilder(field.Source == FieldSource.RootView ? field.ViewQuery! : nodeType.CodeName);
        if (args.TryGetValue("search", out var searchValue) && searchValue is string search && search.Length > 0) {
            sb.Append($".WhereSearch({parameters.Add(search)})");
        }
        if (args.TryGetValue("filter", out var filterValue) && filterValue is Dictionary<string, object?> filter) {
            var filterType = (GqlInputObjectType)field.GetArgument("filter")!.Type.UnwrapNamed();
            var predicate = FilterTranslator.Translate(filter, filterType, parameters);
            if (predicate != null) sb.Append($".Where(n => {predicate})");
        }
        if (args.TryGetValue("ids", out var idsValue) && idsValue is List<object?> idsList) {
            var guids = idsList.Select(v => v is string s && Guid.TryParse(s, out var g)
                ? g : throw new GraphQLFieldException($"\"{v}\" is not a valid node id.")).ToArray();
            sb.Append($".WhereInIds({parameters.Add(guids)})");
        }
        if (args.TryGetValue("orderBy", out var orderValue) && orderValue is GqlEnumValue order && order.Property != null) {
            var descending = Arguments.GetBool(args, "descending");
            sb.Append($".OrderBy(n => n.{order.Property.CodeName}, {(descending ? "true" : "false")})");
        }
        var page = Math.Max(0, Arguments.GetInt(args, "page") ?? 0);
        var pageSize = Math.Clamp(Arguments.GetInt(args, "pageSize") ?? ctx.Endpoint.DefaultPageSize, 1, Math.Max(1, ctx.Endpoint.MaxPageSize));
        sb.Append($".Page({page}, {pageSize})");

        // includes come from the union of all items selections
        var itemsSelections = new List<SelectionSet>();
        foreach (var wf in wrapperFields) {
            if (wrapper.TryGetField(wf.First.Name, out var wfd) && wfd.Source == FieldSource.WrapperItems) {
                itemsSelections.AddRange(wf.SelectionSets);
            }
        }
        if (ctx.Schema.ReferenceTypesByNodeTypeId.TryGetValue(nodeType.Id, out var itemType)) {
            appendIncludes(ctx, sb, itemType, itemsSelections);
        }

        var fetchTimer = Stopwatch.StartNew();
        var collection = RunQuery(ctx, sb.ToString(), parameters);
        var nodes = collection.NodeValues.ToList();
        fetchTimer.Stop();

        var result = new Dictionary<string, object?>(wrapperFields.Count);
        foreach (var wf in wrapperFields) {
            var wrapperFieldName = wf.First.Name;
            if (wrapperFieldName == "__typename") { result[wf.Key] = wrapper.Name; continue; }
            if (!wrapper.TryGetField(wrapperFieldName, out var wfd)) { result[wf.Key] = null; continue; }
            switch (wfd.Source) {
                case FieldSource.WrapperItems: {
                        var items = new List<object?>(nodes.Count);
                        var index = 0;
                        foreach (var node in nodes) {
                            items.Add(Projector.ProjectNode(ctx, node, wf.SelectionSets, [.. path, wf.Key, index]));
                            index++;
                        }
                        result[wf.Key] = items;
                        break;
                    }
                case FieldSource.WrapperTotalCount: result[wf.Key] = collection.TotalCount; break;
                case FieldSource.WrapperPageIndex: result[wf.Key] = collection.PageIndexUsed; break;
                case FieldSource.WrapperPageSize: result[wf.Key] = collection.PageSizeUsed; break;
                case FieldSource.WrapperExecutionTimeMs: result[wf.Key] = Round(fetchTimer.Elapsed.TotalMilliseconds); break;
                default: result[wf.Key] = null; break;
            }
        }
        return result;
    }

    static void appendIncludes(ExecutionContext ctx, StringBuilder sb, GqlNamedType declaredType, IEnumerable<SelectionSet> sets) {
        foreach (var includePath in IncludePlanner.Plan(ctx, declaredType, sets)) {
            sb.Append($".Include(\"{includePath}\")"); // paths contain only Guids and ints
        }
    }

    public static IStoreNodeDataCollection RunQuery(ExecutionContext ctx, string queryText, ParameterBag parameters) {
        object? result;
        try {
            result = ctx.Store.Query(queryText, parameters.Parameters, ctx.QueryContext);
        } catch (Exception ex) {
            throw new GraphQLFieldException("Query execution failed: " + ex.Message);
        }
        return result as IStoreNodeDataCollection
            ?? throw new GraphQLFieldException("The query did not return a node collection.");
    }
}
