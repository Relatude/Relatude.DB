using Microsoft.AspNetCore.Mvc;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Files;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer.Settings;
using Relatude.DB.Tasks;
using Relatude.DB.Web;
using System.Diagnostics;
using System.Reflection;

namespace Relatude.DB.NodeServer;
/// <summary>
/// A wrapper for a NodeStore instance with its settings and lifecycle management
/// </summary>
/// <param name="settings"></param>
/// <param name="server"></param>
public class NodeStoreContainer(NodeStoreContainerSettings settings, RelatudeDBServer server) : IDisposable {

    internal object _lock = new object();
    /// <summary>
    /// Serializes opening against closing. It is deliberately not <see cref="_lock"/>: an open holds
    /// this for the whole log replay, and <see cref="GetLogger"/> - which the admin UI polls to watch
    /// exactly that replay - must not block behind it.
    /// </summary>
    readonly object _openCloseLock = new();
    internal IStoreLogger? _logger;
    public NodeStore? Store { get; private set; }

    public IStoreLogger GetLogger() {
        lock (_lock) {
            if (IsOpenOrOpening()) return Store!.Datastore.Logger;
            if (_logger == null) {
                _logger = new StoreLogger(getLoggerIO(), null);
                // the same switches the open database would start with, so a closed one reports what
                // it will record rather than what an empty logger happens to hold
                var local = settings.LocalSettings;
                if (local?.LogRecording != null) _logger.ApplyRecordingSettings(local.LogRecording);
                if (local != null) _logger.MinDurationMsBeforeLogging = local.MinQueryDurationMsBeforeLogging;
            }
            return _logger;
        }
    }

    public Datamodel? Datamodel { get; private set; }
    public DataStoreStatus GetStatusAndActivity() {
        if (Store == null) return new DataStoreStatus(DataStoreState.Disposed, []);
        return Store.Datastore.GetStatus();
    }
    public bool IsOpen() => Store != null && Store.State == DataStoreState.Open;
    public bool IsOpenOrOpening() => Store != null && (Store.State == DataStoreState.Open || Store.State == DataStoreState.Opening);
    public NodeStoreContainerSettings Settings => settings;
    public void ApplyNewSettings(NodeStoreContainerSettings newSettings, bool reopenIfOpen) {
        lock (_openCloseLock) {
            var isOpen = IsOpenOrOpening();
            disposeCore();
            settings = newSettings;
            if (isOpen && reopenIfOpen && !server.IsShuttingDown) openCore();
        }
    }

    int _initializationCounter = 0;
    int _hasFailedCounter = 0;
    public bool HasInitialized => Interlocked.CompareExchange(ref _initializationCounter, 0, 0) > 0;
    public Exception? StartUpException = null;
    public DateTime? StartUpExceptionDateTimeUTC = null;
    public bool HasFailed => Interlocked.CompareExchange(ref _hasFailedCounter, 0, 0) > 0;

