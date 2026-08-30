namespace Relatude.DB.NodeServer.Settings;

/// <summary>When a changed setting starts to matter.</summary>
public enum SettingApplies {
    /// <summary>Read every time it is used, so a save is enough.</summary>
    Live,
    /// <summary>Read while the database opens: it takes a close and open to apply.</summary>
    Reopen,
    /// <summary>Read while the host starts: it takes a process restart to apply.</summary>
    Restart,
}

/// <summary>
/// One editable setting. <see cref="Path"/> is the property path into the settings object the group
/// belongs to (<see cref="RelatudeDBServerSettings"/> for the server groups,
/// <see cref="NodeStoreContainerSettings"/> for the database groups), so the editor type, the enum
/// members and the default value are all read off the property itself - only what reflection cannot
/// know is written here.
/// </summary>
public sealed class SettingDefinition {
    public required string Path { get; init; }
    public required string Label { get; init; }
    /// <summary>The inline explanation shown under the field. What the setting does, and what
    /// choosing the other value costs - not a restatement of the label.</summary>
    public required string Help { get; init; }
    public SettingApplies Applies { get; init; } = SettingApplies.Reopen;
    /// <summary>Only meaningful inside a <see cref="SettingListDefinition"/>: hides the field unless a
    /// sibling holds one of the given values.</summary>
    public SettingVisibility? VisibleWhen { get; init; }
    /// <summary>Fills the field from a runtime list instead of free text: "databases",
    /// "ioProviders", "fileStores" or "cultures". See <c>UISettings.buildPickers</c>.</summary>
    public string? Picker { get; init; }
    /// <summary>Shown after the input, e.g. "GB", "minutes".</summary>
    public string? Unit { get; init; }
    /// <summary>Never sent to the browser in clear text, and only written when a new value is typed.</summary>
    public bool Secret { get; init; }
    /// <summary>Shown, but not editable from here.</summary>
    public bool ReadOnly { get; init; }
    public string? Placeholder { get; init; }
}

/// <summary>Hides a field until a sibling field holds one of these values, so an element only shows
/// the settings its own type actually uses.</summary>
public sealed class SettingVisibility {
    /// <summary>The sibling field, by the same relative path the fields use.</summary>
    public required string Path { get; init; }
    public required string[] Values { get; init; }
}

/// <summary>
/// Turns a group into an editor for a collection: the elements of <see cref="Path"/> are listed, each
/// showing <see cref="Fields"/>, and elements can be added and removed. Field paths are relative to
/// one element; the server turns them into full paths ("IOSettings[8f1c...].Path"), so an element's
/// fields save, mark defaults and report configuration overrides exactly like any other setting.
/// </summary>
public sealed class SettingListDefinition {
    /// <summary>The collection property on the settings object, e.g. "IOSettings".</summary>
    public required string Path { get; init; }
    /// <summary>What one element is called, used in the add button and in messages: "storage provider".</summary>
    public required string ItemName { get; init; }
    /// <summary>The field whose value names an element in the list header.</summary>
    public required string LabelField { get; init; }
    /// <summary>Shown under the list when it is empty.</summary>
    public required string EmptyHelp { get; init; }
    /// <summary>What a newly added element starts as, by field path. Values are written through the
    /// same conversion as any edit, so enums are named and numbers may be strings. Without this a new
    /// element holds its type's zero values, which for an enum is whatever happens to be first - not
    /// a choice anyone made.</summary>
    public Dictionary<string, string>? NewItem { get; init; }
    public required SettingDefinition[] Fields { get; init; }
}

public sealed class SettingGroupDefinition {
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>Explains the group as a whole, above its fields.</summary>
    public string? Help { get; init; }
    public SettingDefinition[] Settings { get; init; } = [];
    /// <summary>When set, the group edits a collection rather than a fixed set of settings.</summary>
    public SettingListDefinition? List { get; init; }
}

/// <summary>
/// A top level entry in the settings navigation, holding the groups that belong together. Sections
/// exist for the reader, not for the settings: nothing resolves through one, so regrouping is free.
/// </summary>
public sealed class SettingSectionDefinition {
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>A name the admin UI maps to a glyph, so the choice of icon set stays in the UI.
    /// An unknown name falls back to a neutral icon rather than to nothing.</summary>
    public required string Icon { get; init; }
    public required SettingGroupDefinition[] Groups { get; init; }
}

/// <summary>
/// What the settings sections of the admin UI show: which settings are editable, how they are
/// grouped into sections and groups for the navigation, and what each one does. The value, the
/// default, the editor and the enum members are derived from the settings classes themselves (see
/// <see cref="SettingsAccessor"/>), so this file only carries what the types cannot say. A settings property that is missing here is simply not
/// editable from the UI - <c>SettingsCatalogTests</c> lists those, so new ones are noticed.
/// </summary>
public static class SettingsCatalog {

