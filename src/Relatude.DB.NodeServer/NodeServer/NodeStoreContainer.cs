using System.Diagnostics;
using System.Reflection;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Tasks;
using Relatude.DB.DataStores.Files;

using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer;
/// <summary>
/// A wrapper for a NodeStore instance with its settings and lifecycle management
/// </summary>
/// <param name="settings"></param>
/// <param name="server"></param>
public class NodeStoreContainer(NodeStoreContainerSettings settings, RelatudeDBServer server) : IDisposable {

    internal IDataStore? Datastore { get; set; }
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
    public DataStoreStatus Status {
        get {
            if (Datastore == null) return new DataStoreStatus(DataStoreState.Closed, []);
            return Datastore.GetStatus();
        }
    }
    public bool IsOpenOrOpening() => Store != null && (Store.State == DataStoreState.Open || Store.State == DataStoreState.Opening);
    public NodeStoreContainerSettings Settings => settings;
    public void ApplyNewSettings(NodeStoreContainerSettings newSettings, bool reopenIfOpen) {
        var isOpen = IsOpenOrOpening();
        CloseIfOpen();
        settings = newSettings;
        if (isOpen && reopenIfOpen) Open();
    }

    int _initializationCounter = 0;
    int _hasFailedCounter = 0;
    public bool HasInitialized => Interlocked.CompareExchange(ref _initializationCounter, 0, 0) > 0;
    public Exception? StartUpException = null;
    public DateTime? StartUpExceptionDateTimeUTC = null;
    public bool HasFailed => Interlocked.CompareExchange(ref _hasFailedCounter, 0, 0) > 0;

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
    public void Open() {
        try {
            if (_logger != null) _logger.Dispose();
            if (IsOpenOrOpening()) return;
            CloseIfOpen();
            if (settings.LocalSettings == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings, RemoteSettings will be added later");
            Datamodel = loadDatamodel();
            var ioDatabase = server.GetOrNullIO(settings.IoDatabase);
            var ioIndexes = server.GetOrNullIO(settings.IoIndexes);
            var ioSecondary = server.GetOrNullIO(settings.IoDatabaseSecondary);

            string? localFallbackPath = null;
            // fallbackpath used for sqlite and lucene indexes, if used
            if (localFallbackPath == null && ioIndexes is IOProviderDisk ioDisk2) localFallbackPath = ioDisk2.BaseFolder;
            if (localFallbackPath == null && ioDatabase is IOProviderDisk ioDisk) localFallbackPath = ioDisk.BaseFolder;
            if (localFallbackPath == null) localFallbackPath = server.DefaultSubDataFolderPath;

            IFileStore[]? fs = null;
            if (settings.IoFiles != null) {
                foreach (var ioFilesId in settings.IoFiles) {
                    if (!server.TryGetIO(ioFilesId, out var ioFiles)) continue;
                    var fileKey = new FileKeyUtility(settings.LocalSettings.FilePrefix).FileStore_GetLatestFileKey(ioFiles);
                    if (fs == null) fs = [new SingleFileStore(Guid.Empty, ioFiles, fileKey)];
                    else fs = [.. fs, new SingleFileStore(Guid.Empty, ioFiles, fileKey)];
                }
            }
            var ioBackup = server.GetOrNullIO(settings.IoBackup);
            var ioLog = server.GetOrNullIO(settings.IoLog);
            AIEngine? ai = null;
            if (settings.AiProvider.HasValue && settings.AiProvider != Guid.Empty) {
                if (localFallbackPath == null) {
                    throw new Exception("The setting PersistedValueIndexFolderPath is required for the AI provider");
                }
                server.GetAI(settings.AiProvider.Value, Settings.LocalSettings?.FilePrefix, localFallbackPath);
            }
            Func<IPersistedIndexStore>? createIndexStore = null;

            List<string> toLog = new();
            if (settings.LocalSettings.PersistedValueIndexEngine != PersistedValueIndexEngine.Memory) {
                if (settings.LocalSettings.PersistedTextIndexEngine == PersistedTextIndexEngine.Memory) {
                    throw new Exception("The setting PersistedTextIndexEngine must be set to Sqlite or Lucene when PersistedValueIndexEngine is Sqlite.");
                }
                createIndexStore = () => {
                    var indexPath = settings.LocalSettings.PersistedValueIndexFolderPath;
                    if (string.IsNullOrEmpty(indexPath)) indexPath = localFallbackPath;
                    if (string.IsNullOrEmpty(indexPath)) {
                        throw new Exception("The setting PersistedValueIndexFolderPath is required for the persisted index store.");
                    }
                    indexPath = Path.Combine(indexPath, new FileKeyUtility(settings.LocalSettings.FilePrefix).IndexStoreFolderKey);
                    toLog.Add("Index path: " + indexPath);
                    IPersistentWordIndexFactory? textIndexFactory = null;
                    if (settings.LocalSettings.PersistedTextIndexEngine == PersistedTextIndexEngine.Lucene) {
                        textIndexFactory = LateBindings.CreateLucenePersistentWordIndexFactory(indexPath);
                    }
                    return LateBindings.CreatePersistedIndexStore(indexPath, textIndexFactory);
                };
            } else {
                if (settings.LocalSettings.PersistedTextIndexEngine != PersistedTextIndexEngine.Memory) {
                    throw new Exception("The setting PersistedTextIndexEngine must be Memory when PersistedValueIndexEngine is Memory.");
                }
            }
            IQueueStore? queueStore = null;
            if (settings.LocalSettings.PersistedQueueStoreEngine == PersistedQueueStoreEngine.Sqlite) {
                var queuePath = settings.LocalSettings.PersistedQueueStoreFolderPath;
                if (string.IsNullOrEmpty(queuePath)) queuePath = localFallbackPath;
                if (string.IsNullOrEmpty(queuePath)) throw new Exception("The setting PersistedQueueStoreFolderPath is required for the persisted queue store.");
                if (!Path.IsPathRooted(queuePath)) queuePath = server.RootDataFolderPath.SuperPathCombine(queuePath);
                toLog.Add("Queue path: " + queuePath);
                queuePath = Path.Combine(queuePath, new FileKeyUtility(settings.LocalSettings.FilePrefix).Queue_GetFileKey("sqlite"));
                queueStore = LateBindings.CreateSqliteQueueStore(queuePath);
            }
            var sw = Stopwatch.StartNew();
            Datastore = new DataStoreLocal(
                    Datamodel,
                    settings.LocalSettings,
                    ioDatabase,
                    fs,
                    ioBackup,
                    ioLog,
                    ai,
                    createIndexStore,
                    queueStore,
                    ioSecondary,
                    ioIndexes
                    );
            Interlocked.Increment(ref _initializationCounter);
            var runners = server.GetRegisteredTaskRunners(this);
            foreach (var runner in runners) Datastore.RegisterRunner(runner);
            foreach (var msg in toLog) {
                Datastore.LogInfo(msg);
            }
            Store = new NodeStore(Datastore);
            server.RaiseEventStoreInit(this, Store);
            try {
                Datastore.Open(false, false);
            } catch {
                Datastore.Dispose();
                Datastore = null;
                throw;
            }
            Datastore.LogInfo($"NodeStore ready in {sw.ElapsedMilliseconds.To1000N()}ms.");
            server.RaiseEventStoreOpen(this, Store);
        } catch {
            Interlocked.Increment(ref _hasFailedCounter);
            throw;
        }
    }
    public void CloseIfOpen() {
        Datastore = null;
        if (Store != null) {
            Store.Dispose();
            server.RaiseEventStoreDispose(this, Store);
            Store = null;
            Datamodel = null;
        }
    }
    Datamodel loadDatamodel() {
        var dm = new Datamodel();
        if (settings.DatamodelSources != null) {
            foreach (var source in settings.DatamodelSources) {
                try {
                    loadDatamodelSource(dm, source);
                } catch (Exception ex) {
                    var msg = $"Failed to load datamodel source {source.Id}: {ex.Message}";
                    throw new Exception(msg, ex);
                }
            }
        }
        return dm;
    }
    void loadDatamodelSource(Datamodel dm, DatamodelSource source) {
        switch (source.Type) {
            case DatamodelSourceType.AssemblyNameReference:
                Assembly assembly;
                if (source.Reference == null) {
                    assembly = Assembly.GetEntryAssembly()!;
                } else {
                    assembly = Assembly.Load(source.Reference!);
                }
                dm.AddAssembly(assembly, source.Namespace!);
                break;
            case DatamodelSourceType.TypeNameReference:
                var type = Type.GetType(source.Reference!);
                dm.Add(type!);
                break;
            case DatamodelSourceType.AssemblyFileReference:
                throw new NotImplementedException();
            case DatamodelSourceType.TypeNameFileReference:
                throw new NotImplementedException();
            case DatamodelSourceType.JsonFile: {
                    if (source.FileIO == null) throw new Exception("FileIO is required for JsonFile DatamodelSource");
                    if (!server.TryGetIO(source.FileIO.Value, out var io)) throw new Exception("FileIO not found for JsonFile DatamodelSource");
                    var json = io.ReadAllTextUTF8(source.Reference!);
                    var dm2 = System.Text.Json.JsonSerializer.Deserialize<Datamodel>(json);
                    if (dm2 == null) throw new Exception("Failed to deserialize Datamodel from JsonFile");
                    dm.AddDatamodel(dm2);
                    break;
                }
            case DatamodelSourceType.CSharpCodeFile:
                throw new NotImplementedException();
            default:
                throw new NotImplementedException();
        }
    }
    public void Dispose() {
        CloseIfOpen();
    }
}
