using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.Query;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores;
public interface IDataStore : IDisposable {

    void LogInfo(string text, string? details = null);
    void LogWarning(string text, string? details = null);
    void LogError(string description, Exception error);
    void Log(SystemLogEntryType type, string text, string? details = null);
    TraceEntry[] GetSystemTrace(int skip, int take);
    Datamodel Datamodel { get; }
    DataStoreState State { get; }
    DataStoreStatus GetStatus();

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

    // Access controlled changes
    Task<TransactionResult> ExecuteAsync(TransactionData transaction, bool? flushToDisk = null, QueryContext? ctx = null);
    TransactionResult Execute(TransactionData transaction, bool? flushToDisk = null, QueryContext? ctx = null);
    Task FileDeleteAsync(Guid nodeId, Guid propertyId, QueryContext? ctx = null);
    Task FileUploadAsync(Guid nodeId, Guid propertyId, IIOProvider source, string fileKey, string fileName, QueryContext? ctx = null);
    Task FileUploadAsync(Guid nodeId, Guid propertyId, Stream source, string fileKey, string fileName, QueryContext? ctx = null);

    // Access controlled queries
    object? Query(string query, IEnumerable<Parameter> parameters, QueryContext? userCtx = null);
    Task<object?> QueryAsync(string query, IEnumerable<Parameter> parameters, QueryContext? userCtx = null);
    Task<INodeData> GetAsync(Guid id, QueryContext? ctx = null);
    Task<IEnumerable<INodeData>> GetAsync(IEnumerable<int> __ids, QueryContext? ctx = null);
    Task<INodeData> GetAsync(int id, QueryContext? ctx = null);
    INodeData Get(Guid id, QueryContext? ctx = null);
    INodeData Get(int id, QueryContext? ctx = null);
    INodeData Get(IdKey id, QueryContext? ctx = null);
    bool TryGet(Guid id, [MaybeNullWhen(false)] out INodeData nodeData, QueryContext? ctx = null);
    bool TryGet(int id, [MaybeNullWhen(false)] out INodeData nodeData, QueryContext? ctx = null);
    bool TryGetGuid(int id, out Guid guid, QueryContext? ctx = null);
    IEnumerable<INodeData> Get(IEnumerable<int> __ids, QueryContext? ctx = null);
    IEnumerable<INodeData> Get(IEnumerable<Guid> __ids, QueryContext? ctx = null);
    Task FileDownloadAsync(Guid nodeId, Guid propertyId, Stream outStream, QueryContext? ctx = null);
    Task<bool> IsFileUploadedAndAvailableAsync(Guid nodeId, Guid propertyId, QueryContext? ctx = null);

    Guid GetNodeType(Guid id);
    Guid GetNodeType(int id);
    Guid GetNodeType(IdKey id);
    Dictionary<IdKey, Guid> GetNodeType(IEnumerable<IdKey> ids);

    bool ExistsAndIsType(Guid id, Guid nodeTypeId);
    bool ContainsRelation(Guid relationId, Guid from, Guid to, bool fromTargetToSource);
    INodeData[] GetRelatedNodesFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null);
    bool TryGetRelatedNodeFromPropertyId(Guid propertyId, Guid from, [MaybeNullWhen(false)] out INodeData node);
    int GetRelatedCountFromPropertyId(Guid propertyId, Guid from);
    IEnumerable<Guid> GetRelatedNodeIdsFromRelationId(Guid relationId, Guid from, bool fromTargetToSource);

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
    IIOProvider IOBackup { get; }
    void RewriteStore(bool hotSwapToNewFile, string newLogFileKey, IIOProvider? destinationIO = null);
    void CopyStore(string newLogFileKey, IIOProvider? destinationIO = null);
    void SetTimestamp(long timestamp);
    long Timestamp { get; }
    void Rollback(long timestamp);


    (int NodeId, string Text)[] GetTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids);
    (int NodeId, string Text)[] GetSemanticTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids);
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
        if (truncate) {
            store.RewriteStore(false, fileKey, destination);
        } else {
            store.CopyStore(fileKey, destination);
        }
    }
    public static void UpdateProperty(this IDataStore store, Guid nodeId, Guid propertyId, object value, bool? flushToDisk = null) {
        var transaction = new TransactionData();
        transaction.UpdateIfDifferentProperty(nodeId, propertyId, value);
        store.Execute(transaction, flushToDisk);
    }
}
