---
name: relatude-db-intro
description: Short, beginner-friendly introduction to Relatude.DB — what it is, how to model nodes with property types, relations, interfaces and classes, how setup works via relatude.db.json, and how the query and mutation API looks. Use when explaining or pitching Relatude.DB to someone who has never heard of it, writing getting-started docs, README intros, or onboarding material, or when a newcomer asks "what is Relatude.DB" / "how do I get started". For deep API work (full attribute catalog, facets, files, cultures, revisions) use the fuller relatude-db skill instead.
---

# Relatude.DB — introduction

Purpose of this skill: produce a **short, concrete, confidence-building** explanation of
Relatude.DB. The reader should finish thinking "that's easy, I can start today."

## How to use this skill

- Keep it short. Every claim gets a 3–10 line example, not a paragraph.
- Lead with the value ("your C# classes *are* the schema"), not the architecture.
- Show only what a first web app needs. Skip cultures, revisions, access control,
  multipart uploads, transaction plugins unless asked — mention them once in a
  closing "what you also get" list.
- Say it is pre-1.0 once, at the end. Do not promise API stability.
- Every code sample below is verified against the source. Do **not** invent API beyond
  it — if unsure, point at `src/Relatude.DB.NodeStore/` or the examples folder.

Sources of truth in this repo:
`src/Relatude.DB.NodeStore/Nodes/Attributes.cs`,
`src/Relatude.DB.NodeStore/Nodes/NodeStore.cs`,
`src/Relatude.DB.NodeStore/Query/IQueryOfNodes.cs`,
`examples/Website.Simple/Models/ShopModels.cs`,
`examples/Website.Simple/Program.cs`,
`examples/Website.Simple/relatude.db.json`.

---

## 1. The one-paragraph pitch

Relatude.DB is an open-source, object-oriented graph database for .NET. Your C# classes
*are* the schema — no SQL, no migrations, no ORM mapping layer. You save an object and
get an object back, and relations between objects are real navigable links instead of
foreign keys you join on.

Why for a web app: full-text search, semantic/vector search, faceted search, file and
image/video handling, and an admin UI are built in. It runs in-process inside the
ASP.NET Core app, so a query is a method call — typically sub-millisecond, since the
graph is held in memory and persisted to an append-only log.

## 2. Modelling — just write classes

A node type is any class, record, struct **or interface** marked `[Node]`. Plain
properties map automatically by CLR type; attributes only *tune* things.

```csharp
[Node(TextIndex = BoolValue.True)]          // whole node is free-text searchable
public class Product {
    [PublicIdProperty] public Guid Id { get; set; }

    [StringProperty(Indexed = true)] public string Name { get; set; } = "";
    public string Description { get; set; } = "";      // no attribute needed
    [DoubleProperty(Indexed = true)] public double Price { get; set; }
    [BooleanProperty(Indexed = true)] public bool InStock { get; set; }
    [StringArrayProperty(Indexed = true)] public string[] Tags { get; set; } = [];

    [ReferenceProperty(Indexed = true)] public Reference<Brand> Brand { get; set; } = new();
}
```

`Indexed = true` is what makes a property filterable and facetable — the only
performance knob needed on day one.

### Property types

| Group | Types |
|---|---|
| Scalars | `bool`, `int` (incl. enums), `long`, `decimal`, `double`, `float`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan` |
| Text | `string`, `[HtmlProperty] string` |
| Arrays | `string[]`, `Guid[]`, `int[]`, enum arrays, `byte[]`, `float[]` (vectors) |
| Files | `FileValue` — a slot in the file store, with automatic image/video conversion |
| Owned children | `Embedded<T>`, `EmbeddedMap<TKey,T>` — sub-objects that live and die with the parent |
| Links | `Reference<T>`, `References<T>`, and relation properties (below) |

Marker attributes worth naming: `[PublicIdProperty]` (Guid id), `[InternalIdProperty]`
(fast int id), `[DisplayNameProperty]`, `[AddressProperty]` (URL slug),
`[CreatedUtcProperty]`, `[ChangedUtcProperty]`, `[Exclude]`.

### Relations — two levels

A reference property is enough for most things; it saves together with the node:

```csharp
product.Brand.Set(brand.Id);
db.Update(product);

