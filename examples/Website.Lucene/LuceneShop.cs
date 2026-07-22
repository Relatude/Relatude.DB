using System.Diagnostics;
using System.Globalization;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Facet;
using Lucene.Net.Facet.Range;
using Lucene.Net.Facet.SortedSet;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Website.Lucene;

// Pure Lucene.NET implementation of the same facet search as Website.Simple's /shop/search,
// for a side by side comparison. Same deterministic catalog (keep the seeder in sync with
// Website.Simple/Models/ShopSeeder.cs: identical word banks and Random(2026) draw order),
// same facets and the same JSON contract, so both UIs behave the same.
public sealed class LuceneShop : IDisposable {
    const LuceneVersion V = LuceneVersion.LUCENE_48;
    readonly FSDirectory _dir;
    readonly DirectoryReader _reader;
    readonly IndexSearcher _searcher;
    readonly FacetsConfig _config = new();
    readonly SortedSetDocValuesReaderState _ssdvState;
    readonly double _priceMin;
    readonly double _priceMax;
    static readonly string[] _valueDims = ["Category", "Brand", "InStock", "Tags"];

    public LuceneShop(string indexPath, int productCount) {
        _config.SetMultiValued("Tags", true);
        _dir = FSDirectory.Open(indexPath);
        if (!DirectoryReader.IndexExists(_dir)) build(productCount);
        _reader = DirectoryReader.Open(_dir);
        _searcher = new IndexSearcher(_reader);
        _ssdvState = new DefaultSortedSetDocValuesReaderState(_reader);
        _priceMin = priceBound(reverse: false);
        _priceMax = priceBound(reverse: true);
    }
    public int Count => _reader.NumDocs;

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

    void build(int productCount) {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Building Lucene index with {productCount:n0} products...");
        using var analyzer = new StandardAnalyzer(V);
        using var writer = new IndexWriter(_dir, new IndexWriterConfig(V, analyzer) { RAMBufferSizeMB = 256 });
        var rnd = new Random(2026); // deterministic content, same sequence as ShopSeeder
        for (var i = 0; i < productCount; i++) {
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

            var doc = new Document {
                new TextField("text", name + " " + description, Field.Store.NO),
                new StoredField("name", name),
                new StoredField("description", description),
                new StoredField("category", cat.Name),
                new StoredField("brand", brand),
                new StoredField("price", price),
                new StoredField("instock", inStock ? "True" : "False"),
                new StoredField("tags", string.Join("|", tags)),
                new DoubleField("price_num", price, Field.Store.NO), // range filtering
                new DoubleDocValuesField("price_dv", price), // range counting and min/max
                new SortedSetDocValuesFacetField("Category", cat.Name),
                new SortedSetDocValuesFacetField("Brand", brand),
                new SortedSetDocValuesFacetField("InStock", inStock ? "True" : "False"),
            };
            foreach (var tag in tags) doc.Add(new SortedSetDocValuesFacetField("Tags", tag));
            writer.AddDocument(_config.Build(doc));
            if ((i + 1) % 1_000_000 == 0) Console.WriteLine($"  indexed {i + 1:n0} in {sw.Elapsed.TotalSeconds:0}s");
        }
        writer.Commit();
        Console.WriteLine($"Index complete: {productCount:n0} products in {sw.Elapsed.TotalSeconds:0}s");
    }
    #endregion

    double priceBound(bool reverse) {
        var top = _searcher.Search(new MatchAllDocsQuery(), 1, new Sort(new SortField("price_dv", SortFieldType.DOUBLE, reverse)));
        if (top.TotalHits == 0) return 0;
        return Convert.ToDouble(((FieldDoc)top.ScoreDocs[0]).Fields[0]); // Lucene.NET returns J2N.Numerics.Double
    }

    public object Search(string? query, int page, List<Selection>? selections) {
        var sw = Stopwatch.StartNew();
        const int pageSize = 10;
        var textQuery = parseText(query);
        var buckets = priceBuckets();

        // AND across facets, OR within one (DrillDownQuery does exactly that):
        var full = drillDown(textQuery, selections, exceptProperty: null);
        var collector = new FacetsCollector();
        var top = FacetsCollector.Search(_searcher, full, (page + 1) * pageSize, collector);

        // like Relatude: a selected facet's counts are computed against all OTHER selections,
        // so the alternatives in the selected facet stay visible:
        var countsByDim = new Dictionary<string, FacetsCollector> { };
        foreach (var dim in _valueDims.Append("Price")) {
            if (selections?.Any(s => s.Property == dim) == true) {
                var fc = new FacetsCollector();
                _searcher.Search(drillDown(textQuery, selections, exceptProperty: dim), fc);
                countsByDim[dim] = fc;
            } else {
                countsByDim[dim] = collector;
            }
        }

