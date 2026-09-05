# Configuration: relatude.db.json, ServerOptions and datamodel registration

Everything that is not your C# model. Three surfaces, and knowing which one owns what saves most of the confusion:

| Surface | Owns | Changes at |
|---|---|---|
| `relatude.db.json` | Storage, index engines, file stores, AI providers, datamodel sources, credentials | Deploy time, or from the admin UI |
| `ServerOptions` in `Program.cs` | File converters, lifecycle callbacks, folder paths, a custom settings loader | Compile time |
| The admin UI | The same things as the JSON file — **it writes back to it** | Runtime |

## Contents

- [Where the file lives](#where-the-file-lives)
- [The shape of relatude.db.json](#the-shape-of-relatudedbjson)
- [Server level](#server-level)
- [Container level](#container-level)
- [LocalSettings: the engine knobs](#localsettings-the-engine-knobs)
- [ServerOptions](#serveroptions)
- [The startup event order](#the-startup-event-order)
- [Registering a datamodel](#registering-a-datamodel)
- [Admin UI authentication](#admin-ui-authentication)
- [Pitfalls](#pitfalls)
- [Where to look in the source](#where-to-look-in-the-source)

## Where the file lives

`relatude.db.json` sits in the **root data folder**, which is `ServerOptions.DefaultDataFolderPath` resolved against the app's `ContentRootPath` — so by default, next to the app. Two sibling folders appear beside it on first run: `relatude.db/` (the data) and `relatude.db.temp/` (scratch, emptied at every startup).

```csharp
builder.AddRelatudeDB(options => {
    options.DefaultDataFolderPath = "data";        // relative to ContentRootPath, or absolute
    options.DefaultTempFolderPath = "data/tmp";
});
```

**If the file does not exist it is created**, from `RelatudeDBServerSettings.CreateDefault()`. That default points at the bundled demo model (`Relatude.DB.Demo.Models` in the `Relatude.DB.NodeStore` assembly), not at yours — so a first run without a prepared file gives you a working store full of demo types. Replace the datamodel source, or write the file before first boot.

The file is read with `PropertyNameCaseInsensitive`, `ReadCommentHandling = Skip` (so `//` comments survive a *read*) and a `JsonStringEnumConverter` (so enums are written and read as names, not numbers). Property names are PascalCase — no camelCase policy is applied.

## The shape of relatude.db.json

One server, N containers (databases), each with its own IO providers, file stores and datamodel sources:

```jsonc
{
  "MasterUserName": "admin",              // lowercase — see the auth section
  "MasterPassword": "…",                  // plain text; prefer setting these from config, see below
  "TokenEncryptionSecret": "…",           // without it, logins do not survive a restart
  "Name": "Relatude.DB Server",
  "DefaultStoreId": "8f6b…",              // which container RelatudeDBContext.Database resolves to
  "DBAdminUIUrlPath": "/relatude.db",     // overrides the argument passed to UseRelatudeDB()

  "ContainerSettings": [
    {
      "Id": "8f6b…",
      "Name": "MyDatabase",
      "AutoOpen": true,                    // open at startup
      "WaitUntilOpen": false,              // true blocks startup until the store is open

      "IOSettings": [                      // the storage backends this container may use
        {
          "Id": "1a2b…",
          "Name": "Local disk",
          "IOType": "LocalDisk",           // Memory | LocalDisk | AzureBlobStorage
          "Path": "relatude.db"            // "~" is the root data folder; must stay under it
        }
      ],

      "IoDatabase": "1a2b…",               // transaction log — the source of truth
      "IoIndexes": null,                   // persisted index files; falls back to IoDatabase
      "IoBackup": "1a2b…",
      "IoLog": "1a2b…",
      "IoDatabaseSecondary": null,

      "AISettings": {                      // optional; required for semantic/vector search; per database
        "Name": "OpenAI",
        "TypeName": "…",
        "ApiKey": "…",
        "EmbeddingModel": "text-embedding-3-small",
        "ModelDimensions": 1536,
        "DefaultSemanticRatio": 0.5        // the vector index engine is chosen in LocalSettings, see below
      },

      "FileStoreSettings": [               // where FileValue bytes live
        {
          "Id": "…",
          "IoProviderId": "1a2b…",
          "StoreType": "MultiFile",        // SingleFile | MultiFile
          "MultiFileFolderDepth": 2
        }
      ],

      "DatamodelSources": [ /* see below */ ],
      "LocalSettings": { /* see below */ }
    }
  ]
}
```

An `IOSettings` entry is a *storage backend*; the `Io…` fields on the container point at one by id. That indirection is what lets the log, the indexes and the file bytes live in different places — local disk for indexes, Azure blob for files — without repeating connection details.

For `AzureBlobStorage`, the entry carries `BlobConnectionString`, `BlobContainerName` and `LockBlob` instead of `Path`. For `LocalDisk`, a path starting with `~` is rooted at the root data folder, and a path that escapes it is rejected with "Path not under root".

## Server level

| Field | Meaning |
|---|---|
| `MasterUserName` / `MasterPassword` | The single admin-UI login. Plain text. |
| `TokenEncryptionSecret` | Key for the auth cookie. Unset → a random per-process key, so every restart logs everyone out. |
| `TokenCookieName`, `TokenCookieMaxAgeInSec`, `TokenCookieSecure`, `TokenCookieSameSite`, `TokenLockedToIP` | Cookie behaviour. Defaults are secure (`Secure`, `SameSite=Strict`, 10 days). |
| `Id` | Server id. Also the HMAC key for signed file URLs. |
| `Name`, `Description` | Cosmetic. |
| `DefaultStoreId` | Which container `RelatudeDBContext.Database` resolves to. |
| `DBAdminUIUrlPath` | Admin UI path. **Applied after** `UseRelatudeDB("/x")`, so the file wins. |
| `ContainerSettings` | The databases. |
| `DBSettingsFilePath` | Declared but never read in the current build. Ignore it. |

## Container level

`AutoOpen` and `WaitUntilOpen` are the two that change startup behaviour:

- `AutoOpen: false` — the store is registered but not opened; open it from the admin UI.
- `WaitUntilOpen: true` — startup blocks until the store is open, and an open failure throws out of startup. With `false` (the default) the store opens on a thread-pool thread and requests arriving meanwhile get a **503 with a progress page**, which is what `RelatudeDBRuntime.IsReady` guards against in your own middleware.

## LocalSettings: the engine knobs

`SettingsLocal` is the per-store engine configuration. Every field has a working default; these are the ones worth knowing.

**Model-wide defaults** — they decide what indexing a property gets when its attribute says nothing:

| Field | Default | Effect |
|---|---|---|
| `EnableTextIndexByDefault` | `false` | BM25 keyword indexing for types that do not opt in |
| `EnableSemanticIndexByDefault` | `false` | Vector indexing likewise |
| `EnableInstantTextIndexingByDefault` | `false` | Text index written in the transaction instead of by the background queue |
| `DefaultCultureCode` | `null` | Culture for the empty culture id |
| `DefaultReadAccess` / `DefaultWriteAccess` | `Everyone` | ACL default |
| `DefaultFileStore` | unset | Which `FileStoreSettings` entry `FileValue` bytes go to; unset = an implicit `MultiFile` store on `IoDatabase` |

**Index engines** — each index kind has a list of engines it may run on and a default id that picks one; the empty guid is the memory index (resident, saved with the state snapshot, otherwise rebuilt from the log at every open):

```jsonc
"LocalSettings": {
  "ValueIndexes":  [ { "Id": "4d1f…", "TypeName": "Native", "MaxMemoryUsageInMb": 256 } ],
  "TextIndexes":   [ { "Id": "9b2c…", "TypeName": "Native", "MaxMemoryUsageInMb": 256 } ],
  "VectorIndexes": [ { "Id": "e7a0…", "TypeName": "HNSW",   "MaxMemoryUsageInMb": 512 } ],
  "DefaultValueIndex":  "4d1f…",
  "DefaultTextIndex":   "9b2c…",
  "DefaultVectorIndex": "00000000-0000-0000-0000-000000000000"
}
```

| Field | Values | Default |
|---|---|---|
| `ValueIndexes[].TypeName` | `Native`, `Sqlite`, or a custom `IValueIndexEngine` type name | — |
| `TextIndexes[].TypeName` | `Native`, `Sqlite`, `Lucene`, or a custom `ITextIndexEngine` type name | — |
| `VectorIndexes[].TypeName` | `IVS`, `HNSW`, or a custom `ISemanticIndexEngine` type name | — |
| `…[].MaxMemoryUsageInMb` | int; `0` = as little as the engine can | `256` |
| `DefaultValueIndex` / `DefaultTextIndex` / `DefaultVectorIndex` | an engine `Id`, or the empty guid for memory | empty (memory) |
| `PersistedQueueStoreEngine` | `Memory`, `Native`, `Sqlite` | `Native` |
| `PersistedValueIndexFolderPath` | path | beside the data |

Only the engines the defaults point at are created; the other entries wait to be chosen. Each engine writes to its own folder below the index folder, named by its `Id`, so two `Native` engines with different budgets can coexist. `Sqlite` in both the value and text default means one database serving both, committing every index change in one transaction. A property that asks for `IndexStorageType.Persisted` while its kind defaults to memory stays in memory — the store logs a note at open. A fresh `relatude.db.json` (from the server or `relatude init`) seeds one `Native` value engine and one `Native` text engine; a plain `new SettingsLocal()` in code has no engines and keeps everything in memory. Files from before this shape (`PersistedValueIndexEngine`, `UsePersisted…ByDefault`, `AISettings.IndexType`/`IndexCacheSizeInMb`) are migrated once by the loader, and the old per-type engine folders under `indexes/` are deleted at the first open, so those indexes rebuild from the log once.

The memory budget is per engine and applies to what the engine can bound: the Native value engine's page cache and write buffers, the Native text index's decoded-block cache, IVS's cluster cache, HNSW's float mirror (the graph is always resident — a budget below it is exceeded with a log warning), SQLite's page cache, Lucene's writer buffer. Changing it never invalidates an engine's files.

**Durability and flushing** — the defaults favour throughput; raise them for stricter durability:

`FlushDiskOnEveryTransactionByDefault` (`false`), `ForceDiskFlushAfterActionCountLimit` (10 000), `AutoFlushDiskInBackground` (`true`), `AutoFlushDiskIntervalInSeconds` (1), `DeepFlushDisk` (`false`), `DelayAutoDiskFlushIfBusy` (`true`).

**Housekeeping**: `AutoSaveIndexStates` + interval and action-count bounds; `AutoBackUp` with `NoHourly/Daily/Weekly/Montly/YearlyBackUps` retention (note the spelling of `NoMontlyBackUps`); `AutoTruncate` for log compaction; `AutoPurgeCache`; `NodeCacheSizeGb` / `SetCacheSizeGb` (1 GB each).

**Diagnostics**: `WriteSystemLogConsole` (`true`), `ThrowOnBadLogFile` / `ThrowOnBadStateFile` (`false` — a corrupt state file is rebuilt from the log rather than thrown).

`FilePrefix` prefixes every file this store owns, which is what lets two containers share one folder.

## Overriding settings from configuration

Any setting in `relatude.db.json` can be overridden from standard ASP.NET configuration. At startup the server merges the `RelatudeDB` section — from `appsettings.json`, `appsettings.{Environment}.json`, environment variables, user secrets, Key Vault, anything the host reads — over the loaded file. The section has the same shape as `relatude.db.json`.

```jsonc
// appsettings.Development.json
{
  "RelatudeDB": {
    "MasterUserName": "admin",
    "ContainerSettings": [ { "LocalSettings": { "AutoBackUp": false } } ]
  }
}
```

Environment variable form: `RelatudeDB__MasterPassword=…`. User secrets: `dotnet user-secrets set RelatudeDB:TokenEncryptionSecret …`. **This is the intended home for credentials** — user secrets in development, environment variables or a vault in production, and `relatude.db.json` never holds them.

Merge rules:

- Objects merge key by key; scalars replace. Keys are case-insensitive; values are coerced to the setting's type (`"true"`, `"2.5"`, enum names).
- Array elements match on `Id` when the overlay element gives one, on position otherwise; unmatched elements are appended. Overlays can change and add but not remove, and cannot set null (a JSON null is invisible to `IConfiguration`).
- Unrecognized keys, read-only keys and unparsable values are warned about at startup and skipped — never a crash, never silent.
- The startup log lists every overridden key path (paths only, never values). Overriding `Id` or `DefaultStoreId` re-identifies an object and draws an explicit warning.

**Overridden values are never written back to the file.** Before the admin UI's wholesale save, the server restores every overridden key to the file's own value (`SettingsOverlay.RemoveOverridesBeforeSave`), so configuration-supplied secrets do not leak into `relatude.db.json`. Consequence: while a key is overridden, editing it in the admin UI has no lasting effect — configuration wins on the next load.

The overlay applies after `SettingsLoader.ReadAsync()` and before `OnServerSettingsInit`, so callbacks see merged settings, and it composes with a custom `SettingsLoader`. `ServerOptions.ConfigurationSectionName` renames the section; `null` disables it.

## ServerOptions

Everything configurable from `Program.cs`:

```csharp
builder.AddRelatudeDB(options => {

    // File conversion — nothing converts images or video without these
    options.FileConverters.Add(new SkiaImageConverter(1));
    options.FileConverters.Add(new FFMpegVideoConverter());

    // Folders
    options.DefaultDataFolderPath = "data";
    options.DefaultTempFolderPath = "data/tmp";

    // Replace the JSON file entirely (database-backed settings, key vault, tests…)
    options.SettingsLoader = new MySettingsLoader();

    // The configuration section merged over the settings ("RelatudeDB"; null disables)
    options.ConfigurationSectionName = "RelatudeDB";

    // Lifecycle callbacks — see the order below
    options.OnServerSettingsInit    = settings => { };
    options.OnContainerSettingsInit = container => { };
    options.OnStoreSettingsInit     = (local, container) => { };
    options.OnDatamodelInit         = (dm, container) => { };
    options.OnStoreInit             = db => { };
    options.OnStoreOpen             = db => { };
    options.OnStoreOpenBackground   = db => { };
    options.OnStoreClose            = db => { };
});
```

`ISettingsLoader` is two methods — `Task<RelatudeDBServerSettings> ReadAsync()` and `Task WriteAsync(settings)` — so replacing the file with configuration, a database or a secret store is a small class. The default is `LocalSettingsLoaderFile`.

**Every callback is wrapped in a try/catch by the server.** An exception inside one is written to the startup log and swallowed; it does not stop the server. So a callback that quietly fails looks like "my code never ran" — check the startup log in the admin UI rather than expecting a crash.

## The startup event order

```
AddRelatudeDB(options)                     ← options captured
        ↓
StartRelatudeDB() / UseRelatudeDB()
        ↓
temp folder emptied
        ↓
SettingsLoader.ReadAsync()                 ← relatude.db.json read (created if missing)
        ↓
RelatudeDB configuration section merged    ← appsettings, environment variables, user secrets
        ↓
OnServerSettingsInit(serverSettings)       ← last chance to change credentials, container list
        ↓
   for each container:
        OnContainerSettingsInit(container)  ← IO providers, file stores, datamodel sources
        OnStoreSettingsInit(local, container) ← engine knobs
        ↓
   for each container with AutoOpen:
        loadDatamodel()                     ← DatamodelSources from the JSON are applied
        OnDatamodelInit(datamodel, container) ← add types from code here
        ↓
        store constructed
        OnStoreInit(store)                  ← register transaction plugins, task runners
        ↓
        store opens
        OnStoreOpen(store)                  ← blocking
        OnStoreOpenBackground(store)        ← thread pool; seeding belongs here
        ↓
app.Lifetime.ApplicationStopping → Shutdown() → OnStoreClose(store)
```

Two consequences worth carrying:

- **Secrets belong in the `RelatudeDB` configuration section**, not in `OnServerSettingsInit`. Both run before anything uses the settings, but only section-supplied values are stripped again before saves — what a callback sets is written into `relatude.db.json` the next time the admin UI saves settings.

- **`OnStoreOpenBackground` is where seeding goes.** `OnStoreOpen` blocks the open, so heavy work there delays every request behind the startup progress page.

  ```csharp
  options.OnStoreOpenBackground = db => ShopSeeder.SeedIfEmpty(db, 1000, 1000);
  ```

## Registering a datamodel

Two ways, and they **compose** — the JSON sources are loaded first, then `OnDatamodelInit` runs against the same `Datamodel` object, so code adds to what the file declared.

### In relatude.db.json

```jsonc
"DatamodelSources": [
  {
    "Id": "…",
    "Name": "VenueApp",
    "Type": "TypeReference",
    "Namespace": "VenueApp.Models",   // or a pattern: "VenueApp.Models.*" takes it and every namespace under it
    "Reference": "VenueApp"           // assembly name; null means the current project (the entry assembly)
  }
]
```

| `Type` | What it does | Status |
|---|---|---|
| `TypeReference` | `Assembly.Load(Reference)` — or the entry assembly when `Reference` is null — then adds every type in `Namespace`. Called `AssemblyNameReference` before September 2026 (still reads); the single-type `TypeNameReference` kind was removed then | **works** |
| `TextFiles` | Model files on disk read at open, as `FileFormat` says: `Json` (default) reads a serialised `Datamodel` from `Filepath` (file or folder), or from `Reference` via the IO provider named by `FileIO`; `CSharpCode` compiles the `.cs` file(s) at `Filepath` and adds their types. The old kinds `JsonFile` and `CSharpCodeFile` still read and set the format | **works** |

`SourceCodePath` (TypeReference only) names the folder with the C# files so the admin UI model editor can write to them; `GenerateModelFile` on top hands that folder to the editor, which deletes and regenerates every file in it at activation (asking first when a file lacks the `// <auto-generated>` marker). A failing source throws `Failed to load datamodel source {id}` and the container does not open.

`Namespace` matching is **exact, not by prefix** — types in `VenueApp.Models.Sub` are not picked up by a source naming `VenueApp.Models`. Add a second source per namespace.

### In code

```csharp
builder.AddRelatudeDB(options => {
    options.OnDatamodelInit = (dm, container) => {
        dm.AddNamespace<IVenue>();          // every node & relation type in IVenue's namespace
        dm.Add<IEvent>();                   // one type, plus everything it references
        dm.Add(typeof(IAttendee));
        dm.AddAssembly(typeof(IVenue).Assembly, "VenueApp.Models.Sub");
    };
});
```

With more than one container, branch on the second argument:

```csharp
options.OnDatamodelInit = (dm, container) => {
    if (container.Name == "Catalog") dm.AddNamespace<IProduct>();
    else dm.AddNamespace<IAuditEntry>();
};
```

### Which to use

- **Code** when the model ships with the app, which is the common case. It is refactor-safe, it fails at compile time rather than at boot, and there is no file to keep in sync with a rename.
- **JSON** when the model must change without a rebuild, when different deployments of the same binary carry different models, or when you want the admin UI to manage sources.

Either way the built model is the same, and the admin UI's datamodel browser is how you confirm what actually landed.

### AutoDeduceRelations

A static, process-wide flag: `DatamodelSource.AutoDeduceRelations`, off by default, set before the database opens. When **off**, a plain node-typed property with no relation declaration becomes a `Reference`/`References` property. When **on**, such properties are turned into auto-created relations instead — the old behaviour. Turn it on only to keep an existing model working; new models should declare relations explicitly. Until September 2026 it was a per-source key in `relatude.db.json`; that key is now ignored.

### Other rules

- `Add<T>(includeAllReferencedModels: true)` is the default, so adding one type pulls in the types it points at.
- Static classes, enums and anything marked `[Exclude]` are skipped.
- A non-interface node type **must** have a parameterless constructor, or `Add` throws naming the type.
- Two types hashing to the same id throw `Different types have same Id` — that is the signal to pin `[Node(Id = …)]`.
- Adding to an initialised datamodel throws. `OnDatamodelInit` is the window.

## Admin UI authentication

**There is no auto-created admin user, and no credentials are printed anywhere.** `MasterUserName` and `MasterPassword` are `null` until you set them, and logging in without them throws "No master user configured on the server." Set them in `relatude.db.json`, or — better — in the `RelatudeDB` configuration section (user secrets in development, environment variables in production).

Three details that cost people time:

- **The stored user name must be lowercase.** The check is `username.ToLower() != settings.MasterUserName`, so a stored `"Admin"` can never match any input.
- **The password is compared verbatim, in plain text**, and stored that way in the file. Keep the file out of source control (`.gitignore` already lists it) and prefer injecting the credentials at startup.
- **Set `TokenEncryptionSecret`.** Without it the server generates a random key per process, so every restart invalidates every session cookie.

Failed logins are rate limited per IP (30 per minute). Only the admin UI and its API are protected — `requireAuthentication` returns false for every URL outside the admin root, so your own endpoints are unaffected.

## Pitfalls

- **A missing `relatude.db.json` is created with the demo model**, not yours. A store full of `Relatude.DB.Demo.Models` types means the file was never configured.
- **The admin UI writes the whole file back** through `WriteAsync` whenever settings change, so hand-written comments and formatting are lost the first time anything is saved from the UI.
- **`DBAdminUIUrlPath` in the file overrides `UseRelatudeDB("/path")` in code**, because it is applied after the settings load.
- **Datamodel namespace matching is exact**, not prefix-based.
- **Three of the six `DatamodelSourceType` values throw `NotImplementedException`.**
- **Lifecycle callbacks never crash the server** — exceptions are logged to the startup log and swallowed.
- **Seeding in `OnStoreOpen` blocks the store from opening.** Use `OnStoreOpenBackground`.
- **`WaitUntilOpen: false` means requests can arrive before the store is open**, answered with a 503 progress page. Gate your own middleware on `RelatudeDBRuntime.IsReady`.
- **A `Default…Index` of the empty guid keeps that whole index kind in memory, whatever engines are listed** — and a property asking for `IndexStorageType.Persisted` then stays in memory too. The startup log says so; nothing else will. A `Default…Index` naming an id that is not in its list stops the database from opening, by name.
- **File converters are code-only.** No JSON setting adds them.
- **A key overridden from configuration cannot be changed in the admin UI** — the edit is stripped before the save and configuration wins on the next load. The startup log lists the overridden paths.
- **Configuration overlays cannot remove array elements or set a value to null.** Change the file itself for that.

## Where to look in the source

| Topic | Source path |
|---|---|
| `ServerOptions`, startup sequence, event raising | `src/Relatude.DB.NodeServer/NodeServer/RelatudeDBServer.cs` |
| Server / container settings shape | `src/Relatude.DB.NodeServer/NodeServer/Settings/` |
| Engine knobs | `src/Relatude.DB.DataStoreLocal/DataStores/SettingsLocal.cs` |
| Reading and writing the JSON file | `src/Relatude.DB.NodeServer/NodeServer/ISettingsLoader.cs` |
| Configuration section overlay, strip-on-write | `src/Relatude.DB.NodeServer/NodeServer/Settings/SettingsOverlay.cs` |
| Defaults (file names, folders, admin path) | `src/Relatude.DB.NodeServer/Defaults.cs` |
| Datamodel sources and how each is loaded | `src/Relatude.DB.NodeServer/NodeServer/DatamodelSource.cs`, `NodeStoreContainer.cs` |
| Adding types from code | `src/Relatude.DB.NodeStore/Datamodels/DatamodelExtensions.cs` |
| IO providers | `src/Relatude.DB.NodeServer/NodeServer/IOSettings.cs` |
| Admin authentication | `src/Relatude.DB.NodeServer/NodeServer/SimpleAuthentication.cs` |
| `AddRelatudeDB` / `UseRelatudeDB` | `src/Relatude.DB.NodeServer/AddUse.cs` |
