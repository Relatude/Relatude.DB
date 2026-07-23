using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Website.Sqlite;

// Pure SQLite implementation of the same facet search as Website.Simple's /shop/search and
// Website.Lucene, for a side by side comparison. Same deterministic catalog (keep the seeder in
// sync with Website.Simple/Models/ShopSeeder.cs: identical word banks and Random(2026) draw
// order), same facets and the same JSON contract, so all UIs behave the same.
//
// Unlike the Lucene example nothing is cached: every request computes all counts live.
// Performance instead comes from the schema and query shape:
//  - text search via a contentless FTS5 table with detail=none (smallest possible index; it can
//    only answer "which rowids match", which is all the search needs - no phrases, no ranking)
//  - a narrow ints-only fact row per product, with the price bucket and a tag bitmask stamped in,
//    so ONE covering-index group-by scan yields the counts for every facet at once
//  - drill-sideways semantics ("count each selected facet against the other selections only")
//    become one such group-by per selected facet, and each of those is partitioned into chunks
//    (by category, or by rowid range when a text query drives) that run in parallel on a pool of
//    read-only connections
public sealed class SqliteShop : IDisposable {
    readonly string _dbPath;
    readonly string _connString;
    readonly ConcurrentBag<SqliteConnection> _pool = [];
    readonly double _priceMin;
    readonly double _priceMax;
    readonly Bucket[] _buckets;
    static readonly string[] _valueDims = ["Category", "Brand", "InStock", "Tags"];

    static SqliteShop() {
        // by default SQLite guards every malloc/free with a global statistics mutex, which
        // serializes allocation-heavy queries (fts5 doclists, group-by temp b-trees) across ALL
        // connections - turning it off made the parallel count chunks 13x faster on 24 cores
        SQLitePCL.Batteries_V2.Init();
        SQLitePCL.raw.sqlite3_shutdown();
        SQLitePCL.raw.sqlite3_config(9 /* SQLITE_CONFIG_MEMSTATUS */, 0);
        SQLitePCL.raw.sqlite3_config(2 /* SQLITE_CONFIG_MULTITHREAD */);
        SQLitePCL.raw.sqlite3_initialize();
    }

