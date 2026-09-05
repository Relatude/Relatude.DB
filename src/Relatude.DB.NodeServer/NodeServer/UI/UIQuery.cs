using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.FileConversion;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.DB.Query.Data;
using Relatude.DB.Web;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The query section of the admin UI: search a node type, narrow it with facets, open a hit and edit it.
///
/// Two things are worth knowing about how this talks to the store.
///
/// Facets are automatic. A facet query with no AddFacet call lets the engine pick the facetable
/// properties of whatever the result set holds (Definition.GetFacetPropertiesForSet), which is the
/// authoritative answer and covers types this page knows nothing about. The engine leaves out the
/// properties with too many distinct values to facet on, but the ones it keeps can still hold more
/// buckets than a rail can show, so they are trimmed here, after counting - never by asking the
/// engine for fewer, which would turn the automatic selection off (SetFacetOptions implies AddFacet).
///
/// Nothing here goes through the object mapper. The query string is built with the typed query API
/// but executed against the datastore directly, so hits and facet buckets arrive as node data. A
/// database whose model exists only as json has no compiled classes to map to, and this page has to
/// work there too.
/// </summary>
sealed class UIQuery {
    // what a facet rail can show before it becomes a scroll of its own; "show all" raises it per facet
    const int maxFacetValues = 24;
    const int maxFacetValuesExpanded = 500;
    const int maxSummaryValues = 4;
    const int summaryValueLength = 160;
    // the snippet under a hit: long enough to carry the words around the match, short enough to stay
    // two lines. maxSampledProperties bounds the search for it - a wide type holding long texts must
    // not turn one page of hits into a scan of everything it has
    const int snippetLength = 220;
    const int maxSampledProperties = 20;
    const int maxLookupResults = 50;
    const int maxInnerNodesShown = 20;
    const int maxPageSize = 200;
    const int maxTableColumns = 50;
    const int maxTableCellLength = 300;
    const int maxReferencesInCell = 5;
    internal const int maxCsvRows = 50_000;
    const int maxPreviewSize = 4000; // the largest image the media route will render, per side
    const double videoThumbnailAt = 10; // percent into a clip: the first frame of one is often black

    readonly RelatudeDBServer _server;
    internal UIQuery(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("query-model", ctx => model(ctx.Payload<StorePayload>().StoreId));
        commands.Register("query-search", async ctx => await search(ctx.Payload<SearchPayload>()));
        commands.Register("query-node", ctx => node(ctx.Payload<NodePayload>()));
        commands.Register("query-save", async ctx => await save(ctx.Payload<SavePayload>()));
        commands.Register("query-create", async ctx => await create(ctx.Payload<CreatePayload>()));
        commands.Register("query-save-embedded", async ctx => await saveEmbedded(ctx.Payload<SaveEmbeddedPayload>()));
        commands.Register("query-node-meta", ctx => nodeMeta(ctx.Payload<NodePayload>()));
        commands.Register("query-save-meta", ctx => saveMeta(ctx.Payload<SaveMetaPayload>()));
        commands.Register("query-node-versions", ctx => nodeVersions(ctx.Payload<VersionsPayload>()));
        commands.Register("query-lookup", ctx => lookup(ctx.Payload<LookupPayload>()));
        commands.Register("query-pivot-model", ctx => pivotModel(ctx.Payload<PivotModelPayload>()));
        commands.Register("query-pivot", async ctx => await pivot(ctx.Payload<PivotPayload>()));
        commands.Register("query-groupby", async ctx => await groupBy(ctx.Payload<GroupByPayload>()));
        commands.Register("query-columns", ctx => columnsOf(ctx.Payload<ColumnsPayload>()));
    }

    // Reading and writing as an administrator: hidden and unpublished nodes are part of what this
    // page is for. Deleted ones are not - they are not editable and would only be confusing here.
    static readonly QueryContext adminContext = QueryContext.Default.Admin().Hidden().Unpublished().CultureFallbacks();

    NodeStore store(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        if (c.Store == null || c.Store.State != DataStoreState.Open) throw new Exception("The database is not open. ");
        return c.Store;
    }
    Settings.NodeStoreContainerSettings? settingsOf(Guid storeId) => _server.Containers.TryGetValue(storeId, out var c) ? c.Settings : null;

    // ---- the model behind the page: what can be queried, and which search options mean anything ----

    object model(Guid storeId) {
        var s = store(storeId);
        var dm = s.Datastore.Datamodel;
        // A type is worth listing when it can be queried at all. Inner node types cannot: they exist
        // only inside an embedded property of another node and have no id set of their own.
        var types = dm.NodeTypes.Values
            .Where(t => !t.IsInnerNode)
            // the base type is every node at once rather than a type anyone named, so it heads the
            // list instead of sorting under I for INode
            .OrderBy(t => t.Id == NodeConstants.BaseNodeTypeId ? 0 : 1)
            .ThenBy(t => t.CodeName, StringComparer.OrdinalIgnoreCase)
            .Select(t => (object)new {
                t.Id,
                Name = t.CodeName,
                t.FullName,
                t.IsInterface,
                t.Hidden,
                IsBase = t.Id == NodeConstants.BaseNodeTypeId,
                // the type picker marks a type the way the model editor does: its kind by icon shape,
                // the source it came from by colour
                Kind = t.ModelType.ToString(),
                SourceId = t.DatamodelSourceId,
                // whether "New node" can make one: the base type stands for all types at once, and a
                // type the model knows but no loaded assembly implements has nothing to instantiate
                CanCreate = t.Id != NodeConstants.BaseNodeTypeId && findClrType(dm, t) != null,
                // the id set per type is maintained, so counting it is a lookup and not a scan
                Count = count(s, t.Id),
            })
            .ToArray();
        // The semantic half of the search only exists when there is an AI provider to embed the query
        // with AND something indexed by it; without both, the two sliders would change nothing.
        var ai = aiSettings(s);
        var semantic = dm.Properties.Values.Any(p => p is StringPropertyModel sp && sp.IndexedBySemantic && !sp.Internal)
            || dm.NodeTypes.Values.Any(t => t.SemanticIndex == true);
        return new {
            StoreId = storeId,
            Types = types,
            Sources = UIModelInfo.Sources(dm, settingsOf(storeId)),
            BaseTypeId = NodeConstants.BaseNodeTypeId,
            HasAi = ai != null,
            HasSemanticIndex = semantic,
            // what the engine uses when the query leaves them out, so the sliders start where the
            // database itself starts (see NodeCollectionData.ResolveSearchSettings)
            DefaultSemanticRatio = ai?.DefaultSemanticRatio ?? 0.5,
            DefaultMinimumSimilarity = ai?.DefaultMinimumSimilarity ?? 0.34,
        };
    }
    static int count(NodeStore s, Guid typeId) {
        try {
            return s.QueryType(typeId, adminContext).Count();
        } catch {
            return 0; // one type the engine cannot enumerate must not take the whole type list down
        }
    }
    static AI.AIProviderSettings? aiSettings(NodeStore s) {
        if (!s.Datastore.HasAIEngine) return null;
        return s.Datastore.AI.Settings;
    }

    // ---- the search itself ----

