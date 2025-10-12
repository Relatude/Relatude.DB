using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Query;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores;
public interface IDataStore : IDisposable {
    void DeleteIndexStateFile();
    void LogInfo(string text, string? details = null);
    void LogWarning(string text, string? details = null);
    void LogError(string description, Exception error);
    void Log(SystemLogEntryType type, string text, string? details = null);
    TraceEntry[] GetSystemTrace(int skip, int take);
    Datamodel Datamodel { get; }
    DataStoreState State { get; }
    DataStoreStatus GetStatus();
    IAIProvider AI { get; }
    IStoreLogger Logger { get; }

    TaskQueue TaskQueue { get; }
    TaskQueue? TaskQueuePersisted { get; }
    void EnqueueTask(TaskData task, string? jobId = null);
    void RegisterRunner(ITaskRunner runner);

    Task<TransactionResult> ExecuteAsync(TransactionData transaction, bool? flushToDisk = null);
    TransactionResult Execute(TransactionData transaction, bool? flushToDisk = null);
    Task<INodeData> GetAsync(Guid id);
    Task<IEnumerable<INodeData>> GetAsync(IEnumerable<int> __ids);
    Task<INodeData> GetAsync(int id);
    INodeData Get(Guid id);
    INodeData Get(int id);
    INodeData Get(IdKey id);
    Guid GetNodeType(Guid id);
    Guid GetNodeType(int id);
    Guid GetNodeType(IdKey id);
    Dictionary<IdKey, Guid> GetNodeType(IEnumerable<IdKey> ids);
    bool TryGet(Guid id, [MaybeNullWhen(false)] out INodeData nodeData);
    bool TryGet(int id, [MaybeNullWhen(false)] out INodeData nodeData);
    bool TryGetGuid(int id, out Guid guid);
    IEnumerable<INodeData> Get(IEnumerable<int> __ids);
    IEnumerable<INodeData> Get(IEnumerable<Guid> __ids);
    bool Exists(Guid id, Guid nodeTypeId);
    bool ContainsRelation(Guid relationId, Guid from, Guid to, bool fromTargetToSource);
    INodeData[] GetRelatedNodesFromPropertyId(Guid propertyId, Guid from);
    bool TryGetRelatedNodeFromPropertyId(Guid propertyId, Guid from, [MaybeNullWhen(false)] out INodeData node);
    int GetRelatedCountFromPropertyId(Guid propertyId, Guid from);
    IEnumerable<Guid> GetRelatedNodeIdsFromRelationId(Guid relationId, Guid from, bool fromTargetToSource);
    long GetLastTimestampID();
    Task MaintenanceAsync(MaintenanceAction actions);
    void Maintenance(MaintenanceAction actions);
    StoreStatus GetInfo();
    Task<StoreStatus> GetInfoAsync();
    void Open(bool ThrowOnBadLogFile = false, bool ignoreStateFileLoadExceptions = true);
    object Query(string expression, IEnumerable<Parameter> parameters);
    Task<object> QueryAsync(string expression, IEnumerable<Parameter> parameters);

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

    Task FileDeleteAsync(Guid nodeId, Guid propertyId);
    Task FileUploadAsync(Guid nodeId, Guid propertyId, IIOProvider source, string fileKey, string fileName);
    Task FileUploadAsync(Guid nodeId, Guid propertyId, Stream source, string fileKey, string fileName);
    Task FileDownloadAsync(Guid nodeId, Guid propertyId, Stream outStream);
    Task<bool> FileUploadedAndAvailableAsync(Guid nodeId, Guid propertyId);

    (int NodeId, string Text)[] GetTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids);
    (int NodeId, string Text)[] GetSemanticTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids);

}

public static class IDataStoreExtensions {
    public static bool IsTaskQueueBusy(this IDataStore store) {
        if (store.State != DataStoreState.Open) throw new InvalidOperationException("DataStore is not open");
        if(store.TaskQueue.CountTasks(BatchState.Pending) > 0) return true;
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
        transaction.UpdateProperty(nodeId, propertyId, value);
        store.Execute(transaction, flushToDisk);
    }
}
