---
name: relatude-db
description: Model, query, and configure apps with Relatude.DB — an open-source C#-native object-oriented graph database with BM25 and vector search, faceting, geo queries, file storage and an admin UI. Use whenever the user mentions Relatude, Relatude.DB, NodeStore, RelatudeDBContext, AddRelatudeDB, UseRelatudeDB; attributes [Node], [Relation], [StringProperty], [GeoCoordinateProperty], [ReferenceProperty]; types NodeMeta, FileValue, GeoCoordinate, EmbeddedMap, Reference, References; relation bases OneOne, OneToOne, OneToMany, ManyMany, ManyToMany; query methods WhereSearch, WhereRelates, Include, Preload, Facets, Traverse, ShortestPath; or media APIs like FileAdjustmentImage, ImageCropMode, GetUrl, FileHandler and FileUploadAsync. Also for C# projects referencing Relatude.DB.* namespaces or NuGets, designing node/relation models, writing or debugging Relatude queries, wiring a server in Program.cs, or middleware serving Relatude media URLs — even when Relatude is never named but these patterns appear.
---

# Relatude.DB

Relatude.DB is an open-source, C#-native **object-oriented graph database** with integrated full-text (BM25) search, vector/semantic search, faceting, geo/spatial queries, file storage and a built-in web admin UI at `/relatude.db`. It runs in-process or server-hosted and targets **.NET 8+**.

Repository: https://github.com/Relatude/Relatude.DB

**The project is pre-1.0 and the public API still moves.** Never promise API stability, and never invent an API that is not documented here or in the reference files. When something is unclear, say so and point the user at the source — the "Where to look in the source" table in `references/pitfalls.md` maps every topic to a path in the repo.

## Which reference file to read

This SKILL.md is the working knowledge for everyday modelling and querying. Read the relevant reference file before going deeper — they are the authority, this file is the summary.

| File | Read it when |
|---|---|
| `references/datamodels.md` | You need the full property-attribute catalogue, marker attributes, `NodeMeta` fields, embedded types, the five relation shapes in detail, or the model builder's validation rules. |
| `references/modelling-patterns.md` | You are designing a real model. Covers facet interfaces / multiple inheritance, the relation-vs-reference-vs-embedded decision, and a complete worked example domain in both flat and composed form. |
| `references/queries.md` | You are writing anything beyond a simple `Where` — search, geo, facets, traversal, shortest path, includes/preloads, paging, cultures. |
| `references/api-quickref.md` | You need the write surface: create/insert/update/delete/upsert variants, relating, transactions, locks, file upload and conversion. |
| `references/files-and-media.md` | Anything to do with files: uploading (including chunked uploads of large files), generating media URLs with `FileAdjustment`, image/video conversion, and the middleware that serves it all. |
| `references/setup.md` | You are adding Relatude to a project, wiring `Program.cs`, registering a datamodel source, or configuring storage in the admin UI. |
| `references/configuration.md` | You need the detail: every field of `relatude.db.json`, every `ServerOptions` option and lifecycle event in fire order, and the two ways of registering a datamodel. |
| `references/pitfalls.md` | Before finishing any non-trivial answer. It is the checklist of things that actually bite people, plus the source map. |

## The mental model

Everything is a **node**. A node has:

| Ingredient | What it is |
|---|---|
| `Guid Id` | The public identity. The engine also keeps an internal `int __Id` for fast indexing. |
| `NodeMeta Meta` | System-managed metadata: timestamps, culture, revision, ACL, display name, address. Read it; never write it. |
| Scalar properties | `string`, `int`, `decimal`, `DateTime`, `bool`, `Guid`, `GeoCoordinate`, `FileValue`, arrays… |
| Embedded data | Owned sub-objects stored inline in the parent — `Embedded<T>`, `EmbeddedMap<TKey,TValue>`. |
| References | A stored `Guid` (or `Guid[]`) pointing at other nodes — one-directional, no reverse index. |
| Relations | Real graph edges declared as their own classes — bidirectional, indexed, traversable. |

There is no separate schema language. **Your C# types are the schema.** Point the engine at a namespace and it builds the datamodel from your interfaces and classes.

## Interfaces are the model

A node type can be an interface, class, record or struct — but **default to interfaces alone**. An interface on its own is a complete node type: `db.Create<IVenue>()` hands back a generated proxy that implements it, tracks changes and lazily loads relations. No concrete class is written, and none is needed.