    public static SettingSectionDefinition[] Server { get; } = [
        new() {
            Id = "general", Title = "General", Icon = "server",
            Groups = [
                new() {
                    Id = "identity",
                    Title = "Identity",
                    Help = "Names this server and picks the database that answers requests that do not name one.",
                    Settings = [
                        new() {
                            Path = "Name", Label = "Server name", Applies = SettingApplies.Live,
                            Help = "Shown in the admin UI and in log lines. Purely a label - nothing resolves by it.",
                        },
                        new() {
                            Path = "Description", Label = "Description", Applies = SettingApplies.Live,
                            Help = "Free text for whoever opens this admin UI next. Not used by the server.",
                        },
                        new() {
                            Path = "DefaultStoreId", Label = "Default database", Picker = "databases", Applies = SettingApplies.Live,
                            Help = "The database used when application code asks for a store without naming one. Every other database stays reachable by id.",
                        },
                        new() {
                            Path = "Id", Label = "Server id", ReadOnly = true,
                            Help = "Identifies this server when several share storage. Generated once; changing it would make the others treat this server as a new one.",
                        },
                    ],
                },
                new() {
                    Id = "hosting",
                    Title = "Hosting",
                    Help = "Where the admin UI lives and where the settings file is read from. Both are read once, while the routes are mapped.",
                    Settings = [
                        new() {
                            Path = "DBAdminUIUrlPath", Label = "Admin UI path", Applies = SettingApplies.Restart, Placeholder = "/relatude.db",
                            Help = "The URL prefix serving this admin UI and its API. Moving it off the default is mild obscurity, not access control - the login still guards it.",
                        },
                        new() {
                            Path = "DBSettingsFilePath", Label = "Settings file", Applies = SettingApplies.Restart, Placeholder = "relatude.db.json",
                            Help = "The JSON file these settings are read from and written back to, relative to the data folder. Changing it here points the next start at a different file - it does not move the current one.",
                        },
                    ],
                },
            ],
        },
        new() {
            Id = "security", Title = "Security", Icon = "security",
            Groups = [
                new() {
                    Id = "master-login",
                    Title = "Master login",
                    Help = "The fallback account for this admin UI. It exists outside any database, so it still works when every database is closed or broken.",
                    Settings = [
                        new() {
                            Path = "MasterUserName", Label = "Master user name", Applies = SettingApplies.Live,
                            Help = "Leave both this and the password empty to disable the master login entirely and rely on database users only.",
                        },
                        new() {
                            Path = "MasterPassword", Label = "Master password", Secret = true, Applies = SettingApplies.Live,
                            Help = "Stored as written. Keep it in appsettings, an environment variable or user secrets rather than in the settings file - values that come from configuration are never written to disk.",
                        },
                        new() {
                            Path = "AllowMasterLoginOutsideLocalhost", Label = "Allow master login remotely", Applies = SettingApplies.Live,
                            Help = "Off means the master account only works from the machine itself, so a leaked master password cannot be used from the internet. Turn it on only when you administer this server remotely.",
                        },
                        new() {
                            Path = "NoLoginRequiredForLocalhost", Label = "Skip login on localhost", Applies = SettingApplies.Live,
                            Help = "Opens the admin UI without a login for browsers on this machine. The check is deliberately narrow, since a reverse proxy makes every request look local - but on a shared machine every local process gets in.",
                        },
                    ],
                },
                new() {
                    Id = "tokens",
                    Title = "Session cookie",
                    Help = "How an admin session is carried between requests once someone has logged in.",
                    Settings = [
                        new() {
                            Path = "TokenEncryptionSecret", Label = "Token secret", Secret = true, Applies = SettingApplies.Restart,
                            Help = "Signs and encrypts session tokens. It has no default: set a long random value, unique per installation, or tokens minted by one server are readable by another. Changing it logs everyone out.",
                        },
                        new() {
                            Path = "TokenCookieName", Label = "Cookie name", Applies = SettingApplies.Live,
                            Help = "Only worth changing when two Relatude.DB servers share one host name and would otherwise overwrite each other's cookie.",
                        },
                        new() {
                            Path = "TokenCookieMaxAgeInSec", Label = "Session lifetime", Unit = "seconds", Applies = SettingApplies.Live,
                            Help = "How long a login lasts before the admin UI asks again. Shorter limits the damage of a stolen cookie; longer is friendlier on a trusted machine.",
                        },
                        new() {
                            Path = "TokenLockedToIP", Label = "Lock token to IP address", Applies = SettingApplies.Live,
                            Help = "Binds a session to the address it was issued to, so a copied cookie is useless elsewhere. It also logs people out whenever their address changes - mobile networks, VPNs, load-balanced clients.",
                        },
                        new() {
                            Path = "TokenCookieSecure", Label = "Secure cookie", Applies = SettingApplies.Live,
                            Help = "Sends the cookie over HTTPS only. Leave it on; turning it off is for plain-HTTP development, and it lets the session travel in clear text.",
                        },
                        new() {
                            Path = "TokenCookieSameSite", Label = "SameSite cookie", Applies = SettingApplies.Live,
                            Help = "Stops the browser from sending the session cookie on requests started by other sites, which is what blocks cross-site request forgery. Turn it off only if the admin UI is embedded in a page on another domain.",
                        },
                    ],
                },
            ],
        },
    ];

