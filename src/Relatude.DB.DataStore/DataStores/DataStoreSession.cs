using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.Query;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores;

public class DataStoreSession : IDataStore {
    private readonly IDataStore _datastore;
    public DataStoreSession(QueryContext context, IDataStore datastore) {
        DefaultQueryContext = context;
        _datastore = datastore;
    }
    public QueryContext DefaultQueryContext { get; }

    public void SetDefaultQueryContext(QueryContext ctx) => _datastore.SetDefaultQueryContext(ctx);
    public void Dispose() => _datastore.Dispose();
    public void LogInfo(string text, string? details = null, bool replace = false) => _datastore.LogInfo(text, details, replace);
    public void LogWarning(string text, string? details = null) => _datastore.LogWarning(text, details);
    public void LogError(string description, Exception error) => _datastore.LogError(description, error);
    public void Log(SystemLogEntryType type, string text, string? details = null, bool replace = false) => _datastore.Log(type, text, details, replace);
    public TraceEntry[] GetSystemTrace(int skip, int take) => _datastore.GetSystemTrace(skip, take);
    public DateTime GetLatestSystemTraceTimestamp() => _datastore.GetLatestSystemTraceTimestamp();

    public Datamodel Datamodel => _datastore.Datamodel;
    public DataStoreState State => _datastore.State;
    public DataStoreStatus GetStatus() => _datastore.GetStatus();
    public DataStoreOpeningStatus GetOpeningStatus() => _datastore.GetOpeningStatus();

    public long RegisterActvity(DataStoreActivityCategory category, string? description = null, int? percentageProgress = null)
        => _datastore.RegisterActvity(category, description, percentageProgress);
    public long RegisterChildActvity(long parentId, DataStoreActivityCategory category, string? description = null, int? percentageProgress = null)
        => _datastore.RegisterChildActvity(parentId, category, description, percentageProgress);
    public void UpdateActivity(long activityId, string? description = null, int? percentageProgress = null)
        => _datastore.UpdateActivity(activityId, description, percentageProgress);
    public void UpdateActivityProgress(long activityId, int? percentageProgress = null)
        => _datastore.UpdateActivityProgress(activityId, percentageProgress);
    public void DeRegisterActivity(long activityId)
        => _datastore.DeRegisterActivity(activityId);

    public AIEngine AI => _datastore.AI;
    public IStoreLogger Logger => _datastore.Logger;

    public TaskQueue TaskQueue => _datastore.TaskQueue;
    public TaskQueue? TaskQueuePersisted => _datastore.TaskQueuePersisted;
    public void EnqueueTask(TaskData task, string? jobId = null) => _datastore.EnqueueTask(task, jobId);
    public void RegisterRunner(ITaskRunner runner) => _datastore.RegisterRunner(runner);

    // Access controlled changes
    public Task<TransactionResult> ExecuteAsync(TransactionData transaction, bool? flushToDisk = null, QueryContext? ctx = null)
        => _datastore.ExecuteAsync(transaction, flushToDisk, ctx ?? DefaultQueryContext);
    public TransactionResult Execute(TransactionData transaction, bool? flushToDisk = null, QueryContext? ctx = null)
        => _datastore.Execute(transaction, flushToDisk, ctx ?? DefaultQueryContext);


    // Revisions:
    public NodeDataRevision[] GetRevisions(Guid nodeId, QueryContext? ctx = null)
        => _datastore.GetRevisions(nodeId, ctx ?? DefaultQueryContext);

    // Access controlled queries
    public object? Query(string query, IEnumerable<Parameter> parameters, QueryContext? userCtx = null)
        => _datastore.Query(query, parameters, userCtx ?? DefaultQueryContext);
    public Task<object?> QueryAsync(string query, IEnumerable<Parameter> parameters, QueryContext? userCtx = null)
        => _datastore.QueryAsync(query, parameters, userCtx ?? DefaultQueryContext);

    public Task<INodeDataExternal> GetAsync(Guid id, QueryContext? ctx = null) => _datastore.GetAsync(id, ctx ?? DefaultQueryContext);
    public Task<IEnumerable<INodeDataExternal>> GetAsync(IEnumerable<int> __ids, QueryContext? ctx = null)
        => _datastore.GetAsync(__ids, ctx ?? DefaultQueryContext);
    public Task<INodeDataExternal> GetAsync(int id, QueryContext? ctx = null)
        => _datastore.GetAsync(id, ctx ?? DefaultQueryContext);
    public INodeData Get(Guid id, QueryContext? ctx = null)
        => _datastore.Get(id, ctx ?? DefaultQueryContext);
    public INodeData Get(int id, QueryContext? ctx = null)
        => _datastore.Get(id, ctx ?? DefaultQueryContext);
    public INodeData Get(IdKey id, QueryContext? ctx = null)
        => _datastore.Get(id, ctx ?? DefaultQueryContext);
    public bool TryGet(Guid id, [MaybeNullWhen(false)] out INodeDataExternal nodeData, QueryContext? ctx = null)
        => _datastore.TryGet(id, out nodeData, ctx ?? DefaultQueryContext);
    public bool TryGet(int id, [MaybeNullWhen(false)] out INodeDataExternal nodeData, QueryContext? ctx = null)
        => _datastore.TryGet(id, out nodeData, ctx ?? DefaultQueryContext);
    public bool TryGetGuid(int id, out Guid guid, QueryContext? ctx = null)
        => _datastore.TryGetGuid(id, out guid, ctx ?? DefaultQueryContext);
    public IEnumerable<INodeData> Get(IEnumerable<int> __ids, QueryContext? ctx = null)
        => _datastore.Get(__ids, ctx ?? DefaultQueryContext);
    public IEnumerable<INodeData> Get(IEnumerable<Guid> __ids, QueryContext? ctx = null)
        => _datastore.Get(__ids, ctx ?? DefaultQueryContext);

