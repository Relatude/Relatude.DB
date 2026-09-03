# Query reference

The full query surface. Examples use the venue-and-events model from `modelling-patterns.md`; `db` is a `NodeStore`.

## Contents

- [Query anatomy](#query-anatomy)
- [Entry points](#entry-points)
- [Executing](#executing)
- [Filtering with Where](#filtering-with-where)
- [Text and semantic search](#text-and-semantic-search)
- [Geo queries](#geo-queries)
- [Relation filters](#relation-filters)
- [Eager loading: Include and Preload](#eager-loading-include-and-preload)
- [Graph traversal and shortest path](#graph-traversal-and-shortest-path)
- [Sorting, paging and result sets](#sorting-paging-and-result-sets)
- [Aggregates](#aggregates)
- [Faceted search](#faceted-search)
- [Pivot tables](#pivot-tables)
- [GroupBy](#groupby)
- [Cultures, visibility and scoped stores](#cultures-visibility-and-scoped-stores)

## Query anatomy

Every query is built with the `IQueryOfNodes<TNode, TInclude>` builder and finished with `Execute()`.

```csharp
var result = db.Query<IEvent>()                              // 1. entry point
    .Where(e => e.Status == EventStatus.Published)           // 2. filters
    .WhereSearch("jazz quartet")                             // 3. search
    .Include(e => e.Venue)                                   // 4. eager loading
    .OrderBy(e => e.StartsUtc)                               // 5. sorting
    .Page(0, 20)                                             // 6. paging
    .Execute();                                              // 7. run
```

**The order of the chained calls does not matter** — the builder composes a query plan, it does not execute step by step.

**Prefer the builder methods over LINQ extensions on the result set.** The result set is already materialised, so `.Where()` on it happens in your process, while `.Where()` on the builder happens in the engine against the indexes.

## Entry points

```csharp
IQueryOfNodes<object, object> Query(QueryContext? ctx = null);      // all nodes, untyped

IQueryOfNodes<T, T> Query<T>(QueryContext? ctx = null);
IQueryOfNodes<T, T> Query<T>(Guid id, QueryContext? ctx = null);
IQueryOfNodes<T, T> Query<T>(int id, QueryContext? ctx = null);
IQueryOfNodes<T, T> Query<T>(IdKey id, QueryContext? ctx = null);
IQueryOfNodes<T, T> Query<T>(IEnumerable<Guid> ids, QueryContext? ctx = null);
IQueryOfNodes<T, T> Query<T>(Expression<Func<T, bool>> expression, QueryContext? ctx = null);
```

`Query<T>()` with no predicate matches **every instance of `T` and its subtypes**. That is the feature that makes facet-interface queries work — and the thing to narrow with `WhereTypes` when you want one concrete type only.

## Executing

```csharp
ResultSet<IEvent> rs = query.Execute();
ResultSet<IEvent> rs = await query.ExecuteAsync();
```

Single-row helpers (extension methods on the builder):

```csharp
IEvent? one   = query.FirstOrDefault();
IEvent  first = query.First();
IEvent  only  = query.Single();
IEvent? oneA  = await query.FirstOrDefaultAsync();
IEvent  firstA= await query.FirstAsync();

if (query.TryGet(out var ev)) { … }   // succeeds only when exactly one row matches; throws on >1
```

`TryGet` is the safe "I expect at most one" probe.

## Filtering with Where

```csharp
// Expression form — the everyday tool
db.Query<IEvent>().Where(e => e.Price <= 500m && e.Status == EventStatus.Published);

db.Query<IVenue>().Where(v => v.Capacity > 500 && v.CountryCode == "NO");

db.Query<IEvent>().Where(e => e.Title.StartsWith("Winter"));   // see "String and array methods" below

db.Query<IEvent>().Where(e => e.StartsUtc > DateTime.UtcNow
                           && e.StartsUtc < DateTime.UtcNow.AddDays(30));

// By id(s)
db.Query<IVenue>().Where(venueId);
db.Query<IVenue>().Where(new[] { id1, id2, id3 });

// Membership over a property
db.Query<IVenue>().WhereIn(v => v.CountryCode, new[] { "NO", "SE", "DK" });

// Restrict to specific node types (useful when starting from Query())
db.Query().WhereTypes(new[] { typeof(IVenue), typeof(IEvent) }, includeDescendants: true);

// Lambda as a string — for REST endpoints and tooling
db.Query<IEvent>().Where("e => e.Price < 100");
```

Chained `Where` calls are **ANDed** together:

```csharp
db.Query<IEvent>()
    .Where(e => e.Status == EventStatus.Published)
    .Where(e => e.Price == 0)
    .Execute();
```

Every property you filter, sort or facet on should be declared `Indexed = true`. An unindexed filter still returns the right answer, but it scans.

### String and array methods

The familiar C# methods work inside the lambda, and mean what they mean in C#:

```csharp
db.Query<IEvent>().Where(e => e.Title.StartsWith("Winter"));       // ordinal prefix
db.Query<IEvent>().Where(e => e.Title.Contains("jazz"));           // ordinal substring
db.Query<IVenue>().Where(v => v.Tags.Contains("outdoor"));         // the array holds this element
db.Query<IEvent>().Where(e => !e.Title.StartsWith("Cancelled")     // they compose with ! && ||
                           || e.Price == 0);
```

| Method | On | Means, and how it is answered |
|---|---|---|
| `StartsWith(prefix)` | `string` | Ordinal prefix. The prefixed values are one contiguous range in the value index, so this is a single range scan — the cheapest of the three. |
| `Contains(value)` | `string` | Ordinal substring. Nothing can *seek* a substring, so the index's distinct values are scanned and the ids of the matching ones unioned: work proportional to the number of distinct values, not to the number of nodes, and no node is read. |
| `Contains(element)` | `string[]`, `Guid[]`, `int[]`/enum arrays, `float[]`, `byte[]` | The array holds that element. Index-accelerated for string, enum and guid arrays, which keep a set of ids per distinct element. `float[]` and `byte[]` have no per element index and are evaluated row by row. |

Things to know:

- **Matching is ordinal**, exactly like `==` on a string, so case matters. An explicit `StringComparison` argument is *rejected* rather than quietly ignored — only `StringComparison.Ordinal` is accepted, since that is what the engine does.
- The property type decides what `Contains` means, and it is only known once the query runs. Calling it on something that is neither a string nor an array throws a `NotSupportedException` naming the property and its type rather than silently matching nothing.
- Duplicates inside one array do not duplicate the node in the result, matching how a value facet counts.
- All of these fall back to row evaluation on an unindexed property and give the same answer either way. `MatchesSearch` (below) is the exception: it has no fallback.
- A single `Where` is pushed to the indexes **all or nothing** — if any part of the predicate cannot be answered natively, the whole predicate is evaluated row by row. Chained `Where` calls are each pushed down on their own, so splitting is the fix:

  ```csharp
  // one predicate: the unindexed substring test drags the indexed comparison down with it
  db.Query<IEvent>().Where(e => e.Status == EventStatus.Published && e.Notes.Contains("vip"));

  // two predicates: Status filters from the index, the substring test then runs on that smaller set
  db.Query<IEvent>().Where(e => e.Status == EventStatus.Published)
                    .Where(e => e.Notes.Contains("vip"));
  ```

## Text and semantic search

Relatude.DB has BM25 keyword search and vector/semantic search built in, and blends them with a single `semanticRatio` knob: `0.0` is pure keyword, `1.0` is pure vector, anything between is a hybrid.

Three entry points, for three different jobs.

### `WhereSearch` — search as a filter

Returns an `IQueryOfNodes` you can keep composing:

```csharp
var results = db.Query<IEvent>()
    .WhereSearch("outdoor jazz festival", semanticRatio: 0.5)
    .Where(e => e.Status == EventStatus.Published)
    .Where(e => e.Price < 800m)
    .OrderBy(e => e.StartsUtc)
    .Page(0, 20)
    .Execute();
```

```csharp
IQueryOfNodes<T, T> WhereSearch(
    string text,
    double? semanticRatio = null,          // null = engine default
    float? minimumVectorSimilarity = null,
    bool? orSearch = null,                 // true = OR over terms, false = AND
    int? maxWordsEvaluated = null);
```

### `Search` — search as ranking

Returns a `QueryOfSearch` with ranked hits and scores, which is what you want on a search results page:

```csharp
var ranked = db.Query<IEvent>()
    .Where(e => e.Status == EventStatus.Published)   // pre-filter first
    .Search("live electronic music", semanticRatio: 0.7)
    .Execute();
```

```csharp
QueryOfSearch<T, T> Search(
    string text,
    double? semanticRatio = null,
    float? minimumVectorSimilarity = null,
    bool? orSearch = null,
    int? maxWordsEvaluated = null,
    int? maxHitsEvaluated = null);
```

### `MatchesSearch` — search as a predicate, scoped to one property

`WhereSearch` and `Search` both go against the node's **combined** text index, so they answer "this term is somewhere in this node". When you need "this term is in *this* property", use `MatchesSearch` inside a `Where` lambda. It searches that one property's own word index, blended with its semantic index when it has one.

```csharp
// the words must be in Description, not merely somewhere in the event
db.Query<IEvent>().Where(e => e.Description.MatchesSearch("outdoor jazz")).Execute();

// being a predicate it composes, which a chained WhereSearch cannot do
db.Query<IEvent>().Where(e => e.Title.MatchesSearch("jazz") || e.Description.MatchesSearch("jazz"));
db.Query<IEvent>().Where(e => !e.Title.MatchesSearch("cancelled") && e.Price == 0);
```

```csharp
bool MatchesSearch(this string? text, string search);
bool MatchesSearch(this string? text, string search,
    double? semanticRatio,                 // null = engine default, same order as WhereSearch
    float? minimumVectorSimilarity,
    bool? orSearch,                        // true = OR over terms, false = AND
    int? maxWordsEvaluated);
```

Four things this one does differently:

- **It requires an index and says so if there is none.** The property must be declared `IndexedByWords = true` (or `IndexedBySemantic = true`). A search cannot be reproduced row by row — that would take the tokenizer, the term expansion and the ranking — so there is no fallback, and a property without one of those indexes throws a `NotSupportedException` telling you which attribute to add. Note this is independent of `Indexed = true`, which builds the *value* index.
- **It matches words, not substrings.** `Contains("proof")` finds "waterproof"; `MatchesSearch("proof")` does not. The wildcards of the search syntax do apply, so `MatchesSearch("water*")` does.
- **It is a filter, never a ranking.** Use `Search` when you need scores and ranked paging.
- **The two indexes fill at different times.** A property's own word index is written inside the transaction, so `MatchesSearch` sees a node the moment `Insert` returns. The combined text index behind `WhereSearch` is filled by a background queue unless the node type sets `InstantTextIndexing = true`, so a `WhereSearch` immediately after an insert can legitimately miss it.

Because these are extension methods on `string`, add `using Relatude.DB.Query;`. There are two overloads rather than optional parameters because C# forbids omitted optional arguments inside an expression tree, which is what a query predicate compiles to — so tuning means passing all five arguments, nulls included.

### What actually gets searched

A property participates in search only if it opted in:

- `IndexedByWords = true` → BM25 keyword index
- `IndexedBySemantic = true` → vector index
- `TextIndexBoost` on the property, or `TextIndexBoost` on `[Node]`, weights it
- `ExcludeFromTextIndex = true` keeps a property out
- `[RelationProperty(TextIndexRelatedDisplayName = true)]` pulls related nodes' display names into this node's text index — so a venue becomes findable by the events held there

## Geo queries

Spatial filtering is a normal `Where` clause. The query compiler recognises `GeoCoordinate.IsWithin(center, meters)` and accelerates it with the coordinate index.

```csharp
var oslo = new GeoCoordinate(59.9139, 10.7522);

// Every accessible venue within 5 km of Oslo central
var nearby = db.Query<IVenue>()
    .Where(v => v.Location.IsWithin(oslo, 5_000))
    .Where(v => v.IsAccessible)
    .Execute();
```

Compose it with anything else:

```csharp
// Published events, under 500 kr, in the next fortnight
var soon = db.Query<IEvent>()
    .Where(e => e.Status == EventStatus.Published)
    .Where(e => e.Price < 500m)
    .Where(e => e.StartsUtc < DateTime.UtcNow.AddDays(14))
    .Execute();

// …and the ids of venues within 25 km, without materialising the venues
var venueIds = db.Query<IVenue>()
    .Where(v => v.Location.IsWithin(oslo, 25_000))
    .SelectId()
    .Execute();
```

### Sorting by distance

**Do not `OrderBy(v => v.Location)`.** The stored ordering follows a Z-order curve — spatially coherent for index scans, meaningless to a human. Filter by radius in the engine, then order the materialised page in memory:

```csharp
var byDistance = db.Query<IVenue>()
    .Where(v => v.Location.IsWithin(oslo, 10_000))
    .Execute()
    .OrderBy(v => v.Location.DistanceTo(oslo))    // in-process, on a small page
    .ToList();
```

This is cheap precisely because the radius filter already cut the set down. Do not run `DistanceTo` over the whole table.

### A widening search

```csharp
IEnumerable<IVenue> FindNearest(NodeStore db, GeoCoordinate center, int wanted = 10) {
    foreach (var radius in new[] { 1_000d, 5_000, 25_000, 100_000, 500_000 }) {
        var hits = db.Query<IVenue>()
            .Where(v => v.Location.IsWithin(center, radius))
            .Take(wanted * 4)
            .Execute()
            .OrderBy(v => v.Location.DistanceTo(center))
            .Take(wanted)
            .ToList();
        if (hits.Count >= wanted) return hits;
    }
    return [];
}
```

### Things to remember about geo filters

- Venues with `GeoCoordinate.Empty` **never** match `IsWithin`. That is by design — no location means no location, not "at 0°N 0°E".
- The index cover over-scans slightly (square cells, round circles); the engine refines with the exact haversine distance, so results are exact.
- A radius that touches a pole widens the cover to every longitude. Correct, but not cheap.
- Distances are great-circle metres over a mean Earth radius of 6 371 km.

## Relation filters

Filter nodes by what they are related to, **without loading either side**:

```csharp
// Events at a specific venue
db.Query<IEvent>().WhereRelates(e => e.Venue, venueId).Execute();

// Events NOT hosted by a given organizer
db.Query<IEvent>().WhereNotRelates(e => e.Host, organizerId).Execute();

// Events at any of these venues
db.Query<IEvent>().WhereRelatesAny(e => e.Venue, new[] { id1, id2, id3 }).Execute();

// When the relation lives on a derived type, name the subclass explicitly:
// .WhereRelates<TSubClass, TProperty>(expr, nodeId)
db.Query<IEvent>().WhereRelates<IConcert, Attendance.Attendees>(c => c.Attendees, attendeeId);
//                              ^^^^^^^^ a hypothetical IConcert : IEvent
```

Combine freely with scalar filters:

```csharp
var affordableNearby = db.Query<IEvent>()
    .WhereRelates(e => e.Venue, venueId)
    .Where(e => e.Price <= 300m)
    .Where(e => e.Status == EventStatus.Published)
    .OrderBy(e => e.StartsUtc)
    .Execute();
```

A relation-rooted enumeration yields the **stored relation order** — `foreach` over a `Many` side, `Get()`, and preloaded includes all respect it. A `Query()` off that side starts from the same order, so omit `OrderBy` when the curated order is what you want, and add one when it is not:

```csharp
var venue = db.Get<IVenue>(venueId);

var sellingOut = venue.Events
    .Query()
    .Where(e => e.Status == EventStatus.SoldOut)
    .OrderByDescending(e => e.StartsUtc)
    .Take(5)
    .Execute();
```

## Eager loading: Include and Preload

Both fetch related data in the same round trip. The difference is what kind of property they target.

| Method | Targets |
|---|---|
| `Include` | relation properties (`One`/`Many` sides) and collection-shaped properties |
| `Preload` | `IRelationProperty<T>`, `IReference<T>`, `IReferences<T>` |

Remember: `Reference<T>` and `References<T>` yield nothing from `foreach` unless preloaded. That is the whole reason `Preload` exists.

```csharp
// Relations
var events = db.Query<IEvent>()
    .Where(e => e.Status == EventStatus.Published)
    .Include(e => e.Venue)
    .Include(e => e.Attendees, top: 50)      // cap how many related nodes to load
    .Execute();

foreach (var e in events) {
    var venue = e.Venue.Get();               // already loaded — no extra round trip
    foreach (var a in e.Attendees) { … }     // already loaded
}

// References
var withCovers = db.Query<IEvent>()
    .Preload(e => e.Cover)
    .Preload(e => e.Sponsors)
    .Execute();

foreach (var e in withCovers) {
    foreach (var img in e.Cover) { … }       // now yields, because it was preloaded
    foreach (var s in e.Sponsors) { … }
}
```

### Going deeper: `ThenInclude` / `ThenPreload`

`ThenInclude` operates on the previously-included element type, so you can walk down a chain:

```csharp
var deep = db.Query<IVenue>()
    .Include(v => v.Events)
    .ThenInclude(e => e.Attendees, top: 20)
    .ThenPreload(a => a.Friends)
    .Execute();
```

### Filtering what gets included

Every `Include` / `Preload` / `ThenInclude` / `ThenPreload` overload has a variant that takes a filter on the related nodes. The filter **never affects the main result set** — it only narrows what is loaded — and it is applied before `top`:

```csharp
var venues = db.Query<IVenue>()
    .Where(v => v.CountryCode == "NO")
    .Include(v => v.Events,
             e => e.StartsUtc > DateTime.UtcNow,   // only upcoming events loaded
             top: 10)
    .Execute();
```

Every venue in NO still comes back — including those with no upcoming events. Only the attached event lists are filtered.

### When you only need ids

Materialising whole nodes to read their ids is wasteful:

```csharp
IQueryCollection<ResultSet<Guid>> ids = db.Query<IEvent>()
    .Where(e => e.Price == 0)
    .SelectId();

var idList = ids.Execute().ToArray();
```

## Graph traversal and shortest path

This is where the graph model earns its keep. Both operations work **over relations** — not references, not embedded data.

### Traverse

`Traverse` expands the current result set over a relation with a breadth-first walk and returns the nodes it reaches, typed as the related node type. The current result set is the seed at level 0; the result contains every node whose **minimum** distance from any seed falls within `[minLevel, maxLevel]`. It is cycle-safe.

```csharp
IQueryOfNodes<TProperty, TProperty> Traverse<TProperty>(
    Expression<Func<TNode, TProperty>> relationProperty,
    int maxLevel,
    int minLevel = 1,
    GraphDirection direction = GraphDirection.Default,
    int? maxVisited = null);
```

```csharp
// Friends-of-friends of Alice, excluding her direct friends
var fof = db.Query<IAttendee>()
    .Where(aliceId)
    .Traverse(a => a.Friends, maxLevel: 2, minLevel: 2)
    .Execute();

// Everything under a venue complex, to any depth, sorted
var allHalls = db.Query<IVenue>()
    .Where(complexId)
    .Traverse(v => v.Halls, maxLevel: 10)
    .Where(v => v.Capacity > 100)          // the result is a normal node query
    .OrderBy(v => v.Name)
    .Execute();
```

**The crucial detail:** the result of `Traverse` is a regular node query, so `Where`, `OrderBy`, `Count`, `Page`, `Include` and `Facets` all chain after it.

Use `maxVisited` as a safety valve on wide graphs.

### ShortestPath

Finds one shortest unweighted path between two nodes over a relation, breadth-first:

```csharp
var path = db.Query<IAttendee>()
    .ShortestPath(a => a.Friends, fromNodeId: aliceId, toNodeId: zaraId, maxLevel: 6)
    .Execute();
```

The result carries the node ids and the materialised nodes in order, `from` → `to` inclusive.

## Sorting, paging and result sets

```csharp
.OrderBy(Expression<Func<TNode, object>> expression, bool descending = false)
.OrderByDescending(Expression<Func<TNode, object>> expression)
```

Chain them for a compound sort — the first call is the primary key, later calls are tie-breakers:

```csharp
db.Query<IEvent>()
    .OrderBy(e => e.StartsUtc)
    .OrderBy(e => e.Price, descending: true)
    .OrderBy(e => e.Title)
    .Execute();
```

### Paging

```csharp
.Page(int pageIndex0based, int pageSize)
.Take(int maxCount)
.Skip(int offset)
```

`Page(p, n)` is equivalent to `.Skip(p * n).Take(n)`, but the engine recognises it as a paged query and **returns the total count without a second query**. Use `Page` for pagination.

```csharp
var page = db.Query<IEvent>()
    .Where(e => e.Status == EventStatus.Published)
    .OrderBy(e => e.StartsUtc)
    .Page(2, 25)
    .Execute();

Console.WriteLine($"Showing {page.Count} of {page.TotalCount} " +
                  $"(page {page.PageIndexUsed}, size {page.PageSizeUsed}) in {page.DurationMs:0.0}ms");
```

### `ResultSet<T>`

`ResultSet<T>` is `IEnumerable<T>` plus:

| Member | Meaning |
|---|---|
| `Count` | rows returned on this page |
| `TotalCount` | total matching rows across the whole query |
| `PageIndexUsed`, `PageSizeUsed` | echo of the paging that was applied |
| `DurationMs` | server-side execution time |

## Aggregates

```csharp
int count = db.Query<IEvent>().Where(e => e.Status == EventStatus.Published).Count();
int c = await db.Query<IEvent>().CountAsync();

decimal revenue = db.Query<IEvent>()
    .WhereRelates(e => e.Venue, venueId)
    .Sum(e => e.Price);
```

`Count()` on the builder is answered from the index and never materialises nodes — much cheaper than `Execute().Count()`.

## Faceted search

Facets bucket a result set across indexed properties, and are what you build a filter sidebar from. Call `.Facets()` to switch the builder into facet mode, then declare which facets you want.

```csharp
var result = db.Query<IEvent>()
    .Where(e => e.Status == EventStatus.Published)
    .WhereSearch("jazz")
    .Facets()
    .AddValueFacet(e => e.Status)                 // discrete value buckets
    .AddRangeFacet(e => e.Price)                  // auto-bucketed numeric ranges
    .AddRangeFacet(e => e.Price, 0m, 250m)        // …plus an explicit range
    .AddFacet(e => e.Tags)                        // engine picks value vs range
    .SetFacetOptions(e => e.Tags,
                     maxValues: 20,
                     minCount: 1,
                     includeMissing: false,
                     sortByCount: true)
    .Page(0, 20)
    .Execute();

foreach (var facet in result.Facets) {
    Console.WriteLine(facet.DisplayName);
    foreach (var v in facet.Values) {
        Console.WriteLine($"  {v} ({v.Count}){(v.Selected ? " ←" : "")}");
    }
}
```

### Applying the user's selection

The same builder both produces buckets and applies the user's clicks:

```csharp
var filtered = db.Query<IEvent>()
    .Facets()
    .AddValueFacet(e => e.Status)
    .AddRangeFacet(e => e.Price)
    .SetFacetValue(e => e.Status, EventStatus.Published)         // user clicked "Published"
    .SetFacetRangeValue(e => e.Price, 0m, 250m, "Under 250")     // user clicked a range
    .SetFacetMissingValue(e => e.Tags)                           // user clicked "no tags"
    .Execute();
```

The returned `ResultSetFacets<T>` is a normal `ResultSet<T>` plus `Facets` and `SourceCount` — so you get the page of results and the updated bucket counts in one round trip.

### Facet declaration methods

| Method | Purpose |
|---|---|
| `AddFacet(expr / name / propertyId)` | Add a facet, engine chooses value vs range |
| `AddValueFacet(…)` | Force discrete value buckets |
| `AddRangeFacet(…)` | Auto-bucketed numeric/date ranges |
| `AddRangeFacet(…, from, to)` | Add one explicit range bucket |
| `AddSingleRangeFacet(…)` | One bucket spanning min..max |
| `SetFacetValue(…, value)` | Select a value bucket |
| `SetFacetRangeValue(…, from, to)` | Select a range bucket |
| `SetFacetMissingValue(…)` | Select the "no value" bucket |
| `SetFacetOptions(…)` | `maxValues`, `minCount`, `includeMissing`, `sortByCount`, `rangeCount` |

Every method has expression, property-name and `Guid` overloads, plus `<TChild>` variants for subtypes.

**Faceting requires `Indexed = true`.** `NotFacet = true` excludes an indexed property from faceting. Relation properties need `[RelationProperty(Facet = true)]` to opt in. Numeric range bucketing is tuned by `FacetRangePowerBase` and `FacetRangeCount` on the property attribute.

## Pivot tables

A pivot summarises the matching nodes as a table, the way a spreadsheet pivot table does: the rows
and the columns are groups of nodes by property value, and every cell holds measures computed over
the nodes in it — count, sum, average, min, max, distinct count. Call `.Pivot()` to switch the
builder into pivot mode, declare the groups and the measures, and execute. The nodes themselves are
not returned; a pivot is aggregate-only.

```csharp
var pivot = db.Query<IEvent>()
              .Where(e => e.Status == EventStatus.Published)
              .Pivot()
              .AddRow(e => e.Venue)                              // one row per related venue
              .AddColumn(e => e.StartsUtc, DateInterval.Month)   // one column per calendar month
              .AddCount("events")
              .AddSum(e => e.Price, "revenue")
              .AddAverage(e => e.Price)                          // named "Price.Average" unless you name it
              .SetRowOptions(e => e.Venue, maxGroups: 20, sortByMeasure: "revenue", otherGroup: true)
              .Execute();

foreach (var row in pivot.EnumerateRows()) {
    Console.Write(row.Group.DisplayName);                        // the venue's display name
    foreach (var cell in row.Cells) Console.Write("\t" + (cell?.Get("revenue") ?? 0));
    Console.WriteLine("\t" + row.Total!.Get("revenue"));         // the row total
}
```

With no `AddColumn` the pivot is a plain group-by: one column, `(all)`, so every row has one cell
equal to its total. With no `AddRow` and no `AddColumn` it is one cell holding the measures over
everything.

### Groups

Every `AddRow` / `AddColumn` call adds a nesting level to its axis, in call order — two row levels
give one row per distinct combination, grouped under the first level. The bucketing follows the
facet rules, and the same properties are groupable: indexed scalars, string/guid/enum arrays,
references, and relations that opted in with `[RelationProperty(Facet = true)]`.

| Method | Groups by |
|---|---|
| `AddRow(expr)` / `AddColumn(expr)` | The property, engine picks value buckets or ranges (the `AddFacet` rule) |
| `AddRow(expr, DateInterval.Month)` | A date property by calendar interval: `Year`, `Quarter`, `Month`, `Week` (ISO, Monday first), `Day`, `Hour` |
| `AddRowValues(expr)` | One group per distinct value |
| `AddRowRanges(expr, bucketCount)` | Auto-generated numeric/date ranges |
| `AddRowRange(expr, from, to, "label")` | One explicit range; consecutive ranges on the same property form one level |
| `SetRowOptions(expr, …)` / `SetColumnOptions(expr, …)` | `maxGroups`, `minCount`, `includeMissing`, `sortByMeasure`, `descending`, `otherGroup` |

`sortByMeasure` orders the groups of that level by a measure name (`"Count"` always works, even
without a count measure); without it groups come in their natural order — values sorted, ranges in
range order, enum and relation groups by name. `maxGroups` keeps the first N after sorting and
`minCount` drops groups with fewer nodes; with `otherGroup: true` what was trimmed is collected
into one `(other)` group, aggregated over the union of the trimmed nodes. `includeMissing` adds a
`(none)` group for nodes without a value. Every method has expression, property-name, `Guid` and
`<TChild>` overloads, like the facet API.

### Measures

`AddCount(name?)`, `AddCountDistinct(expr, name?)`, `AddSum`, `AddAverage`, `AddMin`, `AddMax`,
and `AddMeasure(PivotFunction, expr, name?)`. Sum, average, min and max need a numeric property
(`int`, `long`, `double`, `float`, `decimal`, `byte`); distinct count works on any indexed scalar
property. Every measure value is a `double?` — `null` when it is undefined for the cell, that is a
sum, average, min or max over nodes that have no value for the property. An average divides by the
nodes that *have* a value, not by the cell count. The default name is `Count` or
`<Property>.<Function>`; name measures yourself when you look them up by name.

### The result

`PivotResult` has `Rows` and `Columns` (each `Levels` and `Groups`, plus `TotalGroupCount`),
`Measures`, and `Cells` — sparse, a row/column pair with no nodes has no cell. Read a cell with
`pivot[row, column]` (null when empty) or by `Row` / `Column` on the cell, and a value with
`cell.Get("revenue")` or `cell.Get(measureIndex)`; `cell.Count` is always there. `RowTotals`,
`ColumnTotals` and `GrandTotal` are aggregated over their own node sets, never added up from cells,
so averages are right and a node that sits in two groups of an array-valued property (two tags) is
counted once in the totals. `EnumerateRows()` gives a dense row-by-row view for rendering,
`ToTable()` a flat table with one line per cell.

A group's `Values` holds one entry per level: the bucket value (a relation group carries the related
node object, an enum group its int), `Values2` the upper bound of a range bucket, `DisplayNames`
the labels — enum names, related node names, `2026-03` for a month, `(none)`, `(other)`. Those
values are exactly what `SetFacetValue` / `SetFacetRangeValue` take, so a cell can be turned back
into the nodes behind it with a facet query.

### Totals, limits and paging

```csharp
.SetTotals(rows: true, columns: true, subTotals: true)   // sub-totals: every group above the leaf level
.SetLimits(maxCells: 10_000, throwWhenExceeded: false)   // default 250 000; past it the row axis is cut and Capped is set
.SetRowPaging(pageIndex: 0, pageSize: 50)                // rows are the long axis; Rows.TotalGroupCount has the full count
```

Sub-totals come back as `RowSubTotals` / `ColumnSubTotals`: the group, its cells against the other
axis and its total. The grand total is always computed, over the whole source.

### After a facet selection

A pivot can be opened on a facet query, where it summarises the nodes the selection leaves — the
sidebar filter, pivoted:

```csharp
var byMonth = db.Query<IEvent>()
                .Facets()
                .SetFacetValue(e => e.Status, EventStatus.Published)
                .SetFacetRangeValue(e => e.Price, 0m, 250m)
                .Pivot()
                .AddRow(e => e.StartsUtc, DateInterval.Month)
                .AddCount()
                .Execute();
```

Only the selection filters apply: the facet buckets are not counted and the facet page is ignored.

The admin UI has this as the third view of the Query section (list, table, pivot): the same search and
facet selection, a builder for the groups and measures, and a click on any cell that turns its groups
back into a facet selection showing the nodes behind the number.

Like every query, a pivot travels to the store as a query string, which is what a REST client sends:
`IEvent.Pivot().AddRow("IEvent.Venue").AddColumn("IEvent.StartsUtc", "Month").AddSum("IEvent.Price", "revenue")`.
`QueryOfPivot.ToString()` gives the string for a typed pivot.

## GroupBy

`GroupBy` is SQL's `GROUP BY`, written the way LINQ and EF Core write it: group the matching nodes by
a key built from their properties, then count and aggregate per group. It is the flat, typed cousin
of the pivot — one axis, one row per group, the result shaped by your own `Select` — and it runs on
the same engine, so it costs the same and obeys the same rules. Reach for `GroupBy` in application
code and for reports with a known shape; reach for `Pivot` when you want two axes, sub-totals, or a
table someone else defines at runtime.

```csharp
// SELECT Status, COUNT(*) FROM Event GROUP BY Status
var byStatus = db.Query<IEvent>().GroupBy(e => e.Status).Execute();
foreach (var g in byStatus) Console.WriteLine($"{g.Label}: {g.Count}");      // g.Key is the EventStatus

// the EF Core shape: a composite key, aggregates, HAVING, ORDER BY, LIMIT
var report = db.Query<IEvent>()
    .Where(e => e.StartsUtc >= since)
    .GroupBy(e => new { e.Venue, e.StartsUtc.Year, e.StartsUtc.Month })
    .Select(g => new {
        Venue    = g.Key.Venue!.Name,                 // a relation key is the related node
        g.Key.Year,
        g.Key.Month,
        Events   = g.Count(),
        Revenue  = g.Sum(e => e.Price),
        AvgPrice = g.Average(e => e.Price),
        Dearest  = g.Max(e => e.Price),
        Cities   = g.CountDistinct(e => e.City),
        PerEvent = g.Sum(e => e.Price) / g.Count(),   // arithmetic over aggregates runs on the rows
    })
    .Where(r => r.Events >= 3)                        // HAVING (Having(...) is the same call)
    .OrderByDescending(r => r.Revenue)
    .Take(20)
    .Execute();
```

No node is read: every group is an id set from the value indexes and every aggregate a pass over
the index, exactly as for a pivot. That is also why **grouping and aggregating need indexed
properties** (`Indexed = true`; relations opt in with `[RelationProperty(Facet = true)]`) — the error
says so at build time. For anything else, load the nodes and group them in memory with LINQ.

#### Keys

The key selector is translated, not run, so only these forms are accepted — each one is a stored
value, which is what keeps every group one value (an expression like `e.Price / 100` is refused;
compute it from the key on the result instead):

| Key | Groups by |
|---|---|
| `e => e.Status` | One group per distinct value. Enums come back as the enum, relations as the related node |
| `e => new { e.Venue, e.Status }` | The distinct combinations; `g.Key.Venue`, `g.Key.Status` |
| `e => e.StartsUtc.Year` / `.Month` / `.Day` / `.Date` / `.Hour` | A calendar interval; `new { d.Year, d.Month }` gives one group per month |
| `e => Bucket.Interval(e.StartsUtc, DateInterval.Quarter)` | Quarter and week have no `DateTime` member; the key is the interval's start |
| `e => Bucket.Ranges(e.Price, 5)` | About five auto-generated ranges; the key is a `GroupRange<decimal>` with `From`, `To`, `Label` |
| `e => Bucket.Ranges(e.Price, new[] { 0m, 100m, 500m })` | Explicit consecutive ranges (inclusive at both ends; values outside fall in no group) |

An array-valued key (`e => e.Tags`) puts a node in one group per element, so the counts add up to
more than the node count; the key of such a group is the one-element array. Nodes **without a value
for a key** — no related venue, no tag — form a group of their own by default, as SQL and LINQ do
with null: its `Key` is null (or the type's default), its `Label` `(none)`, and `IsMissing` is set
on the group. `IncludeMissing(false)` drops it.

#### Aggregates

Inside `Select` the group is an `IGrouping<TKey, T>`, so the calls are the LINQ ones: `g.Count()`,
`g.LongCount()`, `g.Sum(e => e.Price)`, `g.Average(...)`, `g.Min(...)`, `g.Max(...)`, plus
`g.CountDistinct(e => e.City)` (or the LINQ spelling `g.Select(e => e.City).Distinct().Count()`).
Each selector must be a property of the node. Sum, average, min and max need a numeric property;
distinct count works on any indexed scalar. The results keep the call's type — a `decimal` sum, a
`double` average, an `int` count — converted from the engine's `double`, which is exact for integer
sums below 2^53 and for decimals to about 15 significant digits. An aggregate over a group in which
no node has the value gives `0` for a non-nullable result (LINQ's sum over nothing) and `null` for a
nullable one. Anything else on `g` — `g.First()`, `g.Where(...)`, `g.Count(e => ...)` — is rejected
when the query is built: the nodes of a group are never enumerated. Filter with `Where` *before*
`GroupBy` instead.

#### Ordering, filtering and paging the groups

`Where` (or `Having`), `OrderBy`, `OrderByDescending`, `ThenBy`, `Skip`, `Take` and `Page` run over
the group rows, which are few compared to the nodes. One sort by one measure of a single-key query —
`OrderByDescending(r => r.Revenue)`, `OrderBy(g => g.Count)` — is pushed into the engine together
with the paging behind it, so `Take(20)` on a million nodes never materialises every group. Sorting
by anything else, several keys, or a composite key sorts the rows in memory. `Execute()` returns a
`ResultSetGroups<T>`: the page of rows, `TotalCount` (groups after `Where`) and `SourceCount` (the
nodes grouped). `Count()` on the query is the number of groups.

#### Without a Select

`Execute()` straight after `GroupBy` gives `NodeGroup<TKey>` rows: `Key`, `Count`, `Label` (the
engine's name — the related node's display name, the enum name, `2026-03`), `Labels` per key
property, and `IsMissing`. For code that does not know the properties when it is written — a
dynamic report, the admin UI — the keys and the aggregates are chosen at runtime instead:

```csharp
var rows = db.Query<IEvent>()
    .GroupBy(GroupKey.Values(venueId), GroupKey.Interval(startsId, DateInterval.Year))
    .Aggregate(PivotFunction.Sum, priceId)
    .Aggregate(PivotFunction.Average, priceId)
    .OrderByDescending(g => g["Price.Sum"])
    .Page(0, 50)
    .Execute();
foreach (var g in rows) Console.WriteLine($"{g.Label}: {g.Count} events, {g["Price.Sum"]:0} in total");
```

The key of a runtime query is an `object?[]` with one entry per level: the value, or a
`GroupRange<object>` (both bounds and the label) for a calendar or range level. Measures are read by
their default name, `<Property>.<Function>`, case-insensitively.

#### The query string and the admin UI

Like every query, a `GroupBy` travels to the store as a query string — a one-axis pivot in its own
spelling, which the parser accepts from a REST client too:
`IEvent.GroupBy("IEvent.Venue", "IEvent.Status").AddCount().AddSum("IEvent.Price")`. Every argument
of `GroupBy(...)` is one value level (with its missing-value group); calendar and range levels
follow as `AddRow("IEvent.StartsUtc", "Month")` / `AddRowRanges(...)`, then the measures, then
`SetRowOptions` for a sort and `SetRowPaging` for a page. Row totals only and a cell limit that
throws instead of truncating are the GroupBy defaults. `ToString()` on the typed query gives the
string.

The admin UI has it as the third view of the Query section (list, table, **groups**, pivot): the
same search and facet selection, chips for the keys and the aggregates, a click on a column header to
sort, and a click on a row that turns its groups into a facet selection showing the nodes behind it.

## Cultures, visibility and scoped stores

Two equivalent styles. Per query:

```csharp
db.Query<IEvent>()
    .WhereCulture("nb-NO")
    .WhereCultureFallback(true)
    .WhereHidden(false)
    .Execute();

db.Query<IEvent>(QueryContext.Culture("nb-NO")).Execute();
```

Or scope a whole `NodeStore` once and reuse it:

```csharp
var nb = db.Context
    .Culture("nb-NO")
    .CultureFallbacks(true)
    .Hidden(false)
    .Create();

nb.Query<IEvent>().Execute();
nb.Get<IVenue>(venueId);
```

`db.Context.Admin()` returns a store that **bypasses ACL filtering**. Use it in trusted server code only — never hand it to a request handler.
