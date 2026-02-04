using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.Query;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores;

internal class DataStoreSession : IDataStore {
    private readonly IDataStore _datastore;
    public QueryContext Context { get; }

    public DataStoreSession(QueryContext context, IDataStore datastore) {
        Context = context;
        _datastore = datastore;
    }

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
        => _datastore.ExecuteAsync(transaction, flushToDisk, ctx ?? Context);
    public TransactionResult Execute(TransactionData transaction, bool? flushToDisk = null, QueryContext? ctx = null)
        => _datastore.Execute(transaction, flushToDisk, ctx ?? Context);
    public Task FileDeleteAsync(Guid nodeId, Guid propertyId, QueryContext? ctx = null)
        => _datastore.FileDeleteAsync(nodeId, propertyId, ctx ?? Context);
    public Task FileUploadAsync(Guid nodeId, Guid propertyId, IIOProvider source, string fileKey, string fileName, QueryContext? ctx = null)
        => _datastore.FileUploadAsync(nodeId, propertyId, source, fileKey, fileName, ctx ?? Context);
    public Task FileUploadAsync(Guid nodeId, Guid propertyId, Stream source, string fileKey, string fileName, QueryContext? ctx = null)
        => _datastore.FileUploadAsync(nodeId, propertyId, source, fileKey, fileName, ctx ?? Context);

    // Revisions:
    public NodeDataRevision[] GetRevisions(Guid nodeId, QueryContext? ctx = null)
        => _datastore.GetRevisions(nodeId, ctx ?? Context);

    // Access controlled queries
    public object? Query(string query, IEnumerable<Parameter> parameters, QueryContext? userCtx = null)
        => _datastore.Query(query, parameters, userCtx ?? Context);
    public Task<object?> QueryAsync(string query, IEnumerable<Parameter> parameters, QueryContext? userCtx = null)
        => _datastore.QueryAsync(query, parameters, userCtx ?? Context);

    public Task<INodeData> GetAsync(Guid id, QueryContext? ctx = null)
        => _datastore.GetAsync(id, ctx ?? Context);
    public Task<IEnumerable<INodeData>> GetAsync(IEnumerable<int> __ids, QueryContext? ctx = null)
        => _datastore.GetAsync(__ids, ctx ?? Context);
    public Task<INodeData> GetAsync(int id, QueryContext? ctx = null)
        => _datastore.GetAsync(id, ctx ?? Context);
    public INodeData Get(Guid id, QueryContext? ctx = null)
        => _datastore.Get(id, ctx ?? Context);
    public INodeData Get(int id, QueryContext? ctx = null)
        => _datastore.Get(id, ctx ?? Context);
    public INodeData Get(IdKey id, QueryContext? ctx = null)
        => _datastore.Get(id, ctx ?? Context);
    public bool TryGet(Guid id, [MaybeNullWhen(false)] out INodeData nodeData, QueryContext? ctx = null)
        => _datastore.TryGet(id, out nodeData, ctx ?? Context);
    public bool TryGet(int id, [MaybeNullWhen(false)] out INodeData nodeData, QueryContext? ctx = null)
        => _datastore.TryGet(id, out nodeData, ctx ?? Context);
    public bool TryGetGuid(int id, out Guid guid, QueryContext? ctx = null)
        => _datastore.TryGetGuid(id, out guid, ctx ?? Context);
    public IEnumerable<INodeData> Get(IEnumerable<int> __ids, QueryContext? ctx = null)
        => _datastore.Get(__ids, ctx ?? Context);
    public IEnumerable<INodeData> Get(IEnumerable<Guid> __ids, QueryContext? ctx = null)
        => _datastore.Get(__ids, ctx ?? Context);
    public Task FileDownloadAsync(Guid nodeId, Guid propertyId, Stream outStream, QueryContext? ctx = null)
        => _datastore.FileDownloadAsync(nodeId, propertyId, outStream, ctx ?? Context);
    public Task<bool> IsFileUploadedAndAvailableAsync(Guid nodeId, Guid propertyId, QueryContext? ctx = null)
        => _datastore.IsFileUploadedAndAvailableAsync(nodeId, propertyId, ctx ?? Context);

    public bool TryGetNodeType(Guid id, out Guid nodeTypeId)
        => _datastore.TryGetNodeType(id, out nodeTypeId);
    public Guid GetNodeType(Guid id) => _datastore.GetNodeType(id);
    public Guid GetNodeType(int id) => _datastore.GetNodeType(id);
    public Guid GetNodeType(IdKey id) => _datastore.GetNodeType(id);
    public Dictionary<IdKey, Guid> GetNodeType(IEnumerable<IdKey> ids) => _datastore.GetNodeType(ids);

    public bool Exists(Guid id) => _datastore.Exists(id);
    public bool ExistsAndIsType(Guid id, Guid nodeTypeId) => _datastore.ExistsAndIsType(id, nodeTypeId);
    public bool ContainsRelation(Guid relationId, Guid from, Guid to, bool fromTargetToSource)
        => _datastore.ContainsRelation(relationId, from, to, fromTargetToSource);
    public INodeData[] GetRelatedNodesFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null)
        => _datastore.GetRelatedNodesFromPropertyId(propertyId, from, ctx ?? Context);
    public bool TryGetRelatedNodeFromPropertyId(Guid propertyId, Guid from, [MaybeNullWhen(false)] out INodeData node)
        => _datastore.TryGetRelatedNodeFromPropertyId(propertyId, from, out node);
    public int GetRelatedCountFromPropertyId(Guid propertyId, Guid from)
        => _datastore.GetRelatedCountFromPropertyId(propertyId, from);
    public IEnumerable<Guid> GetRelatedNodeIdsFromRelationId(Guid relationId, Guid from, bool fromTargetToSource)
        => _datastore.GetRelatedNodeIdsFromRelationId(relationId, from, fromTargetToSource);

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

    public (int NodeId, string Text)[] GetTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids)
        => _datastore.GetTextExtractsForExistingNodesAndWhereContent(ids);
    public (int NodeId, string Text)[] GetSemanticTextExtractsForExistingNodesAndWhereContent(IEnumerable<int> ids)
        => _datastore.GetSemanticTextExtractsForExistingNodesAndWhereContent(ids);
}