    public void DeleteAllStateAndIndexFiles() {
        var settingsLocal = settings.LocalSettings;
        if (settingsLocal == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings, RemoteSettings will be added later");
        var ioDatabase = server.GetOrNullIO(settings.IoDatabase);
        var ioIndexes = server.GetOrNullIO(settings.IoIndexes);
        var ioProvidersToClean = new List<IIOProvider>();
        if (ioDatabase != null) ioProvidersToClean.Add(ioDatabase);
        if (ioIndexes != null) ioProvidersToClean.Add(ioIndexes);
        foreach (var io in ioProvidersToClean) {
            io.DeleteFolderIfItExists([FileKeyUtility.IndexStoreFolderKey]);
            FileKeyUtility.State_DeleteAll(io);
            FileKeyUtility.MapperDll_GetAllFileKeys(io).ForEach(io.DeleteFileIfItExists);
            FileKeyUtility.Index_GetAll(io).ForEach(io.DeleteFileIfItExists);
        }
        // The index engines own their files on the local disk, in a folder that is not necessarily
        // below any of the IO providers above (PersistedValueIndexFolderPath can point anywhere),
        // so the loop alone can leave engine data behind - and this method exists to force a full
        // rebuild from the log. Deleting the whole folder covers every engine's folder at once,
        // those of engines no default points at any more included.
        var indexFolder = resolveIndexFolderPath(settingsLocal, getLocalDiskFolder(ioIndexes, ioDatabase));
        if (Directory.Exists(indexFolder)) Directory.Delete(indexFolder, true);
    }

    /// <summary>
    /// The local disk folder for the plugins that own their storage: the index engines, the sqlite
    /// queue store and the AI embedding cache all write real files instead of going through an
    /// <see cref="IIOProvider"/>. Prefers the index provider's folder, then the database provider's,
    /// and falls back to the server's data folder when neither is disk backed.
    /// </summary>
    string getLocalDiskFolder(IIOProvider? ioIndexes, IIOProvider? ioDatabase) {
        if (ioIndexes is IOProviderDisk indexDisk) return indexDisk.BaseFolder;
        if (ioDatabase is IOProviderDisk databaseDisk) return databaseDisk.BaseFolder;
        return server.DefaultSubDataFolderPath;
    }

    /// <summary>
    /// The folder the persisted index engines write to: <see cref="SettingsLocal.PersistedValueIndexFolderPath"/>
    /// when set, otherwise <paramref name="localDiskFolder"/>; a relative path is rooted against the
    /// server data folder, as for the queue store. Every engine gets its own folder below the
    /// returned path, named by its id (<see cref="EngineFolderPath"/>), which is what lets two
    /// engines of the same type share one index folder.
    /// </summary>
    string resolveIndexFolderPath(SettingsLocal local, string localDiskFolder) {
        var path = local.PersistedValueIndexFolderPath;
        if (string.IsNullOrEmpty(path)) path = localDiskFolder;
        if (!Path.IsPathRooted(path)) path = server.RootDataFolderPath.SuperPathCombine(path);
        return Path.Combine(path, FileKeyUtility.IndexStoreFolderKey);
    }
    /// <summary>The folder one engine writes to: its id, below the index folder. The engine keeps its
    /// own subfolder inside (nativekv, sqlite, lucene, textindex, vectorindex), as it always did.</summary>
    public static string EngineFolderPath(string indexPath, IndexEngineSettings engine) => Path.Combine(indexPath, engine.Id.ToString("N"));

    /// <summary>
    /// The folders the engines wrote to before each engine had a folder of its own: their subfolders
    /// sat directly below the index folder, one per engine type. Nothing reads them any more, and
    /// a folder that is never read still holds the whole index on disk, so they are deleted at the
    /// first open after the change. The indexes they held are rebuilt from the log - once.
    /// </summary>
    static readonly string[] legacyEngineFolders = [
        FileKeyUtility.IndexEngine_NativeKvFolderKey, FileKeyUtility.IndexEngine_SqliteFolderKey, FileKeyUtility.IndexEngine_LuceneFolderKey,
        FileKeyUtility.IndexEngine_TextIndexFolderKey, FileKeyUtility.IndexEngine_VectorIndexFolderKey, "vectorindex-hnsw",
    ];
    internal static void DeleteLegacyEngineFolders(string indexPath, List<string> toLog) {
        if (!Directory.Exists(indexPath)) return;
        foreach (var name in legacyEngineFolders) {
            var folder = Path.Combine(indexPath, name);
            if (!Directory.Exists(folder)) continue;
            Directory.Delete(folder, true);
            toLog.Add("Deleted the index engine folder \"" + name + "\" left from before each engine had its own folder; the indexes it held are rebuilt from the log. ");
        }
    }

    /// <summary>
    /// Builds the factory for this container's persisted index engines, or null when every index
    /// kind stays in memory (memory indexes persist themselves through state files).
    ///
    /// <para>Only the engines the three defaults name are created: an entry in the engine lists that
    /// nothing points at is configuration, not a running engine. The one case that is not simply "one
    /// engine per default" is SQLite text: the word indexes are FTS5 tables inside a SQLite database,
    /// so when the value default is SQLite too, a single instance fills both roles - registered under
    /// both ids - and all index data commits in one SQLite transaction (<see cref="IndexEngines"/>
    /// de-duplicates the lifecycle calls by reference); otherwise a standalone SQLite engine holds
    /// the word index tables on its own.</para>
    ///
    /// <para>The returned factory runs once per data-store initialization — which happens again when
    /// a bad state file forces a reload — so it must build fresh engine instances every time.
    /// Anything that can be resolved up front (paths, diagnostics) is done here instead, so a
    /// misconfigured path is reported by <see cref="Initialize"/> rather than from deep inside the
    /// data store constructor.</para>
    /// </summary>
    public static Func<IndexEngines>? CreateIndexEngineFactory(SettingsLocal local, string indexPath, bool hasAiProvider, Datamodel? datamodel, List<string> toLog) {
        local.ValidateIndexEngines(); // the data store checks too, but the message is better read here, before anything is created
        DeleteLegacyEngineFolders(indexPath, toLog);
        var value = local.DefaultValueEngine;
        var text = local.DefaultTextEngine;
        var vector = hasAiProvider ? local.DefaultVectorEngine : null;
        if (local.DefaultVectorIndex != Guid.Empty && !hasAiProvider)
            toLog.Add("Note: DefaultVectorIndex names an engine, but without AI settings there are no semantic indexes to put in it.");
        // a property asking to be persisted while its kind defaults to memory has no engine to go to
        // and stays in memory. That is a valid configuration, but silent - and an unexpectedly
        // in-memory index is hard to spot later:
        if (value == null && datamodel != null && datamodel.Properties.Values.Any(p => p.IndexType == IndexStorageType.Persisted))
            toLog.Add("Note: properties ask for a persisted value index while DefaultValueIndex is the memory index, so those indexes stay in memory.");
        if (text == null && datamodel != null && datamodel.Properties.Values.Any(p => p is StringPropertyModel s && s.TextIndexType == IndexStorageType.Persisted))
            toLog.Add("Note: properties ask for a persisted text index while DefaultTextIndex is the memory index, so those indexes stay in memory.");
        if (value == null && text == null && vector == null) {
            toLog.Add("Index engines: none. All indexes are in memory, persisted through state files.");
            return null;
        }
        toLog.Add("Index engines: value " + describe(value) + "; text " + describe(text) + "; vector " + (hasAiProvider ? describe(vector) : "none, no AI provider"));
        var sharedSqlite = value != null && text != null
            && IndexEngineTypes.Is(value.TypeName, IndexEngineTypes.Sqlite) && IndexEngineTypes.Is(text.TypeName, IndexEngineTypes.Sqlite);
        if (sharedSqlite && value!.MaxMemoryUsageInMb != text!.MaxMemoryUsageInMb)
            toLog.Add("Note: the value and text indexes share one SQLite database, which runs on the value engine's memory budget; the text engine's is not used.");
        return () => {
            var valueEngines = new List<(Guid, IValueIndexEngine)>();
            var textEngines = new List<(Guid, ITextIndexEngine)>();
            var vectorEngines = new List<(Guid, ISemanticIndexEngine)>();
            IValueIndexEngine? valueEngine = null;
            if (value != null) {
                valueEngine = LateBindings.CreateValueIndexEngine(value, EngineFolderPath(indexPath, value));
                valueEngines.Add((value.Id, valueEngine));
            }
            if (text != null) {
                var textEngine = sharedSqlite
                    ? (ITextIndexEngine)valueEngine! // dual role: one database, one connection, one transaction
                    : LateBindings.CreateTextIndexEngine(text, EngineFolderPath(indexPath, text));
                textEngines.Add((text.Id, textEngine));
            }
            if (vector != null) vectorEngines.Add((vector.Id, LateBindings.CreateVectorIndexEngine(vector, EngineFolderPath(indexPath, vector))));
            return new IndexEngines(valueEngines, textEngines, vectorEngines);
        };
        static string describe(IndexEngineSettings? engine) => engine == null ? "memory" : engine + " (" + engine.Id.ToString("N") + ")";
    }

    private IIOProvider getLoggerIO() {
        IIOProvider? ioLog = settings.IoLog.HasValue && settings.IoLog != Guid.Empty ? server.GetIO(settings.IoLog.Value) : null;
        if (ioLog == null) {
            ioLog = settings.IoDatabase.HasValue && settings.IoDatabase != Guid.Empty ? server.GetIO(settings.IoDatabase.Value) : null;
        }
        if (ioLog == null) throw new Exception("IoLog or IoDatabase is required for NodeStoreContainerSettings");
        return ioLog;
    }
    public void Initialize() {
        lock (_openCloseLock) initializeCore();
    }
    void initializeCore() {
        AIEngine? ai = null;
        try {
            if (_logger != null) _logger.Dispose();
            if (IsOpenOrOpening()) return;
            disposeCore();
            var local = settings.LocalSettings;
            if (local == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings, RemoteSettings will be added later");
            Datamodel = loadDatamodel();
            server.RaiseEventDatamodelInit(Datamodel, settings);
            var ioDatabase = server.GetOrNullIO(settings.IoDatabase);
            var ioIndexes = server.GetOrNullIO(settings.IoIndexes);
            var ioSecondary = server.GetOrNullIO(settings.IoDatabaseSecondary);

            var localDiskFolder = getLocalDiskFolder(ioIndexes, ioDatabase);
            IFileStore[]? fs = null;
            if (settings.FileStoreSettings != null) {
                foreach (var ioFilesSetting in settings.FileStoreSettings) {
                    if (!server.TryGetIO(ioFilesSetting.IoProviderId, out var ioFiles)) throw new Exception($"IO provider with id {ioFilesSetting.IoProviderId} not found for IoFiles setting.");
                    if (fs == null) fs = [];
                    switch (ioFilesSetting.StoreType) {
                        case FileStoreEngine.SingleFile: {
                                var fileKey = FileKeyUtility.FileStore_GetLatestFileKey(ioFiles);
                                fs = [.. fs, new SingleFileStore(ioFilesSetting.Id, ioFiles, fileKey)];
                            }
                            break;
                        case FileStoreEngine.MultiFile: {
                                fs = [.. fs, new MultiFileStore(ioFilesSetting.Id, ioFiles, ioFilesSetting.MultiFileFolderDepth)];
                            }
                            break;
                        default:
                            break;
                    }
                }
            }
            var ioBackup = server.GetOrNullIO(settings.IoBackup);
            var ioLog = server.GetOrNullIO(settings.IoLog);
            if (settings.AISettings != null) {
                var aiFolder = settings.AISettings.FilePath;
                if (string.IsNullOrEmpty(aiFolder)) aiFolder = localDiskFolder;
                if (!Path.IsPathRooted(aiFolder)) aiFolder = server.RootDataFolderPath.SuperPathCombine(aiFolder);
                if (!Directory.Exists(aiFolder)) Directory.CreateDirectory(aiFolder);
                ai = AIProviderFactory.Create(settings.AISettings, aiFolder);
            }

            List<string> toLog = new();
            var createIndexEngines = CreateIndexEngineFactory(local, resolveIndexFolderPath(local, localDiskFolder), ai != null, Datamodel, toLog);

            IQueueStore? queueStore = null;
            if (local.PersistedQueueStoreEngine == PersistedQueueStoreEngine.Sqlite) {
                var queuePath = local.PersistedQueueStoreFolderPath;
                if (string.IsNullOrEmpty(queuePath)) queuePath = localDiskFolder;
                if (!Path.IsPathRooted(queuePath)) queuePath = server.RootDataFolderPath.SuperPathCombine(queuePath);
                toLog.Add("Queue path: " + queuePath);
                var queueKey = FileKeyUtility.Queue_GetFileKey("sqlite");
                var queueBaseFolder = queuePath;
                queuePath = Path.Combine([queuePath, .. queueKey]);
                // the queue file key is folder qualified (state/); sqlite does not create directories
                var queueDir = Path.GetDirectoryName(queuePath);
                if (!string.IsNullOrEmpty(queueDir) && !Directory.Exists(queueDir)) Directory.CreateDirectory(queueDir);
                // before the folder layout the queue file lived in the root of the queue folder;
                // it keeps its file name, so the legacy path is the base folder plus that name
                var legacyQueuePath = Path.Combine(queueBaseFolder, queueKey.FileName());
                if (File.Exists(legacyQueuePath) && !File.Exists(queuePath)) {
                    File.Move(legacyQueuePath, queuePath);
                    foreach (var suffix in new[] { "-wal", "-shm" }) {
                        if (File.Exists(legacyQueuePath + suffix) && !File.Exists(queuePath + suffix)) File.Move(legacyQueuePath + suffix, queuePath + suffix);
                    }
                }
                queueStore = LateBindings.CreateSqliteQueueStore(queuePath);
            }
            // the url manager owns the mapping between public URLs and content; when the factory
            // returns null the store falls back to the flat built-in TreeUrlManager ("/{address}")
            var urlManager = server?.Options?.CreateUrlManager?.Invoke(settings);

            IDataStore datastore = new DataStoreLocal(
                    Datamodel,
                    local,
                    ioDatabase,
                    fs,
                    ioBackup,
                    ioLog,
                    ai,
                    createIndexEngines,
                    queueStore,
                    ioSecondary,
                    ioIndexes,
                    QueryContext.MasterAdmin,
                    server?.Options?.FileConverters.ToArray(),
                    urlManager: urlManager
                    );
            Interlocked.Increment(ref _initializationCounter);
            //var runners = server.GetRegisteredTaskRunners(this);
            //foreach (var runner in runners) datastore.RegisterRunner(runner);
            foreach (var msg in toLog) {
                datastore.LogInfo(msg);
            }
            Store = new NodeStore(datastore);
            server?.RaiseEventStoreInit(this, Store);
        } catch {
            if (Store == null && ai != null) {
                try { ai.Dispose(); } catch { }
            }
            Interlocked.Increment(ref _hasFailedCounter);
            throw;
        }
    }
    public void Open() {
        lock (_openCloseLock) {
            // an open that lands after the shutdown has disposed the other databases would leave this
            // one open, and unflushed, for the rest of the process
            if (server.IsShuttingDown) throw new InvalidOperationException("The server is shutting down, \"" + settings.Name + "\" was not opened. ");
            openCore();
        }
    }
    void openCore() {
        try {
            var sw = Stopwatch.StartNew();
            if (Store == null) initializeCore();
            Store!.Datastore.LogInfo($"NodeStore initialized in {sw.ElapsedMilliseconds.To1000N()}ms, opening... ");
            if (Store == null) throw new Exception("Datastore is not initialized. ");
            try {
                Store.Datastore.Open(false, false);
            } catch {
                disposeCore();
                throw;
            }
            Store!.Datastore.LogInfo($"NodeStore ready in a total of {sw.ElapsedMilliseconds.To1000N()}ms.");
            server.RaiseEventStoreOpen(this, Store);
        } catch {
            Interlocked.Increment(ref _hasFailedCounter);
            throw;
        }
    }
    public void CloseIfOpen() {
        lock (_openCloseLock) {
            var store = Store; // captured: disposing nulls it, and the event needs the store
            if (store == null) return;
            disposeCore();
            server.RaiseEventStoreClose(this, store);
        }
    }
    /// <summary>
    /// Closes the container as part of a host shutdown, and reports whether there was anything to
    /// close. Waits up to <paramref name="waitForOpening"/> for an <see cref="Open"/> that is still
    /// running: the data store only flushes on dispose if it reached the Open state, and disposing it
    /// during the log replay pulls the WAL and the indexes out from under the opening thread. When the
    /// wait runs out it disposes anyway - a half-opened store has nothing to flush, and the log replay
    /// on the next start covers whatever the opening thread was in the middle of.
    /// </summary>
    public bool CloseForShutdown(TimeSpan waitForOpening) {
        var ms = (int)Math.Clamp(waitForOpening.TotalMilliseconds, 0, int.MaxValue);
        if (!Monitor.TryEnter(_openCloseLock, ms)) {
            server.Log("\"" + settings.Name + "\" is still opening after " + (ms / 1000d).To1000N() + " s, closing it anyway.");
            disposeCore(); // best effort, racing the opening thread
            return true;
        }
        try {
            var store = Store;
            if (store == null) return false;
            disposeCore();
            server.RaiseEventStoreClose(this, store);
            return true;
        } finally {
            Monitor.Exit(_openCloseLock);
        }
    }
    Datamodel loadDatamodel() {
        var dm = new Datamodel();
        if (settings.DatamodelSources != null) {
            foreach (var source in settings.DatamodelSources) {
                try {
                    loadDatamodelSource(dm, source);
                } catch (Exception ex) {
                    var name = string.IsNullOrEmpty(source.Name) ? source.Id.ToString() : source.Name;
                    var msg = $"Failed to load the datamodel source \"{name}\" ({source.Type}"
                        + (string.IsNullOrEmpty(source.Reference) ? "" : $", reference \"{source.Reference}\"") + $"): {ex.Message}";
                    throw new Exception(msg, ex);
                }
            }
        }
        return dm;
    }
    void loadDatamodelSource(Datamodel dm, DatamodelSource source) {
        DatamodelSourceLoader.Load(dm, source, server.RootDataFolderPath, id => server.TryGetIO(id, out var io) ? io : null);
    }
    public void Dispose() {
        lock (_openCloseLock) disposeCore();
    }
    void disposeCore() {
        if (Store != null) {
            Store.Dispose();
            Store = null;
            Datamodel = null;
        }
    }
}
