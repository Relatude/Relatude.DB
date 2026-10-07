using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL;
using Relatude.DB.GraphQL.Endpoints;
using Relatude.DB.GraphQL.Schema;
using Relatude.DB.IO;
using Relatude.DB.Query.Data;

namespace Relatude.DB.NodeServer.UI;

/// <summary>The GraphQL endpoints page: list, edit, preview (schema + TypeScript), save, delete and try out endpoints.</summary>
sealed class UIGraphQL(RelatudeDBServer server) {
    sealed record StorePayload(Guid StoreId);
    sealed record EndpointPayload(Guid StoreId, Guid Id);
    sealed record DefinitionPayload(Guid StoreId, JsonElement Definition);
    sealed record ExecutePayload(Guid StoreId, Guid? Id, JsonElement? Definition, string Query, JsonElement? Variables, string? OperationName);
    sealed record IdsPayload(Guid StoreId, Guid[]? Ids);

    internal void Register(UICommands commands) {
        commands.Register("graphql-endpoints", ctx => (object?)list(ctx.Payload<StorePayload>().StoreId));
        commands.Register("graphql-endpoint", ctx => {
            var p = ctx.Payload<EndpointPayload>();
            var c = container(p.StoreId);
            var file = server.GraphQL.Find(c.Settings.Id, p.Id) ?? throw new Exception("The endpoint was not found.");
            return (object?)new { definition = definitionJson(file.Definition!), file = file.Display, source = sourceName(file.Source), preview = preview(c, file.Definition!) };
        });
        commands.Register("graphql-preview", ctx => {
            var p = ctx.Payload<DefinitionPayload>();
            var c = container(p.StoreId);
            return (object?)preview(c, parse(p.Definition));
        });
        commands.Register("graphql-save", ctx => {
            var p = ctx.Payload<DefinitionPayload>();
            var c = container(p.StoreId);
            var def = parse(p.Definition);
            def.Name = def.Name.Trim();
            def.Url = GraphQLEndpointDefinition.NormalizeUrl(def.Url) ?? def.Url;
            foreach (var key in def.ApiKeys) {
                key.Name = key.Name?.Trim() ?? "";
                key.Key = key.Key?.Trim() ?? "";
            }
            var issues = validate(c, def);
            var errors = issues.Where(i => i.IsError).Select(i => i.Message).ToList();
            if (errors.Count > 0) throw new Exception(string.Join(" ", errors));
            var saved = server.GraphQL.Save(c, def);
            return (object?)new { id = def.Id, file = saved.Display, source = sourceName(saved.Source) };
        });
        commands.Register("graphql-delete", ctx => {
            var p = ctx.Payload<EndpointPayload>();
            server.GraphQL.Delete(container(p.StoreId), p.Id);
            return (object?)new { deleted = true };
        });
        // what DATA holds, and moving it into SETTINGS or dropping it
        commands.Register("graphql-data-move", ctx => {
            var p = ctx.Payload<IdsPayload>();
            var c = container(p.StoreId);
            var moved = server.GraphQL.MoveToSettings(c, p.Ids ?? []);
            return (object?)new { moved, dataEntries = dataEntries(c) };
        });
        commands.Register("graphql-data-discard", ctx => {
            var p = ctx.Payload<IdsPayload>();
            var c = container(p.StoreId);
            var discarded = server.GraphQL.DiscardData(c, p.Ids ?? []);
            return (object?)new { discarded, dataEntries = dataEntries(c) };
        });
        commands.Register("graphql-reload", ctx => {
            server.GraphQL.Invalidate();
            return (object?)list(ctx.Payload<StorePayload>().StoreId);
        });
        commands.Register("graphql-examples", ctx => {
            var p = ctx.Payload<DefinitionPayload>();
            var c = container(p.StoreId);
            var dm = datamodelOf(c) ?? throw new Exception("The database is not open.");
            var schema = RelatudeGraphQL.BuildSchema(dm, parse(p.Definition));
            var store = c.IsOpen() && c.Store != null ? c.Store.Datastore : null;
            // one stored node per type makes the ids and filter values in the examples real
            Func<NodeTypeModel, INodeData?>? sampler = store == null ? null : t =>
                (store.Query(t.CodeName + ".Page(0, 1)", Array.Empty<Relatude.DB.Query.Parameter>(), null) as IStoreNodeDataCollection)?.NodeValues.FirstOrDefault();
            return (object?)new { groups = ExampleQueries.Build(schema, sampler) };
        });
        commands.Register("graphql-explorer", ctx => {
            // what the explorer needs: the schema described type by type, one stored node per type (so ids in the
            // builder are real) and the guide's examples written for this endpoint
            var p = ctx.Payload<DefinitionPayload>();
            var c = container(p.StoreId);
            var dm = datamodelOf(c) ?? throw new Exception("The database is not open.");
            var schema = RelatudeGraphQL.BuildSchema(dm, parse(p.Definition));
            var store = c.IsOpen() && c.Store != null ? c.Store.Datastore : null;
            return (object?)ExplorerData.Build(schema, store == null ? null : ExplorerData.StoreSampler(store, null));
        });
        commands.Register("graphql-execute", ctx => {
            var p = ctx.Payload<ExecutePayload>();
            var c = container(p.StoreId);
            if (!c.IsOpen() || c.Store == null) throw new Exception("The database is not open.");
            RelatudeGraphQL executor;
            if (p.Definition is JsonElement e && e.ValueKind == JsonValueKind.Object) {
                executor = new RelatudeGraphQL(c.Store.Datastore, parse(e), GraphQL.GraphQLEndpointServer.OptionsFor(c.Store));
            } else if (p.Id is Guid id) {
                var file = server.GraphQL.Find(c.Settings.Id, id) ?? throw new Exception("The endpoint was not found.");
                executor = server.GraphQL.GetExecutor(c, file.Definition!);
            } else throw new Exception("Pass the endpoint id or a definition.");
            var result = executor.Execute(new GraphQLRequest {
                Query = p.Query, Variables = p.Variables, OperationName = p.OperationName, Origin = GraphQLHttpHandler.OriginOf(ctx.Http.Request),
            });
            return (object?)new { result = JsonDocument.Parse(result.ToJson()).RootElement.Clone() };
        });
    }

