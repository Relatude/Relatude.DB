# Setup, registration and the admin UI

How a model gets from your C# into a running engine, and what has to be configured outside code.

## Contents

- [Targets](#targets)
- [Server-hosted wiring](#server-hosted-wiring)
- [Pointing the server at your model](#pointing-the-server-at-your-model)
- [The admin UI](#the-admin-ui)
- [Programmatic / embedded registration](#programmatic--embedded-registration)
- [Middleware order](#middleware-order)
- [File converters](#file-converters)
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

Two ways, and they compose — JSON sources load first, then `OnDatamodelInit` adds to the same datamodel. Full treatment in `configuration.md`.

**From `relatude.db.json`** (or the same entry added through the admin UI), when the model should be changeable without a rebuild:

```jsonc
"DatamodelSources": [
  {
    "Id": "...",
    "Name": "VenueApp",
    "Type": "AssemblyNameReference",   // or TypeNameReference | JsonFile
    "Namespace": "VenueApp.Models",    // matched exactly, not by prefix
    "Reference": "VenueApp"            // assembly name; null means the entry assembly
  }
]
```

`AssemblyFileReference`, `TypeNameFileReference` and `CSharpCodeFile` are declared but throw `NotImplementedException` in the current build.

**From `Program.cs`**, which is the common case when the model ships with the app — refactor-safe, and it fails at compile time rather than at boot:

```csharp
builder.AddRelatudeDB(options => {
    options.OnDatamodelInit = (dm, container) => {
        dm.AddNamespace<IVenue>();     // every node & relation type in that namespace
        dm.Add<IEvent>();              // one type, plus everything it references
    };
});
```

Either way the server builds the datamodel, generates the proxy assembly and reloads. From then on the model is **rebuilt automatically whenever the assembly loads** — you do not re-register after each code change.

## The admin UI

The admin UI is not an optional extra — it is where the parts of Relatude.DB that are not code get configured. **Your model lives in C#; the runtime lives in the admin UI.** Worth learning your way around it early, and worth telling users about when they ask why something is not working.

It has its own authentication, and **nothing creates an admin user for you**. `MasterUserName` and `MasterPassword` in `relatude.db.json` are null until you set them, and until then logging in throws "No master user configured on the server." Set them in the file, or inject them from configuration in `OnServerSettingsInit`. The stored user name must be **lowercase** — the check lowercases the input before comparing — and `TokenEncryptionSecret` should be set too, or every restart invalidates every session. Details in `configuration.md`.

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

If you also serve media from the store, that is a middleware **you** write — nothing maps a file endpoint for you. It goes after the static-file middleware, because the default URL root for nodes and files is `/`, so it sees every request:

```csharp
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMiddleware<RelatudeDBMiddleware>();   // your own — see files-and-media.md

app.StartRelatudeDB();                       // or app.UseRelatudeDB()
app.MapRelatudeDBAdmin();
```

`StartRelatudeDB()` + `MapRelatudeDBAdmin()` is what `UseRelatudeDB()` does in one call; splitting them lets you place your own middleware in between.

## File converters

Image and video conversion only works if a converter is registered at startup. There is no default — without one, every adjusted URL serves an error placeholder reading "No converter available from JPEG to WEBP":

```csharp
builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new SkiaImageConverter(1));    // images
    options.FileConverters.Add(new FFMpegVideoConverter());   // video (needs FFmpeg available)
});
```

File **storage** providers are configured in the admin UI under File storage, not here.

## Upgrading

The API is pre-1.0 and minor breaking changes are expected. Before upgrading: take a backup from the admin UI, and expect to re-check the datamodel browser afterwards. When something in these reference files disagrees with the installed build, **the source wins** — see the source map in `pitfalls.md`.