The reason goes beyond saving a file. The engine records **every interface a node type implements as a parent node type**, and C# lets a type implement any number of interfaces. So interface modelling gives you real multiple inheritance in the datamodel — shared, queryable facets that cut across the hierarchy. Classes, limited to one base, cannot express this. See `references/modelling-patterns.md`; this is the single highest-leverage thing to get right in a Relatude model.

```csharp
using Relatude.DB.Common;      // FileValue, GeoCoordinate, IdKey
using Relatude.DB.Datamodels;  // NodeMeta, RevisionType
using Relatude.DB.Nodes;       // attributes, relation bases, Reference, References, EmbeddedMap

namespace VenueApp.Models;

public interface IOrganizer {
    Guid Id { get; set; }

    [DisplayNameProperty]
    [StringProperty(Indexed = true, MaxLength = 200, IndexedByWords = true)]
    string Name { get; set; }

    [StringProperty(StringType = StringValueType.Email, UniqueValues = true)]
    string ContactEmail { get; set; }

    [AddressProperty]
    [StringProperty(Indexed = true, UniqueValues = true, RegularExpression = @"^[a-z0-9-]+$")]
    string Slug { get; set; }

    [CreatedUtcProperty]
    DateTime CreatedUtc { get; set; }

    NodeMeta Meta { get; }          // read-only on the interface
}
```

That is a complete node type:

```csharp
var org = db.Create<IOrganizer>();          // generated proxy implementing IOrganizer
org.Name = "Nordic Live AS";
org.ContactEmail = "hello@nordiclive.no";
db.Insert(org);

var again = db.Get<IOrganizer>(org.Id);
var all = db.Query<IOrganizer>().Where(o => o.Name.StartsWith("Nordic")).Execute();
```

### Three rules for interface node types

1. **`Meta` is getter-only** — and so are relation, reference and embedded properties. The proxy owns their initialisation; you never assign them. Scalar properties are `{ get; set; }`.
2. **Leave `Id` as `Guid.Empty` on insert** and the store assigns one, or set it yourself first.
3. **Put attributes on the interface.** A property is defined once, on the type that first declares it, so an attribute on a class that merely implements an interface member is ignored. The interface is the single source of truth.

Add `[Exclude]` to any type or property the datamodel should skip.

### When a class is warranted

Classes are fully supported, and there are real reasons to reach for one: you want to `new` nodes up without a store (seed data, tests, import jobs), you want behaviour on the type (computed members, helpers, `ToString()`), or you are deserialising straight into the model type from an external feed. Pairing an interface with a class gives you the interface as the queryable contract and the class as a concrete instantiable form.

Writing a class adds four obligations:

```csharp
public class Organizer : IOrganizer {
    public Guid Id { get; set; }

    // Attributes live on IOrganizer — repeating them here has no effect.
    public string Name { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }

    public NodeMeta Meta { get; set; } = NodeMeta.Empty;   // read-write on the class
}
```

1. A **parameterless constructor is mandatory** — the model builder throws without one.
2. **Initialise every reference-typed member**: `string.Empty`, `FileValue.Empty`, `NodeMeta.Empty`, `[]` for embedded maps, `new()` for relation and reference properties. Interfaces need none of this.
3. `Meta` becomes get/set — still never build one, use `NodeMeta.Empty`.
4. Attributes belong on the interface when the class implements one.

Records work with the `record Foo() { … }` form (to satisfy the parameterless-constructor rule); structs are supported but rarely worth it.

### Pin stable ids early

Without an explicit id the engine hashes the type's full name, so renaming a type or moving its namespace creates a **brand-new type with no data**. Pin `[Node(Id = …)]` and `[Relation(Id = …)]` at the start of a project — it costs nothing then and is painful later.

```csharp
[Node(
    Id = "6a1d9f2e-0b41-4c8a-9d7b-3f2c5e8a1b40",
    TextIndex = BoolValue.True,       // include in the BM25 index
    SemanticIndex = BoolValue.False,  // skip the vector index
    TextIndexBoost = 1.5
)]
public interface IOrganizer { /* ... */ }
```

`BoolValue` is tri-state: `Default` (engine decides), `True`, `False`. `[Node]` also takes `MinNoInstances` / `MaxNoInstances`.

