# Getting started with Relatude.DB

**A short introduction**

Relatude.DB is an open-source, object-oriented graph database for .NET. Your C# types *are* the
schema: there is no SQL, no migrations and no ORM mapping layer. You save an object and get an object
back, and the links between objects are real relations you can navigate, not foreign keys you join on.

It runs inside your ASP.NET Core app, so a query is a method call, typically well under a
millisecond. Full-text (BM25) search, semantic search, facets, file and image handling and an admin
UI are part of the engine, not extra services to run. A remote server setup is also supported, with
the same model and queries.

> **Pre-1.0.** The API may still change in small ways. Source and examples:
> [github.com/Relatude/Relatude.DB](https://github.com/Relatude/Relatude.DB)

---

## 1. Install

Add the server package to an ASP.NET Core project:

```bash
dotnet add package Relatude.DB.Server
```

That is all. There is no database server to install, no connection string and no schema to create:
the database is a folder next to your app.

---

## 2. Modelling: just write classes

A node type is any class, record, struct **or interface** in a namespace you register. No attribute
is needed. `[Node]` only tunes a type, and `[Exclude]` leaves one out. Properties map by their C# type;
property attributes only tune them.

```csharp
[Node(TextIndex = BoolValue.True)]          // the whole node is searchable as text
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

`Indexed = true` makes a property fast to filter, sort and facet on. It is the only performance
setting you need on day one. `IndexedByWords = true` adds a word index to one property, which is what
`MatchesSearch` (below) searches.

### Property types

| Group | Types |
|---|---|
| Scalars | `bool`, `int` (and enums), `long`, `decimal`, `double`, `float`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan` |
| Text | `string`, `[HtmlProperty] string` |
| Arrays | `string[]`, `Guid[]`, `int[]`, enum arrays, `byte[]`, `float[]` (vectors) |
| Geo | `GeoCoordinate`: latitude and longitude, with radius and distance queries |
| Files | `FileValue`: a file in the file store, with image and video conversion |
| Owned children | `Embedded<T>`, `EmbeddedMap<TKey,T>`: sub-objects that live and die with the parent |
| Links | `Reference<T>`, `References<T>`, and relation properties (below) |

A few marker attributes are worth knowing: `[PublicIdProperty]` (the Guid id), `[InternalIdProperty]`
(the fast int id), `[DisplayNameProperty]`, `[AddressProperty]` (the URL slug), `[CreatedUtcProperty]`
and `[ChangedUtcProperty]`.

### Relations: two levels

A reference property is enough for most links. It is saved with the node:

```csharp
product.Brand.Set(brand.Id);
db.Update(product);

if (product.Brand.TryGet(out var b)) Console.WriteLine(b.Name);
```

When you need to navigate a link from **both** sides, declare a relation class. There are five
shapes:

| Base class | Meaning |
|---|---|
| `OneOne<T>` | 1↔1, same type, symmetric |
| `OneToOne<TFrom,TTo>` | 1↔1 |
| `OneToMany<TOne,TMany>` | 1↔N |
| `ManyMany<T>` | N↔N, same type |
| `ManyToMany<TFrom,TTo>` | N↔N |

```csharp
public class Tree : OneToMany<Page, Page> {   // a page tree in three lines
    public class Parent : One { }
    public class Children : Many { }
}

// on Page:
public Tree.Parent   Parent   { get; set; } = new();
public Tree.Children Children { get; set; } = new();
```

`page.Children` loads lazily and `page.Parent.Get()` walks up. Both sides always agree, and you never
store a `ParentId`.

### Interfaces and classes

An interface can be a node type too. The classes that implement it become its **subtypes**:

```csharp
public interface IContent {
    Guid Id { get; set; }
    [StringProperty(Indexed = true)] string Title { get; set; }
}

public class Article : IContent { public Guid Id { get; set; } public string Title { get; set; } = ""; }
public class Video   : IContent { public Guid Id { get; set; } public string Title { get; set; } = ""; }

db.Query<IContent>().Where(c => c.Title.StartsWith("Hello")).Execute();  // returns both
```

A query on a base type returns every subtype, correctly typed. You can even skip the class:
`db.Create<IArticle>()` generates the implementation for you.

---

## 3. Setup: one call and one file

The quickest start is the command line tool. It writes a project you can run, with a README that says
where models, pages and endpoints go:

```bash
dotnet tool install -g Relatude.DB.Tool
relatude new MyApp                                 # an API and a React client, for interactive apps
relatude new MySite --projecttype csharp_web_mvc   # an MVC site with server-rendered pages, for content and SEO
```

`relatude new --list-types` describes the project types, and `relatude init` adds Relatude.DB to a
project you already have. Either way, the result is one call and one file:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddRelatudeDB();          // no using needed
var app = builder.Build();

app.UseRelatudeDB();              // opens the database and serves the admin UI at /relatude.db
app.MapGet("/", (RelatudeDBContext ctx) => $"{ctx.Database.Count()} objects");
app.Run();
```

The file is `relatude.settings/relatude.db.json`. It says where the data lives and which namespaces
hold the model:

```json
{
  "ContainerSettings": [{
    "Name": "MyDatabase",
    "AutoOpen": true,
    "IOSettings": [{ "Id": "b195…", "Name": "Local disk", "Path": "relatude.data", "IOType": "LocalDisk" }],
    "IoDatabase": "b195…",
    "DatamodelSources": [
      { "Id": "8a3f…", "Name": "Shop", "Namespace": "MyApp.Models", "Type": "CompiledTypes" }
    ],
    "LocalSettings": { "EnableTextIndexByDefault": true }
  }]
}
```

That is the whole idea: **this folder is the database, and that namespace is the schema.** Everything
else has a sensible default. You rarely edit the file by hand: the admin UI at `/relatude.db` manages
data model sources, file storage, backups, indexing, Azure Blob storage and AI providers.

Settings can differ per environment:

- A `RelatudeDB` section in standard ASP.NET configuration (`appsettings.json`, environment
  variables, user secrets) has the same shape as the file and wins over it. Put credentials there.
- Changes made in the admin UI are kept in a separate file, `relatude.db.overrides.json`, until you
  move them into `relatude.db.json` from the settings page.

**Source control and deployment.** `relatude.settings/` is part of the application: commit it and
deploy it with the code. `relatude.data/` (and its scratch folder `relatude.data.temp/`) is the
database of the machine it runs on: never commit it, and exclude it from the project so a publish does
not copy your development data to a server. Projects made by `relatude new` do both. In a project of
your own, add this to the `.csproj`:

```xml
<PropertyGroup>
  <DefaultItemExcludes>$(DefaultItemExcludes);relatude.data\**;relatude.data.temp\**</DefaultItemExcludes>
</PropertyGroup>
```

Here is a [typical `.gitignore`](https://github.com/Relatude/Relatude.DB/blob/main/docs/.gitignore), and
the manual explains
[what goes where](manual.html#122-what-goes-where-source-control-deployment-and-appsettings).

---

## 4. Querying

One fluent builder, LINQ-style expressions, and `Execute()` at the end:

```csharp
var db = ctx.Database;

var p = db.Get<Product>(id);

var page = db.Query<Product>()
             .Where(p => p.InStock && p.Price < 500)
             .OrderBy(p => p.Price)
             .Page(0, 20)
             .Execute();
```

There are async versions throughout: `ExecuteAsync()`, `CountAsync()`, `FirstOrDefaultAsync()`.

### Search

Free-text and semantic search use the same index, so there is no second search engine to keep in
sync:

```csharp
// BM25 free text, optionally blended with vector similarity
var hits = db.Query<Product>().WhereSearch("wool jacket", semanticRatio: 0.5).Execute();
```

### Following relations

```csharp
// load related nodes in the same query
var pages = db.Query<Page>().Include(p => p.Children).Execute();
```

### Facets

Facet counts follow the current result, so filters on one facet still show counts for the others:

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

The usual C# methods work inside a `Where`. They use the index when the property has one, and check
node by node when it does not:

| Written as | Means | Needs |
|---|---|---|
| `p.Tags.Contains("eco")` | the array holds that element | – |
| `p.Name.Contains("jacket")` | substring (case-sensitive) | – |
| `p.Name.StartsWith("Wool")` | prefix (case-sensitive) | – |
| `p.Description.MatchesSearch("wool jacket")` | word and semantic search in this **one** property | `IndexedByWords = true` |

```csharp
db.Query<Product>().Where(p => p.Tags.Contains("eco") && p.Name.StartsWith("Wo")).Execute();

// the words must be in Description or Name, not just anywhere in the node
db.Query<Product>().Where(p => p.Description.MatchesSearch("waterproof")
                            || p.Name.MatchesSearch("waterproof")).Execute();
```

Good to know:

- Text matching is **ordinal**: case matters, as with `==` on strings. An explicit `StringComparison`
  argument is rejected rather than ignored.
- `Contains` means what it means in C#: an element of an array, a substring of a `string`.
- `MatchesSearch` is `WhereSearch` for one property. Because it is a predicate, it combines with `||`
  and `!`. It needs `IndexedByWords` (or `IndexedBySemantic`) on the property, and tells you when it is
  missing.

---

## 5. Writing data

There is no change tracking and no `SaveChanges()`. Each call is its own ACID transaction, written to
a durable log.

```csharp
var p = new Product { Name = "Wool jacket", Price = 249 };
db.Insert(p);

p.Price = 199;
db.Update(p);

db.Upsert(p);
db.Delete(p);

// create and insert in one step (works for interfaces too)
var art = db.CreateAndInsert<IArticle>(a => a.Title = "Hello");

// relations
db.AddRelation<Page>(parent, x => x.Children, child);   // adds; throws if already related
db.SetRelation<Page>(parent, x => x.Children, child);   // idempotent; replaces the partner on a "one" side
db.RemoveRelation<Page>(parent, x => x.Children, child);

// several changes in one atomic transaction
var t = db.CreateTransaction();
t.Insert(p);
t.Update(other);
t.Execute();

// files: upload into a FileValue, then get a resized URL
await db.FileUploadAsync<IArticle>(art, a => a.File, stream, "hero.jpg");
var url = db.GetUrl(art.File, new FileAdjustmentImage { Width = 400, Height = 300 });
```

The relation methods are `AddRelation`, `SetRelation`, `RemoveRelation`, `ClearRelation` and
`ClearRelations`. There is no `db.Relate(...)`. Use `SetRelation` when you mean "make it so": it is
safe to repeat, and on a "one" side it replaces the current partner.

---

## 6. What you get without writing it

- **Admin UI** at `/relatude.db`: browse and edit data, inspect the data model, take backups, check
  index status, and run queries in a console.
- **Search**: BM25 free text, prefix and infix, fuzzy matching, and semantic search on the same index.
- **Facets**: value and range facets, with counts that follow the current filters, on any indexed
  property or relation.
- **Files**: local disk or Azure Blob storage, CDN-friendly URLs, and image and video conversion on
  demand. Conversion is built in: JPEG, PNG, WebP, GIF and BMP images, and frames of videos through
  FFmpeg, which is downloaded the first time it is needed.
- **Geo**: coordinate properties with radius filters and sorting by distance.
- **And more**: cultures with fallback, revisions, read and write access per node and per property,
  transaction plugins, and GraphQL endpoints generated from your model. Define an endpoint on the admin UI's
  **API** page, or map one in code with `app.MapRelatudeDBGraphQL("/graphql")`. Endpoints are
  read-only unless you allow mutations.

---

## Where next

Add the package, write your classes, point `relatude.db.json` at their namespace and run. The admin
UI is already there, and your data is already searchable.

- [The manual](manual.html): data modelling and querying in depth.
- [relatude-db.skill](relatude-db.skill): the same knowledge packaged for AI coding agents.
- [github.com/Relatude/Relatude.DB](https://github.com/Relatude/Relatude.DB): source, examples and issues.
