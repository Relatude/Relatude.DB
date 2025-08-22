//using System.Diagnostics.CodeAnalysis;
//using WAF.AI;
//using WAF.Common;
//using WAF.Connection;
//using WAF.Datamodels;
//using WAF.Query.Data;
//using WAF.Serialization;
//using WAF.Settings;
//using WAF.Transactions;

//namespace WAF.DataStores {
//    public class DataStoreRemote : IDataStore {
//        readonly Datamodel _datamodel;
//        readonly IMultiThreadedConnection _cn;
//        public DataStoreRemote(Datamodel datamodel, RemoteConfiguration? config = null, IDataStore? local = null) {
//            if (config == null) config = new RemoteConfiguration();
//            _cn = config.Protocol switch {
//                RemoteProtocol.Sockets => new MultiThreadedSocketConnection(config),
//                RemoteProtocol.Http => new MultiThreadedHttpConnection(config),
//                RemoteProtocol.Loopback => new LoopbackConnection(local),
//                _ => throw new NotSupportedException(),
//            };
//            _datamodel = datamodel;
//        }

//        public DataStoreRemote() {
//        }

//        public Datamodel Datamodel => _datamodel;

//        public IAIProvider AI => throw new NotImplementedException();

//        public IO.IIOProvider IO => throw new NotImplementedException();

//        public long Timestamp => throw new NotImplementedException();

//        async Task<Stream> sendAndCheckResponseForServerErrors(Stream s) {
//            var response = await _cn.SendAndReceiveBinary(s);
//            RemoteServerException.ThrowExceptionIfContentIsServerError(response);
//            return response;
//        }
//        public async Task<long> ExecuteAsync(TransactionData transaction, bool flushToDisk = false) {
//            using var request = new MemoryStream();
//            request.WriteString(nameof(ExecuteAsync));
//            ToBytes.ActionBaseList(transaction.Actions, _datamodel, request);
//            using var response = await sendAndCheckResponseForServerErrors(request);
//            return response.ReadLong();
//        }
//        public async Task<INodeData> GetAsync(Guid id) {
//            using var request = new MemoryStream();
//            request.WriteString(nameof(GetAsync));
//            request.WriteGuid(id);
//            using var response = await sendAndCheckResponseForServerErrors(request);
//            return FromBytes.NodeData(_datamodel, response);
//        }
//        public async Task<INodeData> GetAsync(int id) {
//            using var request = new MemoryStream();
//            request.WriteString(nameof(GetAsync) + "_uint");
//            request.WriteUInt(id);
//            using var response = await sendAndCheckResponseForServerErrors(request);
//            return FromBytes.NodeData(_datamodel, response);
//        }
//        public async Task<object> QueryAsync(string query) {
//            using var request = new MemoryStream();
//            request.WriteString(nameof(QueryAsync));
//            request.WriteString(query);
//            using var response = await sendAndCheckResponseForServerErrors(request);
//            return FromBytes.ObjectFromBytes(_datamodel, response);
//        }
//        public async Task<StoreStatus> MaintenanceAsync(MaintenanceAction actions) {
//            using var request = new MemoryStream();
//            request.WriteString(nameof(MaintenanceAsync));
//            request.WriteInt((int)actions);
//            using var response = await sendAndCheckResponseForServerErrors(request);
//            return StoreStatus.DeSerialize(response);
//        }
//        public long Execute(TransactionData transaction, bool flushToDisk = false) => ExecuteAsync(transaction, flushToDisk).Result;
//        public INodeData Get(Guid id) => GetAsync(id).Result;
//        public INodeData Get(int id) => GetAsync(id).Result;
//        public object Query(string query) => QueryAsync(query).Result;
//        public StoreStatus Maintenance(MaintenanceAction actions) => MaintenanceAsync(actions).Result;
//        public long GetStateId() => GetStateIdAsync().Result;
//        public async Task<long> GetStateIdAsync() {
//            using var request = new MemoryStream();
//            request.WriteString(nameof(GetStateIdAsync));
//            using var response = await sendAndCheckResponseForServerErrors(request);
//            return FromBytes.Long(response);
//        }
//        public void Dispose() {
//            _cn.Dispose();
//        }

//        public Task<long> ExecuteAsync(TransactionData transaction, bool flushToDisk = false, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Task<INodeData> GetAsync(Guid id, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Task<INodeData> GetAsync(int id, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Task<object> QueryAsync(string query, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Task<StoreStatus> MaintenanceAsync(MaintenanceAction actions, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public long Execute(TransactionData transaction, bool flushToDisk = false, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Task<ContentSettings> GetContentSettings() {
//            throw new NotImplementedException();
//        }

//        public INodeData Get(Guid id, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public INodeData Get(int id, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public object Query(string query, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Task<object> IncrementValueAsync(Guid nodeID, Guid property, object value, bool flushToDisk = false, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }
//        public Task<object> IncrementValueAsync(int nodeID, Guid property, object value, bool flushToDisk = false, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Task<Guid> RequestLockAsync(Guid nodeID, double lockDurationInMs, double maxWaitTimeInMs, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }
//        public Task<Guid> RequestLockAsync(int nodeID, double lockDurationInMs, double maxWaitTimeInMs, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public void RefreshLock(Guid lockId, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public void Unlock(Guid lockId, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public StoreStatus Maintenance(MaintenanceAction actions, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Task<Guid> AddCulture(int lcid) {
//            throw new NotImplementedException();
//        }

//        public Task DeleteCulture(Guid cultureId) {
//            throw new NotImplementedException();
//        }

//        public Task DeleteCulture(int lcid) {
//            throw new NotImplementedException();
//        }

//        public Task ChangeCulture(Guid cultureId, int newLcid) {
//            throw new NotImplementedException();
//        }

//        public Task SetSettings(ContentSettings settings, out bool restartRequired) {
//            throw new NotImplementedException();
//        }

//        public bool TryGet(Guid id, [MaybeNullWhen(false)] out INodeData nodeData, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }
//        public bool TryGet(int id, [MaybeNullWhen(false)] out INodeData nodeData, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public IEnumerable<INodeData> Get(IEnumerable<Guid> ids, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public IEnumerable<INodeData> Get(IEnumerable<int> ids, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public bool Exists(Guid id, Guid nodeTypeId, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Task RewriteStoreAsync(string newLogFileKey, bool hotSwapToNewFile, IO.IIOProvider? destinationIO = null) {
//            throw new NotImplementedException();
//        }

//        public Task CopyAsync(string newLogFileKey) {
//            throw new NotImplementedException();
//        }

//        public Task UploadFile(Stream stream, Guid nodeId, Guid property, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }

//        public Stream DownloadFile(Guid nodeId, Guid property, UserContext? ctx = null) {
//            throw new NotImplementedException();
//        }
//    }
//}