    public bool TryGetValue<T>(PropertyPath path, [MaybeNullWhen(false)] out T value, QueryContext? ctx = null) => _datastore.TryGetValue(path, out value, ctx ?? DefaultQueryContext);
    public T GetValue<T>(PropertyPath path, QueryContext? ctx = null) => _datastore.GetValue<T>(path, ctx ?? DefaultQueryContext);

    public Task<FileValue> FileUploadAsync(PropertyPath target, IIOProvider source, string fileKey, string? fileName = null, QueryContext? ctx = null) => _datastore.FileUploadAsync(target, source, fileKey, fileName, ctx ?? DefaultQueryContext);
    public Task<FileValue> FileUploadAsync(PropertyPath target, Stream source, string fileName, QueryContext? ctx = null) => _datastore.FileUploadAsync(target, source, fileName, ctx ?? DefaultQueryContext);
    public Task FileDeleteAsync(PropertyPath target, QueryContext? ctx = null) => _datastore.FileDeleteAsync(target, ctx ?? DefaultQueryContext);
    public Task<FileValue> FileDownloadAsync(PropertyPath target, Stream outStream, QueryContext? ctx = null) => _datastore.FileDownloadAsync(target, outStream, ctx ?? DefaultQueryContext);
    public Task<bool> IsFileUploadedAndAvailableAsync(PropertyPath target, QueryContext? ctx = null) => _datastore.IsFileUploadedAndAvailableAsync(target, ctx ?? DefaultQueryContext);

    public bool TryGetNodeType(Guid id, out Guid nodeTypeId)
        => _datastore.TryGetNodeType(id, out nodeTypeId);
    public Guid GetNodeType(Guid id) => _datastore.GetNodeType(id);
    public Guid GetNodeType(int id) => _datastore.GetNodeType(id);
    public Guid GetNodeType(IdKey id) => _datastore.GetNodeType(id);
    public Dictionary<IdKey, Guid> GetNodeType(IEnumerable<IdKey> ids) => _datastore.GetNodeType(ids);

