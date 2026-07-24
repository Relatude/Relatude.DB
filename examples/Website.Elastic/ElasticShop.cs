using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using HttpMethod = Elastic.Transport.HttpMethod;

namespace Website.Elastic;

// Pure Elasticsearch implementation of the same facet search as Website.Simple's /shop/search and
// Website.Lucene / Website.Sqlite, for a side by side comparison. Same deterministic catalog (keep
// the seeder in sync with Website.Simple/Models/ShopSeeder.cs: identical word banks and Random(2026)
// draw order), same facets and the same JSON contract, so all UIs behave the same.
//
// Unlike the embedded Lucene and SQLite examples, Elasticsearch is a separate service: this class
// only talks to a cluster over HTTP (default http://localhost:9200, override with ELASTIC_SHOP_URL).
// On first start it creates the index and bulk loads the catalog (10 million products by default),
// then reuses it. The search is expressed as raw Elasticsearch query DSL sent through the official
// client's low level transport - the same "show the native query shape" spirit as SqliteShop's SQL.
//
// The faceted navigation is the textbook Elasticsearch pattern, which maps one to one onto the
// Relatude drill-sideways semantics ("count each selected facet against the OTHER selections only"):
//  - the free text query lives in `query`, so it constrains BOTH the hits and every aggregation
//  - `post_filter` = AND of all selected facet filters: it narrows the returned hits and the total,
//    but is deliberately NOT applied to aggregations
//  - each facet is a `filter` aggregation whose filter is all selected filters EXCEPT that facet's
//    own dimension, wrapping a terms/range sub-aggregation - so a selected facet is counted against
//    the other selections and its alternatives stay visible, while an unselected facet (no filter of
//    its own to drop) is simply counted against every current selection
public sealed class ElasticShop : IDisposable {
    readonly ElasticsearchClient _client;
    readonly string _index;
    readonly double _priceMin;
    readonly double _priceMax;
    readonly Bucket[] _buckets;
    static readonly string[] _valueDims = ["Category", "Brand", "InStock", "Tags"];
    const int _maxWindow = 100_000; // index.max_result_window: how deep hit paging may go (facets are unaffected)
    // plain options on purpose: the Web defaults camelCase dictionary keys, which would rename the
    // aggregation names we send ("Category" -> "category") and break reading them back by name
    static readonly JsonSerializerOptions _json = new();

    public ElasticShop(string url, string index, int productCount, string? user, string? password) {
        _index = index;
        var settings = new ElasticsearchClientSettings(new Uri(url))
            .RequestTimeout(TimeSpan.FromMinutes(30)) // the one-time force merge over 10M docs can run for minutes
            .ThrowExceptions(false); // inspect ApiCallDetails ourselves, so a 404 on "index exists?" is not an exception
        if (!string.IsNullOrEmpty(user)) settings = settings.Authentication(new BasicAuthentication(user, password ?? ""));
        // DEV ONLY: Elasticsearch 8 enables TLS with a self signed cert out of the box; trust it for
        // localhost so the example runs against a default single node cluster without extra setup.
        if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            settings = settings.ServerCertificateValidationCallback((_, _, _, _) => true);
        _client = new ElasticsearchClient(settings);

        if (!indexHasDocs()) build(productCount);
        Count = (int)count();
        (_priceMin, _priceMax) = priceBounds();
        _buckets = makeBuckets(_priceMin, _priceMax);
    }
    public int Count { get; }