        var facets = new List<object>();
        foreach (var dim in _valueDims) {
            var counts = new SortedSetDocValuesFacetCounts(_ssdvState, countsByDim[dim]);
            var result = counts.GetTopChildren(64, dim);
            var values = (result?.LabelValues ?? [])
                .Select(lv => new { label = lv.Label, count = (int)lv.Value })
                .ToList();
            if (dim == "Tags") values = values.OrderByDescending(v => v.count).Take(8).ToList(); // mirror SetFacetOptions(Tags, maxValues: 8, sortByCount: true)
            else values = values.OrderBy(v => v.label, StringComparer.Ordinal).ToList();
            var selected = selectionValues(selections, dim);
            facets.Add(new {
                property = dim,
                displayName = dim,
                isRange = false,
                values = values.Select(v => new { value = v.label, value2 = (string?)null, display = v.label, count = v.count, selected = selected.Contains(v.label) }),
            });
        }
        {
            var priceCounts = new DoubleRangeFacetCounts("price_dv", countsByDim["Price"], buckets);
            var result = priceCounts.GetTopChildren(buckets.Length, "price_dv");
            var selectedRanges = (selections?.FirstOrDefault(s => s.Property == "Price")?.Ranges ?? [])
                .Select(r => (from: double.Parse(r.From, CultureInfo.InvariantCulture), to: double.Parse(r.To, CultureInfo.InvariantCulture))).ToList();
            facets.Add(new {
                property = "Price",
                displayName = "Price",
                isRange = true,
                values = buckets.Select((b, i) => new {
                    value = str(b.Min),
                    value2 = (string?)str(b.Max),
                    display = b.Label,
                    count = (int)result.LabelValues[i].Value,
                    selected = selectedRanges.Any(r => r.from == b.Min && r.to == b.Max),
                }),
            });
        }

        var items = top.ScoreDocs.Skip(page * pageSize).Take(pageSize).Select(sd => {
            var doc = _searcher.Doc(sd.Doc);
            return new {
                name = doc.Get("name"),
                description = doc.Get("description"),
                category = doc.Get("category"),
                brand = doc.Get("brand"),
                price = doc.GetField("price").GetDoubleValue() ?? 0,
                inStock = doc.Get("instock") == "True",
                tags = doc.Get("tags") is { Length: > 0 } t ? t.Split('|') : [],
            };
        }).ToList();

        var hitCounter = new TotalHitCountCollector();
        _searcher.Search(textQuery, hitCounter);
        var sourceCount = hitCounter.TotalHits;
        return new {
            total = top.TotalHits,
            sourceCount,
            page,
            pageSize,
            durationMs = sw.Elapsed.TotalMilliseconds,
            items,
            facets,
        };
    }

    Query parseText(string? query) {
        if (string.IsNullOrWhiteSpace(query)) return new MatchAllDocsQuery();
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 1) return new TermQuery(new Term("text", terms[0]));
        var bq = new BooleanQuery(); // all words must match, like Relatude's WhereSearch default
        foreach (var t in terms) bq.Add(new TermQuery(new Term("text", t)), Occur.MUST);
        return bq;
    }
    DrillDownQuery drillDown(Query baseQuery, List<Selection>? selections, string? exceptProperty) {
        var ddq = new DrillDownQuery(_config, baseQuery);
        foreach (var sel in selections ?? []) {
            if (sel.Property == exceptProperty) continue;
            foreach (var v in sel.Values ?? []) ddq.Add(sel.Property, v);
            foreach (var r in sel.Ranges ?? []) {
                var from = double.Parse(r.From, CultureInfo.InvariantCulture);
                var to = double.Parse(r.To, CultureInfo.InvariantCulture);
                ddq.Add("Price", NumericRangeQuery.NewDoubleRange("price_num", from, to, true, isLastBucket(from, to)));
            }
        }
        return ddq;
    }
    // buckets are half open except the last, like the generated ranges in Relatude
    bool isLastBucket(double from, double to) => to >= _priceMax;
    DoubleRange[] priceBuckets() {
        const int bucketCount = 10;
        var step = (_priceMax - _priceMin) / bucketCount;
        var buckets = new DoubleRange[bucketCount];
        for (var i = 0; i < bucketCount; i++) {
            var from = Math.Round(_priceMin + i * step);
            var to = i == bucketCount - 1 ? _priceMax : Math.Round(_priceMin + (i + 1) * step);
            var last = i == bucketCount - 1;
            buckets[i] = new DoubleRange($"{from} -> {to}", from, true, to, last);
        }
        return buckets;
    }
    static HashSet<string> selectionValues(List<Selection>? selections, string dim) =>
        selections?.FirstOrDefault(s => s.Property == dim)?.Values?.ToHashSet() ?? [];
    static string str(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    public void Dispose() {
        _reader.Dispose();
        _dir.Dispose();
    }
}

public record Selection(string Property, List<string>? Values, List<RangeSel>? Ranges);
public record RangeSel(string From, string To);
public record SearchRequest(string? Query, int Page, List<Selection>? Selections);