    public bool Exists(Guid id, QueryContext? ctx = null) => _datastore.Exists(id);
    public bool ExistsAndIsType(Guid id, Guid nodeTypeId, QueryContext? ctx = null) => _datastore.ExistsAndIsType(id, nodeTypeId);
    public bool ContainsRelation(Guid relationId, Guid from, Guid to, bool fromTargetToSource, QueryContext? ctx = null) {
        return _datastore.ContainsRelation(relationId, from, to, fromTargetToSource, ctx ?? DefaultQueryContext);
    }
    public INodeData[] GetRelatedNodesFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null)
        => _datastore.GetRelatedNodesFromPropertyId(propertyId, from, ctx ?? DefaultQueryContext);
    public bool TryGetRelatedNodeFromPropertyId(Guid propertyId, Guid from, [MaybeNullWhen(false)] out INodeDataExternal node, QueryContext? ctx = null)
        => _datastore.TryGetRelatedNodeFromPropertyId(propertyId, from, out node, ctx ?? DefaultQueryContext);
    public int GetRelatedCountFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null)
        => _datastore.GetRelatedCountFromPropertyId(propertyId, from, ctx ?? DefaultQueryContext);
    public IEnumerable<Guid> GetRelatedNodeIdsFromRelationId(Guid relationId, Guid from, bool fromTargetToSource, QueryContext? ctx = null)
        => _datastore.GetRelatedNodeIdsFromRelationId(relationId, from, fromTargetToSource, ctx ?? DefaultQueryContext);

    public long GetLastTimestampID() => _datastore.GetLastTimestampID();
    public Task MaintenanceAsync(MaintenanceAction actions) => _datastore.MaintenanceAsync(actions);
    public void Maintenance(MaintenanceAction actions) => _datastore.Maintenance(actions);
    public void SaveIndexStates(bool forceRefresh = false, bool nodeSegmentsOnly = false)
        => _datastore.SaveIndexStates(forceRefresh, nodeSegmentsOnly);
    public DataStoreInfo GetInfo() => _datastore.GetInfo();
    public Task<DataStoreInfo> GetInfoAsync() => _datastore.GetInfoAsync();
    public void Open(bool ThrowOnBadLogFile = false, bool ignoreStateFileLoadExceptions = true)
        => _datastore.Open(ThrowOnBadLogFile, ignoreStateFileLoadExceptions);
    public void Close() => _datastore.Close();

    public void RefreshLock(Guid lockId) => _datastore.RefreshLock(lockId);

    public Task<Guid> RequestGlobalLockAsync(double lockDurationInMs, double maxWaitTimeInMs)
        => _datastore.RequestGlobalLockAsync(lockDurationInMs, maxWaitTimeInMs);
    public Task<Guid> RequestLockAsync(Guid nodeId, double lockDurationInMs, double maxWaitTimeInMs)
        => _datastore.RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs);
    public Task<Guid> RequestLockAsync(int nodeId, double lockDurationInMs, double maxWaitTimeInMs)
        => _datastore.RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs);
    public void ReleaseLock(Guid lockId) => _datastore.ReleaseLock(lockId);

    public FileKeyUtility FileKeys => _datastore.FileKeys;
    public IIOProvider IO => _datastore.IO;
    public IIOProvider IOIndex => _datastore.IOIndex;
    public IIOProvider IOBackup => _datastore.IOBackup;

    public void RewriteStore(bool hotSwapToNewFile, string newLogFileKey, IIOProvider? destinationIO = null)
        => _datastore.RewriteStore(hotSwapToNewFile, newLogFileKey, destinationIO);
    public string? CancelRunningRewriteIfAny() => _datastore.CancelRunningRewriteIfAny();
    public void CopyStore(string newLogFileKey, IIOProvider? destinationIO = null)
        => _datastore.CopyStore(newLogFileKey, destinationIO);
    public int DeleteOldLogs() => _datastore.DeleteOldLogs();
    public void SetTimestamp(long timestamp) => _datastore.SetTimestamp(timestamp);
    public long Timestamp => _datastore.Timestamp;
    public void Rollback(long timestamp) => _datastore.Rollback(timestamp);

    public TextExtract[] GetTextExtract(IEnumerable<int> ids, TextIndexType indexType)
        => _datastore.GetTextExtract(ids, indexType);

    INodeDataExternal IDataStore.Get(Guid id, QueryContext? ctx) => _datastore.Get(id, ctx);

    INodeDataExternal IDataStore.Get(int id, QueryContext? ctx) => _datastore.Get(id, ctx);
    INodeDataExternal IDataStore.Get(IdKey id, QueryContext? ctx) => _datastore.Get(id, ctx);
    IEnumerable<INodeDataExternal> IDataStore.Get(IEnumerable<int> __ids, QueryContext? ctx) => _datastore.Get(__ids, ctx);
    IEnumerable<INodeDataExternal> IDataStore.Get(IEnumerable<Guid> __ids, QueryContext? ctx) => _datastore.Get(__ids, ctx);
    INodeDataExternal[] IDataStore.GetRelatedNodesFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx) => _datastore.GetRelatedNodesFromPropertyId(propertyId, from, ctx);

    public bool TryGetNodeMeta(Guid id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null) => _datastore.TryGetNodeMeta(id, out meta, ctx ?? DefaultQueryContext);
    public bool TryGetNodeMeta(int id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null) => _datastore.TryGetNodeMeta(id, out meta, ctx ?? DefaultQueryContext);
    public bool TryGetNodeMeta(IdKey id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null) => _datastore.TryGetNodeMeta(id, out meta, ctx ?? DefaultQueryContext);

    public bool TryGetAddress(Guid id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null) => _datastore.TryGetAddress(id, out meta, ctx ?? DefaultQueryContext);
    public bool TryGetAddress(int id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null) => _datastore.TryGetAddress(id, out meta, ctx ?? DefaultQueryContext);
    public bool TryGetAddress(IdKey id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null) => _datastore.TryGetAddress(id, out meta, ctx ?? DefaultQueryContext);

    public bool TryGetNodeIdFromAddress(string address, out Guid nodeId) => _datastore.TryGetNodeIdFromAddress(address, out nodeId);
    public bool TryGetNodeIdFromAddress(string address, out Guid nodeId, out string? cultureCode) => _datastore.TryGetNodeIdFromAddress(address, out nodeId, out cultureCode);
    public bool TryGetNodeIdFromAddress(string address, out int nodeId) => _datastore.TryGetNodeIdFromAddress(address, out nodeId);
    public bool TryGetNodeIdFromAddress(string address, out int nodeId, out string? cultureCode) => _datastore.TryGetNodeIdFromAddress(address, out nodeId, out cultureCode);
    public bool TryGetNodeDataFromAddress(string address, [MaybeNullWhen(false)] out INodeDataExternal nodeData) => _datastore.TryGetNodeDataFromAddress(address, out nodeData);

}
