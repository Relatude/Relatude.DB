using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.Query;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using Relatude.DB.Web;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores;

public interface IDataStore : IDisposable {

    // Exposed and access controlled
    Task<TransactionResult> ExecuteAsync(TransactionData transaction, bool? flushToDisk = null, QueryContext? ctx = null);
    TransactionResult Execute(TransactionData transaction, bool? flushToDisk = null, QueryContext? ctx = null);
    QueryContext QueryContext { get; }
    void SetDefaultQueryContext(QueryContext ctx);
    NodeDataRevision[] GetRevisions(Guid nodeId, QueryContext? ctx = null);
    object? Query(string query, IEnumerable<Parameter> parameters, QueryContext? ctx = null);
    Task<object?> QueryAsync(string query, IEnumerable<Parameter> parameters, QueryContext? ctx = null);
    Task<INodeDataExternal> GetAsync(Guid id, QueryContext? ctx = null);
    Task<IEnumerable<INodeDataExternal>> GetAsync(IEnumerable<int> __ids, QueryContext? ctx = null);
    Task<INodeDataExternal> GetAsync(int id, QueryContext? ctx = null);
    int GetId(Guid id);
    Guid GetGuid(int id);
    INodeDataExternal Get(Guid id, QueryContext? ctx = null);
    INodeDataExternal Get(int id, QueryContext? ctx = null);
    INodeDataExternal Get(NodeKey id, QueryContext? ctx = null);
    bool TryGet(Guid id, [MaybeNullWhen(false)] out INodeDataExternal nodeData, QueryContext? ctx = null);
    bool TryGet(int id, [MaybeNullWhen(false)] out INodeDataExternal nodeData, QueryContext? ctx = null);
    bool TryGetGuid(int id, out Guid guid, QueryContext? ctx = null);
    IEnumerable<INodeDataExternal> Get(IEnumerable<int> __ids, QueryContext? ctx = null);
    IEnumerable<INodeDataExternal> Get(IEnumerable<Guid> __ids, QueryContext? ctx = null);

    bool TryGetValue<T>(PropertyPath path, [MaybeNullWhen(false)] out T value, QueryContext? ctx = null);
    T GetValue<T>(PropertyPath path, QueryContext? ctx = null);

