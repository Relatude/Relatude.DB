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
        precomputeLanding();
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
        Console.WriteLine($"  merging segments (single segment makes facet counting much faster)...");
        writer.ForceMerge(1);
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
        var noSelections = selections == null || selections.Count == 0;
        var noQuery = string.IsNullOrWhiteSpace(query);
        var textQuery = parseText(query);
        var buckets = priceBuckets();

        TopDocs top;
        Facets valueCounts;
        int[] priceCounts;
        if (noSelections) {
            if (noQuery && _landing != null) { // landing page: counts precomputed at startup, only the page of items is live
                top = _searcher.Search(textQuery, (page + 1) * pageSize);
                (valueCounts, priceCounts) = _landing.Value;
            } else {
                var collector = new FacetsCollector();
                top = FacetsCollector.Search(_searcher, textQuery, (page + 1) * pageSize, collector);
                (valueCounts, priceCounts) = buildCounts(collector, collector);
            }
        } else {
            // one pass with drill-sideways: results honor all selections (AND across facets, OR
            // within one), while each selected facet is counted against the OTHER selections only,
            // so its alternatives stay visible - the same semantics as Relatude:
            var ds = new ShopDrillSideways(this, buckets);
            var result = ds.Search(drillDown(textQuery, selections), (page + 1) * pageSize);
            top = result.Hits;
            valueCounts = result.Facets;
            priceCounts = ds.PriceCounts!;
        }

        var facets = new List<object>();
        foreach (var dim in _valueDims) {
            var result = valueCounts.GetTopChildren(64, dim);
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
                    count = priceCounts[i],
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

        var sourceCount = noSelections ? top.TotalHits : countTextHits(query!, textQuery);
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

    (Facets values, int[] price) buildCounts(FacetsCollector valuesCollector, FacetsCollector priceCollector) {
        // the two counting passes are independent: run them in parallel
        Facets values = null!;
        int[] price = null!;
        Parallel.Invoke(
            () => values = new SortedSetDocValuesFacetCounts(_ssdvState, valuesCollector),
            () => price = countPriceRanges(priceCollector, priceBuckets()));
        return (values, price);
    }

    // Counts all price buckets in one pass over the matching docs, with the same raw double
    // comparisons the range filter uses, so displayed counts and filtered totals always agree
    // (Lucene's DoubleRangeFacetCounts interval logic was observed double counting a few docs).
    static int[] countPriceRanges(FacetsCollector hits, DoubleRange[] buckets) {
        var counts = new int[buckets.Length];
        var lastMax = buckets[^1].Max;
        foreach (var matching in hits.GetMatchingDocs()) {
            var values = matching.Context.AtomicReader.GetNumericDocValues("price_dv");
            if (values == null || matching.Bits == null) continue;
            var it = matching.Bits.GetIterator();
            if (it == null) continue;
            int doc;
            while ((doc = it.NextDoc()) != DocIdSetIterator.NO_MORE_DOCS) {
                var v = BitConverter.Int64BitsToDouble(values.Get(doc));
                if (v < buckets[0].Min || v > lastMax) continue;
                for (var i = buckets.Length - 1; i >= 0; i--) {
                    if (v >= buckets[i].Min) { counts[i]++; break; } // buckets are contiguous half open ranges
                }
            }
        }
        return counts;
    }

    (Facets values, int[] price)? _landing; // precomputed counts for the landing page (static index)
    void precomputeLanding() {
        var collector = new FacetsCollector();
        FacetsCollector.Search(_searcher, new MatchAllDocsQuery(), 1, collector);
        _landing = buildCounts(collector, collector);
    }

    readonly Dictionary<string, int> _textHitCounts = []; // the index is static, so text hit counts never change
    int countTextHits(string query, Query textQuery) {
        lock (_textHitCounts) if (_textHitCounts.TryGetValue(query, out var cached)) return cached;
        var counter = new TotalHitCountCollector();
        _searcher.Search(textQuery, counter);
        lock (_textHitCounts) {
            if (_textHitCounts.Count > 10_000) _textHitCounts.Clear();
            _textHitCounts[query] = counter.TotalHits;
        }
        return counter.TotalHits;
    }

    // Drill-sideways collects, per selected facet, the documents matching everything EXCEPT that
    // facet's own selection - which is exactly the "count against the other selections" rule.
    sealed class ShopDrillSideways(LuceneShop shop, DoubleRange[] buckets) : DrillSideways(shop._searcher, shop._config, shop._ssdvState) {
        public int[]? PriceCounts;
        protected override Facets BuildFacetsResult(FacetsCollector? drillDowns, FacetsCollector[]? drillSideways, string[]? drillSidewaysDims) {
            var byDim = new Dictionary<string, Facets>();
            Facets main = null!;
            var work = new List<Action> { () => main = new SortedSetDocValuesFacetCounts(shop._ssdvState, drillDowns!) };
            var priceCollector = drillDowns!; // price counted over the full result unless Price itself is selected
            for (var i = 0; i < (drillSidewaysDims?.Length ?? 0); i++) {
                var dim = drillSidewaysDims![i];
                var fc = drillSideways![i];
                if (dim == "Price") priceCollector = fc;
                else work.Add(() => { var f = new SortedSetDocValuesFacetCounts(shop._ssdvState, fc); lock (byDim) byDim[dim] = f; });
            }
            work.Add(() => PriceCounts = countPriceRanges(priceCollector, buckets));
            Parallel.Invoke([.. work]);
            return new MultiFacets(byDim, main);
        }
    }

    Query parseText(string? query) {
        if (string.IsNullOrWhiteSpace(query)) return new MatchAllDocsQuery();
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 1) return new TermQuery(new Term("text", terms[0]));
        var bq = new BooleanQuery(); // all words must match, like Relatude's WhereSearch default
        foreach (var t in terms) bq.Add(new TermQuery(new Term("text", t)), Occur.MUST);
        return bq;
    }
    DrillDownQuery drillDown(Query baseQuery, List<Selection>? selections) {
        var ddq = new DrillDownQuery(_config, baseQuery);
        foreach (var sel in selections ?? []) {
            foreach (var v in sel.Values ?? []) ddq.Add(sel.Property, v);
            foreach (var r in sel.Ranges ?? []) {
                var from = double.Parse(r.From, CultureInfo.InvariantCulture);
                var to = double.Parse(r.To, CultureInfo.InvariantCulture);
                // filter on the same docvalues the range counting reads, so filter and count always agree
                ddq.Add("Price", new ConstantScoreQuery(FieldCacheRangeFilter.NewDoubleRange("price_dv", from, to, true, false)));
            }
        }
        return ddq;
    }
    DoubleRange[] priceBuckets() {
        const int bucketCount = 10;
        var step = (_priceMax - _priceMin) / bucketCount;
        var buckets = new DoubleRange[bucketCount];
        for (var i = 0; i < bucketCount; i++) {
            var lo = Math.Round(_priceMin + i * step);
            var hi = i == bucketCount - 1 ? _priceMax : Math.Round(_priceMin + (i + 1) * step);
            // prices have two decimals, so boundaries offset by 0.005 sit BETWEEN representable
            // prices: counting and filtering can then never disagree about a boundary document
            buckets[i] = new DoubleRange($"{lo} -> {hi}", lo - 0.005, true, hi + (i == bucketCount - 1 ? 0.005 : -0.005), false);
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