    public SqliteShop(string dbPath, int productCount) {
        _dbPath = Path.GetFullPath(dbPath);
        if (!File.Exists(_dbPath)) build(productCount);
        _connString = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString;
        var c = open();
        try {
            Count = (int)(long)scalar(c, "SELECT max(id) FROM product")!; // ids are 1..N without gaps, so max(id) avoids a full COUNT(*) scan
            _priceMin = (double)scalar(c, "SELECT min(price) FROM product")!; // instant via idx_product_price
            _priceMax = (double)scalar(c, "SELECT max(price) FROM product")!;
        } finally { _pool.Add(c); }
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

    record ProductRow(string Name, string Description, int CategoryId, int BrandId, double Price, bool InStock, int[] TagIds);
    static IEnumerable<ProductRow> generate(int count) {
        var rnd = new Random(2026); // deterministic content, same sequence as ShopSeeder / LuceneShop
        for (var i = 0; i < count; i++) {
            var catId = rnd.Next(_categories.Length);
            var cat = _categories[catId];
            var adjective = _adjectives[rnd.Next(_adjectives.Length)];
            var material = _materials[rnd.Next(_materials.Length)];
            var feature = _features[rnd.Next(_features.Length)];
            var feature2 = _features[rnd.Next(_features.Length)];
            var noun = cat.Nouns[rnd.Next(cat.Nouns.Length)];
            var use = cat.Uses[rnd.Next(cat.Uses.Length)];
            var brandId = rnd.Next(_brandNames.Length);
            var brand = _brandNames[brandId];
            var name = $"{adjective} {material} {noun}".Replace(material, upperFirst(material));
            var description = $"A {adjective.ToLower()} {noun.ToLower()} in {material}, {feature} and {feature2}. Made by {brand}, perfect for {use}.";
            var price = Math.Round(9 + Math.Pow(rnd.NextDouble(), 2) * 1990, 2);
            var inStock = rnd.Next(5) > 0;
            var tagIds = Enumerable.Range(0, rnd.Next(3)).Select(_ => rnd.Next(_tags.Length)).Distinct().ToArray();
            yield return new ProductRow(name, description, catId, brandId, price, inStock, tagIds);
        }
    }

    void build(int productCount) {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Building SQLite database with {productCount:n0} products...");
        // pass 1 runs the full deterministic generator just to learn the actual price min/max,
        // so every product can be stamped with its price bucket during insert (the counting
        // index needs the bucket as a plain column to keep the group-by scan streaming)
        double min = double.MaxValue, max = double.MinValue;
        foreach (var p in generate(productCount)) {
            if (p.Price < min) min = p.Price;
            if (p.Price > max) max = p.Price;
        }
        var buckets = makeBuckets(min, max);
        Console.WriteLine($"  price bounds scanned in {sw.Elapsed.TotalSeconds:0}s");

        var tmp = _dbPath + ".building"; // build aside and rename, so a killed build never leaves a half database behind
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ConnectionString)) {
            db.Open();
            exec(db,
                "PRAGMA page_size=8192",
                "PRAGMA journal_mode=OFF", // no durability needed: a crashed build is deleted and redone
                "PRAGMA synchronous=OFF",
                "PRAGMA locking_mode=EXCLUSIVE",
                "PRAGMA cache_size=-262144",
                // narrow ints-only "hot" row, so counting scans and rowid probes touch as few
                // pages as possible; display strings live in product_detail and are only read
                // for the ten visible items
                """
                CREATE TABLE product(
                    id INTEGER PRIMARY KEY,
                    category_id INTEGER NOT NULL,
                    brand_id INTEGER NOT NULL,
                    in_stock INTEGER NOT NULL,
                    bucket INTEGER NOT NULL,
                    tags_mask INTEGER NOT NULL,
                    price REAL NOT NULL)
                """,
                "CREATE TABLE product_detail(id INTEGER PRIMARY KEY, name TEXT NOT NULL, description TEXT NOT NULL, tags TEXT NOT NULL)",
                "CREATE VIRTUAL TABLE product_fts USING fts5(text, content='', detail='none', columnsize='0')");

            using var insProduct = db.CreateCommand();
            insProduct.CommandText = "INSERT INTO product VALUES ($id, $cat, $brand, $stock, $bucket, $mask, $price)";
            var pId = insProduct.Parameters.Add("$id", SqliteType.Integer);
            var pCat = insProduct.Parameters.Add("$cat", SqliteType.Integer);
            var pBrand = insProduct.Parameters.Add("$brand", SqliteType.Integer);
            var pStock = insProduct.Parameters.Add("$stock", SqliteType.Integer);
            var pBucket = insProduct.Parameters.Add("$bucket", SqliteType.Integer);
            var pMask = insProduct.Parameters.Add("$mask", SqliteType.Integer);
            var pPrice = insProduct.Parameters.Add("$price", SqliteType.Real);
            using var insDetail = db.CreateCommand();
            insDetail.CommandText = "INSERT INTO product_detail VALUES ($id, $name, $desc, $tags)";
            var dId = insDetail.Parameters.Add("$id", SqliteType.Integer);
            var dName = insDetail.Parameters.Add("$name", SqliteType.Text);
            var dDesc = insDetail.Parameters.Add("$desc", SqliteType.Text);
            var dTags = insDetail.Parameters.Add("$tags", SqliteType.Text);
            using var insFts = db.CreateCommand();
            insFts.CommandText = "INSERT INTO product_fts(rowid, text) VALUES ($id, $text)";
            var fId = insFts.Parameters.Add("$id", SqliteType.Integer);
            var fText = insFts.Parameters.Add("$text", SqliteType.Text);

            exec(db, "BEGIN");
            var id = 0;
            foreach (var p in generate(productCount)) {
                id++;
                var mask = 0;
                foreach (var t in p.TagIds) mask |= 1 << t;
                pId.Value = id; pCat.Value = p.CategoryId; pBrand.Value = p.BrandId; pStock.Value = p.InStock ? 1 : 0;
                pBucket.Value = bucketOf(p.Price, buckets); pMask.Value = mask; pPrice.Value = p.Price;
                insProduct.ExecuteNonQuery();
                dId.Value = id; dName.Value = p.Name; dDesc.Value = p.Description;
                dTags.Value = string.Join("|", p.TagIds.Select(t => _tags[t]));
                insDetail.ExecuteNonQuery();
                fId.Value = id; fText.Value = p.Name + " " + p.Description;
                insFts.ExecuteNonQuery();
                if (id % 500_000 == 0) exec(db, "COMMIT", "BEGIN");
                if (id % 1_000_000 == 0) Console.WriteLine($"  inserted {id:n0} in {sw.Elapsed.TotalSeconds:0}s");
            }
            exec(db, "COMMIT");
            Console.WriteLine("  creating indexes and merging fts segments...");
            exec(db,
                // the one covering index every count query scans; its column order is also the
                // GROUP BY order, so aggregation streams in index order without a temp b-tree
                "CREATE INDEX idx_product_count ON product(category_id, brand_id, in_stock, bucket, tags_mask)",
                "CREATE INDEX idx_product_price ON product(price)",
                "INSERT INTO product_fts(product_fts) VALUES('optimize')", // like Lucene's ForceMerge(1)
                "PRAGMA analysis_limit=1000",
                "ANALYZE");
        }
        SqliteConnection.ClearAllPools();
        File.Move(tmp, _dbPath);
        Console.WriteLine($"Database complete: {productCount:n0} products in {sw.Elapsed.TotalSeconds:0}s");
    }
    #endregion

    readonly record struct Bucket(double Lo, double Hi, double Min, double Max, string Label);
    static Bucket[] makeBuckets(double priceMin, double priceMax) {
        const int bucketCount = 10;
        var step = (priceMax - priceMin) / bucketCount;
        var buckets = new Bucket[bucketCount];
        for (var i = 0; i < bucketCount; i++) {
            var lo = Math.Round(priceMin + i * step);
            var hi = i == bucketCount - 1 ? priceMax : Math.Round(priceMin + (i + 1) * step);
            // prices have two decimals, so boundaries offset by 0.005 sit BETWEEN representable
            // prices: counting and filtering can then never disagree about a boundary document
            buckets[i] = new Bucket(lo, hi, lo - 0.005, hi + (i == bucketCount - 1 ? 0.005 : -0.005), $"{lo} -> {hi}");
        }
        return buckets;
    }
    static int bucketOf(double v, Bucket[] buckets) {
        for (var i = buckets.Length - 1; i >= 0; i--) {
            if (v >= buckets[i].Min) return i; // buckets are contiguous half open ranges
        }
        return 0;
    }

    public object Search(string? query, int page, List<Selection>? selections) {
        var sw = Stopwatch.StartNew();
        const int pageSize = 10;
        var noSelections = selections == null || selections.Count == 0;
        var f = normalize(selections);
        var fts = ftsQuery(query, out var impossible);

        var full = new Rollup(); // counts under ALL selections: total + every unselected facet
        var byDim = new Dictionary<string, Rollup>(); // per selected facet: counts under the OTHER selections only
        var items = new List<object>();
        long ftsHits = 0;

        if (!impossible) {
            var sets = new List<(string? Exclude, Rollup Roll)> { (null, full) };
            void sideways(string dim) { var r = new Rollup(); byDim[dim] = r; sets.Add((dim, r)); }
            if (f.Categories != null) sideways("Category");
            if (f.Brands != null) sideways("Brand");
            if (f.Stocks != null) sideways("InStock");
            if (f.TagsSelected) sideways("Tags");
            if (f.PriceSelected) sideways("Price");

            // every count set is partitioned into chunks that run concurrently; each chunk query
            // returns at most a few thousand group rows which are rolled up per facet in C#
            var tasks = new List<Action>();
            foreach (var (exclude, roll) in sets) {
                if (fts == null) {
                    // no text query: chunk by (category, brand half) so the covering index is
                    // scanned in 12 parallel ranges, each streaming its GROUP BY in index order;
                    // category/brand selections skip chunks instead of filtering inside them,
                    // which keeps the index seek benefit without disturbing the scan order
                    var where = whereSql(f, exclude, "");
                    for (var cat = 0; cat < _categories.Length; cat++) {
                        if (exclude != "Category" && f.Categories != null && !f.Categories.Contains(cat)) continue;
                        foreach (var (b0, b1) in new[] { (0, _brandNames.Length / 2), (_brandNames.Length / 2, _brandNames.Length) }) {
                            if (exclude != "Brand" && f.Brands != null && !f.Brands.Any(b => b >= b0 && b < b1)) continue;
                            var sql = $"""
                                SELECT category_id, brand_id, in_stock, bucket, tags_mask, count(*)
                                FROM product INDEXED BY idx_product_count
                                WHERE category_id = {cat} AND brand_id >= {b0} AND brand_id < {b1}{where}
                                GROUP BY category_id, brand_id, in_stock, bucket, tags_mask
                                """;
                            tasks.Add(() => roll.Merge(countChunk(sql, null)));
                        }
                    }
                } else {
                    // text query: the fts match drives and probes the narrow product row per hit
                    // (the dominant cost), so chunk by rowid range to spread the probes over cores
                    var where = whereSql(f, exclude, "p.");
                    foreach (var (lo, hi) in rowidChunks()) {
                        var sql = $"""
                            SELECT p.category_id, p.brand_id, p.in_stock, p.bucket, p.tags_mask, count(*)
                            FROM product_fts f CROSS JOIN product p ON p.id = f.rowid
                            WHERE f.product_fts MATCH $q AND f.rowid >= {lo} AND f.rowid < {hi}{where}
                            GROUP BY p.category_id, p.brand_id, p.in_stock, p.bucket, p.tags_mask
                            """;
                        tasks.Add(() => roll.Merge(countChunk(sql, fts)));
                    }
                }
            }
            tasks.Add(() => items.AddRange(fetchItems(f, fts, page, pageSize)));
            if (!noSelections && fts != null) tasks.Add(() => ftsHits = countFts(fts));
            Parallel.ForEach(tasks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, t => t());
        }

        var total = full.Total;
        var sourceCount = impossible ? 0 : noSelections ? total : fts == null ? Count : ftsHits;

        var facets = new List<object>();
        foreach (var dim in _valueDims) {
            var roll = byDim.TryGetValue(dim, out var r) ? r : full;
            var (labels, counts) = dim switch {
                "Category" => (_categories.Select(c => c.Name).ToArray(), roll.Category),
                "Brand" => (_brandNames, roll.Brand),
                "InStock" => (new[] { "False", "True" }, roll.Stock),
                _ => (_tags, roll.Tags),
            };
            var values = labels.Select((label, i) => new { label, count = (int)counts[i] }).Where(v => v.count > 0).ToList();
            if (dim == "Tags") values = values.OrderByDescending(v => v.count).ThenBy(v => v.label, StringComparer.Ordinal).Take(8).ToList(); // mirror SetFacetOptions(Tags, maxValues: 8, sortByCount: true); Lucene breaks count ties by label
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
            var priceCounts = (byDim.TryGetValue("Price", out var pr) ? pr : full).Bucket;
            var selectedRanges = (selections?.FirstOrDefault(s => s.Property == "Price")?.Ranges ?? [])
                .Select(r => (from: double.Parse(r.From, CultureInfo.InvariantCulture), to: double.Parse(r.To, CultureInfo.InvariantCulture))).ToList();
            facets.Add(new {
                property = "Price",
                displayName = "Price",
                isRange = true,
                values = _buckets.Select((b, i) => new {
                    value = str(b.Min),
                    value2 = (string?)str(b.Max),
                    display = b.Label,
                    count = (int)priceCounts[i],
                    selected = selectedRanges.Any(rg => rg.from == b.Min && rg.to == b.Max),
                }),
            });
        }

        return new {
            total,
            sourceCount,
            page,
            pageSize,
            durationMs = sw.Elapsed.TotalMilliseconds,
            items,
            facets,
        };
    }

    // one accumulator per WHERE set: a single group-by scan yields every facet's counts at once
    sealed class Rollup {
        public readonly long[] Category = new long[_categories.Length];
        public readonly long[] Brand = new long[_brandNames.Length];
        public readonly long[] Stock = new long[2];
        public readonly long[] Bucket = new long[10];
        public readonly long[] Tags = new long[_tags.Length];
        public long Total;
        public void Add(int cat, int brand, int stock, int bucket, int mask, long n) {
            Category[cat] += n; Brand[brand] += n; Stock[stock] += n; Bucket[bucket] += n; Total += n;
            while (mask != 0) { Tags[System.Numerics.BitOperations.TrailingZeroCount(mask)] += n; mask &= mask - 1; }
        }
        public void Merge(Rollup o) {
            lock (this) {
                for (var i = 0; i < Category.Length; i++) Category[i] += o.Category[i];
                for (var i = 0; i < Brand.Length; i++) Brand[i] += o.Brand[i];
                for (var i = 0; i < Stock.Length; i++) Stock[i] += o.Stock[i];
                for (var i = 0; i < Bucket.Length; i++) Bucket[i] += o.Bucket[i];
                for (var i = 0; i < Tags.Length; i++) Tags[i] += o.Tags[i];
                Total += o.Total;
            }
        }
    }

    Rollup countChunk(string sql, string? match) {
        var part = new Rollup();
        withConn(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            if (match != null) cmd.Parameters.AddWithValue("$q", match);
            using var r = cmd.ExecuteReader();
            while (r.Read()) part.Add(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt64(5));
            return 0;
        });
        return part;
    }

    IEnumerable<(long Lo, long Hi)> rowidChunks() {
        var n = Math.Clamp(Environment.ProcessorCount, 1, 16);
        var size = Math.Max(1, ((long)Count + n - 1) / n);
        for (var lo = 1L; lo <= Count; lo += size) yield return (lo, Math.Min(lo + size, Count + 1L));
    }

    List<object> fetchItems(Filters f, string? fts, int page, int pageSize) {
        var where = whereSql(f, null, "p.");
        // items come in insertion (rowid) order - the one visible difference to the Lucene
        // example, which orders text hits by relevance score
        var sql = fts == null
            ? $"""
               SELECT p.price, p.category_id, p.brand_id, p.in_stock, d.name, d.description, d.tags
               FROM product AS p NOT INDEXED CROSS JOIN product_detail d ON d.id = p.id
               WHERE 1=1{where}
               ORDER BY p.id LIMIT {pageSize} OFFSET {page * pageSize}
               """
            : $"""
               SELECT p.price, p.category_id, p.brand_id, p.in_stock, d.name, d.description, d.tags
               FROM product_fts f CROSS JOIN product p ON p.id = f.rowid CROSS JOIN product_detail d ON d.id = p.id
               WHERE f.product_fts MATCH $q{where}
               ORDER BY f.rowid LIMIT {pageSize} OFFSET {page * pageSize}
               """;
        return withConn(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            if (fts != null) cmd.Parameters.AddWithValue("$q", fts);
            using var r = cmd.ExecuteReader();
            var list = new List<object>();
            while (r.Read()) {
                list.Add(new {
                    name = r.GetString(4),
                    description = r.GetString(5),
                    category = _categories[r.GetInt32(1)].Name,
                    brand = _brandNames[r.GetInt32(2)],
                    price = r.GetDouble(0),
                    inStock = r.GetInt32(3) == 1,
                    tags = r.GetString(6) is { Length: > 0 } t ? t.Split('|') : [],
                });
            }
            return list;
        });
    }

    long countFts(string match) => withConn(c => {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM product_fts WHERE product_fts MATCH $q";
        cmd.Parameters.AddWithValue("$q", match);
        return (long)cmd.ExecuteScalar()!;
    });

    sealed class Filters {
        public int[]? Categories, Brands, Stocks;
        public bool TagsSelected;
        public int TagsMask;
        public int[]? Buckets; // price selections that align with bucket boundaries (always the case from the UI)
        public List<(double From, double To)>? Ranges; // fallback for arbitrary from/to values
        public bool PriceSelected => Buckets != null || Ranges != null;
    }

    Filters normalize(List<Selection>? selections) {
        var f = new Filters();
        foreach (var sel in selections ?? []) {
            if (sel.Property == "Price") {
                if (sel.Ranges is not { Count: > 0 }) continue;
                List<int>? idx = null;
                foreach (var r in sel.Ranges) {
                    var from = double.Parse(r.From, CultureInfo.InvariantCulture);
                    var to = double.Parse(r.To, CultureInfo.InvariantCulture);
                    var i = Array.FindIndex(_buckets, b => b.Min == from && b.Max == to);
                    if (i >= 0) (idx ??= []).Add(i);
                    else (f.Ranges ??= []).Add((from, to));
                }
                f.Buckets = idx?.ToArray();
            } else if (sel.Values is { Count: > 0 }) {
                switch (sel.Property) { // unknown values map to -1 and can never match, like an unknown drill-down term in Lucene
                    case "Category": f.Categories = sel.Values.Select(v => Array.FindIndex(_categories, c => c.Name == v)).ToArray(); break;
                    case "Brand": f.Brands = sel.Values.Select(v => Array.IndexOf(_brandNames, v)).ToArray(); break;
                    case "InStock": f.Stocks = sel.Values.Select(v => v == "True" ? 1 : v == "False" ? 0 : -1).ToArray(); break;
                    case "Tags":
                        f.TagsSelected = true;
                        f.TagsMask = sel.Values.Select(v => Array.IndexOf(_tags, v)).Where(i => i >= 0).Aggregate(0, (m, i) => m | (1 << i));
                        break;
                }
            }
        }
        return f;
    }

    // AND across facets, OR within one; excludeDim implements drill-sideways: the selected facet
    // itself is counted against the other selections only, so its alternatives stay visible.
    // Every column is prefixed with unary + so the term is a plain row filter: letting the
    // planner "use" a filter on an idx_product_count column demotes the streaming group-by to a
    // temp b-tree over every matching row (3x slower measured, worse for large results)
    string whereSql(Filters f, string? excludeDim, string p) {
        var sb = new StringBuilder();
        if (f.Categories != null && excludeDim != "Category") sb.Append($" AND +{p}category_id IN ({string.Join(',', f.Categories)})");
        if (f.Brands != null && excludeDim != "Brand") sb.Append($" AND +{p}brand_id IN ({string.Join(',', f.Brands)})");
        if (f.Stocks != null && excludeDim != "InStock") sb.Append($" AND +{p}in_stock IN ({string.Join(',', f.Stocks)})");
        if (f.TagsSelected && excludeDim != "Tags") sb.Append(f.TagsMask == 0 ? " AND 0" : $" AND ({p}tags_mask & {f.TagsMask}) != 0");
        if (f.PriceSelected && excludeDim != "Price") {
            var parts = new List<string>();
            if (f.Buckets != null) parts.Add($"+{p}bucket IN ({string.Join(',', f.Buckets)})");
            foreach (var (from, to) in f.Ranges ?? []) parts.Add($"({p}price >= {str(from)} AND {p}price < {str(to)})"); // same half open semantics as the bucket boundaries
            sb.Append($" AND ({string.Join(" OR ", parts)})");
        }
        return sb.ToString();
    }

    // FTS5's unicode61 tokenizer splits on anything that is not a letter or digit, so tokenize
    // the query the same way; every token must match (implicit AND), like Lucene's parseText
    static string? ftsQuery(string? query, out bool impossible) {
        impossible = false;
        if (string.IsNullOrWhiteSpace(query)) return null;
        var tokens = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]+");
        if (tokens.Count == 0) { impossible = true; return null; } // e.g. "&": no searchable token, can never match
        return string.Join(" ", tokens.Select(t => $"\"{t.Value}\""));
    }

    static HashSet<string> selectionValues(List<Selection>? selections, string dim) =>
        selections?.FirstOrDefault(s => s.Property == dim)?.Values?.ToHashSet() ?? [];
    static string str(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    T withConn<T>(Func<SqliteConnection, T> fn) {
        var c = _pool.TryTake(out var pooled) ? pooled : open();
        try { return fn(c); } finally { _pool.Add(c); }
    }
    SqliteConnection open() {
        var c = new SqliteConnection(_connString);
        c.Open();
        // mmap the whole file so reads hit the OS page cache directly instead of being copied
        // into SQLite's own page cache; group-by temp b-trees stay in memory
        exec(c, "PRAGMA mmap_size=17179869184", "PRAGMA temp_store=MEMORY", "PRAGMA cache_size=-65536");
        return c;
    }
    static void exec(SqliteConnection db, params string[] sqls) {
        foreach (var sql in sqls) {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
    static object? scalar(SqliteConnection db, string sql) {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() {
        while (_pool.TryTake(out var c)) c.Dispose();
    }
}

public record Selection(string Property, List<string>? Values, List<RangeSel>? Ranges);
public record RangeSel(string From, string To);
public record SearchRequest(string? Query, int Page, List<Selection>? Selections);