    async Task<object> search(SearchPayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        var typeId = queriedType(dm, p.TypeId);
        var nodeType = dm.NodeTypes[typeId];
        var pageSize = Math.Clamp(p.PageSize <= 0 ? 25 : p.PageSize, 1, maxPageSize);
        var pageIndex = Math.Max(0, p.Page);
        var queryString = queryFor(s, dm, p, typeId, pageIndex, pageSize);

        var sw = Stopwatch.StartNew();
        // executed on the datastore rather than through the facet query object, so hits and buckets
        // stay as node data instead of being mapped to model classes that may not exist
        var data = await s.Datastore.QueryAsync(queryString, [], adminContext);
        sw.Stop();
        // a query with no facet clause answers as a plain collection: same hits, no buckets
        var result = data as FacetQueryResultData;
        var nodes = result?.Result ?? data as IStoreNodeDataCollection
            ?? throw new Exception("The query did not return a collection of nodes. ");

        var expanded = new HashSet<Guid>(p.Expanded ?? []);
        var facets = result == null || !p.Facets ? [] : result.Facets.Values
            .Select(f => facetView(dm, f, expanded.Contains(f.PropertyId)))
            .Where(f => f != null)
            .OrderBy(f => f!.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // the table needs a value per column for every row, which is a node read per cell; the list
        // needs a handful of values per row. Only one of them is built.
        var columns = columnsFor(dm, nodeType, p);
        var terms = termsOf(p.Text);
        var hits = nodes.NodeValues
            .Select(n => columns == null ? hitView(dm, n, terms) : hitView(dm, n, terms, s, columns, maxTableCellLength))
            .ToArray();
        return new {
            TypeId = typeId,
            TypeName = nodeType.CodeName,
            Total = nodes.TotalCount,
            SourceCount = result?.SourceCount ?? nodes.TotalCount, // nothing was filtered out without facets
            Page = pageIndex,
            PageSize = pageSize,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            Query = queryString,
            Facets = facets,
            Columns = columns?.Select(c => (object)new { c.Key, c.Name, Type = c.TypeName, Sortable = c.Property != null && isSortable(c.Property) }).ToArray(),
            // false when a sort was asked for and could not be given, so the page never claims an order it has not got
            SortApplied = string.IsNullOrEmpty(p.SortBy) || queryString.Contains(".OrderBy(", StringComparison.Ordinal),
            Hits = hits,
        };
    }

    static Guid queriedType(Datamodel dm, Guid? given)
        => given is Guid id && dm.NodeTypes.ContainsKey(id) ? id : NodeConstants.BaseNodeTypeId;

    // Every word the page splits the search text into, with a trailing wildcard. The search runs on
    // every keystroke, so the word being typed is almost always half a word, and a term the index
    // has to match whole would find nothing until the moment it is finished - TermSet.Parse reads
    // the trailing star as a prefix term, which is what makes "cor" find "cork".
    //
    // A word already carrying a wildcard or a fuzzy marker is left exactly as written: someone who
    // types their own search syntax means it. The wildcard stays out of the separator set for the
    // same reason - splitting on it would hide the very character being looked for. Only the word
    // index sees any of this; the semantic half is given the plain words (SearchUtil.StripOperators).
    static readonly char[] searchWordSeparators = [.. SearchConst.DEVIDERS.Where(c => c != SearchConst.WILDCARD)];
    static string prefixEachWord(string text) {
        var words = text.Split(searchWordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return text;
        return string.Join(' ', words.Select(w =>
            w.Contains(SearchConst.WILDCARD) || w.Contains(SearchConst.FUZZY) ? w : w + SearchConst.WILDCARD));
    }

    /// <summary>
    /// The query string behind the page: the type, the free text search, and the facet selection.
    ///
    /// The facet clause is left off entirely when the page is neither showing facets nor filtering
    /// by one. Counting buckets is the expensive half of this query - every facetable property of
    /// the result set, every page - and asking for it when nothing will read it is the difference
    /// between browsing a large store and waiting for it.
    /// </summary>
    static string queryFor(NodeStore s, Datamodel dm, SearchPayload p, Guid typeId, int pageIndex, int pageSize) {
        var q = s.QueryType(typeId, adminContext);
        if (!string.IsNullOrWhiteSpace(p.Text)) {
            q = q.WhereSearch(prefixEachWord(p.Text), p.SemanticRatio, (float?)p.MinimumSimilarity);
        }
        var selections = (p.Selections ?? []).Where(s => dm.Properties.ContainsKey(s.PropertyId) && s.Values?.Length > 0).ToArray();
        var order = selections.Length == 0 ? orderClause(dm, p.SortBy, p.SortDescending) : "";
        if (!p.Facets && selections.Length == 0) return paged(q, pageIndex, pageSize, order);
        var fq = q.Facets();
        foreach (var selection in selections) {
            foreach (var value in selection.Values!) {
                if (value.Value == null) fq = fq.SetFacetMissingValue(selection.PropertyId);
                else if (value.Value2 == null) fq = fq.SetFacetValue(selection.PropertyId, value.Value);
                else fq = fq.SetFacetRangeValue(selection.PropertyId, value.Value, value.Value2);
            }
        }
        var withFacets = fq.Page(pageIndex, pageSize).ToString();
        if (order.Length == 0) return withFacets;
        // The ordering has to go on the node query, before the facet clauses, and the facet query
        // has no way to take it: it renders as the node query's own string followed by its clauses.
        // So it is spliced in at that seam, which the check below makes sure is where it looks.
        var baseQuery = q.ToString();
        if (baseQuery == null || !withFacets.StartsWith(baseQuery, StringComparison.Ordinal)) return withFacets;
        return baseQuery + order + withFacets[baseQuery.Length..];
    }

    /// <summary>
    /// An OrderBy clause for the column the table is sorted by, or nothing.
    ///
    /// It is dropped as soon as a facet is selected: filtering by one is a set intersection, and the
    /// set that comes out of an intersection is in id order whatever order went in, so the two
    /// cannot both hold. The selection wins - it is what the result IS, where the order is only how
    /// it is read - and the page is told (SortApplied) so it can say so rather than draw an arrow
    /// over rows that are not in that order. Only a property whose
    /// values have an order can be sorted on: a relation is a list, an embedded value is a document,
    /// and an array has no single key to sort by, so none of them are offered.
    /// </summary>
    static string orderClause(Datamodel dm, string? sortBy, bool descending) {
        if (string.IsNullOrEmpty(sortBy) || !Guid.TryParse(sortBy, out var propertyId)) return "";
        if (!dm.Properties.TryGetValue(propertyId, out var property) || !isSortable(property)) return "";
        return ".OrderBy(n => n." + property.CodeName + (descending ? ", true)" : ")");
    }
    static bool isSortable(PropertyModel property) => property is not RelationPropertyModel && property.PropertyType is
        PropertyType.String or PropertyType.Integer or PropertyType.Long or PropertyType.Double or PropertyType.Float
        or PropertyType.Decimal or PropertyType.DateTime or PropertyType.DateTimeOffset or PropertyType.TimeSpan
        or PropertyType.Boolean or PropertyType.Guid or PropertyType.Reference;

    // Paging appended as text rather than through the query object: its Page operator passes the
    // numbers as query parameters, and the parameter list it builds them in is internal to the query
    // API, so the query string on its own would arrive with them unbound. (The facet query's own
    // Page writes literals and needs none of this.)
    static string paged(IQueryOfNodes<object, object> q, int pageIndex, int pageSize, string order = "")
        => q + order + ".Page(" + pageIndex.ToString(CultureInfo.InvariantCulture) + ", " + pageSize.ToString(CultureInfo.InvariantCulture) + ")";

    // ---- the table: one column per property ----

    /// <summary>A column of the table view: either one of the node's own fields or a property of its type.</summary>
    sealed record Column(string Key, string Name, string TypeName, PropertyModel? Property);

    // Every node has these whatever its type, and on the base type ("all node types") they are the
    // only columns there are - the base type declares nothing but internal properties.
    static readonly Column[] metaColumns = [
        new("__type", "Type", "NodeType", null),
        new("__name", "Display name", "String", null),
        new("__id", "Id", "Guid", null),
        new("__address", "Address", "String", null),
        new("__created", "Created (UTC)", "DateTime", null),
        new("__changed", "Changed (UTC)", "DateTime", null),
    ];

    /// <summary>
    /// The columns of the table, most identifying first: what the type shows itself by, then its own
    /// properties, then the inherited ones. Capped, because a wide type would otherwise make a table
    /// nobody can read out of a query nobody meant to run.
    /// </summary>
    static Column[] tableColumns(Datamodel dm, NodeTypeModel type) => [.. metaColumns, .. propertyColumns(dm, type).Take(maxTableColumns)];
    static IEnumerable<Column> propertyColumns(Datamodel dm, NodeTypeModel type) {
        var display = type.DisplayProperties.Select(p => p.Id).ToHashSet();
        return type.AllProperties.Values
            .Where(p => !p.Internal)
            .OrderBy(p => display.Contains(p.Id) ? 0 : 1)
            .ThenBy(p => p.NodeType == type.Id ? 0 : 1)
            .ThenBy(p => p.CodeName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new Column(p.Id.ToString(), p.CodeName, typeNameOf(dm, p), p));
    }

    /// <summary>
    /// The columns a request wants: none for the list, the type's own table for the table view, and
    /// exactly the ones named - in the order named - when the page has chosen them (its select mode).
    /// A key that is not a column of this type is skipped rather than failing the query: the choice
    /// was made against the type as it was, and the type may have changed since.
    /// </summary>
    static Column[]? columnsFor(Datamodel dm, NodeTypeModel type, SearchPayload p) {
        if (!p.Table) return null;
        if (p.Columns == null) return tableColumns(dm, type);
        var all = metaColumns.Concat(propertyColumns(dm, type)).ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        return p.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Where(all.ContainsKey).Select(k => all[k]).ToArray();
    }

    /// <summary>Every column a type can show, uncapped: what the select mode picks from.</summary>
    object columnsOf(ColumnsPayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        var type = dm.NodeTypes[queriedType(dm, p.TypeId)];
        return new {
            TypeId = type.Id,
            TypeName = type.CodeName,
            Columns = metaColumns.Concat(propertyColumns(dm, type)).Select(c => new {
                c.Key,
                c.Name,
                Type = c.TypeName,
                Sortable = c.Property != null && isSortable(c.Property),
                // a node field is nobody's; a property inherited from another type says whose
                DeclaredBy = c.Property == null || c.Property.NodeType == type.Id ? null
                    : dm.NodeTypes.TryGetValue(c.Property.NodeType, out var owner) ? owner.CodeName : null,
            }).ToArray(),
        };
    }
    static string typeNameOf(Datamodel dm, PropertyModel p) => p switch {
        RelationPropertyModel r => "Relation" + (dm.Relations.TryGetValue(r.RelationId, out var rel) ? " (" + rel.CodeName + ")" : ""),
        _ => p.PropertyType.ToString(),
    };

    /// <summary>One cell. Relations are counted rather than listed: a row is not the place to read a list.</summary>
    static string cell(NodeStore s, Datamodel dm, INodeDataExternal n, Column column, int maxLength) {
        if (column.Property == null) {
            return column.Key switch {
                "__type" => typeName(dm, n),
                "__name" => displayNameOf(dm, n),
                "__id" => n.Id.ToString(),
                "__address" => n.Address ?? "",
                "__created" => utc(n.CreatedUtc),
                "__changed" => utc(n.ChangedUtc),
                _ => "",
            };
        }
        var property = column.Property;
        if (property is RelationPropertyModel) {
            try {
                var count = s.Datastore.GetRelatedCountFromPropertyId(property.Id, n.Id, adminContext);
                return count == 0 ? "" : count.ToString(CultureInfo.InvariantCulture);
            } catch {
                return "";
            }
        }
        if (!n.TryGetValue(property.Id, out var value)) return "";
        // a reference holds an id, which says nothing to whoever is reading the table
        if (property is ReferencePropertyModel && value is Guid one) return truncate(referenceName(s, dm, one), maxLength);
        if (property is ReferencesPropertyModel && value is Guid[] many) {
            return truncate(string.Join(", ", many.Take(maxReferencesInCell).Select(id => referenceName(s, dm, id))), maxLength);
        }
        return truncate(display(property, value), maxLength);
    }
    static string referenceName(NodeStore s, Datamodel dm, Guid id) {
        if (id == Guid.Empty) return "";
        return s.Datastore.TryGet(id, out var n, adminContext) ? displayNameOf(dm, n) : id.ToString();
    }
    static string utc(DateTime value) => value == default ? "" : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    // ---- the result set as a csv file ----

    /// <summary>
    /// Streams the whole matching set as csv, not just the page on screen. Capped: this runs on the
    /// request thread and holds the store's read lock, so an unbounded export of a large database
    /// would be a way to stall it. The header row says when the cap was reached.
    /// </summary>
    internal async Task WriteCsv(HttpContext http, SearchPayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        var typeId = queriedType(dm, p.TypeId);
        var nodeType = dm.NodeTypes[typeId];
        var columns = columnsFor(dm, nodeType, p with { Table = true }) ?? tableColumns(dm, nodeType);
        // whatever the page is showing, an export reads no buckets: ask for none
        var queryString = queryFor(s, dm, p with { Facets = false }, typeId, 0, maxCsvRows);
        var data = await s.Datastore.QueryAsync(queryString, [], adminContext);
        // with a facet selection to apply the answer is a facet result and the rows are inside it;
        // with nothing to filter by there is no facet clause at all and the rows are the answer
        var nodes = (data as FacetQueryResultData)?.Result ?? data as IStoreNodeDataCollection
            ?? throw new Exception("The query did not return a collection of nodes. ");
        var name = nodeType.CodeName + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = "attachment; filename=\"" + name + "\"";
        var writer = new StreamWriter(http.Response.Body, new System.Text.UTF8Encoding(true)); // BOM: spreadsheets read the file as utf-8 without being told
        await using (writer.ConfigureAwait(false)) {
            await writer.WriteAsync(csvRow(columns.Select(c => c.Name)));
            foreach (var n in nodes.NodeValues) {
                if (http.RequestAborted.IsCancellationRequested) return;
                await writer.WriteAsync(csvRow(columns.Select(c => cell(s, dm, n, c, int.MaxValue))));
            }
        }
    }
    // ---- the bytes behind a file property: what the form previews ----

    /// <summary>
    /// Serves a file property: the file as it was uploaded, or an image adjusted to the asked for
    /// size - which for a video is a frame taken out of it. The form reads its previews from here
    /// rather than from the public url of the file, because the admin UI has to work on a database
    /// whose files are not published at all, and because it already knows who is asking.
    ///
    /// Conversions are not waited for beyond what the store itself waits: a variant that is not
    /// converted yet answers with the conversion engine's own status image and says so in
    /// X-Relatude-Ready, and the caller asks again until it is ready.
    /// </summary>
    internal async Task<IResult> WriteMedia(HttpContext http, Guid storeId, string? pathText, int? width, int? height, bool original) {
        var s = store(storeId);
        if (string.IsNullOrEmpty(pathText) || !PropertyPath.TryParse(pathText, out var path)) {
            return Results.Json(new { error = "Not a property path. " }, RelatudeDBJsonOptions.Default, statusCode: 400);
        }
        if (!s.Datastore.Datamodel.Properties.TryGetValue(path.PropertyId, out var property) || property is not FilePropertyModel) {
            return Results.Json(new { error = "The path does not point at a file property. " }, RelatudeDBJsonOptions.Default, statusCode: 400);
        }
        if (!s.Datastore.TryGetValue<FileValue>(path, out var file, adminContext) || file.IsEmpty) {
            return Results.Json(new { error = "The property holds no file. " }, RelatudeDBJsonOptions.Default, statusCode: 404);
        }
        if (original) { // what a video plays from, and what a format no converter reads is shown as
            var stream = await s.Datastore.GetFileStream(path, adminContext);
            return await FileHandler.HandleFileAsync(http, stream, file.Name, false, file.ContentType, true);
        }
        var adj = new FileAdjustmentImage {
            Width = width is > 0 ? Math.Min(width.Value, maxPreviewSize) : null,
            Height = height is > 0 ? Math.Min(height.Value, maxPreviewSize) : null,
        };
        if (file.FileType == FileType.Video) adj.TimeOffsetPercentage = videoThumbnailAt;
        adj.BasicSanitization();
        var state = await s.Datastore.GetFileStreamAndState(path, adj, -1, adminContext);
        http.Response.Headers["X-Relatude-Ready"] = state.IsReady ? "1" : "0";
        var name = Path.GetFileNameWithoutExtension(file.Name) + FileFormatUtil.GetExtensionWithDot(state.RequestedFormat);
        return await FileHandler.HandleFileAsync(http, state.Stream, name, false, FileFormatUtil.GetContentType(state.RequestedFormat), state.IsReady);
    }

    static string csvRow(IEnumerable<string> cells) {
        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var value in cells) {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"'); // every field quoted: values hold commas, quotes and newlines
        }
        return sb.Append("\r\n").ToString();
    }

    /// <summary>
    /// One facet as the rail shows it. Empty buckets are dropped - a selected one never is, it has to
    /// stay clickable to be turned off - and what is left is trimmed to the values worth showing.
    /// </summary>
    static FacetView? facetView(Datamodel dm, Facets facets, bool expanded) {
        dm.Properties.TryGetValue(facets.PropertyId, out var property);
        var isRange = facets.IsRangeFacet == true;
        var values = facets.Values.Where(v => v.Count > 0 || v.Selected).ToList();
        if (values.Count == 0) return null;
        var total = values.Count;
        var limit = expanded ? maxFacetValuesExpanded : maxFacetValues;
        var truncated = values.Count > limit;
        if (truncated) {
            if (isRange) {
                // ranges are a scale, not a ranking: keep the first buckets in order, plus any selection
                var kept = values.Take(limit).ToHashSet();
                foreach (var v in values) if (v.Selected) kept.Add(v);
                values = [.. values.Where(kept.Contains)];
            } else {
                var kept = values.OrderByDescending(v => v.Selected).ThenByDescending(v => v.Count).Take(limit).ToHashSet();
                values = [.. values.Where(kept.Contains)];
            }
        }
        // a range is a scale and keeps the order it was generated in; everything else reads best
        // as a ranking, biggest bucket first
        if (!isRange) values = [.. values.OrderByDescending(v => v.Count)];
        return new FacetView {
            PropertyId = facets.PropertyId,
            CodeName = facets.CodeName ?? "",
            DisplayName = facets.DisplayName,
            ValueType = facets.ValueType.ToString(),
            IsRange = isRange,
            Truncated = truncated,
            TotalValues = total,
            Values = [.. values.Select(v => (object)new {
                Value = wire(v.Value),
                Value2 = wire(v.Value2),
                Display = facetValueLabel(property, v),
                v.Count,
                v.Selected,
            })],
        };
    }
    sealed class FacetView {
        public Guid PropertyId { get; init; }
        public string CodeName { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string ValueType { get; init; } = "";
        public bool IsRange { get; init; }
        public bool Truncated { get; init; }
        public int TotalValues { get; init; }
        public object[] Values { get; init; } = [];
    }

    // What a bucket is called. The engine names the ones only it can name - enum members, related and
    // referenced nodes - and everything else is formatted here, because FacetValue.ToString renders
    // values in the server's culture and a range as "from - to" with no thought for the type.
    static string facetValueLabel(PropertyModel? property, FacetValue v) {
        if (v.ExplicitDisplayName != null) return v.ExplicitDisplayName;
        if (v.Value == null) return "(none)";
        var from = display(property, v.Value);
        if (v.Value2 == null) return from;
        return from + " – " + display(property, v.Value2);
    }

    /// <summary>One hit as the result list shows it: what it is, and enough of it to recognize it by.</summary>
    static object hitView(Datamodel dm, INodeDataExternal n, TermSet terms) => hitView(dm, n, terms, null, null, 0);
    static object hitView(Datamodel dm, INodeDataExternal n, TermSet terms, NodeStore? s, Column[]? columns, int maxLength) {
        dm.NodeTypes.TryGetValue(n.NodeType, out var type);
        var (name, nameProperty) = nameOf(dm, n);
        var searching = terms.Terms.Length > 0;
        // the table carries a value per column and the list a few telling ones; a row never needs both
        var summary = new List<object>();
        object? snippet = null;
        if (type != null && columns == null) {
            var shown = type.DisplayProperties.Select(p => p.Id).ToHashSet(); // already in the title
            if (nameProperty.HasValue) shown.Add(nameProperty.Value);
            var candidates = new List<(PropertyModel Property, string Text)>();
            foreach (var property in type.AllProperties.Values.OrderBy(p => p.CodeName, StringComparer.OrdinalIgnoreCase)) {
                if (candidates.Count >= (searching ? maxSampledProperties : maxSummaryValues)) break;
                if (property.Internal || shown.Contains(property.Id)) continue;
                if (!isSummaryType(property.PropertyType)) continue;
                if (!n.TryGetValue(property.Id, out var value)) continue;
                var text = display(property, value);
                if (string.IsNullOrWhiteSpace(text)) continue;
                candidates.Add((property, text));
            }
            // With a search on, the first value actually holding one of the searched words becomes the
            // snippet: the line that says why this node is in the result, sampled around the match by
            // the engine's own TextSample. Its property is then left out of the summary underneath,
            // which would repeat it in a worse form - a truncated head that may not even reach the word.
            Guid? snippetProperty = null;
            if (searching) {
                foreach (var (property, text) in candidates) {
                    var sample = new TextSample(terms, text, snippetLength);
                    if (!matched(sample)) continue;
                    snippet = new { property.CodeName, Value = sampleText(sample), Sample = sampleView(sample) };
                    snippetProperty = property.Id;
                    break;
                }
            }
            foreach (var (property, text) in candidates) {
                if (summary.Count >= maxSummaryValues) break;
                if (property.Id == snippetProperty) continue;
                summary.Add(new { property.CodeName, Value = truncate(text, summaryValueLength) });
            }
        }
        // the name is where a search matches most often, and it is never sampled away: the whole name
        // is kept and only marked (TextSample clamps the sample length to the text it is given)
        var nameSample = searching && name.Length > 0 ? new TextSample(terms, name, name.Length) : null;
        return new {
            n.Id,
            IntId = n.__Id,
            TypeId = n.NodeType,
            TypeName = type?.CodeName ?? n.NodeType.ToString(),
            DisplayName = name,
            NameSample = nameSample != null && matched(nameSample) ? sampleView(nameSample) : null,
            Snippet = snippet,
            n.Address,
            CreatedUtc = n.CreatedUtc,
            ChangedUtc = n.ChangedUtc,
            Summary = summary,
            Cells = columns == null || s == null ? null : columns.Select(c => cell(s, dm, n, c, maxLength)).ToArray(),
        };
    }

    /// <summary>
    /// The words a hit is marked on: the search text as the index reads it. <see cref="TermSet.Parse"/>
    /// strips the operators, so the wildcard <see cref="prefixEachWord"/> appends is gone and "cor*"
    /// marks the "cor" of "cork" - the part the index actually matched. The word length limits are the
    /// model's defaults rather than any one property's: a hit is sampled from whichever property holds
    /// the text, and they may each say something different.
    /// </summary>
    static TermSet termsOf(string? text) => string.IsNullOrWhiteSpace(text) ? TermSet.Empty
        : TermSet.Parse(text, StringPropertyModel.DefaultMinWordLength, StringPropertyModel.DefaultMaxWordLength, allowInfix: true);

    static bool matched(TextSample sample) => sample.Fragments.Any(f => f.IsMatch);

    /// <summary>
    /// A <see cref="TextSample"/> as the page renders it: the fragments in order, the matching ones
    /// flagged, and whether text was cut off at either end. The client joins the fragments with
    /// nothing between them - <see cref="TextSample.FormatSample"/> puts a space between every
    /// fragment, which would split the word the match sits inside ("cor k").
    /// </summary>
    static object sampleView(TextSample sample) => new {
        Fragments = sample.Fragments.Select(f => new { Text = f.Fragment, f.IsMatch }).ToArray(),
        sample.CutAtStart,
        sample.CutAtEnd,
    };

    /// <summary>The sampled text as one string, ellipses and all: what the marked-up line reads as.</summary>
    static string sampleText(TextSample sample)
        => (sample.CutAtStart ? "…" : "") + string.Concat(sample.Fragments.Select(f => f.Fragment)) + (sample.CutAtEnd ? "…" : "");
    static bool isSummaryType(PropertyType t) => t is PropertyType.String or PropertyType.StringArray or PropertyType.Integer
        or PropertyType.Long or PropertyType.Double or PropertyType.Float or PropertyType.Decimal
        or PropertyType.Boolean or PropertyType.DateTime or PropertyType.DateTimeOffset or PropertyType.TimeSpan;
    static string truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    static string displayNameOf(Datamodel dm, INodeData n) => nameOf(dm, n).Name;

    // Names a node has to be recognized by even when the model never said how. A model that marks
    // its display name properties gets exactly what it asked for; one that does not (most models
    // that were never written for a UI) would otherwise leave every row reading as a guid, so a
    // likely looking text property is used instead - and the caller is told which one, to keep it
    // out of the summary line underneath.
    static readonly string[] likelyNameProperties = ["Name", "Title", "Heading", "Subject", "Code", "Key"];
    const int maxFallbackNameLength = 120;
    static (string Name, Guid? FromProperty) nameOf(Datamodel dm, INodeData n) {
        if (dm.NodeTypes.TryGetValue(n.NodeType, out var type)) {
            var declared = type.GetDisplayName(n);
            if (!string.IsNullOrWhiteSpace(declared)) return (declared, null);
        }
        if (!string.IsNullOrWhiteSpace(n.DisplayName)) return (n.DisplayName!, null);
        if (type != null) {
            foreach (var likely in likelyNameProperties) {
                if (!type.AllPropertiesByName.TryGetValue(likely, out var property)) continue;
                if (shortText(n, property) is string byName) return (byName, property.Id);
            }
            foreach (var property in type.AllProperties.Values.OrderBy(p => p.CodeName, StringComparer.OrdinalIgnoreCase)) {
                if (property.Internal || property is not StringPropertyModel || !property.Indexed) continue;
                if (shortText(n, property) is string indexed) return (indexed, property.Id);
            }
        }
        if (!string.IsNullOrWhiteSpace(n.Address)) return (n.Address!, null);
        return (n.Id.ToString(), null);
    }
    // a usable name: a string property with a short enough value to be one
    static string? shortText(INodeData n, PropertyModel property) {
        if (property.Internal || property is not StringPropertyModel) return null;
        if (!n.TryGetValue(property.Id, out var value) || value is not string s) return null;
        s = s.Trim();
        return s.Length == 0 || s.Length > maxFallbackNameLength ? null : s;
    }

    // ---- one node, as a form ----

    object node(NodePayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        if (!s.Datastore.TryGet(p.Id, out var n, adminContext)) throw new Exception("Node not found. ");
        if (!dm.NodeTypes.TryGetValue(n.NodeType, out var type)) throw new Exception("The node has a type that is not in the current data model. ");
        var properties = type.AllProperties.Values
            .Where(property => !property.Internal)
            .OrderBy(property => property is RelationPropertyModel ? 1 : 0) // relations last: they are lists, not fields
            .ThenBy(property => property.CodeName, StringComparer.OrdinalIgnoreCase)
            .Select(property => propertyView(s, dm, type, n, property))
            .ToArray();
        return new {
            n.Id,
            IntId = n.__Id,
            TypeId = type.Id,
            TypeName = type.CodeName,
            type.FullName,
            DisplayName = displayNameOf(dm, n),
            n.Address,
            CreatedUtc = n.CreatedUtc,
            ChangedUtc = n.ChangedUtc,
            Properties = properties,
        };
    }

    // ---- the node's meta: who may see it, when it is live, which revision and culture it is ----

    /// <summary>
    /// The meta of one node, with every guid explained. An access guid is a group: the well known
    /// ones have names, any other is looked up as a node; the read and edit-view guids are also
    /// resolved the way the query filter resolves them (node, then the type's default, then the
    /// database's), so the page can say what actually applies when the node itself says nothing.
    /// </summary>
    object nodeMeta(NodePayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        if (!s.Datastore.TryGet(p.Id, out var n, adminContext)) throw new Exception("Node not found. ");
        dm.NodeTypes.TryGetValue(n.NodeType, out var type);
        var meta = n.Meta ?? IInnerNodeMeta.Empty;
        var storeDefault = _server.Containers.TryGetValue(p.StoreId, out var c) ? c.Settings.LocalSettings?.DefaultReadAccess : null;
        return new {
            n.Id,
            IntId = n.__Id,
            TypeName = type?.CodeName,
            DisplayName = displayNameOf(dm, n),
            n.Address,
            n.AutoAddress,
            n.CreatedUtc,
            n.ChangedUtc,
            // an empty meta is not stored at all - the most common case, and worth saying
            Stored = n.Meta != null,
            HasRevisions = n is NodeDataRevision,
            RevisionId = n is NodeDataRevision rev ? rev.RevisionId.ToString() : null,
            RevisionType = n.RevisionType.ToString(),
            n.RevisionKey,
            Culture = idView(s, meta.CultureId),
            Collection = idView(s, meta.CollectionId),
            ReadAccess = accessView(s, meta.ReadAccess, type?.DefaultReadAccess, storeDefault),
            EditViewAccess = accessView(s, meta.EditViewAccess, type?.DefaultEditViewAccess, storeDefault),
            EditAccess = accessView(s, meta.EditAccess, type?.DefaultEditAccess, null),
            PublishAccess = accessView(s, meta.PublishAccess, type?.DefaultPublishAccess, null),
            meta.Deleted,
            CreatedBy = idView(s, meta.CreatedBy),
            ChangedBy = idView(s, meta.ChangedBy),
            meta.ReleaseUtc,
            meta.ExpireUtc,
        };
    }

    // the groups every database has, by name
    static string? wellKnownGroup(Guid id) {
        if (id == NodeConstants.UserGroupUnspecified) return "Unspecified";
        if (id == NodeConstants.UserGroupEveryone) return "Everyone";
        if (id == NodeConstants.UserGroupMember) return "Members";
        if (id == NodeConstants.UserGroupAdmins) return "Administrators";
        return null;
    }
    static string? nameOfId(NodeStore s, Guid id) {
        if (id == Guid.Empty) return null;
        if (wellKnownGroup(id) is string group) return group;
        return s.Datastore.TryGetNodeMeta(id, out var meta, adminContext) && !string.IsNullOrEmpty(meta.DisplayName) ? meta.DisplayName : null;
    }
    static object idView(NodeStore s, Guid id) => new { Value = id == Guid.Empty ? null : id.ToString(), Name = nameOfId(s, id) };
    // mirrors NodeTypesByIds.findUserGroup: the node's own value, else the type's default, else the database's
    static object accessView(NodeStore s, Guid fromMeta, Guid? fromType, Native.SystemGroupType? fromSettings) {
        Guid effective;
        string source;
        if (fromMeta != NodeConstants.UserGroupUnspecified) (effective, source) = (fromMeta, "node");
        else if (fromType is Guid t && t != NodeConstants.UserGroupUnspecified) (effective, source) = (t, "type");
        else if (fromSettings is Native.SystemGroupType g) {
            effective = g switch {
                Native.SystemGroupType.Member => NodeConstants.UserGroupMember,
                Native.SystemGroupType.Admins => NodeConstants.UserGroupAdmins,
                _ => NodeConstants.UserGroupEveryone,
            };
            source = "database";
        } else (effective, source) = (Guid.Empty, "none");
        return new {
            Value = fromMeta == Guid.Empty ? null : fromMeta.ToString(),
            Name = nameOfId(s, fromMeta),
            Effective = effective == Guid.Empty ? null : effective.ToString(),
            EffectiveName = nameOfId(s, effective),
            EffectiveSource = source,
        };
    }

    /// <summary>
    /// Writes the meta values that changed. The keys are the meta property names the store's
    /// UpdateMeta accepts; culture and revision key are not among them (they identify the revision
    /// rather than describe it), and the two audit guids are left alone here on purpose.
    /// </summary>
    object saveMeta(SaveMetaPayload p) {
        var s = store(p.StoreId);
        if (!s.Datastore.TryGet(p.Id, out var n, adminContext)) throw new Exception("Node not found. ");
        var values = new List<KeyValuePair<string, object>>();
        foreach (var (key, json) in p.Values ?? []) {
            object value = key switch {
                nameof(IInnerNodeMeta.CollectionId) or nameof(IInnerNodeMeta.ReadAccess) or nameof(IInnerNodeMeta.EditAccess)
                    or nameof(IInnerNodeMeta.EditViewAccess) or nameof(IInnerNodeMeta.PublishAccess) => metaGuid(json),
                nameof(IInnerNodeMeta.Deleted) => json.ValueKind == JsonValueKind.True,
                nameof(IInnerNodeMeta.ReleaseUtc) or nameof(IInnerNodeMeta.ExpireUtc) => metaDate(json)!,
                _ => throw new Exception("The meta property \"" + key + "\" cannot be written here. "),
            };
            values.Add(new(key, value));
        }
        if (values.Count == 0) return new { Changed = 0 };
        if (n is NodeDataRevision rev) s.UpdateMeta(p.Id, rev.RevisionId, [.. values]);
        else s.UpdateMeta(p.Id, [.. values]);
        return new { Changed = values.Count };
    }
    static Guid metaGuid(JsonElement json) {
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return Guid.Empty;
        var text = json.GetString();
        if (string.IsNullOrWhiteSpace(text)) return Guid.Empty;
        return Guid.TryParse(text, out var g) ? g : throw new Exception("\"" + text + "\" is not a guid. ");
    }
    // a date arrives as the ISO text a datetime-local field produces, without a zone: it is UTC, as the store keeps them
    static object? metaDate(JsonElement json) {
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var text = json.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            throw new Exception("\"" + text + "\" is not a date. ");
        return (DateTime?)dt;
    }

    // ---- the node's history: its older versions, read off the transaction log ----

    /// <summary>
    /// The versions of a node, newest first, each with what changed when it was written: the
    /// current version first, compared with the newest older one, then every older version
    /// compared with the one before it. The oldest reachable version has nothing to compare
    /// against, so it lists its values and no changes. Relations are not part of node data and are
    /// not in the history; a property no longer in the data model is shown by its id.
    /// </summary>
    object nodeVersions(VersionsPayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        if (!s.Datastore.TryGet(p.Id, out var current, adminContext)) throw new Exception("Node not found. ");
        var maxCount = Math.Clamp(p.MaxCount, 1, 500);
        var versions = s.Datastore.FindOlderVersions(p.Id, maxCount, adminContext);
        var rows = new List<object> { versionRow(dm, current, versions.Length > 0 ? versions[0].Node : null, null) };
        for (var i = 0; i < versions.Length; i++) {
            rows.Add(versionRow(dm, versions[i].Node, i + 1 < versions.Length ? versions[i + 1].Node : null, versions[i]));
        }
        return new {
            Rows = rows,
            Count = versions.Length,
            // exactly as many as asked for means there may be more behind them
            MayHaveMore = versions.Length >= maxCount,
        };
    }

    static object versionRow(Datamodel dm, INodeDataExternal node, INodeDataExternal? older, NodeVersionData? data) {
        dm.NodeTypes.TryGetValue(node.NodeType, out var type);
        // every property either version has a value for, model order first, then anything not in the model
        var known = type?.AllProperties.Values.Where(property => !property.Internal && property is not RelationPropertyModel)
            .OrderBy(property => property.CodeName, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        // anything the model knows but leaves out above (internal properties, relations) stays out;
        // what is left is a property the model no longer has
        var modelIds = type?.AllProperties.Keys.ToHashSet() ?? [];
        var unknownIds = node.Values.Select(v => v.PropertyId).Concat(older?.Values.Select(v => v.PropertyId) ?? [])
            .Where(id => !modelIds.Contains(id)).Distinct().OrderBy(id => id).ToList();
        var values = new List<object>();
        var changes = older == null ? null : new List<object>();
        foreach (var property in known) values.Add(versionValue(property, property.CodeName, node, older, changes));
        foreach (var id in unknownIds) values.Add(versionValue(null, id.ToString(), node, older, changes));
        // the meta is part of the node record too, and a write that only changed it (a delete flag,
        // an access group) would otherwise show as a version with no changes at all
        var meta = node.Meta ?? IInnerNodeMeta.Empty;
        var olderMeta = older?.Meta ?? IInnerNodeMeta.Empty;
        foreach (var (name, now, was) in new (string, object?, object?)[] {
            ("Deleted", meta.Deleted, olderMeta.Deleted),
            ("ReadAccess", meta.ReadAccess, olderMeta.ReadAccess),
            ("EditAccess", meta.EditAccess, olderMeta.EditAccess),
            ("EditViewAccess", meta.EditViewAccess, olderMeta.EditViewAccess),
            ("PublishAccess", meta.PublishAccess, olderMeta.PublishAccess),
            ("CollectionId", meta.CollectionId, olderMeta.CollectionId),
            ("CultureId", meta.CultureId, olderMeta.CultureId),
            ("RevisionType", meta.RevisionType.ToString(), olderMeta.RevisionType.ToString()),
            ("ReleaseUtc", meta.ReleaseUtc, olderMeta.ReleaseUtc),
            ("ExpireUtc", meta.ExpireUtc, olderMeta.ExpireUtc),
        }) {
            var shown = now is Guid g ? (wellKnownGroup(g) ?? display(null, g)) : display(null, now);
            if (older != null && token(now) != token(was)) {
                changes!.Add(new { Name = "Meta." + name, From = was is Guid wg ? (wellKnownGroup(wg) ?? display(null, wg)) : display(null, was), To = shown });
            }
            values.Add(new { Name = "Meta." + name, Type = "meta", Value = shown });
        }
        return new {
            Current = data == null,
            Timestamp = data?.Timestamp.ToString(CultureInfo.InvariantCulture),
            Utc = data?.EstimatedCreationUtc ?? node.ChangedUtc,
            data?.Source,
            TypeName = type?.CodeName ?? node.NodeType.ToString(),
            DisplayName = displayNameOf(dm, node),
            Values = values,
            Changes = changes,
        };
    }
    static object versionValue(PropertyModel? property, string name, INodeDataExternal node, INodeDataExternal? older, List<object>? changes) {
        node.TryGetValue(property?.Id ?? Guid.Parse(name), out var value);
        var shown = display(property, value);
        if (older != null) {
            older.TryGetValue(property?.Id ?? Guid.Parse(name), out var was);
            if (token(value) != token(was)) changes!.Add(new { Name = name, From = display(property, was), To = shown });
        }
        return new { Name = name, Type = property?.PropertyType.ToString(), Value = shown };
    }
    // equality for the history diff: whatever identifies the value, never its display text (two
    // different dates can display the same, and two identical files can have different display)
    static string token(object? v) => v switch {
        null => "",
        string[] a => "s:" + string.Join('', a),
        Guid[] a => "g:" + string.Join('', a),
        int[] a => "i:" + string.Join('', a),
        byte[] a => "b:" + a.Length + ":" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(a)),
        float[] a => "f:" + a.Length + ":" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()))),
        FileValue f => f.IsEmpty ? "" : "file:" + f.FileId + ":" + f.Hash + ":" + f.Name,
        IInnerNodeDataMap m => "inner:" + string.Join('', m.Select(inner => inner.Id + "=" + string.Join('', inner.Values.Select(e => e.PropertyId + ":" + token(e.Value))))),
        GeoCoordinate g => g.IsEmpty ? "" : "geo:" + g.Latitude.ToString("R", CultureInfo.InvariantCulture) + "," + g.Longitude.ToString("R", CultureInfo.InvariantCulture),
        _ => wire(v) ?? "",
    };

    static PropertyView propertyView(NodeStore s, Datamodel dm, NodeTypeModel type, INodeDataExternal n, PropertyModel property) {
        var view = new PropertyView {
            Id = property.Id,
            Name = property.CodeName,
            Type = property.PropertyType.ToString(),
            DeclaredBy = dm.NodeTypes.TryGetValue(property.NodeType, out var owner) ? owner.CodeName : null,
            Notes = [.. notes(property, type)],
            Indexed = property.Indexed,
            WordIndex = property is StringPropertyModel ws && ws.IndexedByWords,
            SemanticIndex = property is StringPropertyModel ss && ss.IndexedBySemantic,
            Unique = property.UniqueValues,
        };
        n.TryGetValue(property.Id, out var value);
        switch (property) {
            case RelationPropertyModel relation: {
                    view.Editor = "relation";
                    view.IsMany = relation.IsMany;
                    view.TargetTypes = [.. relatedTypes(dm, relation).Select(t => (object)new { t.Id, Name = t.CodeName })];
                    view.Targets = [.. related(s, property.Id, n.Id).Select(r => (object)new { r.Id, Name = displayNameOf(dm, r), TypeName = typeName(dm, r) })];
                    break;
                }
            case ReferencesPropertyModel references: {
                    view.Editor = "references";
                    view.TargetTypes = [.. references.NodeTypes.Where(dm.NodeTypes.ContainsKey).Select(id => (object)new { Id = id, Name = dm.NodeTypes[id].CodeName })];
                    var ids = value as Guid[] ?? [];
                    view.Value = ids.Select(g => g.ToString()).ToArray();
                    view.Targets = [.. describe(s, dm, ids)];
                    break;
                }
            case ReferencePropertyModel reference: {
                    view.Editor = "reference";
                    view.TargetTypes = [.. reference.NodeTypes.Where(dm.NodeTypes.ContainsKey).Select(id => (object)new { Id = id, Name = dm.NodeTypes[id].CodeName })];
                    var id = value is Guid g ? g : Guid.Empty;
                    view.Value = id == Guid.Empty ? null : id.ToString();
                    view.Targets = id == Guid.Empty ? [] : [.. describe(s, dm, [id])];
                    break;
                }
            case EmbeddedPropertyModel emb: {
                    // Inner nodes are a document inside the node. Each one is described the way the
                    // node itself is - its own properties as fields - so the same form renders them,
                    // and the whole list is written back as one (see saveEmbedded).
                    view.Editor = "embedded";
                    var map = value as IInnerNodeDataMap;
                    view.Info = map == null || map.Count == 0 ? "empty"
                        : map.Count + (map.Count == 1 ? " inner node" : " inner nodes");
                    view.TargetTypes = [.. innerTypes(dm, emb).Select(t => (object)new { t.Id, Name = t.CodeName })];
                    var embedded = new PropertyPath(n.Id, property.Id);
                    view.Value = map == null ? [] : map.Take(maxInnerNodesShown).Select(inner => (object)new {
                        inner.Id,
                        TypeId = inner.NodeType,
                        TypeName = typeName(dm, inner),
                        // an inner node is addressed through the property it hangs off, so a file
                        // inside one is previewable in the same way as a file on the node itself
                        Values = innerValues(dm, inner, embedded.CreatePathToInnerNode(inner.Id)),
                        Fields = innerFields(s, dm, inner),
                    }).ToArray();
                    if (map != null && map.Count > maxInnerNodesShown) view.ReadOnly = true; // a list this long is not edited a row at a time
                    break;
                }
            case FilePropertyModel: {
                    view.Editor = "file";
                    view.ReadOnly = true; // uploads belong to the files section, not to a property form
                    var file = value as FileValue;
                    view.Value = file == null || file.IsEmpty ? null : fileView(file, new PropertyPath(n.Id, property.Id));
                    break;
                }
            case ByteArrayPropertyModel: {
                    view.Editor = "binary";
                    view.ReadOnly = true;
                    var bytes = value as byte[];
                    view.Info = bytes == null || bytes.Length == 0 ? "empty" : bytes.Length.To1000N() + " bytes";
                    break;
                }
            case FloatArrayPropertyModel: {
                    view.Editor = "vector";
                    view.ReadOnly = true;
                    var vector = value as float[];
                    view.Info = vector == null || vector.Length == 0 ? "empty" : vector.Length.To1000N() + " dimensions";
                    break;
                }
            case GeoCoordinatePropertyModel: {
                    view.Editor = "geo";
                    var geo = value is GeoCoordinate c ? c : GeoCoordinate.Empty;
                    view.Value = geo.IsEmpty ? null : new { geo.Latitude, geo.Longitude };
                    break;
                }
            case IntegerPropertyModel integer: {
                    view.Editor = integer.IsEnum || integer.LegalValues != null ? "enum" : "integer";
                    view.Options = choices(integer.LegalValues, integer.LegalValueNames);
                    view.Min = integer.MinValue == int.MinValue ? null : integer.MinValue;
                    view.Max = integer.MaxValue == int.MaxValue ? null : integer.MaxValue;
                    view.Value = value is int i ? i : 0;
                    break;
                }
            case EnumArrayPropertyModel enums: {
                    view.Editor = "enumList";
                    view.Options = choices(enums.LegalValues, enums.LegalValueNames);
                    view.Value = value as int[] ?? [];
                    break;
                }
            case StringPropertyModel str: {
                    var code = str.StringType != StringValueType.AnyString;
                    var text = value as string ?? "";
                    view.Editor = code ? "code" : "text";
                    view.Language = code ? str.StringType.ToString() : null;
                    // Nothing in the model says whether a string is a heading or an article, so it is
                    // read off what the property does: one that is value indexed is sorted, filtered
                    // and faceted on, which no article body ever is - and off the value itself, since
                    // a field already holding several lines needs several lines to edit in.
                    view.Multiline = code || text.Length > 120 || text.Contains('\n')
                        || (!str.Indexed && str.MaxLength > 255);
                    view.MaxLength = str.MaxLength == int.MaxValue ? null : str.MaxLength;
                    view.Pattern = str.RegularExpression;
                    view.Value = text;
                    break;
                }
            case StringArrayPropertyModel: {
                    view.Editor = "stringList";
                    view.Value = value as string[] ?? [];
                    break;
                }
            case GuidArrayPropertyModel: {
                    view.Editor = "guidList";
                    view.Value = (value as Guid[] ?? []).Select(g => g.ToString()).ToArray();
                    break;
                }
            case BooleanPropertyModel: {
                    view.Editor = "bool";
                    view.Value = value is bool b && b;
                    break;
                }
            case LongPropertyModel: {
                    view.Editor = "integer";
                    // a long past 2^53 does not survive a javascript number, so it travels as text
                    view.Value = (value is long l ? l : 0L).ToString(CultureInfo.InvariantCulture);
                    break;
                }
            case DecimalPropertyModel: {
                    view.Editor = "number";
                    view.Value = (value is decimal d ? d : 0m).ToString(CultureInfo.InvariantCulture);
                    break;
                }
            case DoublePropertyModel: {
                    view.Editor = "number";
                    view.Value = value is double d ? d : 0d;
                    break;
                }
            case FloatPropertyModel: {
                    view.Editor = "number";
                    view.Value = value is float f ? f : 0f;
                    break;
                }
            case DateTimePropertyModel: {
                    view.Editor = "datetime";
                    view.Value = value is DateTime dt && dt != default ? dt.ToString("O", CultureInfo.InvariantCulture) : null;
                    break;
                }
            case DateTimeOffsetPropertyModel: {
                    view.Editor = "datetimeoffset";
                    view.Value = value is DateTimeOffset dto && dto != default ? dto.ToString("O", CultureInfo.InvariantCulture) : null;
                    break;
                }
            case TimeSpanPropertyModel: {
                    view.Editor = "timespan";
                    view.Value = (value is TimeSpan ts ? ts : TimeSpan.Zero).ToString("c", CultureInfo.InvariantCulture);
                    break;
                }
            case GuidPropertyModel: {
                    view.Editor = "guid";
                    var id = value is Guid g ? g : Guid.Empty;
                    view.Value = id == Guid.Empty ? "" : id.ToString();
                    break;
                }
            default: {
                    view.Editor = "unsupported";
                    view.ReadOnly = true;
                    view.Info = property.PropertyType.ToString();
                    break;
                }
        }
        return view;
    }

    sealed class PropertyView {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public string? DeclaredBy { get; init; }
        public string[] Notes { get; init; } = [];
        // the index marks, as flags rather than as words: every page that shows a property shows the
        // same three icons, and a string has to be read to be understood
        public bool Indexed { get; init; }
        public bool WordIndex { get; init; }
        public bool SemanticIndex { get; init; }
        public bool Unique { get; init; }
        public string Editor { get; set; } = "text";
        public bool ReadOnly { get; set; }
        public object? Value { get; set; }
        public object[]? Options { get; set; }
        public object[]? Targets { get; set; }
        public object[]? TargetTypes { get; set; }
        public bool? IsMany { get; set; }
        public bool? Multiline { get; set; }
        public string? Language { get; set; }
        public int? MaxLength { get; set; }
        public int? Min { get; set; }
        public int? Max { get; set; }
        public string? Pattern { get; set; }
        public string? Info { get; set; }
    }

    static object[]? choices(int[]? values, string[]? names) {
        if (values == null || values.Length == 0) return null;
        return [.. values.Select((v, i) => (object)new { Value = v, Label = names != null && i < names.Length ? names[i] : v.ToString(CultureInfo.InvariantCulture) })];
    }
    static IEnumerable<string> notes(PropertyModel property, NodeTypeModel type) {
        if (property.CodeName == type.NameOfDisplayNameProperty) yield return "display name";
        if (property.CodeName == type.NameOfAddressProperty) yield return "address";
        if (property.CodeName == type.NameOfCreatedUtcProperty) yield return "created utc";
        if (property.CodeName == type.NameOfChangedUtcProperty) yield return "changed utc";
        if (property.Indexed) yield return "indexed";
        if (property.UniqueValues) yield return "unique";
        if (property.CultureSensitive) yield return "culture sensitive";
        if (property is StringPropertyModel s) {
            if (s.IndexedByWords) yield return "word index";
            if (s.IndexedBySemantic) yield return "semantic index";
        }
        if (property is RelationPropertyModel r && r.Facet) yield return "facet";
    }
    static INodeDataExternal[] related(NodeStore s, Guid propertyId, Guid nodeId) {
        try {
            return s.Datastore.GetRelatedNodesFromPropertyId(propertyId, nodeId, adminContext);
        } catch {
            return []; // one relation the engine cannot resolve must not take the whole form down
        }
    }
    // The type on the other side of a relation property. RelationPropertyModel.NodeTypeOfRelated is
    // not reliable for native relation properties (it names the declaring type), so the relation
    // itself is asked, with the property's direction deciding which end is the other one.
    static NodeTypeModel[] relatedTypes(Datamodel dm, RelationPropertyModel property) {
        if (!dm.Relations.TryGetValue(property.RelationId, out var relation)) {
            return dm.NodeTypes.TryGetValue(property.NodeTypeOfRelated, out var t) ? [t] : [];
        }
        var ids = property.FromTargetToSource ? relation.SourceTypes : relation.TargetTypes;
        return [.. ids.Where(dm.NodeTypes.ContainsKey).Select(id => dm.NodeTypes[id])];
    }
    static string typeName(Datamodel dm, INodeData n) => dm.NodeTypes.TryGetValue(n.NodeType, out var t) ? t.CodeName : n.NodeType.ToString();
    static IEnumerable<object> describe(NodeStore s, Datamodel dm, IEnumerable<Guid> ids) {
        foreach (var id in ids) {
            if (id == Guid.Empty) continue;
            if (s.Datastore.TryGet(id, out var n, adminContext)) {
                yield return new { Id = id, Name = displayNameOf(dm, n), TypeName = typeName(dm, n) };
            } else {
                yield return new { Id = id, Name = id.ToString(), TypeName = (string?)null }; // a reference to a node that is gone
            }
        }
    }
    /// <summary>The concrete types an embedded property accepts: the ones it names, and what inherits them.</summary>
    static IEnumerable<NodeTypeModel> innerTypes(Datamodel dm, EmbeddedPropertyModel embedded) {
        return dm.NodeTypes.Values
            .Where(t => t.ModelType is not ModelType.Interface && embedded.InnerNodeTypes.Any(id => t.ThisAndAllInheritedTypes.ContainsKey(id)))
            .OrderBy(t => t.CodeName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An inner node's own properties as form fields, built by the same code as the node's. What it
    /// cannot edit - a file, another embedded list, a relation - is left out rather than shown dead:
    /// those are addressed through the node that owns the list.
    /// </summary>
    static object[] innerFields(NodeStore s, Datamodel dm, NodeData inner) {
        if (!dm.NodeTypes.TryGetValue(inner.NodeType, out var type)) return [];
        return [.. type.AllProperties.Values
            .Where(p => !p.Internal && p is not (EmbeddedPropertyModel or FilePropertyModel or RelationPropertyModel))
            .OrderBy(p => p.CodeName, StringComparer.OrdinalIgnoreCase)
            .Select(p => (object)propertyView(s, dm, type, inner, p))];
    }

    static object[] innerValues(Datamodel dm, INodeData inner, NodePath path) {
        if (!dm.NodeTypes.TryGetValue(inner.NodeType, out var type)) return [];
        return [.. type.AllProperties.Values
            .Where(p => !p.Internal)
            .OrderBy(p => p.CodeName, StringComparer.OrdinalIgnoreCase)
            .Select(p => {
                var has = inner.TryGetValue(p.Id, out var v);
                return (object)new {
                    p.CodeName,
                    Value = has ? truncate(display(p, v), summaryValueLength) : "",
                    File = v is FileValue file && !file.IsEmpty ? fileView(file, path.CreatePropertyPath(p.Id)) : null,
                };
            })];
    }

    /// <summary>
    /// A file value as the form shows it. Beyond what the file is, it carries the property path it
    /// sits at: that is what the media route reads the bytes back from, and it addresses a file on an
    /// inner node exactly as it addresses one on the node itself. The version is the file's own hash,
    /// so replacing a file changes every preview url of it and no browser serves the old picture.
    /// </summary>
    static object fileView(FileValue file, PropertyPath path) => new {
        file.Name,
        file.Size,
        file.ContentType,
        file.Width,
        file.Height,
        file.FileId,
        file.StorageId,
        FileType = file.FileType.ToString(),
        Format = file.Format.ToString(),
        Path = path.ToUrlString(),
        Version = file.Hash.Length > 8 ? file.Hash[..8] : file.Hash,
    };

    // ---- saving the form ----

    async Task<object> save(SavePayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        if (!s.Datastore.TryGet(p.Id, out var n, adminContext)) throw new Exception("Node not found. ");
        if (!dm.NodeTypes.TryGetValue(n.NodeType, out var type)) throw new Exception("The node has a type that is not in the current data model. ");
        var transaction = s.CreateTransaction();
        var changed = 0;
        foreach (var pair in p.Values ?? []) {
            var property = editableProperty(type, pair.Key);
            if (property is RelationPropertyModel) throw new Exception("Relations are saved through \"relations\", not \"values\". ");
            if (pair.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
                transaction.ResetProperty(p.Id, property.Id);
            } else {
                transaction.UpdateProperty(p.Id, property.Id, parse(property, pair.Value));
            }
            changed++;
        }
        foreach (var pair in p.Relations ?? []) {
            var property = editableProperty(type, pair.Key);
            if (property is not RelationPropertyModel) throw new Exception("Property " + property.CodeName + " is not a relation. ");
            changed += relate(s, transaction, p.Id, property.Id, pair.Value ?? []);
        }
        if (changed == 0) return new { Changed = 0 };
        await transaction.ExecuteAsync();
        return new { Changed = changed };
    }
    static PropertyModel editableProperty(NodeTypeModel type, string key) {
        if (!Guid.TryParse(key, out var propertyId)) throw new Exception("Not a property id: " + key + ". ");
        if (!type.AllProperties.TryGetValue(propertyId, out var property)) throw new Exception(type.CodeName + " has no property with id " + key + ". ");
        if (property.Internal) throw new Exception("Property " + property.CodeName + " is maintained by the database. ");
        return property;
    }

    // Only the difference is written: the links the form no longer has are removed and the new ones
    // added, so saving a form that did not touch a relation leaves no trace in the log at all.
    static int relate(NodeStore s, Transaction transaction, Guid nodeId, Guid propertyId, Guid[] wanted) {
        var current = related(s, propertyId, nodeId).Select(r => r.Id).ToHashSet();
        var target = wanted.Where(id => id != Guid.Empty).ToHashSet();
        var changed = 0;
        foreach (var id in current) {
            if (target.Contains(id)) continue;
            transaction.ClearRelation(nodeId, propertyId, id);
            changed++;
        }
        foreach (var id in target) {
            if (current.Contains(id)) continue;
            if (!s.Datastore.Exists(id, adminContext)) throw new Exception("There is no node with id " + id + ". ");
            transaction.AddRelation(nodeId, propertyId, id);
            changed++;
        }
        return changed;
    }

    // ---- making nodes ----

    /// <summary>
    /// The CLR type behind a node type, or null when no loaded assembly has it. An interface model
    /// resolves to the interface: the mapper makes the implementation it generated at start up.
    /// </summary>
    static Type? findClrType(Datamodel dm, NodeTypeModel type) {
        if (string.IsNullOrEmpty(type.FullName)) return null;
        foreach (var assembly in dm.Assemblies) {
            var clr = assembly.GetType(type.FullName, false, false);
            if (clr != null) return clr;
        }
        return null;
    }

    /// <summary>
    /// A new node of a type, written at once with nothing but its defaults. The form that opens on it
    /// is then editing a real node rather than a draft this page would have to hold half-saved, and
    /// the same call is what a picker uses to make the node it is about to point at.
    /// </summary>
    async Task<object> create(CreatePayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        if (!dm.NodeTypes.TryGetValue(p.TypeId, out var type)) throw new Exception("No node type with id " + p.TypeId + " in the data model. ");
        if (p.TypeId == NodeConstants.BaseNodeTypeId) throw new Exception("Pick a type: the base type stands for every node at once. ");
        if (type.IsInnerNode) throw new Exception(type.CodeName + " is an embedded type: it only exists inside another node's property. ");
        var clr = findClrType(dm, type) ?? throw new Exception("The type " + type.FullName + " is in the data model, but no assembly loaded here implements it. ");
        var node = newNode(s, clr);
        var transaction = s.CreateTransaction();
        transaction.Insert(node, out var id);
        await transaction.ExecuteAsync();
        return new { Id = id };
    }

    /// <summary>
    /// An empty instance. NewObjectFromType has non-generic overloads as well, so the generic one has
    /// to be picked by hand; it is the one that returns the generated implementation of an interface.
    /// </summary>
    static object newNode(NodeStore s, Type clr) {
        var method = typeof(NodeMapper).GetMethods()
            .First(m => m.Name == nameof(NodeMapper.NewObjectFromType) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(clr);
        try {
            return method.Invoke(s.Mapper, [null])!;
        } catch (System.Reflection.TargetInvocationException err) {
            throw new Exception("Could not make a " + clr.Name + ": " + (err.InnerException?.Message ?? err.Message), err);
        }
    }

    /// <summary>
    /// Writes the whole inner node list of an embedded property. Adding, removing, editing and
    /// reordering are all the same write: the map is a document inside the node and is replaced as
    /// one, so the form sends the list it wants and this builds it. An inner node keeps its id when
    /// the form sends one, which is what makes an edit an edit rather than a delete and an insert.
    /// </summary>
    async Task<object> saveEmbedded(SaveEmbeddedPayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        if (!s.Datastore.TryGet(p.Id, out var n, adminContext)) throw new Exception("Node not found. ");
        if (!dm.NodeTypes.TryGetValue(n.NodeType, out var type)) throw new Exception("The node has a type that is not in the current data model. ");
        if (editableProperty(type, p.PropertyId.ToString()) is not EmbeddedPropertyModel embedded)
            throw new Exception("Property " + p.PropertyId + " is not an embedded property. ");
        embedded.GetKeyTypeOfPropertyIfPossible(dm); // the map cannot be built before the key type is known
        var nowUtc = DateTime.UtcNow;
        var nodes = new List<NodeData>();
        foreach (var wanted in p.Nodes ?? []) {
            if (!dm.NodeTypes.TryGetValue(wanted.TypeId, out var innerType)) throw new Exception("No node type with id " + wanted.TypeId + " in the data model. ");
            // the property names the types it embeds; a type inheriting one of them is one of them
            if (!embedded.InnerNodeTypes.Any(id => innerType.ThisAndAllInheritedTypes.ContainsKey(id)))
                throw new Exception(innerType.CodeName + " cannot be embedded in " + embedded.CodeName + ". ");
            var values = new Properties<object>(innerType.AllProperties.Count);
            foreach (var property in innerType.AllProperties.Values) {
                if (property.Internal) continue;
                if (property is EmbeddedPropertyModel or FilePropertyModel or RelationPropertyModel) continue; // not editable in this form
                object? value = null;
                if (wanted.Values != null && wanted.Values.TryGetValue(property.Id.ToString(), out var json) && json.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    value = parse(property, json);
                value ??= property.GetDefaultValue();
                if (value != null) values.Add(property.Id, value);
            }
            nodes.Add(new NodeData(wanted.Id is Guid g && g != Guid.Empty ? g : Guid.NewGuid(), 0, wanted.TypeId, nowUtc, nowUtc, values, null));
        }
        var map = embedded.CreateInnerNodeDataMap(new PropertyPath(p.Id, embedded.Id), nodes);
        map.ValidateUniqueKeys();
        var transaction = s.CreateTransaction();
        transaction.UpdateProperty(p.Id, embedded.Id, map);
        await transaction.ExecuteAsync();
        return new { Count = nodes.Count };
    }

    /// <summary>
    /// The value a form field posted, as the property's own type. Anything unparsable is an error
    /// rather than a silent default: the store's own coercion would turn a mistyped id into
    /// Guid.Empty and a mistyped number into 0, and the save would look like it worked.
    /// </summary>
    static object parse(PropertyModel property, JsonElement e) {
        switch (property) {
            case BooleanPropertyModel:
                return e.ValueKind switch {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(e.GetString(), out var b) => b,
                    _ => throw bad(property, e),
                };
            case IntegerPropertyModel:
                return number<int>(property, e, s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null);
            case LongPropertyModel:
                return number<long>(property, e, s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null);
            case DoublePropertyModel:
                return number<double>(property, e, s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null);
            case FloatPropertyModel:
                return number<float>(property, e, s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null);
            case DecimalPropertyModel:
                return number<decimal>(property, e, s => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null);
            case StringPropertyModel:
                return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();
            case StringArrayPropertyModel:
                return array(property, e).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToArray();
            case EnumArrayPropertyModel:
                return array(property, e).Select(x => (int)number<int>(property, x, s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null)).ToArray();
            case ReferencesPropertyModel or GuidArrayPropertyModel:
                return array(property, e).Select(x => guid(property, x)).Where(g => g != Guid.Empty).ToArray();
            case ReferencePropertyModel or GuidPropertyModel:
                return guid(property, e);
            case DateTimePropertyModel: {
                    var s = text(property, e);
                    if (string.IsNullOrWhiteSpace(s)) return default(DateTime);
                    // a value without a zone is read as UTC: the store keeps UTC and the form says so
                    if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)) throw bad(property, e);
                    return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }
            case DateTimeOffsetPropertyModel: {
                    var s = text(property, e);
                    if (string.IsNullOrWhiteSpace(s)) return default(DateTimeOffset);
                    if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) throw bad(property, e);
                    return dto;
                }
            case TimeSpanPropertyModel: {
                    var s = text(property, e);
                    if (string.IsNullOrWhiteSpace(s)) return TimeSpan.Zero;
                    if (!TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts)) throw bad(property, e);
                    return ts;
                }
            case GeoCoordinatePropertyModel: {
                    if (e.ValueKind == JsonValueKind.Object) {
                        if (e.TryGetProperty("latitude", out var lat) && e.TryGetProperty("longitude", out var lon)
                            && lat.ValueKind == JsonValueKind.Number && lon.ValueKind == JsonValueKind.Number) {
                            return new GeoCoordinate(lat.GetDouble(), lon.GetDouble());
                        }
                        throw bad(property, e);
                    }
                    var s = text(property, e);
                    if (string.IsNullOrWhiteSpace(s)) return GeoCoordinate.Empty;
                    if (!GeoCoordinate.TryParse(s, out var geo)) throw bad(property, e);
                    return geo;
                }
            default:
                throw new Exception("Property " + property.CodeName + " (" + property.PropertyType + ") cannot be edited here. ");
        }
    }
    static object number<T>(PropertyModel property, JsonElement e, Func<string, T?> tryParse) where T : struct {
        var s = e.ValueKind switch {
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.String => e.GetString(),
            _ => throw bad(property, e),
        };
        if (string.IsNullOrWhiteSpace(s)) return default(T);
        return tryParse(s) ?? throw bad(property, e);
    }
    static string? text(PropertyModel property, JsonElement e) => e.ValueKind switch {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => e.GetRawText(),
        _ => throw bad(property, e),
    };
    static Guid guid(PropertyModel property, JsonElement e) {
        var s = text(property, e);
        if (string.IsNullOrWhiteSpace(s)) return Guid.Empty;
        return Guid.TryParse(s, out var g) ? g : throw bad(property, e);
    }
    static IEnumerable<JsonElement> array(PropertyModel property, JsonElement e) {
        if (e.ValueKind != JsonValueKind.Array) throw bad(property, e);
        return e.EnumerateArray();
    }
    static Exception bad(PropertyModel property, JsonElement e)
        => new("\"" + e + "\" is not a valid value for " + property.CodeName + " (" + property.PropertyType + "). ");

    // ---- picking a node to relate or refer to ----

    object lookup(LookupPayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        var take = Math.Clamp(p.Take <= 0 ? 20 : p.Take, 1, maxLookupResults);
        var typeIds = (p.TypeIds ?? []).Where(dm.NodeTypes.ContainsKey).ToArray();
        if (typeIds.Length == 0) typeIds = [NodeConstants.BaseNodeTypeId];
        var found = new List<object>();
        var seen = new HashSet<Guid>();
        foreach (var typeId in typeIds) {
            if (found.Count >= take) break;
            var q = s.QueryType(typeId, adminContext);
            if (!string.IsNullOrWhiteSpace(p.Text)) q = q.WhereSearch(prefixEachWord(p.Text));
            if (s.Datastore.Query(paged(q, 0, take), [], adminContext) is not IStoreNodeDataCollection nodes) continue;
            foreach (var n in nodes.NodeValues) {
                if (found.Count >= take) break;
                if (!seen.Add(n.Id)) continue; // the same node can match through two of the given types
                found.Add(new { n.Id, Name = displayNameOf(dm, n), TypeName = typeName(dm, n) });
            }
        }
        return found;
    }

    // ---- values on the wire ----

    /// <summary>
    /// A facet bucket value, in a form that survives the round trip back as a selection. Dates and
    /// times travel as ticks and numbers in the invariant culture, because a selection is put back
    /// into the query string as text and matched against the bucket it came from
    /// (Facets.SetSelected), which a locale formatted value would miss.
    /// </summary>
    static string? wire(object? v) => v switch {
        null => null,
        INodeData n => n.Id.ToString(),
        bool b => b ? "true" : "false",
        DateTime dt => dt.Ticks.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcTicks.ToString(CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture), // not ticks: the coercion back only reads the "c" format
        Enum e => Convert.ToInt32(e).ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    /// <summary>A value as a person reads it, formatted for the property rather than for the server's culture.</summary>
    static string display(PropertyModel? property, object? v) {
        switch (v) {
            case null: return "";
            case string s: return s;
            case bool b: return b ? "Yes" : "No";
            case DateTime dt: return dt == default ? "" : dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            case DateTimeOffset dto: return dto == default ? "" : dto.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            case TimeSpan ts: return ts.ToString("c", CultureInfo.InvariantCulture);
            case Guid g: return g == Guid.Empty ? "" : g.ToString();
            case INodeData n: return string.IsNullOrEmpty(n.DisplayName) ? n.Id.ToString() : n.DisplayName!;
            case FileValue file: return file.IsEmpty ? "" : file.Name;
            case GeoCoordinate geo: return geo.IsEmpty ? "" : geo.ToString();
            case IInnerNodeDataMap map: return map.Count == 0 ? "" : map.Count + " items";
            case byte[] bytes: return bytes.Length.To1000N() + " bytes";
            case float[] vector: return vector.Length.To1000N() + " dimensions";
            case string[] strings: return string.Join(", ", strings);
            case Guid[] guids: return string.Join(", ", guids);
            case int[] ints when property is EnumArrayPropertyModel enums: return string.Join(", ", ints.Select(i => enumName(enums.LegalValues, enums.LegalValueNames, i)));
            case int[] ints: return string.Join(", ", ints);
            case int i when property is IntegerPropertyModel integer && (integer.IsEnum || integer.LegalValues != null):
                return enumName(integer.LegalValues, integer.LegalValueNames, i);
            case double d: return d.ToString("0.######", CultureInfo.InvariantCulture);
            case float f: return f.ToString("0.######", CultureInfo.InvariantCulture);
            case decimal dec: return dec.ToString("0.######", CultureInfo.InvariantCulture);
            case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
            default: return v.ToString() ?? "";
        }
    }
    static string enumName(int[]? values, string[]? names, int value) {
        if (values != null && names != null) {
            for (var i = 0; i < values.Length && i < names.Length; i++) if (values[i] == value) return names[i];
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    // ---- the pivot view: the same search, summarized as groups x measures ----

    /// <summary>
    /// The properties of a type the pivot builder can offer, with what each can do: group (the facet
    /// rules - indexed and not opted out, relations opted in), aggregate (an indexed scalar: distinct
    /// count), and sum/average (a numeric one). Dates are marked so the builder can offer calendar
    /// intervals for them.
    /// </summary>
    object pivotModel(PivotModelPayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        var typeId = queriedType(dm, p.TypeId);
        var type = dm.NodeTypes[typeId];
        var properties = type.AllProperties.Values
            .Where(property => !property.Internal)
            .Select(property => new {
                Id = property.Id,
                Name = property.CodeName,
                Type = property.PropertyType.ToString(),
                Groupable = isGroupable(property),
                Aggregatable = isAggregatable(property),
                Numeric = isNumeric(property),
                IsDate = property.PropertyType is PropertyType.DateTime or PropertyType.DateTimeOffset,
                DeclaredBy = property.NodeType == typeId ? null : dm.NodeTypes.TryGetValue(property.NodeType, out var declaring) ? declaring.CodeName : null,
            })
            .Where(property => property.Groupable || property.Aggregatable)
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new { TypeId = typeId, TypeName = type.CodeName, Properties = properties };
    }
    // mirrors Property.CanBeFacet in the engine, which is what decides at query time
    static bool isGroupable(PropertyModel property) {
        if (property is RelationPropertyModel relation) return relation.Facet;
        if (!property.Indexed || property.NotFacet) return false;
        return property.PropertyType is PropertyType.Boolean or PropertyType.Integer or PropertyType.String or PropertyType.StringArray
            or PropertyType.Double or PropertyType.Float or PropertyType.Decimal or PropertyType.DateTime or PropertyType.DateTimeOffset
            or PropertyType.TimeSpan or PropertyType.Guid or PropertyType.Long or PropertyType.GuidArray or PropertyType.EnumArray
            or PropertyType.Reference or PropertyType.References;
    }
    // mirrors ValueProperty<T>.CanAggregate / IsNumeric
    static bool isAggregatable(PropertyModel property) => property.Indexed && property is not RelationPropertyModel && property.PropertyType is
        PropertyType.Boolean or PropertyType.Integer or PropertyType.String or PropertyType.Double or PropertyType.Float or PropertyType.Decimal
        or PropertyType.DateTime or PropertyType.DateTimeOffset or PropertyType.TimeSpan or PropertyType.Guid or PropertyType.Long or PropertyType.Reference;
    static bool isNumeric(PropertyModel property) => property.Indexed && property.PropertyType is
        PropertyType.Integer or PropertyType.Long or PropertyType.Double or PropertyType.Float or PropertyType.Decimal;

    /// <summary>
    /// Runs the pivot: the page's search and facet selection as the source, the groups and measures
    /// the builder asked for on top. Built with the typed pivot API on Guid overloads and executed as
    /// a query string against the datastore, like the search, so relation groups arrive as node data
    /// and are named by the engine.
    /// </summary>
    async Task<object> pivot(PivotPayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        var typeId = queriedType(dm, p.TypeId);
        var nodeType = dm.NodeTypes[typeId];
        var q = s.QueryType(typeId, adminContext);
        if (!string.IsNullOrWhiteSpace(p.Text)) q = q.WhereSearch(prefixEachWord(p.Text), p.SemanticRatio, (float?)p.MinimumSimilarity);
        var selections = (p.Selections ?? []).Where(sel => dm.Properties.ContainsKey(sel.PropertyId) && sel.Values?.Length > 0).ToArray();
        QueryOfPivot<object, object> pq;
        if (selections.Length == 0) {
            pq = q.Pivot();
        } else { // the selection is the filter; its buckets are neither asked for nor counted
            var fq = q.Facets();
            foreach (var selection in selections) {
                foreach (var value in selection.Values!) {
                    if (value.Value == null) fq = fq.SetFacetMissingValue(selection.PropertyId);
                    else if (value.Value2 == null) fq = fq.SetFacetValue(selection.PropertyId, value.Value);
                    else fq = fq.SetFacetRangeValue(selection.PropertyId, value.Value, value.Value2);
                }
            }
            pq = fq.Pivot();
        }
        pq = addLevels(pq, dm, p.Rows, p.RowOptions, rows: true);
        pq = addLevels(pq, dm, p.Columns, p.ColumnOptions, rows: false);
        // measures get explicit, unique names: the page reads cells by them, and two of a kind
        // (say two counts) must not be an error here
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in p.Measures ?? []) {
            var function = Enum.TryParse<PivotFunction>(m.Function, true, out var f) ? f : PivotFunction.Count;
            if (function != PivotFunction.Count && (m.PropertyId is not Guid propertyId || !dm.Properties.ContainsKey(propertyId))) continue; // half-built measure: skip it, the page is still being edited
            var property = function == PivotFunction.Count ? null : dm.Properties[m.PropertyId!.Value];
            var name = property == null ? "Count" : property.CodeName + "." + function;
            var unique = name;
            for (var i = 2; !names.Add(unique); i++) unique = name + " (" + i + ")";
            pq = function == PivotFunction.Count ? pq.AddCount(unique) : pq.AddMeasure(function, property!.Id, unique);
        }
        pq = pq.SetTotals(rows: true, columns: true, subTotals: p.SubTotals);
        pq = pq.SetLimits(Math.Clamp(p.MaxCells <= 0 ? maxPivotCells : p.MaxCells, 1, maxPivotCells));
        if (p.RowPageSize > 0) pq = pq.SetRowPaging(Math.Max(0, p.RowPage), Math.Min(p.RowPageSize, maxPivotRows));
        var queryString = pq.ToString();

        var sw = Stopwatch.StartNew();
        var data = await s.Datastore.QueryAsync(queryString, [], adminContext);
        sw.Stop();
        if (data is not PivotQueryResultData pivotData) throw new Exception("The query did not return a pivot. ");
        var r = pivotData.Result;
        return new {
            TypeId = typeId,
            TypeName = nodeType.CodeName,
            Query = queryString,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            r.SourceCount,
            r.Capped,
            Measures = r.Measures.Select(m => (object)new { m.Name, Function = m.Function.ToString(), m.PropertyName }).ToArray(),
            Rows = pivotAxisView(r.Rows),
            Columns = pivotAxisView(r.Columns),
            Cells = r.Cells.Select(pivotCellView).ToArray(),
            RowTotals = r.RowTotals.Select(pivotCellView).ToArray(),
            ColumnTotals = r.ColumnTotals.Select(pivotCellView).ToArray(),
            GrandTotal = pivotCellView(r.GrandTotal),
            RowSubTotals = r.RowSubTotals.Select(pivotSubTotalView).ToArray(),
            ColumnSubTotals = r.ColumnSubTotals.Select(pivotSubTotalView).ToArray(),
        };
    }
    const int maxPivotCells = 20_000; // a browser table; the engine's own default is far above this
    const int maxPivotRows = 1_000;
    static QueryOfPivot<object, object> addLevels(QueryOfPivot<object, object> pq, Datamodel dm, PivotLevelPayload[]? levels, PivotAxisOptionsPayload? options, bool rows) {
        foreach (var level in levels ?? []) {
            if (!dm.Properties.TryGetValue(level.PropertyId, out var property)) continue; // a level whose property is not chosen yet
            var mode = (level.Mode ?? "auto").ToLowerInvariant();
            var isDate = property.PropertyType is PropertyType.DateTime or PropertyType.DateTimeOffset;
            pq = mode switch {
                "values" => rows ? pq.AddRowValues(property.Id) : pq.AddColumnValues(property.Id),
                "ranges" => rows ? pq.AddRowRanges(property.Id) : pq.AddColumnRanges(property.Id),
                _ when isDate && Enum.TryParse<DateInterval>(mode, true, out var interval) && interval != DateInterval.None
                    => rows ? pq.AddRow(property.Id, interval) : pq.AddColumn(property.Id, interval),
                _ => rows ? pq.AddRow(property.Id) : pq.AddColumn(property.Id),
            };
            if (options != null && (options.MaxGroups > 0 || options.SortByMeasure != null || options.OtherGroup || options.IncludeMissing || !options.Descending)) {
                pq = rows
                    ? pq.SetRowOptions(property.Id, options.MaxGroups, 0, options.IncludeMissing, options.SortByMeasure, options.Descending, options.OtherGroup)
                    : pq.SetColumnOptions(property.Id, options.MaxGroups, 0, options.IncludeMissing, options.SortByMeasure, options.Descending, options.OtherGroup);
            }
        }
        return pq;
    }
    static object pivotAxisView(PivotAxisResult axis) => new {
        Levels = axis.Levels.Select(l => (object)new { l.PropertyId, l.CodeName, ValueType = l.ValueType.ToString(), l.IsRange, Interval = l.Interval.ToString() }).ToArray(),
        Groups = axis.Groups.Select(pivotGroupView).ToArray(),
        axis.TotalGroupCount,
        axis.PageIndex,
        axis.PageSize,
    };
    // the group's values travel in the facet selection form (see wire), so a cell can be turned into
    // the selection that shows the nodes behind it
    static object pivotGroupView(PivotGroup g) => new {
        Labels = g.DisplayNames,
        Values = g.Values.Select(wire).ToArray(),
        Values2 = g.Values2.Select(wire).ToArray(),
        g.Count,
        g.IsOther,
        g.Depth,
    };
    static object pivotCellView(PivotCell c) => new { c.Row, c.Column, c.Count, c.Values };
    static object pivotSubTotalView(PivotSubTotal s) => new {
        Group = pivotGroupView(s.Group),
        Cells = s.Cells.Select(c => c == null ? null : pivotCellView(c)).ToArray(),
        Total = pivotCellView(s.Total),
    };

    // ---- the group-by view: the same search, one row per group ----

    /// <summary>
    /// Runs a GroupBy: the page's search and facet selection as the source, one key level per chosen
    /// property (one group per value, a calendar interval, or ranges), the aggregates asked for, the
    /// rows in the keys' natural order or sorted by one measure, and paged. Built with the runtime
    /// GroupBy API - GroupKey and Aggregate - so it is the very code path a dynamic report takes, and
    /// the query string shown is the one such a report would send.
    /// </summary>
    async Task<object> groupBy(GroupByPayload p) {
        var s = store(p.StoreId);
        var dm = s.Datastore.Datamodel;
        var typeId = queriedType(dm, p.TypeId);
        var nodeType = dm.NodeTypes[typeId];
        var pageSize = Math.Clamp(p.PageSize <= 0 ? 200 : p.PageSize, 1, maxPivotRows);
        var keys = (p.Keys ?? []).Where(k => dm.Properties.ContainsKey(k.PropertyId)).Select(k => groupKey(dm.Properties[k.PropertyId], k.Mode)).ToArray();
        var keyViews = keys.Select(k => {
            var property = dm.Properties[k.PropertyId];
            return (object)new { k.PropertyId, property.CodeName, ValueType = property.PropertyType.ToString(), k.IsRange, Interval = k.DateInterval.ToString() };
        }).ToArray();
        var measures = (p.Measures ?? [])
            .Select(m => (Function: Enum.TryParse<PivotFunction>(m.Function, true, out var f) ? f : PivotFunction.Count, m.PropertyId))
            .Where(m => m.Function != PivotFunction.Count && m.PropertyId is Guid id && dm.Properties.ContainsKey(id)) // Count is always a column; a half-built measure is skipped
            .Select(m => (m.Function, Property: dm.Properties[m.PropertyId!.Value]))
            .DistinctBy(m => (m.Function, m.Property.Id))
            .ToArray();
        var measureViews = measures.Select(m => (object)new { Name = m.Property.CodeName + "." + m.Function, Function = m.Function.ToString(), PropertyName = m.Property.CodeName }).ToArray();
        var measureNames = measures.Select(m => m.Property.CodeName + "." + m.Function).ToArray();
        if (keys.Length == 0) // nothing chosen yet: an empty table, not an error
            return new { TypeId = typeId, TypeName = nodeType.CodeName, Query = "", DurationMs = 0.0, SourceCount = 0, Keys = keyViews, Measures = measureViews, Rows = Array.Empty<object>(), TotalRows = 0, Page = 0, PageSize = pageSize };

        var q = s.QueryType(typeId, adminContext);
        if (!string.IsNullOrWhiteSpace(p.Text)) q = q.WhereSearch(prefixEachWord(p.Text), p.SemanticRatio, (float?)p.MinimumSimilarity);
        var selections = (p.Selections ?? []).Where(sel => dm.Properties.ContainsKey(sel.PropertyId) && sel.Values?.Length > 0).ToArray();
        QueryOfGroups<object, object, object?[]> groups;
        if (selections.Length == 0) {
            groups = q.GroupBy(keys);
        } else { // the selection is the filter; its buckets are neither asked for nor counted
            var fq = q.Facets();
            foreach (var selection in selections) {
                foreach (var value in selection.Values!) {
                    if (value.Value == null) fq = fq.SetFacetMissingValue(selection.PropertyId);
                    else if (value.Value2 == null) fq = fq.SetFacetValue(selection.PropertyId, value.Value);
                    else fq = fq.SetFacetRangeValue(selection.PropertyId, value.Value, value.Value2);
                }
            }
            groups = fq.GroupBy(keys);
        }
        foreach (var m in measures) groups = groups.Aggregate(m.Function, m.Property.Id);
        groups = groups.IncludeMissing(p.IncludeMissing);
        QueryOfGroupRows<NodeGroup<object?[]>> rows = groups;
        if (!string.IsNullOrEmpty(p.SortBy)) {
            if (string.Equals(p.SortBy, "Count", StringComparison.OrdinalIgnoreCase)) {
                rows = p.Descending ? rows.OrderByDescending(g => g.Count) : rows.OrderBy(g => g.Count);
            } else if (measureNames.FirstOrDefault(n => string.Equals(n, p.SortBy, StringComparison.OrdinalIgnoreCase)) is { } name) {
                rows = p.Descending ? rows.OrderByDescending(g => g[name]) : rows.OrderBy(g => g[name]);
            }
        }
        rows = rows.Page(Math.Max(0, p.Page), pageSize);
        var queryString = rows.ToString();
        var sw = Stopwatch.StartNew();
        var result = await rows.ExecuteAsync();
        sw.Stop();
        return new {
            TypeId = typeId,
            TypeName = nodeType.CodeName,
            Query = queryString,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            result.SourceCount,
            Keys = keyViews,
            Measures = measureViews,
            Rows = result.Select(g => groupRowView(s, g)).ToArray(),
            TotalRows = result.TotalCount,
            Page = result.PageIndex,
            PageSize = pageSize,
        };
    }
    /// <summary>Mode: values (default) | ranges, or a calendar interval (year, quarter, month, week, day, hour) on a date property.</summary>
    static GroupKey groupKey(PropertyModel property, string? mode) {
        var m = (mode ?? "values").ToLowerInvariant();
        var isDate = property.PropertyType is PropertyType.DateTime or PropertyType.DateTimeOffset;
        return m switch {
            "ranges" => GroupKey.Ranges(property.Id),
            _ when isDate && Enum.TryParse<DateInterval>(m, true, out var interval) && interval != DateInterval.None => GroupKey.Interval(property.Id, interval),
            _ => GroupKey.Values(property.Id),
        };
    }
    // the key parts travel in the facet selection form (see wire), so a row can be turned into the
    // selection that shows the nodes behind it; a range or calendar part gives both bounds
    static object groupRowView(NodeStore s, NodeGroup<object?[]> g) => new {
        g.Labels,
        Values = g.Key.Select(v => wire(s, v is GroupRange<object> r ? r.From : v)).ToArray(),
        Values2 = g.Key.Select(v => v is GroupRange<object> r ? wire(s, r.To) : null).ToArray(),
        g.Count,
        g.IsMissing,
        Measures = g.MeasureValues,
    };
    // a GroupBy hands relation keys back as mapped node objects (the typed API's contract); their id is the token
    static string? wire(NodeStore s, object? v) {
        if (v is null or INodeData or string or bool or Enum or IFormattable) return wire(v);
        try { return s.Mapper.GetIdGuid(v).ToString(); } catch { return wire(v); }
    }

    sealed record StorePayload(Guid StoreId);
    sealed record NodePayload(Guid StoreId, Guid Id);
    internal sealed record PivotModelPayload(Guid StoreId, Guid? TypeId);
    /// <summary>Mode: auto | values | ranges, or a calendar interval (year, quarter, month, week, day, hour) on a date property.</summary>
    internal sealed record PivotLevelPayload(Guid PropertyId, string? Mode);
    internal sealed record PivotMeasurePayload(string Function, Guid? PropertyId);
    internal sealed record PivotAxisOptionsPayload(int MaxGroups = 0, string? SortByMeasure = null, bool Descending = true, bool OtherGroup = false, bool IncludeMissing = false);
    internal sealed record PivotPayload(Guid StoreId, Guid? TypeId, string? Text, double? SemanticRatio, double? MinimumSimilarity, FacetSelection[]? Selections,
        PivotLevelPayload[]? Rows, PivotLevelPayload[]? Columns, PivotMeasurePayload[]? Measures,
        PivotAxisOptionsPayload? RowOptions, PivotAxisOptionsPayload? ColumnOptions, bool SubTotals = false, int MaxCells = 0, int RowPage = 0, int RowPageSize = 0);
    internal sealed record GroupByPayload(Guid StoreId, Guid? TypeId, string? Text, double? SemanticRatio, double? MinimumSimilarity, FacetSelection[]? Selections,
        PivotLevelPayload[]? Keys, PivotMeasurePayload[]? Measures, bool IncludeMissing = true, string? SortBy = null, bool Descending = true, int Page = 0, int PageSize = 200);
    internal sealed record FacetSelectionValue(string? Value, string? Value2);
    internal sealed record FacetSelection(Guid PropertyId, FacetSelectionValue[]? Values);
    internal sealed record SearchPayload(Guid StoreId, Guid? TypeId, string? Text, double? SemanticRatio, double? MinimumSimilarity,
        FacetSelection[]? Selections, Guid[]? Expanded, int Page = 0, int PageSize = 25, bool Table = false, bool Facets = true,
        string? SortBy = null, bool SortDescending = false, string[]? Columns = null);
    internal sealed record ColumnsPayload(Guid StoreId, Guid? TypeId);
    sealed record SavePayload(Guid StoreId, Guid Id, Dictionary<string, JsonElement>? Values, Dictionary<string, Guid[]>? Relations);
    sealed record CreatePayload(Guid StoreId, Guid TypeId);
    sealed record InnerNodePayload(Guid? Id, Guid TypeId, Dictionary<string, JsonElement>? Values);
    sealed record SaveEmbeddedPayload(Guid StoreId, Guid Id, Guid PropertyId, InnerNodePayload[]? Nodes);
    sealed record SaveMetaPayload(Guid StoreId, Guid Id, Dictionary<string, JsonElement>? Values);
    sealed record VersionsPayload(Guid StoreId, Guid Id, int MaxCount = 50);
    sealed record LookupPayload(Guid StoreId, Guid[]? TypeIds, string? Text, int Take = 20);
}