## Properties: `Indexed` is the flag that matters

A plain property with no attribute works — the engine infers a property model from the CLR type. Add an attribute when you want indexing, validation, faceting or search behaviour.

**A property must be `Indexed = true` to be filtered, sorted or faceted efficiently.** Without it the engine falls back to scanning. This is the most common cause of "why is my query slow", so set it deliberately on every property a query touches.

The string attribute is the richest, and its flags decide what search can see:

```csharp
[StringProperty(
    Indexed = true,             // value index: equality, range, sort, StartsWith, Contains
    IndexedByWords = true,      // BM25 keyword index; also what MatchesSearch needs
    IndexedBySemantic = true,   // vector / semantic index
    PrefixSearch = true,        // allow a "term*" wildcard in a search
    InfixSearch = false,        // allow a "*term" wildcard in a search — expensive, opt in deliberately
    MaxLength = 4000,
    UniqueValues = true,
    RegularExpression = @"^[a-z0-9-]+$"
)]
string Slug { get; set; }
```

Use `[HtmlProperty]` for rich text — it is a `[StringProperty]` pre-set to `StringType = HTML`, so markup is stripped before word indexing.

Two details that trip people up, both in `references/datamodels.md`:

- **`decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan` and `Guid` bounds are passed as strings**, because those are not legal C# attribute parameter types. `MinValue = "0"` (invariant culture), `MinValue = "2000-01-01T00:00:00.0000000Z"` (round-trip "O"), `MaxValue = "1.00:00:00"` (constant "c").
- **Enums use `[IntegerProperty]`** (and `[EnumArrayProperty]` for arrays). The engine stores an int and auto-populates the enum metadata so the admin UI and facets show names, not numbers.

`LegalValues`, `RegularExpression`, min/max, length bounds, `UniqueValues` and `MinNoInstances`/`MaxNoInstances` are all **enforced at write time** — a violating transaction fails rather than silently storing bad data.

Six marker attributes tag structural roles, at most one property per type per role: `[DisplayNameProperty]`, `[AddressProperty]`, `[PublicIdProperty]`, `[InternalIdProperty]`, `[CreatedUtcProperty]`, `[ChangedUtcProperty]`.

## Relations, references, embedded — the decision you make most often

|  | Embedded | Reference | Relation |
|---|---|---|---|
| Identity | none — owned by parent | target is an independent node | both sides independent |
| Direction | n/a | one-way | bidirectional, automatic |
| Reverse lookup | n/a | no | **yes, indexed** |
| Traversable (`Traverse`, `ShortestPath`) | no | no | **yes** |
| Filter by target | no | yes, with `Indexed = true` | yes, `WhereRelates` |
| Order preserved | yes | `References<T>`: yes | **yes, per side** — reorder with the `MoveRelation…` family |
| Duplicates | yes | `References<T>`: yes | no — relating an existing pair throws |
| Cost to change | rewrites parent node | rewrites parent node | index update, no node rewrite |
| Lifecycle | dies with parent | independent | independent |

Decision guide:

- Child has no meaning outside the parent and is never queried independently → **embedded**.
- You need the **same target more than once** and never need the reverse lookup → **`References<T>`**. Ordering alone is no longer a reason to choose this — relations are ordered too.
- You point at one thing cheaply and never ask "what points at me?" → **`Reference<T>`**.
- You need reverse navigation, traversal, relation filters, relation facets, reorderable ordering, or referential bookkeeping → **relation**. When in doubt, this is the right default.

### Relations are graph edges, not foreign keys

You never store `VenueId` on an event. Declare a relation class and expose one nested property class per side. Relating A to B automatically relates B back to A.

```csharp
public class EventsAtVenue : OneToMany<IVenue, IEvent> {
    public class Venue : One { }     // goes on IEvent  — the "one" side
    public class Events : Many { }   // goes on IVenue  — the "many" side
}

public interface IVenue {
    EventsAtVenue.Events Events { get; }     // many events at this venue
}

public interface IEvent {
    EventsAtVenue.Venue Venue { get; }       // the one venue of this event
}
```