    public static SettingSectionDefinition[] Database { get; } = [
        new() {
            Id = "general", Title = "General", Icon = "database",
            Groups = [
                new() {
                    Id = "database",
                    Title = "Database",
                    Help = "Identity of this database and how it behaves while the server starts.",
                    Settings = [
                        new() {
                            Path = "Name", Label = "Name", Applies = SettingApplies.Live,
                            Help = "Shown in the admin UI and used in log lines. Application code addresses the database by id, so renaming is safe.",
                        },
                        new() {
                            Path = "Description", Label = "Description", Applies = SettingApplies.Live,
                            Help = "Free text describing what this database holds. Not used by the server.",
                        },
                        new() {
                            Path = "Id", Label = "Database id", ReadOnly = true,
                            Help = "How application code and the settings file address this database. It is part of the stored data's identity, so it cannot be changed here.",
                        },
                        new() {
                            Path = "AutoOpen", Label = "Open on start-up", Applies = SettingApplies.Restart,
                            Help = "Opens the database as the server starts. Off leaves it closed until someone opens it from the admin UI - useful for a database that is large, broken, or only needed occasionally.",
                        },
                        new() {
                            Path = "WaitUntilOpen", Label = "Hold requests until open", Applies = SettingApplies.Restart,
                            Help = "Makes incoming requests wait behind the opening database instead of failing. Replaying a large log can take a while, so the first requests after a start are slow rather than broken.",
                        },
                    ],
                },
            ],
        },
        new() {
            Id = "storage", Title = "Storage", Icon = "storage",
            Groups = [
                new() {
                    Id = "providers",
                    Title = "Storage providers",
                    Help = "The places this database may keep files. A provider is only a place; what actually goes there is decided by the assignments below. Providers are defined per database, but their ids are resolved across the whole server, so one that another database points at cannot be removed here.",
                    List = new() {
                        Path = "IOSettings",
                        ItemName = "storage provider",
                        LabelField = "Name",
                        EmptyHelp = "This database has nowhere to put anything. Add a provider, then point the assignments below at it.",
                        // local disk, because a new provider that quietly threw everything away would
                        // be the worst of the three to land on by accident
                        NewItem = new() { ["Name"] = "New provider", ["IOType"] = "LocalDisk" },
                        Fields = [
                            new() {
                                Path = "Name", Label = "Name",
                                Help = "What this provider is called in the assignments below and in the Files and Storage sections. A location, usually: \"Local disk\", \"Archive blob container\".",
                            },
                            new() {
                                Path = "IOType", Label = "Type",
                                Help = "Local disk keeps files in a folder under the application. Azure Blob Storage keeps them in a container, which is what survives a redeployed container image. Memory keeps nothing: everything written to it is gone when the process stops, which is what makes it right for tests and wrong for anything else.",
                            },
                            new() {
                                Path = "Path", Label = "Folder", Placeholder = "~/relatude.db",
                                VisibleWhen = new() { Path = "IOType", Values = ["LocalDisk"] },
                                Help = "The folder on disk, relative to the application root unless it is rooted. It has to stay under the application root - a path that escapes it is refused when the provider is created, not when it is saved.",
                            },
                            new() {
                                Path = "BlobConnectionString", Label = "Connection string", Secret = true,
                                VisibleWhen = new() { Path = "IOType", Values = ["AzureBlobStorage"] },
                                Help = "The storage account connection string. Keep it in appsettings, an environment variable or user secrets rather than in the settings file - values that come from configuration are never written to disk.",
                            },
                            new() {
                                Path = "BlobContainerName", Label = "Container",
                                VisibleWhen = new() { Path = "IOType", Values = ["AzureBlobStorage"] },
                                Help = "The blob container these files live in. It is created if it does not exist.",
                            },
                            new() {
                                Path = "LockBlob", Label = "Lease the blobs",
                                VisibleWhen = new() { Path = "IOType", Values = ["AzureBlobStorage"] },
                                Help = "Takes a lease on the database blobs, so a second instance cannot open the same database and corrupt it. Turn it off only when you are certain one process at a time will write, since a lease left behind by a crashed instance blocks the next start until it expires.",
                            },
                            new() {
                                Path = "Id", Label = "Provider id", ReadOnly = true,
                                Help = "How the assignments and the settings file refer to this provider. Generated when it is added.",
                            },
                        ],
                    },
                },
                new() {
                    Id = "storage",
                    Title = "Storage assignments",
                    Help = "Which of the providers above each kind of file goes to. Changing an assignment does not move anything: the files already written stay where they are, and only what is written from then on goes to the new provider.",
                    Settings = [
                        new() {
                            Path = "IoDatabase", Label = "Database files", Picker = "ioProviders",
                            Help = "Holds the transaction log and the state snapshots - the database itself. Every other assignment falls back to this one when left empty.",
                        },
                        new() {
                            Path = "IoDatabaseSecondary", Label = "Secondary log copy", Picker = "ioProviders",
                            Help = "A second provider the transaction log is mirrored to, so the data survives losing the primary storage. Every write goes to both, which costs write latency.",
                        },
                        new() {
                            Path = "IoIndexes", Label = "Index files", Picker = "ioProviders",
                            Help = "Where index snapshots are written. Pointing this at fast local disk while the database lives on network storage is the usual reason to set it - indexes are rebuilt from the log if lost.",
                        },
                        new() {
                            Path = "IoBackup", Label = "Backup files", Picker = "ioProviders",
                            Help = "Where automatic and manual backups are written. A provider on different hardware than the database is what makes a backup worth having.",
                        },
                        new() {
                            Path = "IoLog", Label = "Activity log files", Picker = "ioProviders",
                            Help = "Where the activity log (queries, transactions, errors shown under Logs) is stored. Separate from the transaction log, which is data, not diagnostics.",
                        },
                    ],
                },
                new() {
                    Id = "file-stores",
                    Title = "File stores",
                    Help = "Where uploaded files are kept. Each store sits on one of the providers above and decides how the files are laid out inside it. With none configured the database uses an implicit store on its own provider, which is enough for most installations - add stores here to split files across providers, or to move new uploads somewhere else without touching the ones already stored.",
                    List = new() {
                        Path = "FileStoreSettings",
                        ItemName = "file store",
                        LabelField = "IoProviderId",
                        EmptyHelp = "No file store is configured, so uploads go to an implicit one on this database's own storage provider.",
                        // the same layout the database would have used on its own
                        NewItem = new() { ["StoreType"] = "MultiFile" },
                        Fields = [
                            new() {
                                Path = "IoProviderId", Label = "Provider", Picker = "ioProviders",
                                Help = "Which of the storage providers above holds this store's files. Changing it on a store that already holds files does not move them - the files stay where they were written and stop resolving.",
                            },
                            new() {
                                Path = "StoreType", Label = "Layout",
                                Help = "MultiFile writes one file per stored file, which is easy to inspect and to back up piecemeal. SingleFile packs everything into one container, which is faster with very many small files and kinder to file-count limits. The layout is how existing files are read, so changing it on a store that holds files makes them unreadable.",
                            },
                            new() {
                                Path = "MultiFileFolderDepth", Label = "Folder depth",
                                VisibleWhen = new() { Path = "StoreType", Values = ["MultiFile"] },
                                Help = "How many nested folders the files are spread over. Deeper keeps any single folder small, which matters on file systems that slow down with very many entries in one directory. Empty uses the built-in depth.",
                            },
                            new() {
                                Path = "Id", Label = "Store id", ReadOnly = true,
                                Help = "Stored on every file uploaded into this store, which is how a file value finds its bytes again. Generated when the store is added, and never reused.",
                            },
                        ],
                    },
                },
            ],
        },
        new() {
            Id = "content", Title = "Content", Icon = "content",
            Groups = [
                new() {
                    Id = "content",
                    Title = "Content defaults",
                    Help = "The values used when a node, a file or a query does not state one of its own.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.DefaultCultureCode", Label = "Default culture", Picker = "cultures", Placeholder = "invariant",
                            Help = "The culture localized properties fall back to when a query names none. Empty means the invariant culture. It also decides how text is folded for search, so changing it on a database with content means re-indexing to get matching behavior.",
                        },
                        new() {
                            Path = "LocalSettings.DefaultReadAccess", Label = "Default read access",
                            Help = "Who may read a node type that does not declare its own read access. Everyone includes anonymous callers; Member requires a signed-in user; Admins locks it to administrators.",
                        },
                        new() {
                            Path = "LocalSettings.DefaultWriteAccess", Label = "Default write access",
                            Help = "Who may create, change or delete a node type that does not declare its own write access. Leaving this at Everyone means unauthenticated callers can write.",
                        },
                        new() {
                            Path = "LocalSettings.DefaultFileStore", Label = "Default file store", Picker = "fileStores",
                            Help = "The file store new uploads land in when the code does not name one. Empty uses the first configured store.",
                        },
                        new() {
                            Path = "LocalSettings.DefaultFileStoreEngine", Label = "Default file store engine",
                            Help = "How a file store created on demand lays out its data. MultiFile writes one file per stored file, which is easy to inspect and back up piecemeal; SingleFile packs everything into one container, which is faster with very many small files and kinder to file-count limits.",
                        },
                    ],
                },
                new() {
                    Id = "urls",
                    Title = "URLs",
                    Help = "How this database renders URLs for pages and for files. The parent relations and host mappings that shape the tree are part of the data model, not editable here.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.UrlOptions.UrlFormat", Label = "Page URL format",
                            Help = "OnlyAddress gives readable paths built from node addresses. The id-based formats always resolve, even when an address changes, at the cost of readability.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.PrimaryBaseAddress", Label = "Base address", Placeholder = "/",
                            Help = "Prepended to every URL, pages and files alike. A path (\"/app\") keeps URLs relative; a scheme and host (\"https://www.site.com\") makes them absolute.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.BaseAddressPages", Label = "Page base address",
                            Help = "Added after the base address for page URLs only, e.g. \"/content\". Giving it its own scheme and host replaces the base address for pages.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.IncludeTrailingSlash", Label = "Trailing slash on pages",
                            Help = "Whether page URLs end in a slash. Pick one and keep it - the two forms are different URLs to search engines and to caches.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.Scheme", Label = "Scheme",
                            Help = "The scheme used when a URL has to be absolute and no request context says otherwise.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.MaxDepth", Label = "Maximum tree depth",
                            Help = "How far the walk up the parent chain may go before it gives up. It is the guard against a relation cycle turning URL building into an infinite loop.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.FallbackRootId", Label = "Fallback root node",
                            Help = "The root used for requests whose host matches no configured domain - local development and staging. Empty falls back to the first domain's root.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.AssetUrlStyle", Label = "File URL style",
                            Help = "AssetRoot puts file URLs under their own root. UnderPageUrl builds them on top of the owning page's URL, which keeps a file's address next to the page it belongs to.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.AssetUrlRoot", Label = "File URL root", Placeholder = "/assets/",
                            Help = "The path file URLs live under when the AssetRoot style is used.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.BaseAddressAssets", Label = "File base address",
                            Help = "Added after the base address for file URLs only. A CDN origin here (\"https://cdn.site.com\") moves file traffic off this server entirely.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.AssetUrlParamName", Label = "File URL parameter",
                            Help = "The query parameter carrying the file token in the UnderPageUrl style. Change it only if it collides with a parameter your application already uses.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.PropertyPathFormat", Label = "File target format",
                            Help = "How a file URL names which property of which node it points at. Encrypted hides it inside the token; the readable forms expose the node and property in the URL, which is friendlier to debug and to cache.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.AssetUrlFormat", Label = "File adjustment format",
                            Help = "How resize and crop instructions appear. Encrypted keeps them opaque, so only URLs your code produced are valid; readable forms let anyone request any size, which is a resource cost worth weighing.",
                        },
                        new() {
                            Path = "LocalSettings.UrlOptions.AssetUrlSignatureKey", Label = "File URL signing key",
                            Help = "Set a value here and file tokens are signed, so a tampered or guessed file URL stops resolving. Empty leaves tokens unsigned. Changing it invalidates every file URL already handed out.",
                        },
                    ],
                },
                new() {
                    Id = "images",
                    Title = "Images",
                    Help = "What the adaptive image format resolves to when a request does not ask for a specific one.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.ImageDefaultFormat", Label = "Default image format", Applies = SettingApplies.Live,
                            Help = "WebP is markedly smaller than Jpeg at the same quality; Jpeg is the safest for very old clients. Png is lossless, and much larger for photographs.",
                        },
                        new() {
                            Path = "LocalSettings.ImageDefaultQuality", Label = "Default image quality", Applies = SettingApplies.Live,
                            Help = "The encoder quality, 1-100. Above roughly 90 the file grows faster than the picture improves. Changing it does not touch images already converted and cached.",
                        },
                    ],
                },
            ],
        },
        new() {
            Id = "performance", Title = "Performance", Icon = "performance",
            Groups = [
                new() {
                    Id = "memory",
                    Title = "Memory and caches",
                    Help = "How much memory this database may spend on caching, before it starts re-reading from disk.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.NodeCacheSizeGb", Label = "Node cache", Unit = "GB",
                            Help = "Holds decoded nodes in memory. Too small and every read hits storage again; too large and the process competes with everything else on the machine. Sized at open, so it takes a reopen to change.",
                        },
                        new() {
                            Path = "LocalSettings.SetCacheSizeGb", Label = "Result set cache", Unit = "GB",
                            Help = "Holds the id sets that queries build, so a repeated filter is answered without touching the indexes. This is what makes faceted search fast on a warm database.",
                        },
                        new() {
                            Path = "LocalSettings.AutoPurgeCache", Label = "Purge caches in the background", Applies = SettingApplies.Live,
                            Help = "Trims cached entries that are past their budget instead of waiting for the next read to notice. Off, memory is only released when a cache is next used.",
                        },
                        new() {
                            Path = "LocalSettings.AutoPurgeCacheIntervalInMinutes", Label = "Purge interval", Unit = "minutes", Applies = SettingApplies.Live,
                            Help = "How often the purge runs. Frequent purges keep memory flat at the cost of dropping entries that were about to be used again.",
                        },
                        new() {
                            Path = "LocalSettings.AutoPurgeCacheLowerSizeLimitInMb", Label = "Purge floor", Unit = "MB", Applies = SettingApplies.Live,
                            Help = "Caches below this size are left alone, so a small working set is not thrown away for nothing.",
                        },
                        new() {
                            Path = "LocalSettings.DoNotCacheMapperFile", Label = "Do not cache the mapper assembly",
                            Help = "The mapper between your model classes and stored nodes is compiled once and cached as a file. Turning the cache off recompiles it on every open - slower starts, but no stale mapper while you iterate on the model.",
                        },
                    ],
                },
                new() {
                    Id = "durability",
                    Title = "Durability",
                    Help = "How eagerly writes are pushed from memory to disk. This is the trade-off between write latency and how much of the most recent work a power loss could cost.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.FlushDiskOnEveryTransactionByDefault", Label = "Flush on every transaction", Applies = SettingApplies.Live,
                            Help = "Every commit waits for the disk before it returns, so nothing acknowledged is ever lost. It costs a disk round-trip per transaction; individual calls can still ask for a flush when this is off.",
                        },
                        new() {
                            Path = "LocalSettings.DeepFlushDisk", Label = "Deep flush", Applies = SettingApplies.Live,
                            Help = "Asks the drive to empty its own write cache too, not just the operating system's. This is what survives a power cut on consumer hardware, and it is markedly slower.",
                        },
                        new() {
                            Path = "LocalSettings.ForceDiskFlushAfterActionCountLimit", Label = "Force flush after", Unit = "actions", Applies = SettingApplies.Live,
                            Help = "An upper bound on unflushed work: once this many actions are queued, the next commit flushes whatever the other settings say. It caps both memory use and how much a crash can cost.",
                        },
                        new() {
                            Path = "LocalSettings.AutoFlushDiskInBackground", Label = "Flush in the background",
                            Help = "A timer flushes pending writes so commits do not have to. This is what makes the default configuration fast without leaving writes in memory indefinitely.",
                        },
                        new() {
                            Path = "LocalSettings.AutoFlushDiskIntervalInSeconds", Label = "Background flush interval", Unit = "seconds",
                            Help = "How often that timer runs, and so roughly the worst-case age of a write that a crash could lose.",
                        },
                        new() {
                            Path = "LocalSettings.DelayAutoDiskFlushIfBusy", Label = "Delay flush while busy", Applies = SettingApplies.Live,
                            Help = "Skips a background flush while the database is under load, keeping the disk free for the work in front of it. The delay is bounded by the next setting.",
                        },
                        new() {
                            Path = "LocalSettings.MaxDelayAutoDiskFlushIfBusyInSeconds", Label = "Maximum flush delay", Unit = "seconds", Applies = SettingApplies.Live,
                            Help = "How long a busy database may postpone flushing before it flushes anyway. Without this bound, sustained load would keep writes in memory forever.",
                        },
                        new() {
                            Path = "LocalSettings.BusyThresholdActivitiesLast10Sec", Label = "Busy threshold, write actions", Unit = "per 10 s", Applies = SettingApplies.Live,
                            Help = "Write actions in the last ten seconds above which the database counts as busy, for every setting that defers work while busy.",
                        },
                        new() {
                            Path = "LocalSettings.BusyThresholdQueriesLast10Sec", Label = "Busy threshold, queries", Unit = "per 10 s", Applies = SettingApplies.Live,
                            Help = "The same test for reads. Raise both on a server that is always somewhat busy, or background maintenance never gets a turn.",
                        },
                    ],
                },
                new() {
                    Id = "snapshots",
                    Title = "Index snapshots",
                    Help = "Opening a database replays its transaction log. A snapshot is the shortcut: the state as of a point in the log, so only what came after it has to be replayed. It is a start-up time optimization, never a source of truth.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.AutoSaveIndexStates", Label = "Save snapshots automatically", Applies = SettingApplies.Live,
                            Help = "Off, every open replays the whole log from the beginning, which on a large database is minutes rather than seconds.",
                        },
                        new() {
                            Path = "LocalSettings.AutoSaveIndexStatesIntervalInMinutes", Label = "Snapshot interval", Unit = "minutes", Applies = SettingApplies.Live,
                            Help = "The soonest a new snapshot is written. Writing one costs a pass over the indexes, so this is start-up speed traded against background work.",
                        },
                        new() {
                            Path = "LocalSettings.AutoSaveIndexStatesActionCountLowerLimit", Label = "Minimum actions", Unit = "actions", Applies = SettingApplies.Live,
                            Help = "Skips the snapshot until at least this many actions have accumulated since the last one - a quiet database does not rewrite the same state hourly.",
                        },
                        new() {
                            Path = "LocalSettings.AutoSaveIndexStatesActionCountUpperLimit", Label = "Forced at", Unit = "actions", Applies = SettingApplies.Live,
                            Help = "Above this many unsnapshotted actions the snapshot is written even while the database is busy, so replay time cannot grow without limit.",
                        },
                    ],
                },
            ],
        },
        new() {
            Id = "search", Title = "Search & AI", Icon = "search",
            Groups = [
                new() {
                    Id = "indexes",
                    Title = "Indexes",
                    Help = "Which engine backs each kind of index. Memory engines are rebuilt from the log at every open - fastest to query, slowest to start, and bounded by RAM. Persisted engines keep their data on disk.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.UsePersistedValueIndexesByDefault", Label = "Persist value indexes",
                            Help = "Applies to properties that do not state otherwise. Off keeps every value index in memory, so opening a large database has to rebuild all of them.",
                        },
                        new() {
                            Path = "LocalSettings.PersistedValueIndexEngine", Label = "Value index engine",
                            Help = "Native is the built-in disk engine and the default. Sqlite stores indexes in SQLite files, which are easy to inspect with other tools. Memory forces value indexes to be rebuilt at every open.",
                        },
                        new() {
                            Path = "LocalSettings.PersistedValueIndexFolderPath", Label = "Index folder", Placeholder = "next to the database files",
                            Help = "Where the disk engines put their files; each claims a subfolder. A relative path is resolved against the server data folder. Empty follows the index storage provider, then the database one. Point it at fast local disk when the database lives on network storage.",
                        },
                        new() {
                            Path = "LocalSettings.EnableTextIndexByDefault", Label = "Index text by default",
                            Help = "Whether string properties are full-text indexed unless they say otherwise. Indexing everything makes WhereSearch work everywhere, at the cost of index size and write throughput.",
                        },
                        new() {
                            Path = "LocalSettings.UsePersistedTextIndexesByDefault", Label = "Persist text indexes",
                            Help = "Keeps the full-text index on disk instead of rebuilding it from the log at every open. Text indexes are the expensive ones to rebuild, so this matters most on large databases.",
                        },
                        new() {
                            Path = "LocalSettings.PersistedTextIndexEngine", Label = "Text index engine",
                            Help = "Native is the built-in disk engine. Lucene and Sqlite need their plug-in package referenced and bring their own file layout and behavior. Memory rebuilds the index at every open.",
                        },
                        new() {
                            Path = "LocalSettings.EnableInstantTextIndexingByDefault", Label = "Index text immediately",
                            Help = "New text is searchable the moment the transaction commits, instead of shortly after via the background queue. It moves indexing cost into the write path, so bulk imports get slower.",
                        },
                        new() {
                            Path = "LocalSettings.EnableSemanticIndexByDefault", Label = "Semantic index by default",
                            Help = "Embeds indexed text with the AI provider so queries can match by meaning as well as by word. It needs a working AI provider below, and every indexed value costs an embedding call.",
                        },
                    ],
                },
                new() {
                    Id = "ai",
                    Title = "AI provider",
                    Help = "The service behind embeddings (semantic search) and completions. Leave it empty on a database that uses neither.",
                    Settings = [
                        new() {
                            Path = "AISettings.TypeName", Label = "Provider type", Placeholder = "OpenAI",
                            Help = "Selects the provider implementation, e.g. OpenAI, AzureOpenAI or Anthropic. It is resolved by name when the database opens, so a typo shows up as a start-up error.",
                        },
                        new() { Path = "AISettings.Name", Label = "Display name", Help = "A label for this configuration in the admin UI. Not sent to the service." },
                        new() {
                            Path = "AISettings.ServiceUrl", Label = "Service URL",
                            Help = "The endpoint the provider calls. Set it for Azure deployments, self-hosted gateways and proxies; leave it empty to use the provider's own default.",
                        },
                        new() {
                            Path = "AISettings.ApiKey", Label = "API key", Secret = true,
                            Help = "Keep this in appsettings, an environment variable or user secrets rather than the settings file - configuration values are never written back to disk.",
                        },
                        new() {
                            Path = "AISettings.ApiVersion", Label = "API version",
                            Help = "Overrides the api-version query parameter for providers that take one, such as Azure OpenAI.",
                        },
                        new() {
                            Path = "AISettings.EmbeddingModel", Label = "Embedding model",
                            Help = "The model used to turn text into vectors. Changing it changes the vector space, so an existing semantic index has to be rebuilt to stay comparable.",
                        },
                        new() {
                            Path = "AISettings.ModelDimensions", Label = "Embedding dimensions",
                            Help = "The vector length the model returns. Set it when the model or endpoint does not report one - a wrong value makes every vector a placeholder, and search quietly returns nothing useful.",
                        },
                        new() {
                            Path = "AISettings.EmbeddingServiceUrl", Label = "Embedding endpoint",
                            Help = "Only when embeddings come from a different endpoint than completions. Required for providers without an embeddings API, where it must point at an OpenAI-compatible one.",
                        },
                        new() { Path = "AISettings.EmbeddingApiKey", Label = "Embedding API key", Secret = true, Help = "The key for that separate embedding endpoint. Falls back to the main API key when empty." },
                        new() { Path = "AISettings.CompletionModel", Label = "Completion model", Help = "The model used for text generation, for code that asks the store for completions." },
                        new() {
                            Path = "AISettings.MaxOutputTokens", Label = "Max output tokens",
                            Help = "Upper bound on a completion's length. Sent only when set; providers that require it default to 4096.",
                        },
                        new() {
                            Path = "AISettings.IndexType", Label = "Vector index",
                            Help = "How vectors are searched. Memory scans them all - exact, and fine up to tens of thousands. IVS and HNSW are approximate disk-backed indexes that stay fast into the millions, HNSW at higher recall and higher build cost.",
                        },
                        new() {
                            Path = "AISettings.IndexCacheSizeInMb", Label = "Vector index cache", Unit = "MB",
                            Help = "How much memory the disk-backed vector indexes may hold. Below what the graph itself needs, the index warns and uses more anyway; above it, more of the vectors stay resident and searches stop hitting disk.",
                        },
                        new() {
                            Path = "AISettings.CacheType", Label = "Embedding cache",
                            Help = "Remembers the vector for text already embedded, so a reindex does not pay for it twice. None calls the service every time, which is the expensive option.",
                        },
                        new() {
                            Path = "AISettings.FilePath", Label = "Embedding cache folder", Placeholder = "next to the index files",
                            Help = "Where the embedding cache is kept. A relative path is resolved against the server data folder. Deleting the folder only costs the money to embed that text again.",
                        },
                        new() {
                            Path = "AISettings.DefaultSemanticRatio", Label = "Default semantic ratio",
                            Help = "How much a search leans on meaning versus matching words, from 0 (keywords only) to 1 (meaning only), when a query does not say. Individual queries can always override it.",
                        },
                        new() {
                            Path = "AISettings.DefaultMinimumSimilarity", Label = "Default minimum similarity",
                            Help = "The similarity below which a semantic hit is discarded. Too low and every query returns something vaguely related; too high and near-misses disappear.",
                        },
                        new() { Path = "AISettings.MaxCharsInBatch", Label = "Max characters per batch", Help = "Caps how much text is sent in one embedding request. Lower it when the provider rejects large batches. Defaults to 50 000." },
                        new() { Path = "AISettings.MaxCountInBatch", Label = "Max items per batch", Help = "Caps how many texts go in one embedding request. Defaults to 500." },
                        new() { Path = "AISettings.MaxCharsOfEach", Label = "Max characters per item", Help = "Longer values are truncated before embedding. This bounds the cost of one very long document. Defaults to 20 000." },
                    ],
                },
            ],
        },
        new() {
            Id = "maintenance", Title = "Maintenance", Icon = "maintenance",
            Groups = [
                new() {
                    Id = "backup",
                    Title = "Backup",
                    Help = "Automatic copies of the transaction log, written to the backup storage provider. They are kept in generations: the newest of each period is retained and older ones in that period are dropped.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.AutoBackUp", Label = "Back up automatically", Applies = SettingApplies.Live,
                            Help = "Off, the only backups are the ones taken by hand from the Storage section. Turning it on for the first time takes effect when the database is next opened.",
                        },
                        new() { Path = "LocalSettings.NoHourlyBackUps", Label = "Hourly copies kept", Applies = SettingApplies.Live, Help = "How many hourly backups to keep before they are thinned into daily ones. This is the window for undoing a mistake made minutes ago." },
                        new() { Path = "LocalSettings.NoDailyBackUps", Label = "Daily copies kept", Applies = SettingApplies.Live, Help = "How far back you can restore day by day." },
                        new() { Path = "LocalSettings.NoWeeklyBackUps", Label = "Weekly copies kept", Applies = SettingApplies.Live, Help = "Weekly generations, for damage noticed after a few days." },
                        new() { Path = "LocalSettings.NoMontlyBackUps", Label = "Monthly copies kept", Applies = SettingApplies.Live, Help = "Monthly generations. Each generation costs a full copy of the log, so weigh these against the storage they occupy." },
                        new() { Path = "LocalSettings.NoYearlyBackUps", Label = "Yearly copies kept", Applies = SettingApplies.Live, Help = "Yearly generations, usually kept for retention rules rather than for recovery." },
                        new() {
                            Path = "LocalSettings.TruncateBackups", Label = "Truncate backups", Applies = SettingApplies.Live,
                            Help = "Writes each backup as the current state instead of the full history, which makes the files far smaller. The cost is that those backups can no longer be reverted to a point in time before they were taken.",
                        },
                        new() {
                            Path = "LocalSettings.SecondaryBackupLog", Label = "Back up the secondary log",
                            Help = "Also backs up the mirrored log from the secondary storage provider. Only meaningful when a secondary log copy is configured above.",
                        },
                    ],
                },
                new() {
                    Id = "truncate",
                    Title = "Log truncation",
                    Help = "The transaction log holds every change ever made, so it only grows. Truncating rewrites it as the current state plus what came after, reclaiming the space - and giving up the history before that point, including the ability to revert into it.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.AutoTruncate", Label = "Truncate automatically", Applies = SettingApplies.Live,
                            Help = "Off, the log grows until someone truncates it from the Storage section. Take a backup first if the history matters - truncation is not reversible.",
                        },
                        new() {
                            Path = "LocalSettings.AutoTruncateIntervalInMinutes", Label = "Truncate interval", Unit = "minutes", Applies = SettingApplies.Live,
                            Help = "The soonest a truncation may run again. It rewrites the whole log, so it is deliberately infrequent and waits for a quiet moment.",
                        },
                        new() {
                            Path = "LocalSettings.AutoTruncateActionCountLowerLimit", Label = "Minimum actions", Unit = "actions", Applies = SettingApplies.Live,
                            Help = "Below this many actions since the last truncation nothing happens - rewriting a log to reclaim very little is not worth the I/O.",
                        },
                        new() {
                            Path = "LocalSettings.AutoTruncateDeleteOldFileOnSuccess", Label = "Delete the old log", Applies = SettingApplies.Live,
                            Help = "Removes the pre-truncation log once the new one is verified. Off keeps it on disk as a safety net, which means truncation frees no space until you delete it yourself.",
                        },
                    ],
                },
                new() {
                    Id = "tasks",
                    Title = "Background tasks",
                    Help = "The queue behind deferred work: text indexing, embedding, file conversion and anything your own code queues.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.AutoDequeTasks", Label = "Run queued tasks",
                            Help = "Off, tasks pile up unprocessed - text stops becoming searchable and conversions never run. Worth turning off only on a node that should queue work for another to do.",
                        },
                        new() {
                            Path = "LocalSettings.PersistedQueueStoreEngine", Label = "Queue engine",
                            Help = "Where queued tasks live. Native and Sqlite survive a restart, so work in flight is picked up again. Memory loses the queue when the process stops.",
                        },
                        new() {
                            Path = "LocalSettings.PersistedQueueStoreFolderPath", Label = "Queue folder", Placeholder = "next to the database files",
                            Help = "Where the persisted queue writes its files. A relative path is resolved against the server data folder; empty follows the database storage.",
                        },
                    ],
                },
            ],
        },
        new() {
            Id = "diagnostics", Title = "Diagnostics", Icon = "diagnostics",
            Groups = [
                new() {
                    Id = "diagnostics",
                    Title = "Diagnostics",
                    Help = "How loudly this database complains, and what it does about damaged files.",
                    Settings = [
                        new() {
                            Path = "LocalSettings.WriteSystemLogConsole", Label = "Write system log to console", Applies = SettingApplies.Live,
                            Help = "Mirrors the database's own log to standard output, where the host's logging picks it up. Useful in containers; noisy in a console application.",
                        },
                        new() {
                            Path = "LocalSettings.ThrowOnBadLogFile", Label = "Fail on a damaged transaction log",
                            Help = "Normally a truncated tail - the usual result of a power cut - is logged and the database opens with everything before it. Turning this on refuses to open instead, so nothing proceeds on partial data.",
                        },
                        new() {
                            Path = "LocalSettings.ThrowOnBadStateFile", Label = "Fail on a damaged snapshot",
                            Help = "A damaged snapshot is normally discarded and the state rebuilt from the log, which is slower but correct. Turning this on refuses to open, which is for diagnosing why snapshots are being corrupted.",
                        },
                    ],
                },
            ],
        },
    ];
}
