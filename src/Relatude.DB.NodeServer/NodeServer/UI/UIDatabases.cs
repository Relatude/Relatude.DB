using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The Databases section: every database this server knows about, what state it is in, and the three
/// things that are decided about a database rather than inside one - whether it is running, which one
/// applications get when they ask for no database in particular, and adding another.
///
/// The list is deliberately cheaper than the dashboard's picture of a single database: counts come
/// from <see cref="IDataStore.PeekCounters"/> (already kept, no locks) and everything else is read off
/// the settings, so this page can be refreshed on the ordinary cadence with a dozen databases on it.
///
/// Creating and re-pointing the default both write the settings file, so both go through
/// <see cref="RelatudeDBServer.UpdateWAFServerSettingsFile"/> - which rebuilds the container array
/// from the live containers, meaning a new container has to be in the dictionary before the file is
/// written or it would be written straight back out of existence.
/// </summary>
sealed class UIDatabases {
    readonly RelatudeDBServer _server;
    readonly object _createLock = new();
    internal UIDatabases(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("databases", ctx => list());
        commands.Register("database-create", ctx => create(ctx.Payload<CreatePayload>()));
        commands.Register("database-set-default", ctx => setDefault(ctx.Payload<StorePayload>().StoreId));
    }

    // ---- reading ----

    object list() {
        var defaultId = _server.Settings.DefaultStoreId;
        var overlay = _server.ConfigurationOverlay;
        return new {
            // the default is a server setting like any other, so configuration can decide it - and
            // then this page must not pretend the button would do anything
            DefaultLocked = overlay != null && overlay.IsOverridden(nameof(RelatudeDBServerSettings.DefaultStoreId), out _),
            ConfigSection = overlay?.SectionName,
            SettingsFile = _server.Settings.DBSettingsFilePath ?? Defaults.SettingsFileName,
            Databases = _server.GetContainers()
                .OrderBy(c => c.Settings.Name ?? c.Settings.Id.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(c => describe(c, defaultId))
                .ToArray(),
        };
    }

    object describe(NodeStoreContainer c, Guid defaultId) {
        var settings = c.Settings;
        var state = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed";
        long? nodes = null, relations = null;
        if (c.Store != null && c.Store.State == DataStoreState.Open) {
            try {
                var counters = c.Store.Datastore.PeekCounters();
                nodes = counters.NodeCount;
                relations = counters.RelationCount;
            } catch { } // a database closing mid-call has no counters, which is not an error here
        }
        return new {
            settings.Id,
            Name = string.IsNullOrEmpty(settings.Name) ? settings.Id.ToString() : settings.Name,
            settings.Description,
            State = state,
            IsDefault = settings.Id == defaultId,
            settings.AutoOpen,
            NodeCount = nodes,
            RelationCount = relations,
            Storage = storage(settings),
            ModelSources = (settings.DatamodelSources ?? []).Count(s => s.Enabled),
            StartupError = c.StartUpException?.Message,
        };
    }

    /// <summary>Where this database keeps its files, as one line: which provider and, for a local
    /// disk, the folder - the one thing that says two databases apart at a glance.</summary>
    static string storage(NodeStoreContainerSettings settings) {
        var io = (settings.IOSettings ?? []).FirstOrDefault(i => i.Id == settings.IoDatabase);
        if (io == null) return "-";
        if (io.IOType == IOTypes.LocalDisk) return string.IsNullOrEmpty(io.Path) ? "Local disk" : io.Path;
        return string.IsNullOrEmpty(io.Name) ? io.IOType.ToString() : io.Name;
    }

    // ---- the default database ----

    /// <summary>
    /// Which database an application gets when it asks for none in particular. Nothing is opened,
    /// closed or moved: it is one id in the settings file, and the server re-points itself at it as
    /// the file is written.
    /// </summary>
    object setDefault(Guid storeId) {
        lock (_createLock) {
            if (!_server.Containers.ContainsKey(storeId)) throw new Exception("Database not found. ");
            var overlay = _server.ConfigurationOverlay;
            if (overlay != null && overlay.IsOverridden(nameof(RelatudeDBServerSettings.DefaultStoreId), out _)) {
                throw new Exception("The default database is set by configuration (" + overlay.SectionName
                    + ") and cannot be changed here: the overlay would win again at the next start. ");
            }
            _server.Settings.DefaultStoreId = storeId;
            _server.UpdateWAFServerSettingsFile();
            return list();
        }
    }