Five shapes exist and no others: `OneOne<T>` (symmetric 1↔1, nested `One`), `OneToOne<TFrom,TTo>` (`OneFrom`, `OneTo`), `OneToMany<TOne,TMany>` (`One`, `Many` — the workhorse), `ManyMany<T>` (symmetric, `Many`), `ManyToMany<TFrom,TTo>` (`ManyFrom`, `ManyTo`). There is no zero-or-one and no asymmetric many-to-one — model the asymmetric case as `OneToMany` in the appropriate direction. **If the edge itself carries data** (a ticket price, a role), promote the edge to a node type with two relations hanging off it.

You may omit a side you do not need; declare both only when you want navigation in both directions. On concrete classes, initialise with `new()`.

The "one" side is an `OneProperty<T>`: `IsSet()`, `Count()`, `Get()` (throws when unset), `TryGet(out …)`, `Contains(id)`. The "many" side is a `ManyProperty<T>`: `Count()` (from the index, does not load nodes), `Contains(id)`, `Get()`, `foreach` (lazy), and `Query()` — which returns a full node query rooted at that relation, so everything in `references/queries.md` composes onto it.

### Relation lists are ordered

Each node keeps its related items in a **fixed order per side**, so a "many" side is a list rather than a set. `Relate` appends to the bottom, and enumerating the side yields the stored order. That order is a real modelling feature — use it for hand-curated sequences (a programme running order, a menu, a gallery) instead of adding a `SortIndex` property.

Reorder with the `MoveRelation…` family on `NodeStore` or `Transaction`:

```csharp
db.MoveRelationToTop<IVenue>(venue, v => v.Events, headliner);
db.MoveRelation<IVenue>(venue, v => v.Events, ev, offset: -1);      // negative = toward the top
db.MoveRelationAfter<IVenue>(venue, v => v.Events, ev, anchor: other);
db.SetRelationOrder<IVenue>(venue, v => v.Events, orderedEvents);   // replace the whole order
```

Every method takes a single item or a collection, and multi-item moves behave like a list UI: the selection keeps its internal order and compacts against the ends. Positions are clamped, so moving past the top or bottom never throws. Full overload list in `references/api-quickref.md`.

For a many-to-many relation **each side is ordered independently** — reordering a venue's events says nothing about the order of venues on an event.

### The reference enumeration trap