    #region seeding (keep in sync with ShopSeeder in Website.Simple)
    record CategoryDef(string Name, string[] Nouns, string[] Uses);
    static readonly CategoryDef[] _categories = [
        new("Furniture", ["Chair", "Table", "Desk", "Shelf", "Sofa", "Bench"], ["living room", "home office", "reading corner", "hallway"]),
        new("Electronics", ["Headphones", "Speaker", "Keyboard", "Monitor", "Camera", "Charger"], ["travel", "gaming", "video calls", "music production"]),
        new("Outdoor", ["Tent", "Backpack", "Lantern", "Hammock", "Thermos", "Boots"], ["hiking", "camping", "fishing trips", "mountain weather"]),
        new("Kitchen", ["Kettle", "Knife", "Pan", "Grinder", "Blender", "Cutting Board"], ["daily cooking", "baking", "meal prep", "espresso lovers"]),
        new("Clothing", ["Jacket", "Sweater", "Gloves", "Scarf", "Cap", "Vest"], ["cold winter days", "commuting", "layering", "rainy weather"]),
        new("Toys", ["Puzzle", "Robot", "Building Kit", "Board Game", "Kite", "Race Car"], ["family evenings", "curious kids", "rainy days", "collectors"]),
    ];
    static readonly string[] _adjectives = ["Compact", "Classic", "Foldable", "Ergonomic", "Portable", "Sturdy", "Elegant", "Rustic", "Modern", "Silent", "Adjustable", "Ultralight"];
    static readonly string[] _materials = ["oak", "leather", "bamboo", "titanium", "wool", "canvas", "steel", "walnut", "aluminium", "cork", "linen", "recycled plastic"];
    static readonly string[] _features = ["waterproof", "wireless", "stackable", "dishwasher safe", "handmade", "foldable", "rechargeable", "machine washable", "scratch resistant", "weatherproof"];
    static readonly string[] _tags = ["bestseller", "eco", "new", "sale", "premium", "handmade", "limited"];
    static readonly string[] _brandNames = ["Fjellrev", "Nordlys", "Kvist & Co", "Bluewhale", "Habitat 7", "Solvind", "Granheim", "Urban Nest", "Polarix", "Drivved", "Lysne", "Vandrer"];
    static string upperFirst(string s) => char.ToUpper(s[0]) + s[1..];

    record ProductDoc(string text, string name, string description, string category, string brand, double price, string instock, string[] tags);
    static IEnumerable<ProductDoc> generate(int count) {
        var rnd = new Random(2026); // deterministic content, same sequence as ShopSeeder / LuceneShop / SqliteShop
        for (var i = 0; i < count; i++) {
            var cat = _categories[rnd.Next(_categories.Length)];
            var adjective = _adjectives[rnd.Next(_adjectives.Length)];
            var material = _materials[rnd.Next(_materials.Length)];
            var feature = _features[rnd.Next(_features.Length)];
            var feature2 = _features[rnd.Next(_features.Length)];
            var noun = cat.Nouns[rnd.Next(cat.Nouns.Length)];
            var use = cat.Uses[rnd.Next(cat.Uses.Length)];
            var brand = _brandNames[rnd.Next(_brandNames.Length)];
            var name = $"{adjective} {material} {noun}".Replace(material, upperFirst(material));
            var description = $"A {adjective.ToLower()} {noun.ToLower()} in {material}, {feature} and {feature2}. Made by {brand}, perfect for {use}.";
            var price = Math.Round(9 + Math.Pow(rnd.NextDouble(), 2) * 1990, 2);
            var inStock = rnd.Next(5) > 0;
            var tags = Enumerable.Range(0, rnd.Next(3)).Select(_ => _tags[rnd.Next(_tags.Length)]).Distinct().ToArray();
            yield return new ProductDoc(name + " " + description, name, description, cat.Name, brand, price, inStock ? "True" : "False", tags);
        }
    }

    void build(int productCount) {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Building Elasticsearch index '{_index}' with {productCount:n0} products...");
        es(HttpMethod.DELETE, $"/{_index}", allowStatus: 404); // a leftover empty index from a killed build
        es(HttpMethod.PUT, $"/{_index}", new {
            settings = new {
                index = new {
                    number_of_shards = 1, // single shard, like Lucene's single segment: fastest aggregation counting
                    number_of_replicas = 0,
                    refresh_interval = "-1", // no periodic refresh during the bulk load
                    max_result_window = _maxWindow, // allow deeper hit paging than the 10,000 default
                },
            },
            mappings = new {
                properties = new {
                    text = new { type = "text" }, // name + description, analyzed for free text search
                    name = new { type = "keyword", index = false, doc_values = false }, // display only (kept in _source)
                    description = new { type = "keyword", index = false, doc_values = false }, // display only
                    category = new { type = "keyword" },
                    brand = new { type = "keyword" },
                    price = new { type = "double" },
                    instock = new { type = "keyword" }, // "True"/"False" so the terms facet matches the shared contract
                    tags = new { type = "keyword" },
                },
            },
        });

        const int bulkSize = 5000;
        var sb = new StringBuilder();
        var batch = 0;
        var indexed = 0;
        foreach (var doc in generate(productCount)) {
            sb.Append("{\"index\":{}}\n"); // let Elasticsearch assign the _id; the catalog is rebuilt whole, never patched
            sb.Append(JsonSerializer.Serialize(doc, _json)).Append('\n');
            if (++batch == bulkSize) {
                flush(sb);
                indexed += batch;
                batch = 0;
                if (indexed % 1_000_000 == 0) Console.WriteLine($"  indexed {indexed:n0} in {sw.Elapsed.TotalSeconds:0}s");
            }
        }
        if (batch > 0) flush(sb);

        Console.WriteLine("  refreshing and force merging to a single segment (much faster facet counting)...");
        es(HttpMethod.POST, $"/{_index}/_refresh");
        es(HttpMethod.POST, $"/{_index}/_forcemerge", query: [("max_num_segments", 1)]);
        es(HttpMethod.PUT, $"/{_index}/_settings", new { index = new { refresh_interval = "1s" } });
        Console.WriteLine($"Index complete: {productCount:n0} products in {sw.Elapsed.TotalSeconds:0}s");
    }