if (product.Brand.TryGet(out var b)) Console.WriteLine(b.Name);
```

When the relation must be navigable from **both** sides, declare it as a class. There
are exactly five shapes, so there is nothing to get wrong:

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

`page.Children` enumerates lazily, `page.Parent.Get()` walks up, and both sides stay
consistent automatically — you never store a `ParentId`.

### Interfaces and classes

An interface can be a node type too; classes implementing it become **subtypes**:

```csharp
public interface IContent {
    Guid Id { get; set; }
    [StringProperty(Indexed = true)] string Title { get; set; }
}

[Node] public class Article : IContent { public Guid Id { get; set; } public string Title { get; set; } = ""; }
[Node] public class Video   : IContent { public Guid Id { get; set; } public string Title { get; set; } = ""; }

db.Query<IContent>().Where(c => c.Title.StartsWith("Hello")).Execute();  // returns both
```

Query a base type and every descendant comes back, correctly typed. The class can also
be skipped entirely — `db.Create<IArticle>()` generates the implementation.

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

`relatude.db.json` beside the app says where data lives and which namespaces hold the
model. Show only the interesting part:

```jsonc
{
  "MasterUserName": "m",
  "MasterPassword": "m",
  "ContainerSettings": [{
    "Name": "MyDatabase",
    "AutoOpen": true,
    "IOSettings": [{ "Id": "b195...", "Name": "Local disk", "Path": "relatude.db", "IOType": "LocalDisk" }],
    "IoDatabase": "b195...",
    "DatamodelSources": [
      { "Name": "Shop", "Namespace": "Website.Simple.Models", "Type": "AssemblyNameReference" }
    ],
    "LocalSettings": { "EnableTextIndexByDefault": true }
  }]
}
```

The message to land: *this folder is the database, that namespace is the schema.*
Everything else has a sensible default, and the file is normally written by the admin
UI at `/relatude.db` (datamodel sources, file storage, backups, indexing, Azure blob,
Lucene, AI provider) rather than by hand.

## 4. Query API

One fluent builder, LINQ-style expressions, `Execute()` at the end.

```csharp
var db = ctx.Database;

var p = db.Get<Product>(id);

var page = db.Query<Product>()
             .Where(p => p.InStock && p.Price < 500)
             .OrderBy(p => p.Price)
             .Page(0, 20)
             .Execute();

// full-text + hybrid semantic search, no extra search engine to run
var hits = db.Query<Product>().WhereSearch("wool jacket", semanticRatio: 0.5).Execute();

// follow relations eagerly, in one query
var pages = db.Query<Page>().Include(p => p.Children).Execute();

// faceted search — counts adapt to the current result set
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

Async twins exist: `ExecuteAsync()`, `CountAsync()`, `FirstOrDefaultAsync()`.

## 5. Mutation API

No change tracking to reason about, no `SaveChanges()` at the end of a request. Each
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
db.SetRelation<Page>(parent, x => x.Children, child);
db.RemoveRelation<Page>(parent, x => x.Children, child);

// batch several changes into one atomic transaction
var t = db.CreateTransaction();
t.Insert(p);
t.Update(other);
t.Execute();

// files: upload into a FileValue slot, get a resized/converted URL back
await db.FileUploadAsync(art.File, stream, "hero.jpg");
var url = db.GetUrl(art.File, new FileAdjustmentImage { Width = 400, Height = 300 });
```

Naming trap to avoid: relation mutation is `SetRelation` / `RemoveRelation` /
`ClearRelations` on `NodeStore`. `Relate<R>(from, to)` exists on `Transaction`, not as
`db.Relate(...)`.

## 6. Close with "what you get without writing it"

- **Admin UI** at `/relatude.db` — browse and edit data, inspect the datamodel, backups, index status, query console.
- **Search** — BM25 free text, prefix/infix, fuzzy, plus semantic vector search on the same index.
- **Facets** — value and range facets with drill-sideways counts off any indexed property or relation.
- **Files** — disk or Azure blob, on-demand image/video conversion, CDN-friendly URLs.
- **Extras** — cultures and fallbacks, revisions, per-property read/write access, transaction plugins, and a GraphQL **read** endpoint generated from the model (`app.MapRelatudeDBGraphQL("/graphql")`; there are no GraphQL mutations).

Then the closing line: add the NuGet, write the classes, point `relatude.db.json` at
their namespace, run — the admin UI is already there and the data is already searchable.

> Pre-1.0 — the API still moves in small ways. Source and examples:
> https://github.com/Relatude/Relatude.DB