`Reference<T>` and `References<T>` both implement `IEnumerable<T>`, but **`foreach` only yields preloaded data**. Without `.Preload(...)` in the query, the loop silently yields nothing. Use `.Get()` / `.TryGet(out …)` for lazy access, and `foreach` only after a preload. This is deliberate: it makes accidental N+1 loads impossible. (A relation's `Many` side *does* load lazily in `foreach` — the trap is references only.)

## Writing data

`db` below is `ctx.Database`, a `NodeStore`.

```csharp
var venue = db.Create<IVenue>();
venue.Name = "Sentrum Scene";
venue.Slug = "sentrum-scene";
venue.Location = new GeoCoordinate(59.9200, 10.7480);
venue.Capacity = 1750;
db.Insert(venue);                     // returns TransactionResult

// …or in one call
var ev = db.CreateAndInsert<IEvent>((e, t) => {
    e.Title = "Winter Session";
    e.StartsUtc = new DateTime(2026, 11, 14, 19, 0, 0, DateTimeKind.Utc);
    e.Price = 450m;
    e.Status = EventStatus.Published;
    e.Tags = ["live", "electronic"];
});

db.Relate<IVenue>(venue, v => v.Events, ev);     // bidirectional — either side works
```

The suffix convention is consistent across the whole API: bare name / `OrFail` → throw when the precondition fails; `IfExists` / `IfNotExists` → no-op; `Force…` → skip change detection and write anyway. Every mutating call returns a `TransactionResult` and accepts `flushToDisk: bool = false` — the queued default is what you want in hot paths.

Batch related writes in a transaction, which mirrors every mutating `NodeStore` call:

```csharp
var t = db.CreateTransaction();
t.Insert(venue);
t.Insert(ev);
t.Relate<IVenue>(venue, v => v.Events, ev);
TransactionResult result = t.Execute();
```

Full write surface, locks, file upload and conversion: `references/api-quickref.md`.

## Querying

Every query is built with `IQueryOfNodes<TNode, TInclude>` and finished with `Execute()`. **Chain order does not matter** — the builder composes a plan rather than executing step by step.

```csharp
var result = db.Query<IEvent>()                              // entry point
    .Where(e => e.Status == EventStatus.Published)           // filters
    .WhereSearch("jazz quartet", semanticRatio: 0.5)         // hybrid keyword+vector search
    .Include(e => e.Venue)                                   // eager loading
    .OrderBy(e => e.StartsUtc)                               // sorting
    .Page(0, 20)                                             // paging
    .Execute();
```

`Query<T>()` matches `T` **and every type descending from it** — that is the feature that makes facet-interface queries work, and the thing to narrow with `WhereTypes` when you want one concrete type only.

Prefer builder methods over LINQ on the result set: the result set is already materialised, so `.Where()` on it runs in your process, while `.Where()` on the builder runs in the engine against the indexes.

The moves worth knowing, all detailed in `references/queries.md`:

```csharp
.WhereSearch(text, semanticRatio)   // search as a filter, keeps composing
.Search(text, semanticRatio)        // search as ranking, returns scored hits
.Where(e => e.Title.StartsWith("Winter"))       // ordinal prefix, one index range scan
.Where(e => e.Title.Contains("jazz"))           // ordinal substring; element on an array property
.Where(e => e.Description.MatchesSearch("jazz"))// search one property, composes with || and !
.Where(v => v.Location.IsWithin(oslo, 5_000))   // geo, accelerated by the spatial index
.WhereRelates(e => e.Venue, venueId)            // filter by graph edge, loads neither side
.Include(e => e.Attendees, top: 50)             // eager-load relations
.Preload(e => e.Cover)                          // eager-load Reference / References
.Traverse(a => a.Friends, maxLevel: 2)          // breadth-first walk, cycle-safe
.Facets().AddValueFacet(e => e.Status)          // filter sidebar buckets
.SelectId()                                     // ids only, no node materialisation
.Page(2, 25)                                    // paged — gives you TotalCount for free
```

Use `Count()` on the builder (answered from the index) rather than `Execute().Count()`. Use `Page(p, n)` rather than `Skip().Take()` so `TotalCount` comes back with the page.

## Configuration and startup

Three surfaces, and they compose. `references/configuration.md` has the full field list.

**`relatude.db.json`** sits beside the app (in `DefaultDataFolderPath`, resolved against the content root) and owns everything that is not code: storage backends, index engines, file stores, AI providers, admin credentials, and the datamodel sources. **It is created from a default template if missing — and that template points at the bundled demo model**, so a store full of `Relatude.DB.Demo.Models` types means the file was never configured. The admin UI edits the same file and rewrites it wholesale.

**The `RelatudeDB` configuration section** overrides the file from standard ASP.NET configuration — appsettings.json, `appsettings.{Environment}.json`, environment variables, user secrets. Same shape as `relatude.db.json`, merged key by key (array elements match on `Id`, else position), values coerced from strings, unknown keys warned about at startup. Overridden values are stripped again before saves, so **credentials belong here, not in the file** — and an admin-UI edit to an overridden key does not stick.

```jsonc
// appsettings.Development.json
{ "RelatudeDB": { "MasterUserName": "admin", "ContainerSettings": [{ "LocalSettings": { "AutoBackUp": false } }] } }
```

**`ServerOptions`** in `Program.cs` owns what only code can express — file converters and the lifecycle callbacks:

```csharp
builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new SkiaImageConverter(1));      // no converters, no image/video conversion
    options.OnDatamodelInit = (dm, container) => dm.AddNamespace<IVenue>();
    options.OnStoreInit = db => db.RegisterTransactionPlugin(new AuditPlugin());
    options.OnStoreOpenBackground = db => Seeder.SeedIfEmpty(db);   // never in OnStoreOpen — it blocks
});
```

Fire order: *file read, `RelatudeDB` section merged* → `OnServerSettingsInit` → `OnContainerSettingsInit` → `OnStoreSettingsInit` → *JSON datamodel sources load* → `OnDatamodelInit` → `OnStoreInit` → `OnStoreOpen` / `OnStoreOpenBackground` → `OnStoreClose`. **Every callback's exceptions are caught and logged, never rethrown** — a callback that silently did nothing is a startup-log question, not a crash. Values a callback sets are written back to the file on admin-UI saves; only configuration-section values are stripped.

### Two ways to register a datamodel

They are additive: JSON sources load first, then `OnDatamodelInit` adds to the same `Datamodel`.

```jsonc
// relatude.db.json — no rebuild needed to change the model
"DatamodelSources": [{
  "Type": "AssemblyNameReference",   // or TypeNameReference | JsonFile
  "Namespace": "VenueApp.Models",    // matched exactly, not by prefix
  "Reference": "VenueApp"            // assembly name; null = the entry assembly
}]
```

```csharp
// Program.cs — refactor-safe, fails at compile time instead of at boot
options.OnDatamodelInit = (dm, container) => {
    dm.AddNamespace<IVenue>();     // every node & relation type in that namespace
    dm.Add<IEvent>();              // one type plus everything it references
};
```

Prefer code when the model ships with the app; prefer JSON when the model must change without a rebuild or differs per deployment. Only `AssemblyNameReference`, `TypeNameReference` and `JsonFile` are implemented — the other three `DatamodelSourceType` values throw `NotImplementedException`.

**Admin credentials are not created for you.** `MasterUserName` / `MasterPassword` are null until set, login throws "No master user configured on the server" until then, the stored user name must be **lowercase**, and without `TokenEncryptionSecret` every restart logs everyone out.

## Files, media and URLs

A `FileValue` property is a slot in the file store; the bytes never live in the node. Four steps get a file from disk to a browser, and `references/files-and-media.md` covers all of them properly.

**1. Upload — after the node is stored.** `FileValue.PropertyPath` is `null` on an unsaved node, so insert first.

```csharp
await db.FileUploadAsync<IArticle>(article, a => a.Photo, stream, "cover.jpg");
```

Large files go in chunks instead — `InitiateMultipartUploadAsync` → `AppendMultipartUploadAsync` (**in order**) → `FinalizeMultipartUploadAsync`, guarded by `FileStoreSupportsMultipartUploads`. The session is in-memory, per store instance, and expires after 10 minutes idle.

**2. Build a URL, describing the variant you want.** The adjustment is encoded *into* the URL — there is no server-side list of allowed sizes.

```csharp
var adj = new FileAdjustmentImage {
    Width = 440, Height = 400,
    CropMode = ImageCropMode.Fill,
    RequestedFormat = FileFormat.Webp,
    Quality = 85,
};
var url = db.GetUrl(article.Photo, adj);       // throws if the file slot is empty
```

`FileAdjustmentVideo` transcodes video; `FileAdjustmentMeta` returns conversion status and metadata as JSON. An *image* adjustment on a *video* file extracts a still frame — that is what `TimeOffsetMs` is for.

**3. Conversion runs in the background.** The URL is not servable the moment you build it. Check `db.IsFileReady(path, adj, requestIfNot: true)`, and expect a generated status placeholder — not an error — while it runs. Converters must be registered at startup (`options.FileConverters.Add(new SkiaImageConverter(1))`), or every conversion fails.

**4. Serve it from your own middleware.** Relatude.DB maps no file endpoint. The whole thing is `TryParseUrlForContent` plus `FileHandler.HandleFileAsync`, which gives you cache headers, `Content-Disposition` and HTTP range support (so video seeking works):

```csharp
public async Task Invoke(HttpContext http, RelatudeDBContext ctx) {
    if (RelatudeDBRuntime.IsReady) {
        var url = http.Request.Path.Value + http.Request.QueryString;   // query string included!
        if (ctx.Database.TryParseUrlForContent(url, out var c) && c.Stream != null) {
            var result = await FileHandler.HandleFileAsync(
                http, c.Stream, c.FileName, c.Attachment, c.ContentType, c.Cacheable);
            await result.ExecuteAsync(http);
            return;
        }
    }
    await _next.Invoke(http);        // everything that is not ours falls through
}
```

Register it **after** `UseStaticFiles()` — the default URL root is `/`, so it sees every request.

## Top pitfalls to watch for

The full checklist is `references/pitfalls.md` — read it before finishing any non-trivial answer. The ones worth carrying in your head:

- **`Reference`/`References` `foreach` yields nothing unless preloaded.** Use `.Get()` / `.TryGet(out …)` or `.Preload(...)`.
- **Never construct a `NodeMeta`.** Default to `NodeMeta.Empty`, treat it as read-only.
- **Put attributes on the interface, not the implementing class.**
- **Two parent interfaces may not declare the same property name**, and **property overriding is not supported.** Both are caught at model-build time.
- **Nullable value types are not supported** — no `int?`, `DateTime?`, `GeoCoordinate?`. Use the type's empty value; that is what `GeoCoordinate.Empty` and `FileValue.Empty` are for.
- **Never `OrderBy` a `GeoCoordinate`.** Storage order follows a Z-order curve — spatially coherent, meaningless to a human. Filter by radius in the engine, then sort by `DistanceTo` on the materialised page.
- **`GeoCoordinate.Empty` never matches `IsWithin`** and every distance to it is infinite. That is by design.
- **You cannot read an embedded map by key until the parent is persisted.** Before insert, only `Add` is safe.
- **Filter, sort and facet only on `Indexed = true` properties.**
- **A single `Where` is pushed to the indexes all or nothing.** One unindexed part drags the whole predicate into row evaluation; chained `Where` calls are pushed down independently, so split it.
- **`MatchesSearch` needs `IndexedByWords = true` and has no unindexed fallback**, unlike every other filter. It matches indexed *words*, so it will not find "waterproof" from `"proof"` — `Contains` will. And `PrefixSearch`/`InfixSearch` are about `term*`/`*term` wildcards in a search, not about the `StartsWith`/`Contains` methods.
- **`Traverse` and `ShortestPath` work over relations only** — not references, not embedded data. Needing traversal is the signal to model the link as a relation.
- **`Relate` appends to the bottom of the relation list, and relating an existing pair throws.** Order is preserved per side; change it with `MoveRelation…` / `SetRelationOrder`, not by re-relating.
- **`relatude.db.json` is created with the demo model if missing**, and the admin UI rewrites the whole file when anything is saved from it.
- **The `RelatudeDB` configuration section wins over `relatude.db.json` and is stripped from saves** — an admin-UI edit to an overridden key does not stick, and overlays cannot remove elements or set null.
- **Datamodel source namespaces are matched exactly, not by prefix**, and three of the six source types are not implemented.
- **A file can only be uploaded to a node that is already stored** — `FileValue.PropertyPath` is `null` before that — and `GetUrl` throws on an empty file slot.
- **File conversion is asynchronous, and a not-ready variant serves a status placeholder rather than failing.** Never cache a response whose `UrlContent.Cacheable` is `false`.
- **The `FileAdjustment` is the conversion cache key.** Identical parameters hit the cache; varying them per request means unbounded conversions. Use a few presets, and turn on `HashPropertyUrls` in production so only URLs you signed are honoured.
- **`GetUrl(…, absolute: true)` throws `NotImplementedException`** in the current `DefaultUrlProvider`. Prefix the relative URL yourself.

## How to behave with this skill

1. **Default to interface-only node types.** Offer a paired class only when the user has a stated reason (instantiation without a store, behaviour on the type, external deserialisation), and explain the four extra obligations when you do.
2. **Reach for facet interfaces when the model has more than two or three node types.** Cross-cutting properties like a title, a slug, timestamps or a location belong in a small shared interface. Read `references/modelling-patterns.md` and show the user the query-time payoff — one indexed query across heterogeneous types instead of one query per type merged in memory.
3. **Set `Indexed = true` deliberately** on everything you filter, sort or facet, and say why when you do.
4. **Prefer builder methods to LINQ on result sets**, `Page` to `Skip`/`Take`, builder `Count()` to `Execute().Count()`, and `SelectId()` when only ids are needed.
5. **Use the expression overloads** of `Relate`, `WhereRelates`, `Include`, `Preload` and `FileUploadAsync` — they are readable and type-checked.
6. **Push back when a relationship does not fit the five shapes.** If the edge carries data, say so and promote it to a node type.
7. **Treat media as a pipeline, not a call.** Whenever a user asks for thumbnails, resizing or video, cover all four steps: converters registered at startup, the adjustment preset, the readiness check, and the middleware that serves the URL. Skipping any one of them produces something that looks right and serves nothing. The adjustment fields are a fixed catalogue in `references/files-and-media.md` — do not invent options.
8. **Recommend the admin UI datamodel browser** after any modelling change — it shows each type's parent chain, which is the fastest way to confirm facet interfaces really landed as parent node types and that a property really is indexed.
9. **Do not fabricate.** The docs are thin and the API is pre-1.0. If something is not covered here or in the references, say so plainly and point at the mapped source path.
