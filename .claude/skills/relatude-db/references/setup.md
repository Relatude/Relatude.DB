# Setup, registration and the admin UI

How a model gets from your C# into a running engine, and what has to be configured outside code.

## Contents

- [Targets](#targets)
- [Server-hosted wiring](#server-hosted-wiring)
- [Pointing the server at your model](#pointing-the-server-at-your-model)
- [The admin UI](#the-admin-ui)
- [Programmatic / embedded registration](#programmatic--embedded-registration)
- [Middleware order](#middleware-order)
- [Upgrading](#upgrading)

## Targets

.NET 8+. The engine runs in-process or server-hosted. `Relatude.DB.Server` is the package that ships the server and the admin UI; plugin packages exist for Azure, Lucene and Sqlite (`Relatude.DB.Plugins.Azure` / `.Lucene` / `.Sqlite`).

Because the project is pre-1.0, check the current package list on the repo rather than trusting a hard-coded list here: https://github.com/Relatude/Relatude.DB

## Server-hosted wiring

The usual case:

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.AddRelatudeDB(options => {
    // options.FileConverters.Add(new SkiaImageConverter());
    // options.FileConverters.Add(new FFMpegVideoConverter());
});

var app = builder.Build();

app.MapGet("/", (RelatudeDBContext ctx) => $"{ctx.Database.Count()} nodes.");

app.UseRelatudeDB();     // mounts the admin UI at /relatude.db
app.Run();
```

- `AddRelatudeDB` lives in the **default global namespace** — no `using` required.
- `RelatudeDBContext` is injected by DI; **`ctx.Database` is the `NodeStore`**, which is the API surface for everything in `api-quickref.md` and `queries.md`.
- File converters are registered here, at startup. They are what make image and video conversion work when serving files.

Move the admin UI by passing a path:

```csharp
app.UseRelatudeDB("/admin/db");
```

## Pointing the server at your model

On first boot, open `/relatude.db` and add a **datamodel source** pointing at your model namespace:

```json
{
  "Id": "...",
  "Name": "VenueApp",
  "Type": "AssemblyNameReference",   // or TypeNameReference | AssemblyFileReference | JsonFile | CSharpCodeFile
  "Namespace": "VenueApp.Models",
  "Reference": "VenueApp"            // assembly name or path
}
```

The server scans that namespace, builds the datamodel, generates the proxy assembly and reloads. From then on the model is **rebuilt automatically whenever the assembly loads** — you do not re-register after each code change.

## The admin UI

The admin UI is not an optional extra — it is where the parts of Relatude.DB that are not code get configured. **Your model lives in C#; the runtime lives in the admin UI.** Worth learning your way around it early, and worth telling users about when they ask why something is not working.

It has its own authentication. On first boot an admin user is created and **the credentials are printed to the application log** — grab them from there.

| Area | What it is for |
|---|---|
| Datamodels | Register and reload datamodel sources; browse the built model — every node type, its parents, its properties and its relations, exactly as the engine sees them. |
| Data browser | Inspect, search and edit actual nodes. Invaluable while modelling: create a node by hand and confirm the shape is what you intended. |
| Indexing | Text, semantic and value index configuration; reindexing. |
| File storage | Add and configure storage providers (local disk, Azure Blob) and pick a default. The id you paste into `[FileProperty(FileStorageProviderId = "…")]` comes from here. |
| IO | Where the append-only transaction log and backups are written. |
| Backups | One-click backup and restore. **Take one before upgrading** — the project is pre-1.0. |
| Status | Store state, running file conversions, activity and timings. |

Two habits worth forming, and worth recommending to users:

1. **Check the datamodel browser after any modelling change.** It shows the parent chain of each type, so it is the fastest way to confirm that facet interfaces actually landed as parent node types and that a property you expected to be indexed really is.
2. **Take a backup before upgrading.**

## Programmatic / embedded registration

```csharp
var datamodel = new Datamodel();
datamodel.AddNamespace<IVenue>();     // every node & relation type in that namespace
// or, one at a time:
datamodel.Add<IEvent>();
datamodel.Add(typeof(IAttendee));
```

`AddNamespace<T>` scans the assembly containing `T` and adds every type whose namespace matches `T`'s, skipping enums, static classes and anything marked `[Exclude]`.

For what the model builder validates at this point — and the errors you will see on startup if a model is ambiguous — see the validation section of `datamodels.md`.

## Middleware order

`UseRelatudeDB` installs the engine's own startup-progress and auth middleware, so **call it after** your own `UseCors` / `UseHttpsRedirection` / `UseAuthentication`. Getting this wrong produces confusing auth behaviour on the admin UI rather than an obvious error.

## Upgrading

The API is pre-1.0 and minor breaking changes are expected. Before upgrading: take a backup from the admin UI, and expect to re-check the datamodel browser afterwards. When something in these reference files disagrees with the installed build, **the source wins** — see the source map in `pitfalls.md`.