    void flush(StringBuilder ndjson) {
        // filter_path=errors keeps the response tiny (just {"errors":false}) instead of one status
        // object per document; the controlled seeding data should never produce a rejected document
        using var doc = es(HttpMethod.POST, $"/{_index}/_bulk", PostData.String(ndjson.ToString()), query: [("filter_path", "errors")])!;
        if (doc.RootElement.TryGetProperty("errors", out var e) && e.GetBoolean())
            throw new InvalidOperationException("Elasticsearch bulk index reported document level errors during seeding.");
        ndjson.Clear();
    }
    #endregion

    internal readonly record struct Bucket(double Min, double Max, string Label);
    internal static Bucket[] makeBuckets(double priceMin, double priceMax) {
        const int bucketCount = 10;
        var step = (priceMax - priceMin) / bucketCount;
        var buckets = new Bucket[bucketCount];
        for (var i = 0; i < bucketCount; i++) {
            var lo = Math.Round(priceMin + i * step);
            var hi = i == bucketCount - 1 ? priceMax : Math.Round(priceMin + (i + 1) * step);
            // prices have two decimals, so boundaries offset by 0.005 sit BETWEEN representable prices:
            // the range aggregation (from inclusive, to exclusive) and the range filter that reads the
            // same field can then never disagree about a boundary document
            buckets[i] = new Bucket(lo - 0.005, hi + (i == bucketCount - 1 ? 0.005 : -0.005), $"{lo} -> {hi}");
        }
        return buckets;
    }

    const int _pageSize = 10;

    // Builds the raw Elasticsearch _search body: the full text query, drill-sideways facet
    // aggregations and the post_filter. Kept static and free of connection state so the query DSL
    // can be unit tested without a live cluster.
    internal static Dictionary<string, object> BuildSearchBody(string? query, int page, List<Selection>? selections, Bucket[] buckets) {
        // AND across facets, OR within one. `post_filter` narrows the hits/total to all selections;
        // each facet aggregation is filtered by every OTHER selection (drill-sideways).
        var allFilters = (selections ?? []).Select(selectionFilter).Where(f => f != null).ToList()!;
        var aggs = new Dictionary<string, object> {
            // doc_count of a match_all filter in query context = the free text hit count, independent
            // of the post_filter -> exactly the "source count" before facet filtering
            ["__source"] = new { filter = new { match_all = new { } } },
        };
        foreach (var dim in _valueDims)
            aggs[dim] = new { filter = dimFilter(selections, dim), aggs = new Dictionary<string, object> { ["v"] = new { terms = new { field = fieldOf(dim), size = 100 } } } };
        aggs["Price"] = new { filter = dimFilter(selections, "Price"), aggs = new Dictionary<string, object> { ["v"] = new { range = new { field = "price", ranges = buckets.Select(b => new { from = b.Min, to = b.Max }).ToArray() } } } };

        // clamp how deep we fetch hits so a very deep page never exceeds max_result_window and errors;
        // the requested page is still echoed back, and facet counts (which ignore paging) are unaffected
        var from = (int)Math.Clamp((long)page * _pageSize, 0, _maxWindow - _pageSize);
        var body = new Dictionary<string, object> {
            ["track_total_hits"] = true, // exact totals past the default 10,000 cap
            ["size"] = _pageSize,
            ["from"] = from,
            ["query"] = textQuery(query),
            ["aggregations"] = aggs,
        };
        if (allFilters.Count > 0) body["post_filter"] = new { @bool = new { filter = allFilters } };
        return body;
    }