    NodeStoreContainer container(Guid storeId) {
        if (!server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Container not found. ");
        return c;
    }

    static GraphQLEndpointDefinition parse(JsonElement e) => GraphQLEndpointDefinition.FromJson(e.GetRawText());

    // the definition's own json shape (camelCase, enums by name), the same as in its file
    static JsonElement definitionJson(GraphQLEndpointDefinition def) => JsonDocument.Parse(def.ToJson()).RootElement.Clone();

    static Datamodel? datamodelOf(NodeStoreContainer c) => c.IsOpen() && c.Store != null ? c.Store.Datastore.Datamodel : c.Datamodel;

    List<GraphQLEndpointIssue> validate(NodeStoreContainer c, GraphQLEndpointDefinition def) {
        var issues = GraphQLEndpointValidator.Validate(def, datamodelOf(c), server.GraphQL.AllDefinitions());
        var url = GraphQLEndpointDefinition.NormalizeUrl(def.Url);
        if (url != null && !string.IsNullOrEmpty(server.ApiUrlRoot)) {
            var root = GraphQLEndpointDefinition.NormalizeUrl(server.ApiUrlRoot);
            if (root != null && (url.Equals(root, StringComparison.OrdinalIgnoreCase) || url.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))) {
                issues.Add(new GraphQLEndpointIssue { Severity = "error", Message = $"The url is inside the admin API root {root}." });
            }
        }
        return issues;
    }

