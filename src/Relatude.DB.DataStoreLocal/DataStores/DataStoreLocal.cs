using System.Data;
using System.Diagnostics;
using Relatude.DB.AccessControl;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Files;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Scheduling;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Query;
using Relatude.DB.Query.Data;
using Relatude.DB.Tasks;
using Relatude.DB.Tasks.TextIndexing;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores;
public delegate byte[][] ReadSegmentsFunc(NodeSegment[] segments, out int noDiskReads);
public sealed partial class DataStoreLocal : IDataStore {
    readonly SettingsLocal _settings;
    DataStoreState _state = DataStoreState.Closed;
    internal Definition _definition = default!;
    internal GuidStore _guids = default!;
    internal IndexStore _index = default!;
    internal RelationStore _relations = default!;
    internal WALFile _wal = default!;
    internal NodeStore _nodes = default!;
    internal Variables _variables = default!;
    long _startUpTimeMs;
    readonly ReaderWriterLockSlim _lock;
    readonly FileKeyUtility _fileKeys;
    readonly IIOProvider _io;
    readonly IIOProvider _ioLog;
    readonly IIOProvider _ioLog2;
    readonly IIOProvider _ioAutoBackup;
    readonly Scheduler _scheduler;
    readonly Dictionary<Guid, IFileStore> _fileStores = new();
    readonly IFileStore _defaultFileStore;
    readonly StoreLogger _logger;
    public TaskQueue TaskQueue { get; }
    public TaskQueue TaskQueuePersisted { get; }
    internal readonly AIEngine? _ai;
    LogRewriter? _rewriter = null;
    NodeWriteLocks _nodeWriteLocks = default!;
    public Datamodel Datamodel { get; }
    SetRegister _sets = default!;
    DateTime _initiatedUtc;
    NativeModelStore _nativeModelStore;
    // QueryContext _defaultUserCtx;
    internal IPersistedIndexStore? PersistedIndexStore;
    Func<IPersistedIndexStore>? _createPersistedIndexStore;
    long _noPrimitiveActionsSinceLastStateSnaphot;
    long _noPrimitiveActionsInLogThatCanBeTruncated;
    long _noPrimitiveActionsSinceClearCache;

    long _noTransactionsSinceLastStateSnaphot;
    long _noTransactionsSinceClearCache;
    long _noNodeGetsSinceClearCache;

    long _noQueriesSinceClearCache;
    Dictionary<string, ITaskRunner> _taskRunners = [];