    public object Search(string? query, int page, List<Selection>? selections) {
        var sw = Stopwatch.StartNew();
        const int pageSize = _pageSize;
        var noSelections = selections == null || selections.Count == 0;
        var body = BuildSearchBody(query, page, selections, _buckets);

        using var resp = es(HttpMethod.POST, $"/{_index}/_search", body)!;
        var root = resp.RootElement;
        var aggResult = root.GetProperty("aggregations");

        var total = root.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64();
        var sourceCount = aggResult.GetProperty("__source").GetProperty("doc_count").GetInt64();

        var facets = new List<object>();
        foreach (var dim in _valueDims) {
            var values = aggResult.GetProperty(dim).GetProperty("v").GetProperty("buckets").EnumerateArray()
                .Select(b => new { label = b.GetProperty("key").GetString()!, count = (int)b.GetProperty("doc_count").GetInt64() })
                .ToList();
            if (dim == "Tags") values = values.OrderByDescending(v => v.count).ThenBy(v => v.label, StringComparer.Ordinal).Take(8).ToList(); // mirror SetFacetOptions(Tags, maxValues: 8, sortByCount: true)
            else values = values.OrderBy(v => v.label, StringComparer.Ordinal).ToList();
            var selected = selectionValues(selections, dim);
            facets.Add(new {
                property = dim,
                displayName = dim,
                isRange = false,
                values = values.Select(v => new { value = v.label, value2 = (string?)null, display = v.label, count = v.count, selected = selected.Contains(v.label) }).ToList(),
            });
        }
        {
            // materialize the counts now: the response object is serialized lazily by ASP.NET AFTER
            // this method returns and `resp` (the JsonDocument) is disposed, so nothing handed back
            // may still hold a JsonElement into it
            var priceCounts = aggResult.GetProperty("Price").GetProperty("v").GetProperty("buckets").EnumerateArray()
                .Select(b => (int)b.GetProperty("doc_count").GetInt64()).ToArray();
            var selectedRanges = (selections?.FirstOrDefault(s => s.Property == "Price")?.Ranges ?? [])
                .Select(r => (from: parseD(r.From), to: parseD(r.To))).ToList();
            facets.Add(new {
                property = "Price",
                displayName = "Price",
                isRange = true,
                values = _buckets.Select((b, i) => new {
                    value = str(b.Min),
                    value2 = (string?)str(b.Max),
                    display = b.Label,
                    count = priceCounts[i],
                    selected = selectedRanges.Any(r => r.from == b.Min && r.to == b.Max),
                }).ToList(),
            });
        }

        var items = root.GetProperty("hits").GetProperty("hits").EnumerateArray().Select(hit => {
            var s = hit.GetProperty("_source");
            return new {
                name = s.GetProperty("name").GetString(),
                description = s.GetProperty("description").GetString(),
                category = s.GetProperty("category").GetString(),
                brand = s.GetProperty("brand").GetString(),
                price = s.GetProperty("price").GetDouble(),
                inStock = s.GetProperty("instock").GetString() == "True",
                tags = s.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray(),
            };
        }).ToList();

        return new {
            total,
            sourceCount = noSelections ? total : sourceCount, // no selections: source == total, like the other examples
            page,
            pageSize,
            durationMs = sw.Elapsed.TotalMilliseconds,
            items,
            facets,
        };
    }