    object list(Guid storeId) {
        var c = container(storeId);
        var files = server.GraphQL.Files(c.Settings.Id);
        var store = server.GraphQL.StoreFor(c);
        var dm = datamodelOf(c);
        return new {
            open = c.IsOpen(),
            state = c.StateName,
            adminRoot = server.ApiUrlRoot,
            endpoints = files.Select(f => f.Definition == null
                ? new EndpointSummary(null, f.FileName, f.Display, sourceName(f.Source), null, false, "Selected", false, false, false, false, false, false, 0, 0, f.Error)
                : new EndpointSummary(f.Definition.Id, f.Definition.Name, f.Display, sourceName(f.Source), f.Definition.Url, f.Definition.Enabled, f.Definition.Mode.ToString(),
                    f.Definition.ExactNames, f.Definition.AllowMutations, f.Definition.EnableExplorer, f.Definition.EnableFacetSearch, f.Definition.EnableIntrospection,
                    f.Definition.RequiresApiKey,
                    f.Definition.Mode == GraphQLEndpointMode.WholeDatamodel ? (dm == null ? 0 : exposableTypes(dm, f.Definition.IncludeSystemTypes).Count()) : f.Definition.Types.Count,
                    f.Definition.Views.Count, null)).ToList(),
            catalog = dm == null ? null : catalog(dm),
            // where the definitions are kept: SETTINGS in relatude.settings, DATA with the database
            settingsFolder = store?.Folders.Settings?.Display,
            dataFolder = store?.Folders.Data.Display,
            dataEntries = dataEntries(c),
            // where relatude.settings is not the one in source control, moving into it is undone by the next deployment
            development = server.IsDevelopment,
            environment = server.EnvironmentName,
            // a url is the server's, across every database, so a new endpoint is given one nobody answers on yet
            usedUrls = server.GraphQL.AllDefinitions().Select(d => GraphQLEndpointDefinition.NormalizeUrl(d.Url)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    static string sourceName(DefinitionSource source) => source == DefinitionSource.Settings ? "settings" : "data";

    /// <summary>What DATA holds, endpoint by endpoint: added, changed or taken away on this installation.</summary>
    object[] dataEntries(NodeStoreContainer c) {
        var store = server.GraphQL.StoreFor(c);
        if (store == null) return [];
        return [.. server.GraphQL.Layers(c.Settings.Id).Where(l => l.DataKey != null).Select(l => {
            string? name = null;
            try {
                name = l.DataText != null ? GraphQLEndpointDefinition.FromJson(l.DataText).Name : l.SettingsText != null ? GraphQLEndpointDefinition.FromJson(l.SettingsText).Name : null;
            } catch { }
            return (object)new {
                id = l.Id, name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(l.DataKey!.FileName()) : name,
                change = UICustomLogs.changeName(l.Change!.Value),
                dataFile = store.Folders.Data.DisplayOf(l.DataKey!),
                settingsFile = l.SettingsKey == null ? null : store.Folders.Settings?.DisplayOf(l.SettingsKey),
            };
        })];
    }

    sealed record EndpointSummary(Guid? Id, string Name, string File, string Source, string? Url, bool Enabled, string Mode, bool ExactNames, bool AllowMutations, bool Explorer, bool Facets, bool Introspection, bool ApiKey, int TypeCount, int ViewCount, string? Error);

    static IEnumerable<NodeTypeModel> exposableTypes(Datamodel dm, bool includeSystem)
        => dm.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId && !t.Hidden && !t.IsInnerNode && (includeSystem || t.Namespace != "Relatude.DB.Native.Models"));

    /// <summary>The datamodel as the editor shows it: every exposable type with its properties, by id.</summary>
    static object catalog(Datamodel dm) {
        dm.EnsureInitalization();
        return new {
            types = exposableTypes(dm, true).OrderBy(t => t.CodeName, StringComparer.Ordinal).Select(t => new {
                id = t.Id,
                name = t.CodeName,
                fullName = t.FullName,
                isInterface = t.IsInterface,
                isSystem = t.Namespace == "Relatude.DB.Native.Models",
                parents = t.Parents,
                properties = t.AllProperties.Values.Where(p => !p.Internal).OrderBy(p => p.CodeName, StringComparer.Ordinal).Select(p => new {
                    id = p.Id,
                    name = p.CodeName,
                    kind = kindOf(p),
                    inherited = p.NodeType != t.Id,
                    target = targetOf(dm, p),
                    supported = p.PropertyType is not (PropertyType.Any or PropertyType.ByteArray or PropertyType.FloatArray or PropertyType.Embedded),
                    writable = p.PropertyType is not (PropertyType.Any or PropertyType.ByteArray or PropertyType.FloatArray or PropertyType.Embedded or PropertyType.File),
                }).ToList(),
            }).ToList(),
        };
    }

    static string kindOf(PropertyModel p) => p switch {
        RelationPropertyModel r => r.IsMany ? "Relation (many)" : "Relation",
        ReferencePropertyModel => "Reference",
        ReferencesPropertyModel => "References",
        IntegerPropertyModel { IsEnum: true } => "Enum",
        EnumArrayPropertyModel => "Enum array",
        _ => p.PropertyType.ToString(),
    };

    static string? targetOf(Datamodel dm, PropertyModel p) {
        List<Guid>? ids = p switch {
            RelationPropertyModel r when dm.Relations.TryGetValue(r.RelationId, out var rel) => r.FromTargetToSource ? rel.SourceTypes : rel.TargetTypes,
            ReferencePropertyModel rp => rp.NodeTypes,
            ReferencesPropertyModel rsp => rsp.NodeTypes,
            _ => null,
        };
        if (ids == null || ids.Count == 0) return null;
        return string.Join(", ", ids.Select(id => dm.NodeTypes.TryGetValue(id, out var t) ? t.CodeName : id.ToString()));
    }

    /// <summary>What the definition gives: validation issues, builder warnings, the schema and the TypeScript code.</summary>
    object preview(NodeStoreContainer c, GraphQLEndpointDefinition def) {
        var issues = validate(c, def);
        var dm = datamodelOf(c);
        if (dm == null) return new { issues, warnings = Array.Empty<string>(), sdl = (string?)null, types = (string?)null, sample = (string?)null, csharpTypes = (string?)null, csharpSample = (string?)null, sampleQuery = (string?)null, typeCount = 0, mutationCount = 0 };
        GqlSchema schema;
        try {
            schema = RelatudeGraphQL.BuildSchema(dm, def);
        } catch (Exception ex) {
            issues.Add(new GraphQLEndpointIssue { Severity = "error", Message = "The schema could not be built: " + ex.Message });
            return new { issues, warnings = Array.Empty<string>(), sdl = (string?)null, types = (string?)null, sample = (string?)null, csharpTypes = (string?)null, csharpSample = (string?)null, sampleQuery = (string?)null, typeCount = 0, mutationCount = 0 };
        }
        return new {
            issues,
            warnings = schema.Warnings,
            sdl = SdlWriter.Write(schema),
            types = TypeScriptWriter.WriteTypes(schema),
            sample = TypeScriptWriter.WriteSample(schema),
            csharpTypes = CSharpWriter.WriteTypes(schema),
            csharpSample = CSharpWriter.WriteSample(schema),
            sampleQuery = TypeScriptWriter.WriteSampleQuery(schema),
            typeCount = schema.ObjectTypesByNodeTypeId.Count,
            mutationCount = schema.MutationType?.Fields.Count ?? 0,
        };
    }
}