    // ---- adding one ----

    /// <summary>
    /// A new, empty database: its own folder, its own storage provider, the native engines, and no
    /// datamodel sources at all - the model is the Data model section's business, and guessing one
    /// here would put types in a database nobody asked to have them in.
    ///
    /// It is created closed. Opening replays a log that does not exist yet and writes the first
    /// files, which is a real operation on a real folder, so it stays a separate press.
    /// </summary>
    object create(CreatePayload payload) {
        lock (_createLock) {
            var name = (payload.Name ?? "").Trim();
            if (name.Length == 0) throw new Exception("The database needs a name. ");
            if (_server.GetContainers().Any(c => string.Equals(c.Settings.Name, name, StringComparison.OrdinalIgnoreCase))) {
                throw new Exception("There is already a database called \"" + name + "\". ");
            }
            var folder = uniqueFolder(name);
            var io = new IOSettings {
                Id = SecureGuid.New(),
                Name = "Local disk",
                Path = folder,
                IOType = IOTypes.LocalDisk,
            };
            var settings = new NodeStoreContainerSettings {
                Id = SecureGuid.New(),
                Name = name,
                AutoOpen = payload.AutoOpen,
                LocalSettings = SettingsLocal.CreateWithNativeEngines(),
                IOSettings = [io],
                IoDatabase = io.Id,
                IoBackup = io.Id,
                IoLog = io.Id,
                FileStoreSettings = [],
                DatamodelSources = [],
            };
            // the same hooks the startup path runs, so a database added here is built the way one
            // read from the settings file is - an application that fills in settings in code must
            // not get a database that skipped it
            _server.RaiseEventContainerSettingsInit(settings);
            if (settings.LocalSettings != null) _server.RaiseEventStoreSettingsInit(settings.LocalSettings, settings);
            var container = new NodeStoreContainer(settings, _server);
            lock (_server.Containers) _server.Containers.Add(settings.Id, container);
            // written only now: the file is rebuilt from the live containers, so a container added
            // afterwards would be the one thing the file does not have
            _server.UpdateWAFServerSettingsFile();
            RelatudeDBServer.Trace("Database \"" + name + "\" created in \"" + folder + "\". ");
            // "List", not "Databases": what comes back is the whole payload of the list command, and the
            // array of databases is one field inside it
            return new { StoreId = settings.Id, Folder = folder, List = list() };
        }
    }

    /// <summary>
    /// A folder of its own, under the data folder and named after the database. Two databases sharing
    /// a folder would write over each other's log and state files, which is why this checks against
    /// every provider of every database rather than only against what is on disk.
    /// </summary>
    string uniqueFolder(string name) {
        var taken = _server.GetContainers()
            .SelectMany(c => c.Settings.IOSettings ?? [])
            .Where(io => io.IOType == IOTypes.LocalDisk && !string.IsNullOrEmpty(io.Path))
            .Select(io => io.Path!.Replace('\\', '/').TrimEnd('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = Defaults.DataFolderPath + "/" + slug(name);
        var candidate = stem;
        for (var n = 2; taken.Contains(candidate) || Directory.Exists(candidate); n++) candidate = stem + "-" + n;
        return candidate;
    }

    /// <summary>A folder name from a database name: what a file system takes everywhere, lower case
    /// so two names differing only in case cannot pick the same folder on Linux and collide.</summary>
    static string slug(string name) {
        var text = new string([.. name.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')]);
        while (text.Contains("--")) text = text.Replace("--", "-");
        text = text.Trim('-');
        return text.Length == 0 ? "db" : text.Length > 40 ? text[..40].Trim('-') : text;
    }

    sealed record StorePayload(Guid StoreId);
    sealed record CreatePayload(string? Name, bool AutoOpen);
}
