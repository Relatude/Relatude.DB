using System.Data;
using System.Diagnostics;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Files;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Scheduling;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.DataStores.Uploads;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Query;
using Relatude.DB.Query.Data;
using Relatude.DB.Tasks;
using Relatude.DB.Tasks.TextIndexing;
using Relatude.DB.Transactions;
using Relatude.DB.Web;

namespace Relatude.DB.DataStores;

public delegate byte[][] ReadSegmentsFunc(NodeSegment[] segments, out int noDiskReads);
public sealed partial class DataStoreLocal : IDataStore {
    readonly SettingsLocal _settings;
    DataStoreState _state;
    internal Definition _definition = default!;
    internal GuidStore _guids = default!;
    internal AddressRegistry _addresses = default!;
    internal IndexStore _index = default!;
    internal RelationStore _relations = default!;
    internal WALFile _wal = default!;
    internal NodeStore _nodes = default!;
    internal Variables _variables = default!;
    long _startUpTimeMs;
    readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    internal readonly UrlSystem _urls;
    readonly FileConversionEngine _fileConversionEngine;
    readonly IIOProvider _io;
    readonly IIOProvider _ioIndex;
    readonly IIOProvider _ioLog;
    readonly IIOProvider _ioLog2;
    readonly IIOProvider _ioAutoBackup;
    readonly UploadSessions _uploads;

    readonly Scheduler _scheduler;
    readonly Dictionary<Guid, IFileStore> _fileStores = new();
    readonly IFileStore _defaultFileStore;
    readonly StoreLogger _logger;
    QueryContext _defaultQueryCtx;
    public TaskQueue TaskQueue { get; }
    public TaskQueue TaskQueuePersisted { get; }
    public int TaskQueueThrottle { get => _scheduler.GetTaskQueuesThrottle(); set => _scheduler.ThrottleTaskQueue(value); }
    internal readonly AIEngine? _ai;
    LogRewriter? _rewriter = null;
    NodeWriteLocks _nodeWriteLocks = default!;
    public Datamodel Datamodel { get; }
    SetRegister _sets = default!;
    DateTime _initiatedUtc;
    internal readonly NativeModelStore _nativeModelStore;
    internal IndexEngines Engines = IndexEngines.Empty;
    Func<IndexEngines>? _createIndexEngines;

    long _noPrimitiveActionsSinceStartup;
    long _noPrimitiveActionsSinceLastStateSnapshot;

    long _noPrimitiveActionsInLogThatCanBeTruncated;
    long _noPrimitiveActionsSinceClearCache;

    long _noTransactionsSinceLastStateSnapshot;
    long _noTransactionsSinceClearCache;
    long _noNodeGetsSinceClearCache;

    long _noActionsSinceLastMetric;
    long _noTransactionsSinceLastMetric;
    long _noQueriesSinceLastMetric;

    long _noQueriesSinceClearCache;
    Dictionary<string, ITaskRunner> _taskRunners = [];