    public DataStoreLocal(
        Datamodel dm,
        SettingsLocal? settings = null,
        IIOProvider? dbIO = null,
        IFileStore[]? filestores = null,
        IIOProvider? bkup = null,
        IIOProvider? log = null,
        AIEngine? ai = null,
        Func<IPersistedIndexStore>? createPersistedIndexStore = null,
        IQueueStore? queueStore = null,
        IIOProvider? secondaryLogIO = null
        ) {
        _initiatedUtc = DateTime.UtcNow;
        //_defaultUserCtx = null!;// UserContext.Anonymous(1033); // _settings?.DefaultLcid ?? 1033);
        _lock = new(LockRecursionPolicy.SupportsRecursion);
        if (dbIO == null) dbIO = new IOProviderMemory();
        _io = dbIO;
        _ioAutoBackup = bkup ?? _io;
        _ioLog = log ?? _io;
        _ioLog2 = secondaryLogIO ?? _io;
        if (filestores != null) foreach (var fs in filestores) _fileStores.Add(fs.Id, fs);
        _ai = ai;
        if (_ai != null) _ai.LogCallback = (string text) => Log(SystemLogEntryType.Info, text);
        _settings = settings ?? new();
        _createPersistedIndexStore = createPersistedIndexStore;
        _fileKeys = new FileKeyUtility(_settings.FilePrefix);
        _logger = new(_ioLog, _fileKeys, dm);
        RegisterRunner(new IndexTaskRunner(this));
        RegisterRunner(new SemanticIndexTaskRunner(this, _ai));
        TaskQueue = new(this, new DefaultQueueStore(_taskRunners), _taskRunners);
        if (queueStore == null) {
            if (_settings.PersistedQueueStoreEngine == PersistedQueueStoreEngine.BuiltIn) {
                queueStore = new DefaultQueueStore(_taskRunners, _io, _fileKeys.Queue_GetFileKey("bin"));
            } else if (_settings.PersistedQueueStoreEngine == PersistedQueueStoreEngine.Memory) {
                queueStore = new DefaultQueueStore(_taskRunners);
            } else {
                throw new Exception("Queue store engine must be set to either BuiltIn or Memory if no queueStore is provided.");
            }
        }
        TaskQueuePersisted = new(this, queueStore, _taskRunners);
        Datamodel = dm;
        dm.EnsureInitalization();
        dm.SetIndexDefaults(_settings.EnableTextIndexByDefault, _settings.EnableSemanticIndexByDefault, _settings.EnableInstantTextIndexingByDefault);
        if (!_fileStores.ContainsKey(Guid.Empty)) {
            var fs = new SingleFileStore(Guid.Empty, _io, _fileKeys.FileStore_GetLatestFileKey(_io));
            _fileStores.Add(fs.Id, fs);
        }
        _nativeModelStore = new(this);
        _defaultFileStore = _fileStores[Guid.Empty];
        LogRewriter.CleanupOldPartiallyCompletedLogRewriteIfAny(_io);
        _scheduler = new(this);
        try {
            initialize();
        } catch {
            Dispose(); // release resources
            throw;
        }
    }
    void validateDatabaseState() {
        if (_state != Common.DataStoreState.Open) throw new Exception("Store not opened. Current state is: " + _state);
    }
    public void RegisterRunner(ITaskRunner runner) {
        if (_state != DataStoreState.Closed) {
            throw new InvalidOperationException("Cannot register task runner while the datastore not in closed state. Current state: " + _state);
        }
        _taskRunners[runner.TaskTypeId] = runner;
    }
    public void EnqueueTask(TaskData task, string? jobId = null) {
        if (!_taskRunners.TryGetValue(task.TaskTypeId, out var runner)) {
            throw new Exception("No task runner registered for task type: " + task.TaskTypeId);
        }
        if (!runner.PersistToDisk || TaskQueuePersisted == null) {
            TaskQueue.Enqueue(task, jobId);
        } else {
            TaskQueuePersisted.Enqueue(task, jobId);
        }
    }
    public FileKeyUtility FileKeys => _fileKeys;
    public ILogStore LogStore => _logger.LogStore;
    public AIEngine AI => _ai ?? throw new Exception("No AI provider configured for this datastore.");
    public IStoreLogger Logger => _logger;
    public IIOProvider IO => _io;
    public IIOProvider IOBackup => _ioAutoBackup;
    public SettingsLocal Settings => _settings;
    public long Timestamp {
        get {
            validateDatabaseState();
            _lock.EnterReadLock();
            try {
                return _wal.LastTimestamp;
            } finally {
                _lock.ExitReadLock();
            }
        }
    }
    public static DataStoreLocal Open(
        Datamodel dm,
        SettingsLocal? settings = null,
        IIOProvider? dbIO = null,
        IFileStore[]? filestores = null,
        IIOProvider? bkup = null,
        IIOProvider? log = null,
        AIEngine? ai = null,
        Func<IPersistedIndexStore>? createPersistedIndexStore = null,
        bool? throwOnBadStateFile = false,
        bool? throwOnBadLogFile = false
        ) {
        settings ??= new();
        var d = new DataStoreLocal(dm, settings, dbIO, filestores, bkup, log, ai, createPersistedIndexStore);
        try {
            d.Open(throwOnBadLogFile ?? settings.ThrowOnBadLogFile,
                throwOnBadStateFile ?? settings.ThrowOnBadStateFile);
            return d;
        } catch (Exception err) {
            Console.WriteLine("Datastore open failed: " + err.Message);
            d.Dispose();
            throw;
        }
    }
    public DataStoreState State => _state;
    void initialize() {
        _sets = new((long)(_settings.SetCacheSizeGb * 1024d * 1024d * 1024d));
        _guids = new();
        _definition = new(_sets, Datamodel, this);

        if (_createPersistedIndexStore != null) {
            PersistedIndexStore = _createPersistedIndexStore();
        }
        DiskFlushCallback? indexStoreFlushCallback = timestamp => {
            _lock.EnterWriteLock();
            try {
                if (PersistedIndexStore != null) PersistedIndexStore.Commit(timestamp);
                if (TaskQueuePersisted != null) TaskQueuePersisted.FlushDisk();
            } finally {
                _lock.ExitWriteLock();
            }
        };
        var fileKey = _fileKeys.WAL_GetLatestFileKey(_io);
        var io2 = _settings.SecondaryBackupLog ? _ioLog2 : null;
        var fileKey2 = _settings.SecondaryBackupLog ? _fileKeys.WAL_GetSecondaryFileKey() : null;
        _wal = new(fileKey, _definition, _io, updateNodeDataPositionInLogFile, indexStoreFlushCallback, io2, fileKey2);
        _nodes = new(_definition, _settings, readSegments);
        _relations = new(_definition);
        _index = new(_definition);
        _definition.Initialize(this, _settings, _io, _ai);
        _variables = getRootVariables();
        _nodeWriteLocks = new();
        PersistedIndexStore?.ReOpen();
        TaskQueue?.ReOpen();
        TaskQueuePersisted?.ReOpen();
        logLine___________________________();
        LogInfo("Database intialized");
    }
    public void Open(bool throwOnBadLogFile = false, bool throwOnBadStateFile = false) {
        var sw = Stopwatch.StartNew();
        _scheduler.Stop();
        _lock.EnterWriteLock();
        LogInfo("Database opening");
        var activityId = RegisterActvity(DataStoreActivityCategory.Opening, "Database opening", 0);
        var currentModelHash = getCheckSumForStateFileAndIndexes();
        try {
            if (_state != DataStoreState.Closed) throw new Exception("Store cannot be opened as current state is " + _state);
            _state = DataStoreState.Opening;
            _wal.EnsureSecondaryLogFile(activityId, this, false);
            readState(throwOnBadStateFile, currentModelHash, activityId);
            _state = DataStoreState.Open;
            _startUpTimeMs = sw.ElapsedMilliseconds;
            LogInfo("Database ready in " + _startUpTimeMs.To1000N() + "ms.");
        } catch (StateFileReadException e) {
            LogInfo("Indexfile out of sync: " + e.Message);
            if (throwOnBadStateFile) {
                _state = DataStoreState.Error;
                throw;
            } else { // delete state file and reload
                try {
                    LogInfo("Rebuilding index from log");
                    UpdateActivity(activityId, "Rebuilding index from log", 0);
                    _io.DeleteIfItExists(_fileKeys.StateFileKey);
                    Dispose();
                    initialize();
                    readState(throwOnBadStateFile, currentModelHash, activityId);
                    _state = DataStoreState.Open;
                    _startUpTimeMs = sw.ElapsedMilliseconds;
                    LogInfo("Database ready in " + _startUpTimeMs.To1000N() + "ms.");
                } catch (Exception reloadError) {
                    LogInfo("Reopen failed. " + reloadError.Message);
                    _state = DataStoreState.Error;
                    throw;
                }
            }
        } finally {
            if (_state == DataStoreState.Error) Dispose();
            DeRegisterActivity(activityId);
            _lock.ExitWriteLock();
        }
        if (_state == DataStoreState.Open) {
            _scheduler.Start();
        }
    }

