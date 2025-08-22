using System.Diagnostics;
using System.Reflection;
using WAF.AI;
using WAF.Common;
using WAF.Datamodels;
using WAF.DataStores.Indexes;
using WAF.DataStores;
using WAF.IO;
using WAF.Nodes;
using WAF.Tasks;
using WAF.DataStores.Files;

namespace WAF.NodeServer;
public class NodeStoreContainer(NodeStoreContainerSettings settings) : IDisposable {

    internal IDataStore? datastore { get; set; }
    public NodeStore? Store { get; private set; }
    public Datamodel? Datamodel { get; private set; }
    public DataStoreStatus Status {
        get {
            if (datastore == null) return new DataStoreStatus(DataStoreState.Closed, DataStoreActivity.None);
            return datastore.GetStatus();
        }
    }
    public bool IsOpen() => Store != null;
    public NodeStoreContainerSettings Settings => settings;
    public void ApplyNewSettings(NodeStoreContainerSettings newSettings, bool reopenIfOpen) {
        var isOpen = IsOpen();
        CloseIfOpen();
        settings = newSettings;
        if (isOpen && reopenIfOpen) Open(false);
    }

    public ContainerLog ContainerLog { get; set; } = new(1000, TimeSpan.FromDays(2));
    void log(string msg) => ContainerLog.Add(msg);
    int _initializationCounter = 0;
    int _hasFailedCounter = 0;
    public bool HasInitialized => Interlocked.CompareExchange(ref _initializationCounter, 0, 0) > 0;
    public bool HasFailed => Interlocked.CompareExchange(ref _hasFailedCounter, 0, 0) > 0;
    public void Open(bool ignoreErrors) {
        try {
            if (IsOpen()) return;
            if (settings.LocalSettings == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings, RemoteSettings will be added later");
            Datamodel = loadDatamodel(ignoreErrors);
            IIOProvider? ioDatabase = settings.IoDatabase.HasValue && settings.IoDatabase != Guid.Empty ? WAFServer.GetIO(settings.IoDatabase.Value) : null;
            IFileStore[]? fs = null;
            if (settings.IoFiles != null) {
                foreach (var ioFilesId in settings.IoFiles) {
                    if (!WAFServer.TryGetIO(ioFilesId, out var ioFiles)) continue;
                    var fileKey = new FileKeyUtility(settings.LocalSettings.FilePrefix).FileStore_GetLatestFileKey(ioFiles);
                    if (fs == null) fs = [new SingleFileStore(Guid.Empty, ioFiles, fileKey)];
                    else fs = [.. fs, new SingleFileStore(Guid.Empty, ioFiles, fileKey)];
                }
            }
            IIOProvider? ioBackup = settings.IoBackup.HasValue && settings.IoBackup != Guid.Empty ? WAFServer.GetIO(settings.IoBackup.Value) : null;
            IIOProvider? ioLog = settings.IoLog.HasValue && settings.IoLog != Guid.Empty ? WAFServer.GetIO(settings.IoLog.Value) : null;
            IAIProvider? ai = settings.AiProvider.HasValue && settings.AiProvider != Guid.Empty ?
                WAFServer.GetAI(settings.AiProvider.Value, Settings.LocalSettings?.FilePrefix, log) : null;
            Func<IPersistedIndexStore>? createIndexStore = null;

            if (settings.LocalSettings.PersistedValueIndexEngine != PersistedValueIndexEngine.Memory) {
                if (settings.LocalSettings.PersistedTextIndexEngine == PersistedTextIndexEngine.Memory) {
                    throw new Exception("The setting PersistedTextIndexEngine must be set to Sqlite or Lucene when PersistedValueIndexEngine is Sqlite.");
                }
                createIndexStore = () => {
                    var indexPath = settings.LocalSettings.PersistedValueIndexFolderPath;
                    if (indexPath == null) {
                        if (ioDatabase is IODisk ioDisk) indexPath = ioDisk.BaseFolder;
                        else throw new Exception("The setting PersistedValueIndexFolderPath is required for LocalSettings.PersistedValueIndexFolderPath");
                    }
                    indexPath = Path.Combine(indexPath, new FileKeyUtility(settings.LocalSettings.FilePrefix).IndexStoreFolderKey);
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
                if (queuePath == null) {
                    if (ioDatabase is IODisk ioDisk) queuePath = ioDisk.BaseFolder;
                    else throw new Exception("The setting PersistedQueueStoreFolderPath is required for LocalSettings.PersistedQueueStoreFolderPath");
                }
                queuePath = Path.Combine(queuePath, new FileKeyUtility(settings.LocalSettings.FilePrefix).Queue_GetFileKey("sqlite"));
                queueStore = LateBindings.CreateSqliteQueueStore(queuePath);
            }
            var sw = Stopwatch.StartNew();
            datastore = new DataStoreLocal(
                    Datamodel,
                    settings.LocalSettings,
                    ioDatabase,
                    fs,
                    ioBackup,
                    ioLog,
                    ai,
                    log,
                    createIndexStore,
                    queueStore);
            Interlocked.Increment(ref _initializationCounter);
            var runners = WAFServer.GetRegisteredTaskRunners(this);
            foreach (var runner in runners) datastore.RegisterRunner(runner);
            Store = new NodeStore(datastore);
            WAFServer.RaiseEventStoreInit(this, Store);
            try {
                datastore.Open(false, false);
            } catch {
                datastore.Dispose();
                datastore = null;
                throw;
            }
            datastore.Log($"NodeStore ready in {sw.ElapsedMilliseconds.To1000N()}ms.");
            WAFServer.RaiseEventStoreOpen(this, Store);
        } catch {
            Interlocked.Increment(ref _hasFailedCounter);
            throw;
        }
    }
    public void CloseIfOpen() {
        datastore = null;
        if (Store != null) {
            Store.Dispose();
            Store = null;
            Datamodel = null;
        }
    }
    Datamodel loadDatamodel(bool ignoreErrors) {
        var dm = new Datamodel();
        if (settings.DatamodelSources != null) {
            foreach (var source in settings.DatamodelSources) {
                try {
                    loadDatamodelSource(dm, source);
                } catch (Exception ex) {
                    var msg = $"Failed to load datamodel source {source.Id}: {ex.Message}";
                    if (ignoreErrors) log(msg);
                    else throw new Exception(msg, ex);
                }
            }
        }
        return dm;
    }
    public void ClearContainerLog() {
        ContainerLog.Clear();
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
                    if (!WAFServer.TryGetIO(source.FileIO.Value, out var io)) throw new Exception("FileIO not found for JsonFile DatamodelSource");
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