    public DataStoreLocal(
        Datamodel datamodel,
        SettingsLocal? settings = null,
        IIOProvider? dbIO = null,
        IFileStore[]? filestores = null,
        IIOProvider? bkup = null,
        IIOProvider? log = null,
        AIEngine? ai = null,
        Func<IndexEngines>? createIndexEngines = null,
        IQueueStore? queueStore = null,
        IIOProvider? secondaryLogIO = null,
        IIOProvider? indexIO = null,
        QueryContext? defaultQueryContext = null,
        IFileConverter[]? fileConverters = null,
        IIOProvider? converterIoProvider = null,
        IUrlManager? urlManager = null
        ) {
        _state = DataStoreState.Closed;
        _initiatedUtc = DateTime.UtcNow;
        _defaultQueryCtx = defaultQueryContext ?? QueryContext.Default;
        if (dbIO == null) dbIO = new IOProviderMemory();
        _io = dbIO;
        _ioIndex = indexIO ?? _io;
        _ioAutoBackup = bkup ?? _io;
        _ioLog = log ?? _io;
        _ioLog2 = secondaryLogIO ?? _io;
        fileConverters = [.. (fileConverters ?? []), new NativeImageConverter()];
        if (filestores != null) foreach (var fs in filestores) _fileStores.Add(fs.Id, fs);
        _ai = ai;
        if (_ai != null) _ai.LogCallback = (string text) => Log(SystemLogEntryType.Info, text);
        _settings = settings ?? new();
        _settings.ValidateIndexEngines(); // a default naming an engine its list lacks fails here, by name, not inside index creation

        var treeUrlOptions = settings?.UrlOptions ?? new DefaultUrlManagerOptions();
        urlManager ??= new DefaultUrlManager(treeUrlOptions); // without a parent relation the built-in manager runs flat: every node at "/{address}"
        _urls = new UrlSystem(this, urlManager);

        _createIndexEngines = createIndexEngines;
        // must run before the logger below (which reads its files), before the rewrite cleanup and
        // before the WAL opens; the messages are buffered and logged once the logger exists
        var migrationLog = moveLegacyFilesIntoFolders();
        _logger = new(_ioLog, datamodel);
        // a fresh logger records nothing: the settings are what a log turned on in the admin UI and
        // saved there is restored from
        if (_settings.LogRecording != null) _logger.ApplyRecordingSettings(_settings.LogRecording);
        _logger.MinDurationMsBeforeLogging = _settings.MinQueryDurationMsBeforeLogging;
        foreach (var line in migrationLog) LogInfo(line);
        if (converterIoProvider == null) converterIoProvider = _ioIndex;
        _fileConversionEngine = new(this, fileConverters, converterIoProvider);
        RegisterRunner(new TextIndexTaskRunner(this));
        if (_ai != null) RegisterRunner(new SemanticIndexTaskRunner(this, _ai));
        RegisterRunner(new RewriteTaskRunner(this));
        TaskQueue = new(this, new DefaultQueueStore(_taskRunners), _taskRunners);
        if (queueStore == null) {
            if (_settings.PersistedQueueStoreEngine == PersistedQueueStoreEngine.Native) {
                queueStore = new DefaultQueueStore(_taskRunners, _ioIndex, FileKeyUtility.Queue_GetFileKey("bin"));
            } else if (_settings.PersistedQueueStoreEngine == PersistedQueueStoreEngine.Memory) {
                queueStore = new DefaultQueueStore(_taskRunners);
            } else {
                throw new Exception("Queue store engine must be set to either BuiltIn or Memory if no queueStore is provided.");
            }
        }
        TaskQueuePersisted = new(this, queueStore, _taskRunners);
        Datamodel = datamodel;
        datamodel.EnsureInitalization();
        datamodel.SetIndexDefaults(_settings.EnableTextIndexByDefault, _settings.EnableSemanticIndexByDefault, _settings.EnableInstantTextIndexingByDefault);
        urlManager.Initialize(this); // after the datamodel is set, so managers can resolve relations and types
        _nativeModelStore = new(this);
        if (_settings.DefaultFileStore.HasValue) {
            if (_fileStores.TryGetValue(_settings.DefaultFileStore.Value, out var fileStore)) {
                _defaultFileStore = fileStore;
            } else {
                throw new Exception("Default file store with ID " + _settings.DefaultFileStore.Value + " not found among provided file stores.");
            }
        }
        // no configured store named as the default: the implicit one, a MultiFile store on the
        // database's own provider. Files record Guid.Empty as their store id, so this is not a choice
        // that can change once files exist - which is why it is not a setting
        _defaultFileStore ??= new MultiFileStore(Guid.Empty, _io, 2);
        LogRewriter.CleanupOldPartiallyCompletedLogRewriteIfAny(_io);
        _scheduler = new(this);
        _uploads = new(this);
        try {
            initialize();
        } catch {
            Dispose(); // release resources
            throw;
        }
    }
    // Before the folder layout (data/state/backup/log) every file lived in the storage root. On
    // startup the old files are moved into their folders: the database log files, backups, the
    // state snapshot, index states, mapper dlls, queue files and the logger files. The moved state
    // snapshot is then used at open, so the upgrade keeps the fast startup. (The ai cache and the
    // sqlite queue live on the local disk outside the IO providers and are moved by the server
    // layer.) Returns the log lines describing what was moved, to be logged once the logger exists.
    List<string> moveLegacyFilesIntoFolders() {
        var log = new List<string>();
        foreach (var key in FileKeyUtility.WAL_GetLegacyRootFileKeys(_io)) moveLegacyFileIntoFolder(_io, key, FileKeyUtility.DataFolderName, LegacyConflict.Throw, log);
        var legacySecondary = FileKeyUtility.WAL_GetLegacyRootSecondaryFileKey();
        if (_ioLog2.Exists(legacySecondary)) moveLegacyFileIntoFolder(_ioLog2, legacySecondary, FileKeyUtility.DataFolderName, LegacyConflict.Throw, log);
        foreach (var key in FileKeyUtility.Legacy_GetRootBackupFileKeys(_ioAutoBackup)) moveLegacyFileIntoFolder(_ioAutoBackup, key, FileKeyUtility.BackupFolderName, LegacyConflict.LeaveLegacy, log);
        foreach (var key in FileKeyUtility.Legacy_GetRootStateFileKeys(_ioIndex)) moveLegacyFileIntoFolder(_ioIndex, key, FileKeyUtility.StateFolderName, LegacyConflict.DeleteLegacy, log);
        foreach (var key in FileKeyUtility.Legacy_GetRootLoggerFileKeys(_ioLog)) moveLegacyFileIntoFolder(_ioLog, key, FileKeyUtility.LogFolderName, LegacyConflict.LeaveLegacy, log);
        return log;
    }
    // What to do when the destination already exists with a DIFFERENT size than the legacy file:
    // Throw for the database log files (primary data, ambiguity must stop the startup), DeleteLegacy
    // for everything rebuildable from the log (the folder version is the one the store has been
    // using, the root file is stale), LeaveLegacy for backups and logger history (never delete,
    // never block the startup over them).
    enum LegacyConflict { Throw, DeleteLegacy, LeaveLegacy }
    static void moveLegacyFileIntoFolder(IIOProvider io, string[] legacyKey, string folder, LegacyConflict onConflict, List<string> log) {
        string[] newKey = [folder, .. legacyKey];
        if (io.Exists(newKey)) {
            if (io.GetFileSizeOrZeroIfUnknown(newKey) == io.GetFileSizeOrZeroIfUnknown(legacyKey)) {
                // an earlier migration crashed between copy and delete; finish it
                io.DeleteFileIfItExists(legacyKey);
                log.Add($"Removed legacy file {legacyKey.AsKeyString()}, already migrated to {newKey.AsKeyString()}. ");
                return;
            }
            switch (onConflict) {
                case LegacyConflict.DeleteLegacy:
                    io.DeleteFileIfItExists(legacyKey);
                    log.Add($"Removed stale legacy file {legacyKey.AsKeyString()}; {newKey.AsKeyString()} is in use. ");
                    return;
                case LegacyConflict.LeaveLegacy:
                    log.Add($"Legacy file {legacyKey.AsKeyString()} left in the storage root: {newKey.AsKeyString()} already exists with a different size. ");
                    return;
                default:
                    throw new Exception($"Cannot move legacy file {legacyKey.AsKeyString()} to {newKey.AsKeyString()} as both exist with different sizes. Remove one of them manually. ");
            }
        }
        io.EnsureFolder([folder]);
        if (io.CanRenameFile) {
            io.RenameFile(legacyKey, newKey);
        } else {
            io.CopyFile(io, legacyKey, newKey);
            if (io.GetFileSizeOrZeroIfUnknown(newKey) != io.GetFileSizeOrZeroIfUnknown(legacyKey))
                throw new Exception($"Failed to copy legacy file {legacyKey.AsKeyString()} to {newKey.AsKeyString()}, size mismatch. ");
            io.DeleteFileIfItExists(legacyKey);
        }
        log.Add($"Moved legacy file {legacyKey.AsKeyString()} to {newKey.AsKeyString()}. ");
    }
    public QueryContext QueryContext => _defaultQueryCtx;
    public void SetDefaultQueryContext(QueryContext ctx) {
        _lock.EnterWriteLock();
        try {
            _defaultQueryCtx = ctx;
        } finally {
            _lock.ExitWriteLock();
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
    public ILogStore LogStore => _logger.LogStore;
    public AIEngine AI => _ai ?? throw new Exception("No AI provider configured for this datastore.");
    public IStoreLogger Logger => _logger;
    public IIOProvider IO => _io;
    public IIOProvider IOIndex => _ioIndex;
    public IIOProvider IOBackup => _ioAutoBackup;
    // distinct: unconfigured roles fall back to the same instance, and consumers (file size totals,
    // stream cleanup) must see each provider once
    public IEnumerable<IIOProvider> AllIOs => new[] { _io, _ioIndex, _ioAutoBackup, _ioLog, _ioLog2 }.Where(io => io != null).Distinct();
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
        Func<IndexEngines>? createIndexEngines = null,
        bool? throwOnBadStateFile = false,
        bool? throwOnBadLogFile = false
        ) {
        settings ??= new();
        var d = new DataStoreLocal(dm, settings, dbIO, filestores, bkup, log, ai, createIndexEngines);
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
        _addresses = new();
        _definition = new(_sets, Datamodel, this);

        Engines = _createIndexEngines?.Invoke() ?? IndexEngines.Empty;
        var fileKey = FileKeyUtility.WAL_GetLatestFileKey(_io);
        var io2 = _settings.SecondaryBackupLog ? _ioLog2 : null;
        var fileKey2 = _settings.SecondaryBackupLog ? FileKeyUtility.WAL_GetSecondaryFileKey() : null;
        _wal = new(fileKey, _definition, _io, updateNodeDataPositionInLogFile, io2, fileKey2);
        _nodes = new(_definition, _settings, readSegments);
        _relations = new(_definition);
        _index = new(_definition);
        _definition.Initialize(this, _settings, _io, _ai);  // this will open all indexes and set up the variables
        Engines.DeleteUnopenedIndexes(); // delete any indexes that were not opened in the current session (e.g. if they were deleted from the datamodel)
        _variables = getRootVariables();
        _nodeWriteLocks = new();
        logLine___________________________();
        LogInfo("Database intialized");
        _state = DataStoreState.Closed; // ready to be opened
    }
    // persisted array-index mirrors load lazily on first use; loading them right after open moves
    // that read off the first user query. Queries arriving before it finishes simply block on the
    // same load lock they would have taken anyway. Facet caches warm afterwards for the same
    // reason: the first FILTERED facet query builds per-bucket id sets from the persisted value
    // indexes (full value-tree reads, hundreds of ms at millions of nodes) unless they are built
    // here first. Everything below is read-only work through the same paths a query takes.
    void warmIndexesInBackground() {
        var mirrors = _definition.GetAllIndexes().OfType<IIndexMirror>().ToArray();
        var facetProps = _definition.Properties.Values.Where(p => p.CanBeFacet()).ToArray();
        if (mirrors.Length == 0 && facetProps.Length == 0) return;
        Task.Run(() => {
            var sw = Stopwatch.StartNew();
            LogInfo("Background warm-up of indexes started");
            var totalSteps = mirrors.Length + facetProps.Length + 1; // +1 for the cache save at the end
            var completedSteps = 0;
            var activityId = RegisterActvity(DataStoreActivityCategory.IndexWarmup,
                "Warming indexes", 0);
            try {
                foreach (var mirror in mirrors) {
                    try {
                        if (_state != DataStoreState.Open) return;
                        UpdateActivity(activityId, "Loading index " + mirror.FriendlyName, 100 * completedSteps / totalSteps);
                        mirror.EnsureLoaded();
                    } catch (Exception e) {
                        LogInfo("Background load of index " + mirror.FriendlyName + " failed: " + e.Message);
                    }
                    completedSteps++;
                }
                // each property warms under its own short read lock (the lock queries count under),
                // so pending writers wait for at most one property, not the whole warm-up:
                foreach (var prop in facetProps) {
                    if (_state != DataStoreState.Open) return;
                    UpdateActivity(activityId, "Warming facet " + prop.CodeName, 100 * completedSteps / totalSteps);
                    _lock.EnterReadLock();
                    try {
                        if (_state == DataStoreState.Open) prop.WarmFacetCaches(QueryContext.Default);
                    } catch (Exception e) {
                        LogInfo("Background facet warm-up of " + prop.CodeName + " failed: " + e.Message);
                    } finally {
                        _lock.ExitReadLock();
                    }
                    completedSteps++;
                }
                // persist the freshly built sets right away (a no-op when nothing new was built), so
                // even a process killed before a scheduled save or clean dispose reopens warm:
                try {
                    UpdateActivity(activityId, "Saving index caches", 100 * completedSteps / totalSteps);
                    if (_state == DataStoreState.Open) SaveIndexCaches(false);
                } catch (Exception e) {
                    LogInfo("Saving index caches after warm-up failed: " + e.Message);
                }
                LogInfo("Background warm-up of indexes finished in " + sw.ElapsedMilliseconds.To1000N() + "ms");
                sw.Restart();
            } finally {
                DeRegisterActivity(activityId);
            }
        });
    }
    public void Open(bool throwOnBadLogFile = false, bool throwOnBadStateFile = false) {
        var sw = Stopwatch.StartNew();
        _startedOpeningUtc = DateTime.UtcNow;
        _scheduler.Stop();
        _lock.EnterWriteLock();
        LogInfo("Database opening");
        var activityId = RegisterActvity(DataStoreActivityCategory.Opening, "Database opening", 0);
        setStartupProgressEstimate(1);
        var currentModelHash = Guid.Empty;
        try { // inside try so the write lock is released if it throws
            currentModelHash = getCheckSumForStateFileAndIndexes();
            if (_state != DataStoreState.Closed) throw new Exception("Store cannot be opened as current state is " + _state);
            _state = DataStoreState.Opening;
            _wal.EnsureSecondaryLogFile(activityId, this, false);
            readState(throwOnBadStateFile, currentModelHash, activityId);
            TaskQueue?.ReOpen();
            TaskQueuePersisted?.ReOpen();
            _state = DataStoreState.Open;
            _startUpTimeMs = sw.ElapsedMilliseconds;
            LogInfo("Database ready in " + _startUpTimeMs.To1000N() + "ms.");
        } catch (StateFileReadException e) {
            LogInfo("Indexfile out of sync: " + e.Message);
            if (throwOnBadStateFile) {
                throw createCriticalErrorAndSetDbToErrorState("Opening error. ", e);
            } else { // delete state file and reload
                try {
                    LogInfo("Rebuilding index from log");
                    UpdateActivity(activityId, "Rebuilding index from log", 0);
                    resetStateAndIndexes();
                    // dispose only the components that initialize() recreates,
                    // a full Dispose() would also destroy the logger, AI engine, task queues and file stores,
                    // which are created in the constructor only and never recreated by initialize():
                    try { _index?.Dispose(); } catch { }
                    try { _wal?.Dispose(); } catch { }
                    try { Engines.Dispose(); } catch { }
                    initialize();
                    readState(throwOnBadStateFile, currentModelHash, activityId);
                    TaskQueue?.ReOpen();
                    TaskQueuePersisted?.ReOpen();
                    _state = DataStoreState.Open;
                    _startUpTimeMs = sw.ElapsedMilliseconds;
                    LogInfo("Database ready in " + _startUpTimeMs.To1000N() + "ms.");
                } catch (Exception reloadError) {
                    throw createCriticalErrorAndSetDbToErrorState("Reopen failed. ", reloadError);
                }
            }
        } finally {
            if (_state == DataStoreState.Error) Dispose();
            DeRegisterActivity(activityId);
            _lock.ExitWriteLock();
        }
        if (_state == DataStoreState.Open) {
            _fileConversionEngine.ClearTempFolder();
            _scheduler.Start();
            warmIndexesInBackground();
        }
    }
    public void Close() {
        _lock.EnterWriteLock();
        try {
            if (_state != DataStoreState.Open) throw new Exception("Store not opened. Current state is: " + _state);
            endRevertWindowAsCommitIfActive(); // before the flush, so the engines become durable at the head
            FlushToDisk(true, 0);
            _state = DataStoreState.Closing;
            LogInfo("Database closing");
            _scheduler.Stop();
            _state = DataStoreState.Closed;
            LogInfo("Database closed");
        } finally {
            _lock.ExitWriteLock();
        }
    }
    Variables getRootVariables() {
        Variables vars = Variables.CreateRootScope();
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
            var callback = (Metrics metrics, QueryContext ctx) => new NodeCollectionData(this, ctx, metrics, _definition.GetAllIdsForType(type.Id, ctx), type, null);
            vars.DeclarerAndSetCallback(type.CodeName, callback);
            if (type.Id == NodeConstants.BaseNodeTypeId) vars.DeclarerAndSetCallback(nameof(Object), callback); // INode == Object
        }
        return vars;
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
        try { _scheduler.Stop(); } catch { }
        try { TaskQueue?.TryGracefulShutdown(5000); } catch { }
        try { TaskQueuePersisted?.TryGracefulShutdown(5000); } catch { }
        try { endRevertWindowAsCommitIfActive(); } catch { } // before the flush, so the engines become durable at the head
        try { if (_state == DataStoreState.Open) FlushToDisk(true, 0); } catch { }
        try { _index?.Dispose(); } catch { }
        try { _wal?.Dispose(); } catch { }
        try { _logger?.Dispose(); } catch { }
        try { _ai?.Dispose(); } catch { }
        try { Engines.Dispose(); } catch { }
        try { TaskQueue?.Dispose(); } catch { }
        try { TaskQueuePersisted?.Dispose(); } catch { }
        if (_state == DataStoreState.Open) _state = DataStoreState.Disposed; // if in error state, do not change state
        try { this._io.CloseAllOpenStreams(); } catch { }
        try { this._ioAutoBackup.CloseAllOpenStreams(); } catch { }
        try { this._ioLog.CloseAllOpenStreams(); } catch { }
        try { this._ioLog2.CloseAllOpenStreams(); } catch { }
        try { foreach (var fs in _fileStores.Values) fs.Dispose(); } catch { }
    }
}