    // free text: all words must match, like Relatude's WhereSearch default and Lucene's parseText.
    // The standard analyzer lowercases and splits the query the same way the `text` field was indexed;
    // a query with no analyzable token (e.g. "&") yields zero hits, matching the other examples.
    static object textQuery(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? new { match_all = new { } }
            : new { match = new Dictionary<string, object> { ["text"] = new { query, @operator = "and" } } };

    // one selection -> one query clause: a terms clause (OR within the facet) for value dimensions,
    // or an OR of range clauses for Price. Unknown values simply never match, like an unknown
    // drill-down term in Lucene.
    static object? selectionFilter(Selection sel) {
        if (sel.Property == "Price") {
            if (sel.Ranges is not { Count: > 0 }) return null;
            var shoulds = sel.Ranges.Select(r => rangeClause(parseD(r.From), parseD(r.To))).ToList();
            return new { @bool = new { should = shoulds, minimum_should_match = 1 } };
        }
        if (sel.Values is not { Count: > 0 }) return null;
        return new { terms = new Dictionary<string, object> { [fieldOf(sel.Property)] = sel.Values } };
    }
    // filter for one facet's aggregation: every OTHER selection, so its own alternatives stay visible.
    // A `filter` aggregation needs a query, so fall back to match_all when nothing else is selected.
    static object dimFilter(List<Selection>? selections, string dim) {
        var others = (selections ?? []).Where(s => s.Property != dim).Select(selectionFilter).Where(f => f != null).ToList()!;
        return others.Count == 0 ? new { match_all = new { } } : new { @bool = new { filter = others } };
    }
    // filter on the same field the range aggregation counts, so filtered totals and displayed counts
    // always agree (from inclusive, to exclusive - the same half open semantics as the bucket bounds)
    static object rangeClause(double from, double to) =>
        new { range = new Dictionary<string, object> { ["price"] = new { gte = from, lt = to } } };

    static string fieldOf(string dim) => dim switch {
        "Category" => "category",
        "Brand" => "brand",
        "InStock" => "instock",
        "Tags" => "tags",
        _ => throw new ArgumentOutOfRangeException(nameof(dim), dim, "unknown facet dimension"),
    };

    long count() {
        using var doc = es(HttpMethod.GET, $"/{_index}/_count")!;
        return doc.RootElement.GetProperty("count").GetInt64();
    }
    (double min, double max) priceBounds() {
        using var doc = es(HttpMethod.POST, $"/{_index}/_search",
            new { size = 0, aggregations = new { lo = new { min = new { field = "price" } }, hi = new { max = new { field = "price" } } } })!;
        var aggs = doc.RootElement.GetProperty("aggregations");
        return (aggs.GetProperty("lo").GetProperty("value").GetDouble(), aggs.GetProperty("hi").GetProperty("value").GetDouble());
    }
    bool indexHasDocs() {
        var resp = _client.Transport.Request<StringResponse>(HttpMethod.GET, $"/{_index}/_count");
        if (resp.ApiCallDetails?.HttpStatusCode == 404) return false; // index does not exist yet
        ensureOk(resp, $"GET /{_index}/_count");
        using var doc = JsonDocument.Parse(resp.Body!);
        return doc.RootElement.GetProperty("count").GetInt64() > 0;
    }

    // low level helpers: send raw query DSL through the official client's transport and parse the JSON.
    // The transport rejects a query string baked into the path, so any ?params go through RequestParameters.
    JsonDocument? es(HttpMethod method, string path, object body, int allowStatus = -1, (string name, object value)[]? query = null)
        => es(method, path, PostData.String(JsonSerializer.Serialize(body, _json)), allowStatus, query);
    JsonDocument? es(HttpMethod method, string path, PostData? body = null, int allowStatus = -1, (string name, object value)[]? query = null) {
        RequestParameters? rp = null;
        if (query is { Length: > 0 }) {
            rp = new DefaultRequestParameters();
            foreach (var (name, value) in query) rp.SetQueryString(name, value);
        }
        var resp = (body, rp) switch {
            (null, null) => _client.Transport.Request<StringResponse>(method, path),
            (null, _) => _client.Transport.Request<StringResponse>(method, path, PostData.Empty, rp),
            (_, null) => _client.Transport.Request<StringResponse>(method, path, body),
            _ => _client.Transport.Request<StringResponse>(method, path, body, rp),
        };
        if (allowStatus >= 0 && resp.ApiCallDetails?.HttpStatusCode == allowStatus) return null;
        ensureOk(resp, $"{method} {path}");
        return string.IsNullOrEmpty(resp.Body) ? null : JsonDocument.Parse(resp.Body);
    }
    static void ensureOk(StringResponse resp, string what) {
        if (resp.ApiCallDetails?.HasSuccessfulStatusCode == true) return;
        var status = resp.ApiCallDetails?.HttpStatusCode;
        var detail = resp.ApiCallDetails?.DebugInformation ?? resp.Body ?? "no detail";
        throw new InvalidOperationException(
            $"Elasticsearch request failed: {what} (status {status?.ToString() ?? "none - is the cluster reachable?"}).\n{detail}");
    }

    static HashSet<string> selectionValues(List<Selection>? selections, string dim) =>
        selections?.FirstOrDefault(s => s.Property == dim)?.Values?.ToHashSet() ?? [];
    static double parseD(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    static string str(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    public void Dispose() { } // the transport owns no disposable per-request state
}

public record Selection(string Property, List<string>? Values, List<RangeSel>? Ranges);
public record RangeSel(string From, string To);
public record SearchRequest(string? Query, int Page, List<Selection>? Selections);
