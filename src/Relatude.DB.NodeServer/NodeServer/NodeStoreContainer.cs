using Microsoft.AspNetCore.Mvc;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
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
    internal IStoreLogger? _logger;
    public NodeStore? Store { get; private set; }

    public IStoreLogger GetLogger() {
        lock (_lock) {
            if (IsOpenOrOpening()) return Store!.Datastore.Logger;
            if (_logger == null) _logger = new StoreLogger(getLoggerIO(), getLoggerFileKeys(), null);
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
        var isOpen = IsOpenOrOpening();
        Dispose();
        settings = newSettings;
        if (isOpen && reopenIfOpen) Open();
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
        var fileKeyUtil = new FileKeyUtility(settingsLocal.FilePrefix);
        var ioDatabase = server.GetOrNullIO(settings.IoDatabase);
        var ioIndexes = server.GetOrNullIO(settings.IoIndexes);
        var ioProvidersToClean = new List<IIOProvider>();
        if (ioDatabase != null) ioProvidersToClean.Add(ioDatabase);
        if (ioIndexes != null) ioProvidersToClean.Add(ioIndexes);
        foreach (var io in ioProvidersToClean) {
            io.DeleteFolderIfItExists([fileKeyUtil.IndexStoreFolderKey]);
            io.DeleteFileIfItExists(fileKeyUtil.StateFileKey);
            fileKeyUtil.MapperDll_GetAllFileKeys(io).ForEach(io.DeleteFileIfItExists);
            fileKeyUtil.Index_GetAll(io).ForEach(io.DeleteFileIfItExists);
        }
        // The index engines own their files on the local disk, in a folder that is not necessarily
        // below any of the IO providers above (PersistedValueIndexFolderPath can point anywhere),
        // so the loop alone can leave engine data behind - and this method exists to force a full
        // rebuild from the log. Deleting the whole folder covers every engine subfolder at once.
        if (usesPersistedIndexEngines(settingsLocal, semanticIndexType())) {
            var indexFolder = resolveIndexFolderPath(settingsLocal, getLocalDiskFolder(ioIndexes, ioDatabase), fileKeyUtil);
            if (Directory.Exists(indexFolder)) Directory.Delete(indexFolder, true);
        }
    }

    AIIndexType semanticIndexType() => settings.AISettings?.IndexType ?? AIIndexType.Memory;

    static bool usesPersistedIndexEngines(SettingsLocal local, AIIndexType semanticIndexType)
        => local.PersistedValueIndexEngine != PersistedValueIndexEngine.Memory
        || local.PersistedTextIndexEngine != PersistedTextIndexEngine.Memory
        || semanticIndexType != AIIndexType.Memory;

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
    /// server data folder, as for the queue store. Every engine claims its own subfolder below the
    /// returned path (nativekv, sqlite, lucene), which is what lets them share one index folder.
    /// </summary>
    string resolveIndexFolderPath(SettingsLocal local, string localDiskFolder, FileKeyUtility fileKeys) {
        var path = local.PersistedValueIndexFolderPath;
        if (string.IsNullOrEmpty(path)) path = localDiskFolder;
        if (!Path.IsPathRooted(path)) path = server.RootDataFolderPath.SuperPathCombine(path);
        return Path.Combine(path, fileKeys.IndexStoreFolderKey);
    }

    /// <summary>
    /// Builds the factory for this container's persisted index engines, or null when every index
    /// kind stays in memory (memory indexes persist themselves through state files).
    ///
    /// <para>The engines are independent: each index kind picks its own, and every combination of
    /// <see cref="SettingsLocal.PersistedValueIndexEngine"/>,
    /// <see cref="SettingsLocal.PersistedTextIndexEngine"/> and the AI settings' index type is
    /// supported. The one case that is not simply "one engine per kind" is SQLite text: the word
    /// indexes are FTS5 tables inside a SQLite database, so when the values are SQLite too, a single
    /// instance fills both roles and all index data commits in one SQLite transaction
    /// (<see cref="IndexEngines"/> de-duplicates the lifecycle calls by reference); otherwise a
    /// standalone SQLite engine holds the word index tables on its own.</para>
    ///
    /// <para>The returned factory runs once per data-store initialization — which happens again when
    /// a bad state file forces a reload — so it must build fresh engine instances every time.
    /// Anything that can be resolved up front (paths, diagnostics) is done here instead, so a
    /// misconfigured path is reported by <see cref="Initialize"/> rather than from deep inside the
    /// data store constructor.</para>
    /// </summary>
    Func<IndexEngines>? getIndexEngineFactory(SettingsLocal local, string indexPath, List<string> toLog, AIProviderSettings? aiSettings) {
        var valueEngine = local.PersistedValueIndexEngine;
        var textEngine = local.PersistedTextIndexEngine;
        var semanticEngine = aiSettings?.IndexType ?? AIIndexType.Memory;
        var semanticCacheSizeInMb = aiSettings?.IndexCacheSizeInMb;
        if (!usesPersistedIndexEngines(local, semanticEngine)) {
            toLog.Add("Index engines: none. All indexes are in memory, persisted through state files.");
            return null;
        }
        toLog.Add("Index engines: " + valueEngine + ", " + textEngine + ", " + semanticEngine);
        // A persisted default that no engine can serve falls back to memory indexes. That is a valid
        // configuration, but silent - and an unexpectedly in-memory index is hard to spot later:
        if (local.UsePersistedValueIndexesByDefault && valueEngine == PersistedValueIndexEngine.Memory)
            toLog.Add("Note: UsePersistedValueIndexesByDefault is on while PersistedValueIndexEngine is Memory, so value indexes stay in memory.");
        if (local.UsePersistedTextIndexesByDefault && textEngine == PersistedTextIndexEngine.Memory)
            toLog.Add("Note: UsePersistedTextIndexesByDefault is on while PersistedTextIndexEngine is Memory, so word indexes stay in memory.");
        return () => {
            var value = valueEngine == PersistedValueIndexEngine.Memory ? null
                : LateBindings.CreateValueIndexEngine(valueEngine, indexPath);
            ITextIndexEngine? text = textEngine switch {
                PersistedTextIndexEngine.Memory => null,
                PersistedTextIndexEngine.Sqlite => valueEngine == PersistedValueIndexEngine.Sqlite
                    ? (ITextIndexEngine)value! // dual role: one database, one connection, one transaction
                    : LateBindings.CreateSqliteTextIndexEngine(indexPath),
                PersistedTextIndexEngine.Lucene => LateBindings.CreateLuceneTextIndexEngine(indexPath),
                PersistedTextIndexEngine.Native => LateBindings.CreateNativeTextIndexEngine(indexPath),
                _ => throw new Exception("Unknown PersistedTextIndexEngine: " + textEngine),
            };
            var semantic = semanticEngine == AIIndexType.Memory ? null
                : LateBindings.CreateSemanticIndexEngine(semanticEngine, indexPath, semanticCacheSizeInMb);
            return new IndexEngines(value, text, semantic);
        };
    }

    private FileKeyUtility getLoggerFileKeys() {
        if (settings.LocalSettings == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings, RemoteSettings will be added later");
        return new FileKeyUtility(settings.LocalSettings.FilePrefix);
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
        AIEngine? ai = null;
        try {
            if (_logger != null) _logger.Dispose();
            if (IsOpenOrOpening()) return;
            Dispose();
            var local = settings.LocalSettings;
            if (local == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings, RemoteSettings will be added later");
            Datamodel = loadDatamodel();
            server.RaiseEventDatamodelInit(Datamodel, settings);
            var ioDatabase = server.GetOrNullIO(settings.IoDatabase);
            var ioIndexes = server.GetOrNullIO(settings.IoIndexes);
            var ioSecondary = server.GetOrNullIO(settings.IoDatabaseSecondary);

            var localDiskFolder = getLocalDiskFolder(ioIndexes, ioDatabase);
            FileKeyUtility fileKeyUtility = new FileKeyUtility(local.FilePrefix);
            IFileStore[]? fs = null;
            if (settings.FileStoreSettings != null) {
                foreach (var ioFilesSetting in settings.FileStoreSettings) {
                    if (!server.TryGetIO(ioFilesSetting.IoProviderId, out var ioFiles)) throw new Exception($"IO provider with id {ioFilesSetting.IoProviderId} not found for IoFiles setting.");
                    if (fs == null) fs = [];
                    switch (ioFilesSetting.StoreType) {
                        case FileStoreEngine.SingleFile: {
                                var fileKey = fileKeyUtility.FileStore_GetLatestFileKey(ioFiles);
                                fs = [.. fs, new SingleFileStore(ioFilesSetting.Id, ioFiles, fileKey)];
                            }
                            break;
                        case FileStoreEngine.MultiFile: {
                                fs = [.. fs, new MultiFileStore(ioFilesSetting.Id, ioFiles, fileKeyUtility, ioFilesSetting.MultiFileFolderDepth)];
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
                ai = AIProviderFactory.Create(settings.AISettings, aiFolder, local.FilePrefix);
            }

            List<string> toLog = new();
            var createIndexEngines = getIndexEngineFactory(local, resolveIndexFolderPath(local, localDiskFolder, fileKeyUtility), toLog, settings.AISettings);

            IQueueStore? queueStore = null;
            if (local.PersistedQueueStoreEngine == PersistedQueueStoreEngine.Sqlite) {
                var queuePath = local.PersistedQueueStoreFolderPath;
                if (string.IsNullOrEmpty(queuePath)) queuePath = localDiskFolder;
                if (!Path.IsPathRooted(queuePath)) queuePath = server.RootDataFolderPath.SuperPathCombine(queuePath);
                toLog.Add("Queue path: " + queuePath);
                queuePath = Path.Combine(queuePath, fileKeyUtility.Queue_GetFileKey("sqlite"));
                queueStore = LateBindings.CreateSqliteQueueStore(queuePath);
            }
            var urlOptions = new UrlProviderOptions() {
                HashKey = settings.Id,
                //UrlNodeRoot = "assets",
                //HashNodeUrls = true,
                //HashPropertyUrls = true,
                //UrlHashSeed = Guid.Empty,
                //IncludeTrailingSlash = false,
                //UrlFormat = UrlFormat.AddressOrIntId,
            };
            var urlProvider = new DefaultUrlProvider(urlOptions);
            //var urlProvider = new InternalUrlProvider();

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
                    urlProvider,
                    server?.Options?.FileConverters.ToArray()
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
        try {
            var sw = Stopwatch.StartNew();
            if (Store == null) Initialize();
            Store!.Datastore.LogInfo($"NodeStore initialized in {sw.ElapsedMilliseconds.To1000N()}ms, opening... ");
            if (Store == null) throw new Exception("Datastore is not initialized. ");
            try {
                Store.Datastore.Open(false, false);
            } catch {
                Dispose();
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
        if (Store != null) {
            Dispose();
            server.RaiseEventStoreClose(this, Store!);
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
        if (Store != null) {
            Store.Dispose();
            Store = null;
            Datamodel = null;
        }
    }
}