    bool Exists(int id, QueryContext? ctx = null);
    bool Exists(Guid id, QueryContext? ctx = null);
    bool ExistsAndIsType(Guid id, Guid nodeTypeId, QueryContext? ctx = null);
    bool ContainsRelation(Guid relationId, Guid from, Guid to, bool fromTargetToSource, QueryContext? ctx = null);
    INodeDataExternal[] GetRelatedNodesFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null);
    bool TryGetRelatedNodeFromPropertyId(Guid propertyId, Guid from, [MaybeNullWhen(false)] out INodeDataExternal node, QueryContext? ctx = null);
    int GetRelatedCountFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null);
    IEnumerable<Guid> GetRelatedNodeIdsFromRelationId(Guid relationId, Guid from, bool fromTargetToSource, QueryContext? ctx = null);

    // Exposed, but not Access Controlled
    bool TryGetNodeType(Guid id, out Guid nodeTypeId);
    Guid GetNodeType(Guid id);
    Guid GetNodeType(int id);
    Guid GetNodeType(NodeKey id);
    Dictionary<NodeKey, Guid> GetNodeType(IEnumerable<NodeKey> ids);

    bool TryGetNodeMeta(Guid id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null);
    bool TryGetNodeMeta(int id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null);
    bool TryGetNodeMeta(NodeKey id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null);

    bool TryGetAddress(Guid id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null);
    bool TryGetAddress(int id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null);
    bool TryGetAddress(NodeKey id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null);

    bool TryGetNodeIdFromAddress(string address, out Guid nodeId);
    bool TryGetNodeIdFromAddress(string address, out Guid nodeId, out string? cultureCode);
    bool TryGetNodeIdFromAddress(string address, out int nodeId);
    bool TryGetNodeIdFromAddress(string address, out int nodeId, out string? cultureCode);
    bool TryGetNodeDataFromAddress(string address, [MaybeNullWhen(false)] out INodeDataExternal nodeData);

    bool CanConvert(FileFormat from, FileFormat to);
    bool CanConvert(PropertyPath propertyPath, FileAdjustment adj, QueryContext? ctx = null);

    string GetUrl(NodeKey nodeKey, bool absolute = false, QueryContext? ctx = null);
    string GetUrl(NodePath nodePath, bool absolute = false, QueryContext? ctx = null);
    string GetUrl(PropertyPath propertyPath, bool absolute = false, QueryContext? ctx = null);
    string GetUrl(PropertyPath propertyPath, FileAdjustment adj, bool absolute = false, QueryContext? ctx = null);

    bool TryParseUrl(string url, [MaybeNullWhen(false)] out UrlKeys result, QueryContext? ctx = null);
    bool TryParseUrlForContent(string url, [MaybeNullWhen(false)] out UrlContent result, int maxWaitMs = -1, QueryContext? ctx = null);

    Task<Stream> GetFileStream(string url, int maxWait, QueryContext? ctx = null);
    Task<StateAndStream> GetFileStreamAndState(string url, int maxWait = -1, QueryContext? ctx = null);
    Task<Stream> GetFileStream(PropertyPath propertyPath, QueryContext? ctx = null);
    Task<Stream> GetFileStream(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null);
    Task<StreamAndValue> GetFileStreamAndValue(PropertyPath propertyPath, QueryContext? ctx = null);
    Task<StateAndStream> GetFileStreamAndState(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null);
    bool TryGetConversionInfo(PropertyPath propertyPath, FileAdjustment adj, bool queueConversionIfNotRequested, [MaybeNullWhen(false)] out FileConversionProgressInfo progressInfo, QueryContext? ctx = null);
    bool IsFileReady(PropertyPath propertyPath, FileAdjustment adj, bool requestIfNot, QueryContext? ctx = null);
    void EnsureConversionRequested(PropertyPath propertyPath, FileAdjustment adj, QueryContext? ctx = null);
    FileConversions GetConversions(QueryContext? ctx = null);
    Task CancelAllConversions(bool permanently, QueryContext? ctx = null);
    Task CancelConversion(Guid conversionId, bool permanently, QueryContext? ctx = null);
    void ClearAllCachedConversions(QueryContext? ctx = null);
    void ClearAllCachedConversionsErrors(QueryContext? ctx = null);

    // Internal not controlled
    void LogInfo(string text, string? details = null, bool replace = false);
    void LogWarning(string text, string? details = null);
    void LogError(string description, Exception error);
    void Log(SystemLogEntryType type, string text, string? details = null, bool replace = false);
    TraceEntry[] GetSystemTrace(int skip, int take);
    DateTime GetLatestSystemTraceTimestamp();
    Datamodel Datamodel { get; }
    DataStoreState State { get; }
    DataStoreStatus GetStatus();
    DataStoreOpeningStatus GetOpeningStatus();
    long RegisterActvity(DataStoreActivityCategory category, string? description = null, int? percentageProgress = null);
    long RegisterChildActvity(long parentId, DataStoreActivityCategory category, string? description = null, int? percentageProgress = null);
    void UpdateActivity(long activityId, string? description = null, int? percentageProgress = null);
    void UpdateActivityProgress(long activityId, int? percentageProgress = null);
    void DeRegisterActivity(long activityId);
    AIEngine AI { get; }
    IStoreLogger Logger { get; }
    TaskQueue TaskQueue { get; }
    TaskQueue? TaskQueuePersisted { get; }
    void EnqueueTask(TaskData task, string? jobId = null);
    void RegisterRunner(ITaskRunner runner);

    // File handling
    Task<FileValue> FileUploadAsync(PropertyPath target, IIOProvider source, string sourceFileKey, string? fileName = null, int? maxWaitForMetaUpdate = null, QueryContext? ctx = null);
    Task<FileValue> FileUploadAsync(PropertyPath target, Stream source, string fileName, int? maxWaitForMetaUpdate = null, QueryContext? ctx = null);
    Task FileDeleteAsync(PropertyPath target, QueryContext? ctx = null);
    Task<FileValue> FileDownloadAsync(PropertyPath target, Stream outStream, QueryContext? ctx = null);
    Task<bool> IsFileUploadedAndAvailableAsync(PropertyPath target, QueryContext? ctx = null);
    FileValue? UpdateFileMetaIfNotSet(PropertyPath propertyPath, Guid fileId, BasicFileMeta meta, QueryContext? ctx = null);

    Task<Guid> InitiateMultipartUploadAsync(PropertyPath propertyPath, string fileName, QueryContext? ctx = null);
    Task AppendMultipartUploadAsync(Guid fileId, byte[] data, int length);
    Task<FileValue> FinalizeMultipartUploadAsync(Guid fileId, int? maxWaitForMetaUpdate = null, QueryContext? ctx = null);
    Task CancelMultipartUpload(Guid fileId);
    bool FileStoreSupportsMultipartUploads(PropertyPath propertyPath);

    long GetLastTimestampID();
    Task MaintenanceAsync(MaintenanceAction actions);
    void Maintenance(MaintenanceAction actions);
    void SaveIndexStates(bool forceRefresh = false, bool nodeSegmentsOnly = false);
    DataStoreInfo GetInfo();
    Task<DataStoreInfo> GetInfoAsync();
    void Open(bool ThrowOnBadLogFile = false, bool ignoreStateFileLoadExceptions = true);
    void Close();

    void RefreshLock(Guid lockId);
    Task<Guid> RequestGlobalLockAsync(double lockDurationInMs, double maxWaitTimeInMs);
    Task<Guid> RequestLockAsync(Guid nodeId, double lockDurationInMs, double maxWaitTimeInMs);
    Task<Guid> RequestLockAsync(int nodeId, double lockDurationInMs, double maxWaitTimeInMs);
    void ReleaseLock(Guid lockId);
    FileKeyUtility FileKeys { get; }
    IIOProvider IO { get; }
    IIOProvider IOIndex { get; }
    IIOProvider IOBackup { get; }
    void RewriteStore(bool hotSwapToNewFile, string newLogFileKey, IIOProvider? destinationIO = null);
    string? CancelRunningRewriteIfAny();
    void CopyStore(string newLogFileKey, IIOProvider? destinationIO = null);
    int DeleteOldLogs();
    void SetTimestamp(long timestamp);
    long Timestamp { get; }

    // Reverting: rolling the database back to an earlier log timestamp. Two forms exist.
    //
    // The revert window is the cheap, planned form: BeginRevertWindow marks the current position
    // and suspends everything that would persist state past it (engine durability, state
    // snapshots, log rewrites), so RollbackRevertWindow only truncates the log tail and reloads.
    // CommitRevertWindow keeps the changes and resumes normal persistence. Intended for
    // experiments, tests and seeding: begin, mutate freely, then keep or discard.
    //
    // DeleteTransactionsAfter is the general, unplanned form: it works against any timestamp
    // (e.g. one remembered before the changes) but may have to reset and rebuild whatever
    // persisted state has advanced past it — state snapshot, memory index files, index engines —
    // which on a large database means a full replay of the log. Both forms permanently remove the
    // deleted transactions from the log, as if they never happened; file store content uploaded by
    // deleted transactions is not removed and becomes orphaned.

    /// <summary>The active revert window, or null when none is.</summary>
    RevertWindowInfo? RevertWindow { get; }
    /// <summary>
    /// Marks the current log position as a rollback target and suspends engine durability, state
    /// snapshots and log rewrites until the window ends. Returns the window's timestamp. With
    /// <paramref name="saveStateFirst"/> (the default) the state snapshot is written first, so a
    /// later rollback reloads from the snapshot instead of replaying the log. Only one window can
    /// be active; closing the store ends the window as a commit.
    /// </summary>
    long BeginRevertWindow(bool saveStateFirst = true);
    /// <summary>Ends the revert window keeping every change made inside it, and makes the engines durable again.</summary>
    void CommitRevertWindow();
    /// <summary>
    /// Ends the revert window by permanently deleting every transaction made inside it: the log is
    /// truncated back to the window's position and the store reloads. Engines that persist
    /// per-transaction (e.g. the SQLite index engine) are reset and rebuilt from the log; the
    /// deferring engines reopen at the window start without a rebuild.
    /// </summary>
    DeleteTransactionsResult RollbackRevertWindow();
    /// <summary>
    /// Permanently deletes every transaction with a timestamp after <paramref name="afterTimestamp"/>
    /// (take it from <see cref="GetLastTimestampID"/> before making changes). The log is truncated
    /// and the store reloads; any persisted state that has advanced past the timestamp is reset and
    /// rebuilt from the remaining log, which on a large database can be slow. With
    /// <paramref name="dryRun"/> nothing is changed and the result reports what would be deleted.
    /// </summary>
    DeleteTransactionsResult DeleteTransactionsAfter(long afterTimestamp, bool dryRun = false);

    /// <summary>
    /// Finds strictly older versions of a node, newest first, read directly from the transaction
    /// log files on every call (nothing is cached). Every write of a node appends the full node to
    /// the log together with the position of its previous version, so history is followed by chain
    /// rather than by scan — one read per version. The primary log covers the versions since the
    /// last log rewrite; a secondary backup log survives rewrites and extends the reach. The
    /// current version is not included, deleted nodes are not supported, relations are not part of
    /// node data, and versions written in the pre-chain log format (before the file was created or
    /// rewritten under log format 1001) are not reachable.
    /// </summary>
    NodeVersionData[] FindOlderVersions(Guid nodeId, int maxCount = 100, QueryContext? ctx = null);
    TextExtract[] GetTextExtract(IEnumerable<int> ids, TextIndexType indexType);
}

