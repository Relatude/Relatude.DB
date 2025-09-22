﻿using System.Diagnostics;
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
using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;

namespace Relatude.DB.NodeServer;
public class NodeStoreContainer(NodeStoreContainerSettings settings, RelatudeDBServer server) : IDisposable {

    internal IDataStore? datastore { get; set; }
    internal object _lock = new object();
    internal Logger? _logger;
    public NodeStore? Store { get; private set; }


    public Logger GetLogger() {
        lock (_lock) {
            if (IsOpenOrOpening()) return Store!.Datastore.Logger;
            if (_logger == null) _logger = new Logger(getLoggerIO(), getLoggerFileKeys(), null);
            return _logger;
        }
    }


    public Datamodel? Datamodel { get; private set; }
    public DataStoreStatus Status {
        get {
            if (datastore == null) return new DataStoreStatus(DataStoreState.Closed, []);
            return datastore.GetStatus();
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
        if(ioLog == null) throw new Exception("IoLog or IoDatabase is required for NodeStoreContainerSettings");
        return ioLog;
    }
    public void Open() {
        try {
            if (_logger != null) _logger.Dispose();
            if (IsOpenOrOpening()) return;
            CloseIfOpen();
            if (settings.LocalSettings == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings, RemoteSettings will be added later");
            Datamodel = loadDatamodel();
            IIOProvider? ioDatabase = settings.IoDatabase.HasValue && settings.IoDatabase != Guid.Empty ? server.GetIO(settings.IoDatabase.Value) : null;
            string? diskFallBackPath = null;
            if (ioDatabase is IODisk ioDisk) diskFallBackPath = ioDisk.BaseFolder;
            IFileStore[]? fs = null;
            if (settings.IoFiles != null) {
                foreach (var ioFilesId in settings.IoFiles) {
                    if (!server.TryGetIO(ioFilesId, out var ioFiles)) continue;
                    var fileKey = new FileKeyUtility(settings.LocalSettings.FilePrefix).FileStore_GetLatestFileKey(ioFiles);
                    if (fs == null) fs = [new SingleFileStore(Guid.Empty, ioFiles, fileKey)];
                    else fs = [.. fs, new SingleFileStore(Guid.Empty, ioFiles, fileKey)];
                }
            }
            IIOProvider? ioBackup = settings.IoBackup.HasValue && settings.IoBackup != Guid.Empty ? server.GetIO(settings.IoBackup.Value) : null;
            IIOProvider? ioLog = settings.IoLog.HasValue && settings.IoLog != Guid.Empty ? server.GetIO(settings.IoLog.Value) : null;
            IAIProvider? ai = settings.AiProvider.HasValue && settings.AiProvider != Guid.Empty ?
                server.GetAI(settings.AiProvider.Value, Settings.LocalSettings?.FilePrefix, diskFallBackPath) : null;
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
                if (string.IsNullOrEmpty(queuePath)) queuePath = diskFallBackPath;
                if (string.IsNullOrEmpty(queuePath)) throw new Exception("The setting PersistedQueueStoreFolderPath is required for LocalSettings.PersistedQueueStoreFolderPath");
                if (!Path.IsPathRooted(queuePath)) queuePath = server.RootDataFolderPath.SuperPathCombine(queuePath);
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
                    createIndexStore,
                    queueStore);
            Interlocked.Increment(ref _initializationCounter);
            var runners = server.GetRegisteredTaskRunners(this);
            foreach (var runner in runners) datastore.RegisterRunner(runner);
            Store = new NodeStore(datastore);
            server.RaiseEventStoreInit(this, Store);
            try {
                datastore.Open(false, false);
            } catch {
                datastore.Dispose();
                datastore = null;
                throw;
            }
            datastore.LogInfo($"NodeStore ready in {sw.ElapsedMilliseconds.To1000N()}ms.");
            server.RaiseEventStoreOpen(this, Store);
        } catch {
            Interlocked.Increment(ref _hasFailedCounter);
            throw;
        }
    }
    public void CloseIfOpen() {
        datastore = null;
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