    Variables getRootVariables() {
        Variables vars = new Variables();
        // Sample static data:
        //vars.DeclarerAndSet("Culture", () => {
        //    TableData countries;
        //    countries = new TableData();
        //    countries.AddColumn("LCID", PropertyType.Integer);
        //    countries.AddColumn("Country", PropertyType.String);
        //    foreach (var c in CultureInfo.GetCultures(CultureTypes.AllCultures)) countries.AddRow(c.LCID, c.EnglishName);
        //    return countries;
        //});
        foreach (var type in _definition.NodeTypes.Values) {
            var callback = (Metrics metrics) => new NodeCollectionData(this, metrics, _definition.GetAllIdsForType(type.Id, true), type, null);
            vars.DeclarerAndSet(type.CodeName, callback);
            if (type.Id == NodeConstants.BaseNodeTypeId) vars.DeclarerAndSet(nameof(Object), callback); // INode == Object
        }
        return vars;
    }
    void validateIndexesIfDebug() {
        //#if DEBUG
        //        // temporary code, to be deleted later on;
        //        // testing indexes
        //        foreach (var n in _nodes.Snapshot()) {
        //            var uid = n.nodeId;
        //            var node = _nodes.Get(uid);
        //            if (_definition.GetTypeOfNode(uid) != node.NodeType) {
        //                throw new Exception("Node type mismatch. ");
        //            }
        //            if (_guids.GetId(node.Id) != uid) {
        //                throw new Exception("Guid mismatch. ");
        //            }
        //            if (_guids.GetGuid(uid) != node.Id) {
        //                throw new Exception("Guid mismatch. ");
        //            }
        //        }



        //        // validating all relations, to ensure that all nodes exists, this step is not needed for normal operation, but is needed for recovery
        //        foreach (var r in _definition.Relations.Values) {
        //            foreach (var v in r.Values) {
        //                if (!_nodes.Contains(v.Target)) {
        //                    throw new Exception("Relation to node ID : " + v + " refers to a non-existing node. RelationID " + r.Id);
        //                    // r.DeleteIfReferenced(id); // fix
        //                }
        //                if (!_nodes.Contains(v.Source)) {
        //                    throw new Exception("Relation to node ID : " + v + " refers to a non-existing node. RelationID " + r.Id);
        //                    // r.DeleteIfReferenced(id); // fix
        //                }
        //            }
        //        }
        //#endif
    }
    public long GetLastTimestampID() {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            return _wal.LastTimestamp;
        } finally {
            _lock.ExitReadLock();
        }
    }
    public void Dispose() {
        try { TaskQueue?.TryGracefulShutdown(5000); } catch { }
        try { TaskQueuePersisted?.TryGracefulShutdown(5000); } catch { }
        try { foreach (var fs in _fileStores.Values) fs.Dispose(); } catch { }
        try { _scheduler.Stop(); } catch { }
        try { if (_state == DataStoreState.Open) FlushToDisk(true, 0); } catch { }
        try { _index?.Dispose(); } catch { }
        try { _wal?.Dispose(); } catch { }
        try { _logger?.Dispose(); } catch { }
        try { _ai?.Dispose(); } catch { }
        try { PersistedIndexStore?.Dispose(); } catch { }
        try { TaskQueue?.Dispose(); } catch { }
        try { TaskQueuePersisted?.Dispose(); } catch { }
        if (_state == DataStoreState.Open) _state = DataStoreState.Closed; // if in error state, do not change state
        try { this._io.CloseAllOpenStreams(); } catch { }
        try { this._ioAutoBackup.CloseAllOpenStreams(); } catch { }
        try { this._ioLog.CloseAllOpenStreams(); } catch { }
        try { this._ioLog2.CloseAllOpenStreams(); } catch { }
    }
}