public static class IDataStoreExtensions {
    public static bool IsTaskQueueBusy(this IDataStore store) {
        if (store.State != DataStoreState.Open) throw new InvalidOperationException("DataStore is not open");
        if (store.TaskQueue.CountTasks(BatchState.Pending) > 0) return true;
        if (store.TaskQueue.CountTasks(BatchState.Running) > 0) return true;
        if (store.TaskQueuePersisted != null) {
            if (store.TaskQueuePersisted.CountTasks(BatchState.Pending) > 0) return true;
            if (store.TaskQueuePersisted.CountTasks(BatchState.Running) > 0) return true;
        }
        return false;
    }
    public static void BackUpNow(this IDataStore store, bool truncate, bool keepForever, IIOProvider? destination = null) {
        if (destination == null) destination = store.IOBackup;
        var fileKey = store.FileKeys.WAL_GetFileKeyForBackup(DateTime.UtcNow, keepForever);
        var task = new RewriteTask() {
            HotSwapToNewFile = false,
            DeleteOldDbFilesAfterHotSwap = false,
            NewLogFileKey = fileKey,
            IO = destination,
            Truncate = truncate,
        };
        store.EnqueueTask(task, "Backup");
    }
    public static void UpdateProperty(this IDataStore store, Guid nodeId, Guid propertyId, object value, bool? flushToDisk = null) {
        var transaction = new TransactionData();
        transaction.UpdateIfDifferentProperty(nodeId, propertyId, value);
        store.Execute(transaction, flushToDisk);
    }
}
