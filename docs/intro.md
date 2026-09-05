# Getting started with Relatude.DB

**A short introduction**

Relatude.DB is an open-source, object-oriented graph database for .NET. Your C# classes *are* the
schema — there is no SQL, no migrations and no ORM mapping layer. You save an object and you get an
object back, and the links between objects are real, navigable relations instead of foreign keys you
have to join on.

It runs in-process inside your ASP.NET Core app, so a query is a method call — typically
sub-millisecond. (A remote server setup is supported too, and you can move to it later on — the model
and the queries stay the same.) Full-text (BM25) search, semantic/vector search, faceted search, file and image handling and
an admin UI are part of the engine, not extra services you have to run.

> **Pre-1.0.** The public API still moves in small ways. Source and examples:
> [github.com/Relatude/Relatude.DB](https://github.com/Relatude/Relatude.DB)

---

## 1. Install

Add the server package to a normal ASP.NET Core project:

```bash
dotnet add package Relatude.DB.Server
```

That is the whole installation. There is no database server to set up, no connection string and no
schema to create up front — the database is a folder next to your app.

---

## 2. Modelling — just write classes

A node type is any class, record, struct **or interface** in a namespace you point the engine at.
No attribute is needed to opt in — `[Node]` only *tunes* a type, and `[Exclude]` opts one out. Plain
properties map automatically by their CLR type; property attributes likewise only tune things.

```csharp
[Node(TextIndex = BoolValue.True)]          // whole node is free-text searchable
public class Product {
    [PublicIdProperty] public Guid Id { get; set; }

    [StringProperty(Indexed = true, IndexedByWords = true)] public string Name { get; set; } = "";
    [StringProperty(IndexedByWords = true)] public string Description { get; set; } = "";
    public string Sku { get; set; } = "";              // no attribute needed
    [DoubleProperty(Indexed = true)] public double Price { get; set; }
    [BooleanProperty(Indexed = true)] public bool InStock { get; set; }
    [StringArrayProperty(Indexed = true)] public string[] Tags { get; set; } = [];

    [ReferenceProperty(Indexed = true)] public Reference<Brand> Brand { get; set; } = new();
}
```

`Indexed = true` is what makes a property filterable and facetable — the only performance knob you
need on day one. `IndexedByWords = true` is the one other knob worth knowing: it adds a word index to
that single property, which is what `MatchesSearch` (see below) searches.

### Property types

| Group | Types |
|---|---|
| Scalars | `bool`, `int` (incl. enums), `long`, `decimal`, `double`, `float`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan` |
| Text | `string`, `[HtmlProperty] string` |
| Arrays | `string[]`, `Guid[]`, `int[]`, enum arrays, `byte[]`, `float[]` (vectors) |
| Geo | `GeoCoordinate` — latitude/longitude with radius and distance queries |
| Files | `FileValue` — a slot in the file store, with automatic image/video conversion |
| Owned children | `Embedded<T>`, `EmbeddedMap<TKey,T>` — sub-objects that live and die with the parent |
| Links | `Reference<T>`, `References<T>`, and relation properties (below) |

A few marker attributes are worth naming: `[PublicIdProperty]` (the Guid id), `[InternalIdProperty]`
(the fast int id), `[DisplayNameProperty]`, `[AddressProperty]` (URL slug), `[CreatedUtcProperty]`,
`[ChangedUtcProperty]` and `[Exclude]`.

### Relations — two levels

A reference property is enough for most things. It saves together with the node:

```csharp
product.Brand.Set(brand.Id);
db.Update(product);

if (product.Brand.TryGet(out var b)) Console.WriteLine(b.Name);
```

When the link has to be navigable from **both** sides, declare it as a class. There are exactly five
shapes, so there is very little to get wrong:

| Base class | Meaning |
|---|---|
| `OneOne<T>` | 1↔1, same type, symmetric |
| `OneToOne<TFrom,TTo>` | 1↔1 |
| `OneToMany<TOne,TMany>` | 1↔N |
| `ManyMany<T>` | N↔N, same type |
| `ManyToMany<TFrom,TTo>` | N↔N |

```csharp
public class Tree : OneToMany<Page, Page> {   // a page tree, in 3 lines
    public class Parent : One { }
    public class Children : Many { }
}

// on Page:
public Tree.Parent   Parent   { get; set; } = new();
public Tree.Children Children { get; set; } = new();
```

`page.Children` enumerates lazily, `page.Parent.Get()` walks up, and both sides stay consistent
automatically — you never store a `ParentId`.

### Interfaces and classes

An interface can be a node type too, and the classes implementing it become **subtypes**:

```csharp
public interface IContent {
    Guid Id { get; set; }
    [StringProperty(Indexed = true)] string Title { get; set; }
}

public class Article : IContent { public Guid Id { get; set; } public string Title { get; set; } = ""; }
public class Video   : IContent { public Guid Id { get; set; } public string Title { get; set; } = ""; }

db.Query<IContent>().Where(c => c.Title.StartsWith("Hello")).Execute();  // returns both
```

Query a base type and every descendant comes back, correctly typed. You can even skip the class
entirely — `db.Create<IArticle>()` generates the implementation for you.

---

## 3. Setup — one call plus one file

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddRelatudeDB();          // no using needed
var app = builder.Build();

app.MapGet("/", (RelatudeDBContext ctx) => $"{ctx.Database.Count()} objects");

app.StartRelatudeDB();
app.MapRelatudeDBAdmin();         // admin UI at /relatude.db
app.Run();
```

A `relatude.db.json` file beside the app says where the data lives and which namespaces hold the
model:

```json
{
  "MasterUserName": "m",
  "MasterPassword": "m",
  "ContainerSettings": [{
    "Name": "MyDatabase",
    "AutoOpen": true,
    "IOSettings": [{ "Id": "b195...", "Name": "Local disk", "Path": "relatude.db", "IOType": "LocalDisk" }],
    "IoDatabase": "b195...",
    "DatamodelSources": [
      { "Name": "Shop", "Namespace": "Website.Simple.Models", "Type": "TypeReference" }
    ],
    "LocalSettings": { "EnableTextIndexByDefault": true }
  }]
}
```

That is the whole idea: **this folder is the database, that namespace is the schema.** Everything
else has a sensible default, and the file is normally written for you by the admin UI at
`/relatude.db` — datamodel sources, file storage, backups, indexing, Azure blob, Lucene and AI
provider settings all live there.

Any of it can be overridden per environment from standard ASP.NET configuration: a `RelatudeDB`
section with the same shape as the file — in `appsettings.json`, `appsettings.Development.json`,
environment variables or user secrets — wins over `relatude.db.json`, and what it supplies is never
written back to the file. Credentials belong there, not in the file.

---

## 4. Querying

One fluent builder, LINQ-style expressions, `Execute()` at the end.

```csharp
var db = ctx.Database;

var p = db.Get<Product>(id);

var page = db.Query<Product>()
             .Where(p => p.InStock && p.Price < 500)
             .OrderBy(p => p.Price)
             .Page(0, 20)
             .Execute();
```

Async twins exist throughout: `ExecuteAsync()`, `CountAsync()`, `FirstOrDefaultAsync()`.

### Search

Free-text and semantic search are part of the same index — there is no second search engine to run
and keep in sync:

```csharp
// BM25 free text, optionally blended with vector similarity
var hits = db.Query<Product>().WhereSearch("wool jacket", semanticRatio: 0.5).Execute();
```

### Following relations

```csharp
// follow relations eagerly, in one query
var pages = db.Query<Page>().Include(p => p.Children).Execute();
```

### Facets

Facet counts adapt to the current result set, so you get drill-sideways behaviour for free:

```csharp
var res = db.Query<Product>()
            .WhereSearch("jacket")
            .Facets()
            .AddValueFacet(p => p.Brand)
            .AddValueFacet(p => p.Tags)
            .AddRangeFacet(p => p.Price)
            .Execute();

foreach (var f in res.Facets)
    foreach (var v in f.Values) Console.WriteLine($"{f.DisplayName}: {v.DisplayName} ({v.Count})");
```

### Filtering text and arrays

Inside a `Where` lambda the familiar C# methods work. They are answered from the index when the
property has one, and row by row when it does not:

| Written as | Means | Needs |
|---|---|---|
| `p.Tags.Contains("eco")` | the array holds that element | – |
| `p.Name.Contains("jacket")` | ordinal substring | – |
| `p.Name.StartsWith("Wool")` | ordinal prefix | – |
| `p.Description.MatchesSearch("wool jacket")` | word + semantic search in that **one** property | `IndexedByWords = true` |

```csharp
db.Query<Product>().Where(p => p.Tags.Contains("eco") && p.Name.StartsWith("Wo")).Execute();

// scoped search: the words must be in Description, not merely somewhere in the node
db.Query<Product>().Where(p => p.Description.MatchesSearch("waterproof")
                            || p.Name.MatchesSearch("waterproof")).Execute();
```

Three things worth saying once:

- Text matching is **ordinal** — case matters, just like `==` on strings. Passing an explicit
  `StringComparison` is rejected rather than quietly ignored.
- `Contains` means what it means in C#: an element on `string[]`/`Guid[]`/`int[]`/enum
  arrays/`float[]`/`byte[]`, a substring on a `string`.
- `MatchesSearch` is `WhereSearch` narrowed to one property, and being a predicate it composes with
  `||` and `!` where a chained `WhereSearch` cannot. It is the one filter with no unindexed fallback
  — a search cannot be evaluated row by row — so it requires `IndexedByWords` (or
  `IndexedBySemantic`) and tells you when the property lacks it.

---

## 5. Writing data

There is no change tracking to reason about and no `SaveChanges()` at the end of a request. Each
call is its own ACID transaction, durably logged.

```csharp
var p = new Product { Name = "Wool jacket", Price = 249 };
db.Insert(p);

p.Price = 199;
db.Update(p);

db.Upsert(p);
db.Delete(p);

// create + insert in one go (works for interfaces too)
var art = db.CreateAndInsert<IArticle>(a => a.Title = "Hello");

// relations
db.AddRelation<Page>(parent, x => x.Children, child);   // append; throws if already related
db.SetRelation<Page>(parent, x => x.Children, child);   // idempotent; replaces on a "one" side
db.RemoveRelation<Page>(parent, x => x.Children, child);

// batch several changes into one atomic transaction
var t = db.CreateTransaction();
t.Insert(p);
t.Update(other);
t.Execute();

// files: upload into a FileValue slot, get a resized/converted URL back
await db.FileUploadAsync<IArticle>(art, a => a.File, stream, "hero.jpg");
var url = db.GetUrl(art.File, new FileAdjustmentImage { Width = 400, Height = 300 });
```

One naming trap to avoid: the relation verbs are `AddRelation` / `SetRelation` / `RemoveRelation` /
`ClearRelation` / `ClearRelations`. There is no `db.Relate(...)` or `db.UnRelate(...)`.
`AddRelation` appends and throws if the pair is already related; `SetRelation` is the idempotent
one — it also drops whatever it has to on a "one" side to make room, which is why it reads as an
assignment.

---

## 6. What you get without writing it

- **Admin UI** at `/relatude.db` — browse and edit data, inspect the datamodel, take backups, check
  index status, run queries in a console.
- **Search** — BM25 free text, prefix/infix, fuzzy, plus semantic vector search on the same index.
- **Facets** — value and range facets with drill-sideways counts off any indexed property or relation.
- **Files** — local disk or Azure blob, on-demand image and video conversion, CDN-friendly URLs.
  Conversion needs a converter registered at startup — `Relatude.DB.Plugins.Skia` for images,
  `Relatude.DB.Plugins.FFMpeg` for video.
- **Geo** — coordinate properties with radius filters and sort-by-distance.
- **Extras** — cultures and fallbacks, revisions, per-property read/write access, transaction plugins,
  and a GraphQL **read** endpoint generated from your model
  (`app.MapRelatudeDBGraphQL("/graphql")`; there are no GraphQL mutations).

---

## Where next

Add the NuGet, write the classes, point `relatude.db.json` at their namespace and run — the admin UI
is already there and your data is already searchable.

- [The manual](manual.html) — data modelling and querying in depth.
- [relatude-db.skill](relatude-db.skill) — the same knowledge packaged for AI coding agents.
- [github.com/Relatude/Relatude.DB](https://github.com/Relatude/Relatude.DB) — source, examples and issues.
