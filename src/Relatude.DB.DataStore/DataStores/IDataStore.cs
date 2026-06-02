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
    QueryContext DefaultQueryContext { get; }
    void SetDefaultQueryContext(QueryContext ctx);
    NodeDataRevision[] GetRevisions(Guid nodeId, QueryContext? ctx = null);
    object? Query(string query, IEnumerable<Parameter> parameters, QueryContext? ctx = null);
    Task<object?> QueryAsync(string query, IEnumerable<Parameter> parameters, QueryContext? ctx = null);
    Task<INodeDataExternal> GetAsync(Guid id, QueryContext? ctx = null);
    Task<IEnumerable<INodeDataExternal>> GetAsync(IEnumerable<int> __ids, QueryContext? ctx = null);
    Task<INodeDataExternal> GetAsync(int id, QueryContext? ctx = null);
    INodeDataExternal Get(Guid id, QueryContext? ctx = null);
    INodeDataExternal Get(int id, QueryContext? ctx = null);
    INodeDataExternal Get(IdKey id, QueryContext? ctx = null);
    bool TryGet(Guid id, [MaybeNullWhen(false)] out INodeDataExternal nodeData, QueryContext? ctx = null);
    bool TryGet(int id, [MaybeNullWhen(false)] out INodeDataExternal nodeData, QueryContext? ctx = null);
    bool TryGetGuid(int id, out Guid guid, QueryContext? ctx = null);
    IEnumerable<INodeDataExternal> Get(IEnumerable<int> __ids, QueryContext? ctx = null);
    IEnumerable<INodeDataExternal> Get(IEnumerable<Guid> __ids, QueryContext? ctx = null);

    bool TryGetValue<T>(PropertyPath path, [MaybeNullWhen(false)] out T value, QueryContext? ctx = null);
    T GetValue<T>(PropertyPath path, QueryContext? ctx = null);

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
    Guid GetNodeType(IdKey id);
    Dictionary<IdKey, Guid> GetNodeType(IEnumerable<IdKey> ids);

    bool TryGetNodeMeta(Guid id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null);
    bool TryGetNodeMeta(int id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null);
    bool TryGetNodeMeta(IdKey id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null);

    bool TryGetAddress(Guid id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null);
    bool TryGetAddress(int id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null);
    bool TryGetAddress(IdKey id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null);

    bool TryGetNodeIdFromAddress(string address, out Guid nodeId);
    bool TryGetNodeIdFromAddress(string address, out Guid nodeId, out string? cultureCode);
    bool TryGetNodeIdFromAddress(string address, out int nodeId);
    bool TryGetNodeIdFromAddress(string address, out int nodeId, out string? cultureCode);
    bool TryGetNodeDataFromAddress(string address, [MaybeNullWhen(false)] out INodeDataExternal nodeData);



    bool CanConvert(FileFormat from, FileFormat to);
    bool CanConvert(PropertyPath propertyPath, FileAdjustment adj, QueryContext? ctx = null);

    string GetUrl(NodePath nodePath, bool absolute = false, QueryContext? ctx = null);
    string GetUrl(PropertyPath propertyPath, FileAdjustment adj, bool absolute = false, QueryContext? ctx = null);
    Task<Stream> GetFileStream(string url, int maxWait, QueryContext? ctx = null);
    Task<StateAndStream> GetFileStreamAndState(string url, int maxWait = -1, QueryContext? ctx = null);
    Task<Stream> GetFileStream(PropertyPath propertyPath, QueryContext? ctx = null);
    Task<Stream> GetFileStream(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null);
    Task<StateAndStream> GetFileStreamAndState(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null);
    bool TryGetConversionInfo(PropertyPath propertyPath, FileAdjustment adj, bool queueConversionIfNotRequested, [MaybeNullWhen(false)] out FileConversionProgressInfo progressInfo, QueryContext? ctx = null);
    bool IsFileReady(PropertyPath propertyPath, FileAdjustment adj, bool requestIfNot, QueryContext? ctx = null);
    void EnsureConversionRequested(PropertyPath propertyPath, FileAdjustment adj, QueryContext? ctx = null);
    FileConversions GetRunningConversions(QueryContext? ctx = null);
    void CancelAllConversions(QueryContext? ctx = null);
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
    Task<FileValue> FileUploadAsync(PropertyPath target, IIOProvider source, string sourceFileKey, string? fileName = null, bool noNodeUpdate = false, QueryContext? ctx = null);
    Task<FileValue> FileUploadAsync(PropertyPath target, Stream source, string fileName, bool noNodeUpdate = false, QueryContext? ctx = null);
    Task FileDeleteAsync(PropertyPath target, QueryContext? ctx = null);
    Task<FileValue> FileDownloadAsync(PropertyPath target, Stream outStream, QueryContext? ctx = null);
    Task<bool> IsFileUploadedAndAvailableAsync(PropertyPath target, QueryContext? ctx = null);

    Task<Guid> InitiateMultipartUploadAsync(PropertyPath propertyPath, string fileName, QueryContext? ctx = null);
    Task AppendMultipartUploadAsync(Guid fileId, byte[] data, int length);
    Task<FileValue> FinalizeMultipartUploadAsync(Guid fileId, bool noNodeUpdate = false, QueryContext? ctx = null);
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
    void Rollback(long timestamp);
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
