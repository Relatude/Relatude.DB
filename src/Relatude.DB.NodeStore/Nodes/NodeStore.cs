
using Relatude.DB.AI;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Query;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;


namespace Relatude.DB.Nodes;

//public enum NodeOperation : byte {
//    InsertOrFail, // [DEFAULT] inserts a new node, fails if a node with same ID already exists ( if ID is set )
//    InsertIfNotExists, // inserts a new node, does nothing if a node with the ID already exists
//    DeleteOrFail, // [DEFAULT] deletes a node, fails if the node does not exist
//    DeleteIfExists, // deletes a node, ignored if the node does not exist
//    UpdateIfExists, // updates a node, ignored if the node does not exist and only updates if changed, faster if not changed (avoids disk writes), slower if changed due to comparison
//    UpdateOrFail, // [DEFAULT] updates a node, fails if the node does not exist
//    ForceUpdate, // updates a node, fails if the node does not exist, but update even if not different ( faster if changed as no comparison, slower if not changed )
//    Upsert, // inserts a new node or updates an existing one, checks if node is different before updating, faster if not changed (avoids disk writes), slower if changed due to unnecessary compare
//    ForceUpsert, // inserts a new node or update an existing one, update even if node is the same  ( faster if changed as no comparison, slower if not changed )
//    ChangeType, // changes the type of a node, fails if node does not exist
//    ReIndex, // triggers a re-index of the node, ignored if the node does not exist
//}

public sealed class NodeStore : IDisposable {
    public readonly IDataStore Datastore;
    public readonly NodeMapper Mapper;
    public AIEngine AI => Datastore.AI;
    internal readonly List<INodeTransactionPlugin> TransactionPlugins = new();
    public void RegisterTransactionPlugin(INodeTransactionPlugin plugin) {
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        if (Datastore.State == DataStoreState.Open) {
            throw new InvalidOperationException("Cannot register transaction plugin after the store is opened. ");
        }
        TransactionPlugins.Add(plugin);
        plugin.Store = this;
    }
    public NodeStore(IDataStore datastore) {
        Datastore = datastore;
        var sw = Stopwatch.StartNew();
        datastore.Datamodel.EnsureInitalization();
        foreach (var plugin in TransactionPlugins) plugin.Store = this;
        var interfaceClasses = InterfaceGen.GetImplementations(datastore.Datamodel);
        var mappers = MapperGen.GenerateValueMappers(datastore.Datamodel);
        var code = interfaceClasses.Concat(mappers).ToList();
        var totalCode = string.Join("\n", code.Select(c => c.code));
        ulong codeHash = 0;
        foreach (var c in code) codeHash ^= c.code.XXH64Hash();
        var fileKey = datastore.FileKeys.MapperDll_GetFileKey(codeHash);
        foreach (var f in datastore.FileKeys.MapperDll_GetAllFileKeys(datastore.IO)) {
            if (f != fileKey) datastore.IO.DeleteIfItExists(f);
        }
        byte[] dll;
        if (datastore.IO.DoesNotExistsOrIsEmpty(fileKey)) {
            Stopwatch sw2 = Stopwatch.StartNew();
            dll = Compiler.BuildDll(code, datastore.Datamodel);
            datastore.LogInfo("Recompiled mapper DLL in " + sw2.ElapsedMilliseconds.To1000N() + "ms.");
            datastore.IO.WriteAllBytes(fileKey, dll);
        } else {
            dll = datastore.IO.ReadAllBytes(fileKey);
            datastore.LogInfo("Loading mapper DLL from disk. ");
        }
        var types = Compiler.LoadDll(dll);
        Mapper = new NodeMapper(types, this);
        sw.Stop();
        datastore.LogInfo("Mapper ready with " + code.Count + " model" + (code.Count != 1 ? "s" : "") + " in " + sw.ElapsedMilliseconds.To1000N() + "ms.");
    }
    public DataStoreState State => Datastore.State;

    public T Create<T>() => Mapper.NewObjectFromType<T>();
    public T CreateAndInsert<T>(Action<T, Transaction>? setProperties = null) where T : notnull {
        var node = Mapper.NewObjectFromType<T>();
        var t = CreateTransaction();
        t.Insert(node);
        if (setProperties != null) {
            setProperties.Invoke(node, t);
            t.Update(node);
        }
        t.Execute();
        return Get<T>(Mapper.GetIdGuidOrCreate(node!));
    }

    public TransactionResult Insert(object node, bool flushToDisk = false, bool ignoreRelated = false) => Execute(new Transaction(this).Insert(node, ignoreRelated), flushToDisk);
    public TransactionResult Insert(IEnumerable<object> nodes, bool flushToDisk = false, bool ignoreRelated = false) => Execute(new Transaction(this).Insert(nodes, ignoreRelated), flushToDisk);
    public TransactionResult InsertOrFail(object node, bool flushToDisk = false, bool ignoreRelated = false) => Execute(new Transaction(this).InsertOrFail(node, ignoreRelated), flushToDisk);
    public TransactionResult InsertOrFail(IEnumerable<object> nodes, bool flushToDisk = false, bool ignoreRelated = false) => Execute(new Transaction(this).InsertOrFail(nodes, ignoreRelated), flushToDisk);
    public TransactionResult InsertIfNotExists(object node, bool flushToDisk = false, bool ignoreRelated = false) => Execute(new Transaction(this).InsertIfNotExists(node, ignoreRelated), flushToDisk);
    public TransactionResult InsertIfNotExists(IEnumerable<object> nodes, bool flushToDisk = false, bool ignoreRelated = false) => Execute(new Transaction(this).InsertIfNotExists(nodes, ignoreRelated), flushToDisk);

    public Task<TransactionResult> InsertAsync(object node, bool flushToDisk = false, bool ignoreRelated = false) => ExecuteAsync(new Transaction(this).Insert(node, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertAsync(IEnumerable<object> nodes, bool flushToDisk = false, bool ignoreRelated = false) => ExecuteAsync(new Transaction(this).Insert(nodes, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertOrFailAsync(object node, bool flushToDisk = false, bool ignoreRelated = false) => ExecuteAsync(new Transaction(this).InsertOrFail(node, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertOrFailAsync(IEnumerable<object> nodes, bool flushToDisk = false, bool ignoreRelated = false) => ExecuteAsync(new Transaction(this).InsertOrFail(nodes, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertIfNotExistsAsync(object node, bool flushToDisk = false, bool ignoreRelated = false) => ExecuteAsync(new Transaction(this).InsertIfNotExists(node, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertIfNotExistsAsync(IEnumerable<object> nodes, bool flushToDisk = false, bool ignoreRelated = false) => ExecuteAsync(new Transaction(this).InsertIfNotExists(nodes, ignoreRelated), flushToDisk);

    public TransactionResult Update(object node, bool flushToDisk = false) => Execute(new Transaction(this).Update(node), flushToDisk);
    public TransactionResult Update<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).Update(nodes), flushToDisk);
    public TransactionResult UpdateIfExists(object node, bool flushToDisk = false) => Execute(new Transaction(this).UpdateIfExists(node), flushToDisk);
    public TransactionResult UpdateIfExists<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).UpdateIfExists(nodes), flushToDisk);
    public TransactionResult UpdateOrFail(object node, bool flushToDisk = false) => Execute(new Transaction(this).UpdateOrFail(node), flushToDisk);
    public TransactionResult UpdateOrFail<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).UpdateOrFail(nodes), flushToDisk);
    public TransactionResult ForceUpdate(object node, bool flushToDisk = false) => Execute(new Transaction(this).ForceUpdate(node), flushToDisk);
    public TransactionResult ForceUpdate<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ForceUpdate(nodes), flushToDisk);

    public Task<TransactionResult> UpdateAsync(object node, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Update(node), flushToDisk);
    public Task<TransactionResult> UpdateAsync<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => ExecuteAsync(new Transaction(this).Update(nodes), flushToDisk);
    public Task<TransactionResult> UpdateIfExistsAsync(object node, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).UpdateIfExists(node), flushToDisk);
    public Task<TransactionResult> UpdateIfExistsAsync<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => ExecuteAsync(new Transaction(this).UpdateIfExists(nodes), flushToDisk);
    public Task<TransactionResult> UpdateOrFailAsync(object node, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).UpdateOrFail(node), flushToDisk);
    public Task<TransactionResult> UpdateOrFailAsync<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => ExecuteAsync(new Transaction(this).UpdateOrFail(nodes), flushToDisk);
    public Task<TransactionResult> ForceUpdateAsync(object node, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).ForceUpdate(node), flushToDisk);
    public Task<TransactionResult> ForceUpdateAsync<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => ExecuteAsync(new Transaction(this).ForceUpdate(nodes), flushToDisk);

    public void DeleteMany<T>(Expression<Func<T, bool>> expression, bool flushDisk = false) {
        var ids = Query<T>().Where(expression).SelectId().Execute();
        DeleteIfExists(ids, flushDisk);
    }
    public void DeleteMany<T>(bool includeDescendants = true, bool flushDisk = false) {
        var ids = Query<T>().WhereTypes([typeof(T)], includeDescendants).SelectId().Execute();
        DeleteIfExists(ids, flushDisk);
    }

    public TransactionResult Delete(int id, bool flushToDisk = false) => Execute(new Transaction(this).Delete(id), flushToDisk);
    public TransactionResult Delete(IEnumerable<int> ids, bool flushToDisk = false) => Execute(new Transaction(this).Delete(ids), flushToDisk);
    public TransactionResult DeleteOrFail(int id, bool flushToDisk = false) => Execute(new Transaction(this).DeleteOrFail(id), flushToDisk);
    public TransactionResult DeleteOrFail(IEnumerable<int> ids, bool flushToDisk = false) => Execute(new Transaction(this).DeleteOrFail(ids), flushToDisk);
    public TransactionResult DeleteIfExists(int id, bool flushToDisk = false) => Execute(new Transaction(this).DeleteIfExists(id), flushToDisk);
    public TransactionResult DeleteIfExists(IEnumerable<int> ids, bool flushToDisk = false) => Execute(new Transaction(this).DeleteIfExists(ids), flushToDisk);

    public TransactionResult Delete(Guid id, bool flushToDisk = false) => Execute(new Transaction(this).Delete(id), flushToDisk);
    public TransactionResult Delete(IEnumerable<Guid> ids, bool flushToDisk = false) => Execute(new Transaction(this).Delete(ids), flushToDisk);
    public TransactionResult DeleteOrFail(Guid id, bool flushToDisk = false) => Execute(new Transaction(this).DeleteOrFail(id), flushToDisk);
    public TransactionResult DeleteOrFail(IEnumerable<Guid> ids, bool flushToDisk = false) => Execute(new Transaction(this).DeleteOrFail(ids), flushToDisk);
    public TransactionResult DeleteIfExists(Guid id, bool flushToDisk = false) => Execute(new Transaction(this).DeleteIfExists(id), flushToDisk);
    public TransactionResult DeleteIfExists(IEnumerable<Guid> ids, bool flushToDisk = false) => Execute(new Transaction(this).DeleteIfExists(ids), flushToDisk);

    public Task<TransactionResult> DeleteAsync(Guid id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Delete(id), flushToDisk);
    public Task<TransactionResult> DeleteAsync(IEnumerable<Guid> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Delete(ids), flushToDisk);
    public Task<TransactionResult> DeleteOrFailAsync(Guid id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteOrFail(id), flushToDisk);
    public Task<TransactionResult> DeleteOrFailAsync(IEnumerable<Guid> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteOrFail(ids), flushToDisk);
    public Task<TransactionResult> DeleteIfExistsAsync(Guid id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteIfExists(id), flushToDisk);
    public Task<TransactionResult> DeleteIfExistsAsync(IEnumerable<Guid> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteIfExists(ids), flushToDisk);

    public Task<TransactionResult> DeleteAsync(int id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Delete(id), flushToDisk);
    public Task<TransactionResult> DeleteAsync(IEnumerable<int> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Delete(ids), flushToDisk);
    public Task<TransactionResult> DeleteOrFailAsync(int id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteOrFail(id), flushToDisk);
    public Task<TransactionResult> DeleteOrFailAsync(IEnumerable<int> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteOrFail(ids), flushToDisk);
    public Task<TransactionResult> DeleteIfExistsAsync(int id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteIfExists(id), flushToDisk);
    public Task<TransactionResult> DeleteIfExistsAsync(IEnumerable<int> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteIfExists(ids), flushToDisk);

    public TransactionResult ForceUpsert(object node, bool flushToDisk = false) => Execute(new Transaction(this).ForceUpsert(node), flushToDisk);
    public TransactionResult ForceUpsert<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ForceUpsert(nodes), flushToDisk);
    public TransactionResult Upsert(object node, bool flushToDisk = false) => Execute(new Transaction(this).Upsert(node), flushToDisk);
    public TransactionResult Upsert<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).Upsert(nodes), flushToDisk);

    public TransactionResult Relate<T>(T fromNode, Expression<Func<T, object>> expression, object toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(fromNode, expression, toNode), flushToDisk);
    public TransactionResult Relate<T>(int fromId, Expression<Func<T, object>> expression, int toId, bool flushToDisk = false) => Execute(new Transaction(this).Relate(fromId, expression, toId), flushToDisk);
    public TransactionResult Relate<T>(Guid fromId, Expression<Func<T, object>> expression, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).Relate(fromId, expression!, toId), flushToDisk);
    public TransactionResult Relate<T>(Guid fromId, Expression<Func<T, object>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) => Execute(new Transaction(this).Relate(fromId, expression, toIds), flushToDisk);
    public TransactionResult Relate(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).Relate(fromId, propertyId, toId), flushToDisk);
    public TransactionResult Relate(int fromId, Guid propertyId, int toId, bool flushToDisk = false) => Execute(new Transaction(this).Relate(fromId, propertyId, toId), flushToDisk);

    //public TransactionResult Relate<T>(OneOne<T> relation, T fromNode, T toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);
    //public TransactionResult Relate<T>(ManyMany<T> relation, T fromNode, T toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);
    //public TransactionResult Relate<TFrom, TTo>(OneToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);
    //public TransactionResult Relate<TFrom, TTo>(OneToOne<TFrom, TTo> relation, TFrom fromNode, TTo toNode, bool flushToDisk = false) => throw new NotImplementedException();
    //public TransactionResult Relate<TFrom, TTo>(ManyToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);

    public TransactionResult UnRelate<T>(T fromNode, Expression<Func<T, object>> expression, object toNode, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).UnRelate(fromNode, expression, toNode), flushToDisk);
    public TransactionResult UnRelate<T>(Guid fromId, Expression<Func<T, object>> expression, Guid toId, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).UnRelate(fromId, expression, toId), flushToDisk);
    public TransactionResult UnRelate<T>(Guid fromId, Expression<Func<T, object>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).UnRelate(fromId, expression, toIds), flushToDisk);
    public TransactionResult UnRelate(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).UnRelate(fromId, propertyId, toId), flushToDisk);
    public TransactionResult UnRelate(int fromId, Guid propertyId, int toId, bool flushToDisk = false) => Execute(new Transaction(this).UnRelate(fromId, propertyId, toId), flushToDisk);

    public TransactionResult SetRelation<T>(T fromNode, Expression<Func<T, object>> expression, object toNode, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).SetRelation(fromNode, expression, toNode), flushToDisk);
    public TransactionResult SetRelation<T>(Guid fromId, Expression<Func<T, object>> expression, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, expression, toId), flushToDisk);
    public TransactionResult SetRelation<T>(int fromId, Expression<Func<T, object>> expression, int toId, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, expression, toId), flushToDisk);
    public TransactionResult SetRelation(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, propertyId, toId), flushToDisk);

    public TransactionResult SetRelation<T>(T fromNode, Expression<Func<T, object>> expression, IEnumerable<object> toNodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).SetRelation(fromNode, expression, toNodes), flushToDisk);
    public TransactionResult SetRelation<T>(T fromNode, Expression<Func<T, object>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).SetRelation(fromNode, expression, toIds), flushToDisk);
    public TransactionResult SetRelation<T>(Guid fromId, Expression<Func<T, object>> expression, IEnumerable<int> toIds, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, expression, toIds), flushToDisk);
    public TransactionResult SetRelation<T>(int fromId, Expression<Func<T, object>> expression, IEnumerable<int> toIds, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, expression, toIds), flushToDisk);
    public TransactionResult SetRelation(Guid fromId, Guid propertyId, IEnumerable<Guid> toIds, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, propertyId, toIds), flushToDisk);

    public TransactionResult ClearAndSetRelation<T>(T fromNode, Expression<Func<T, object>> expression, IEnumerable<object> toNodes, bool flushToDisk = false) where T : notnull
        => Execute(new Transaction(this).ClearRelations(fromNode, expression).SetRelation(fromNode, expression, toNodes), flushToDisk);
    public TransactionResult ClearAndSetRelation<T>(T fromNode, Expression<Func<T, object>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) where T : notnull
        => Execute(new Transaction(this).ClearRelations(fromNode, expression).SetRelation(fromNode, expression, toIds), flushToDisk);
    public TransactionResult ClearAndSetRelation<T>(Guid fromId, Expression<Func<T, object>> expression, IEnumerable<int> toIds, bool flushToDisk = false) where T : notnull
        => Execute(new Transaction(this).ClearRelations(fromId, expression).SetRelation(fromId, expression, toIds), flushToDisk);
    public TransactionResult ClearAndSetRelation<T>(int fromId, Expression<Func<T, object>> expression, IEnumerable<int> toIds, bool flushToDisk = false) where T : notnull
        => Execute(new Transaction(this).ClearRelations(fromId, expression).SetRelation(fromId, expression, toIds), flushToDisk);
    public TransactionResult ClearAndSetRelation(Guid fromId, Guid propertyId, IEnumerable<Guid> toIds, bool flushToDisk = false)
        => Execute(new Transaction(this).ClearRelations(fromId, propertyId).SetRelation(fromId, propertyId, toIds), flushToDisk);

    public TransactionResult ClearRelation<T>(T fromNode, Expression<Func<T, object>> expression, object toNode, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelation(fromNode, expression, toNode), flushToDisk);
    public TransactionResult ClearRelation<T>(Guid fromId, Expression<Func<T, object>> expression, Guid toId, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelation(fromId, expression, toId), flushToDisk);
    public TransactionResult ClearRelation<T>(int fromId, Expression<Func<T, object>> expression, Guid toId, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelation(fromId, expression, toId), flushToDisk);
    public TransactionResult ClearRelation(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).ClearRelation(fromId, propertyId, toId), flushToDisk);
    public TransactionResult ClearRelation(int fromId, Guid propertyId, int toId, bool flushToDisk = false) => Execute(new Transaction(this).ClearRelation(fromId, propertyId, toId), flushToDisk);
    public TransactionResult ClearRelations<T>(T fromNode, Expression<Func<T, object>> expression, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelations(fromNode, expression), flushToDisk);
    public TransactionResult ClearRelations<T>(int fromId, Expression<Func<T, object>> expression, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelations(fromId, expression), flushToDisk);
    public TransactionResult ClearRelations<T>(Guid fromId, Expression<Func<T, object>> expression, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelations(fromId, expression), flushToDisk);
    public TransactionResult ClearRelations(Guid fromId, Guid propertyId, bool flushToDisk = false) => Execute(new Transaction(this).ClearRelations(fromId, propertyId), flushToDisk);

    public TransactionResult ReIndex(Guid id, bool flushToDisk = false) => Execute(new Transaction(this).ReIndex(id), flushToDisk);
    public TransactionResult ReIndex(int id, bool flushToDisk = false) => Execute(new Transaction(this).ReIndex(id), flushToDisk);

    public void ChangeType(Guid id, Guid newTypeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType(id, newTypeId), flushToDisk);
    public void ChangeType(int id, Guid newTypeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType(id, newTypeId), flushToDisk);
    public void ChangeType<T>(object node, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType<T>(node), flushToDisk);
    public void ChangeType<T>(Guid nodeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType<T>(nodeId), flushToDisk);
    public void ChangeType<T>(int nodeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType<T>(nodeId), flushToDisk);

    public async Task<T> GetAsync<T>(Guid id) => Mapper.CreateObjectFromNodeData<T>(await Datastore.GetAsync(id));
    public Task<TransactionResult> ExecuteAsync(Transaction transaction, bool flushToDisk = false) => Datastore.ExecuteAsync(transaction._transactionData, flushToDisk);
    public IQueryOfNodes<object, object> Query() => new QueryOfNodes<object, object>(this);
    public IQueryOfNodes<object, object> QueryType(Guid nodeTypeId) => new QueryOfNodes<object, object>(this, Datastore.Datamodel.NodeTypes[nodeTypeId].CodeName);
    public IQueryOfNodes<object, object> QueryType(string typeName) => new QueryOfNodes<object, object>(this, typeName);

    public Task<object?> EvaluateForJsonAsync(string query, List<Parameter> parameters) {
        return new QueryStringBuilder(this, query, parameters).Prepare().EvaluateForJsonAsync();
    }
    public IQueryOfNodes<T, T> Query<T>(Guid id) => new QueryOfNodes<T, T>(this).Where("a => a." + Datastore.Datamodel.NodeTypes[Mapper.GetNodeTypeId(typeof(T))].NameOfPublicIdProperty + " == \"" + id + "\"");
    public IQueryOfNodes<T, T> Query<T>(int id) => new QueryOfNodes<T, T>(this).Where("a => a." + Datastore.Datamodel.NodeTypes[Mapper.GetNodeTypeId(typeof(T))].NameOfInternalIdProperty + " == " + id + "");
    public IQueryOfNodes<T, T> Query<T>(IdKey id) => id.Int == 0 ? Query<T>(id.Guid) : Query<T>(id.Int);
    public IQueryOfNodes<T, T> Query<T>() => new QueryOfNodes<T, T>(this);
    public IQueryOfNodes<T, T> Query<T>(IEnumerable<Guid> ids) => new QueryOfNodes<T, T>(this).WhereInIds(ids);
    public IQueryOfNodes<T, T> Query<T>(Expression<Func<T, bool>> expression) => new QueryOfNodes<T, T>(this).Where(expression);
    public IQueryOfNodes<T, T> QueryRelated<T>(Guid propertyId, Guid nodeId) => new QueryOfNodes<T, T>(this).WhereRelates(propertyId, nodeId);

    public bool RelationExists<T>(Guid fromId, Expression<Func<T, object>> expression, Guid toId) => Query<T>(fromId).WhereRelates<T, object>(expression, toId).Count() > 0;
    public Task FlushAsync() => Datastore.MaintenanceAsync(MaintenanceAction.FlushDisk);
    public Task MaintenanceAsync(MaintenanceAction options) => Datastore.MaintenanceAsync(options);


    public long Count() => Query<object>().Count();
    public long Count<T>() => Query<T>().Count();

    public object Get(int id) => Mapper.CreateObjectFromNodeData(Datastore.Get(id));
    public object Get(Guid id) => Mapper.CreateObjectFromNodeData(Datastore.Get(id));
    public object Get(IdKey id) => Mapper.CreateObjectFromNodeData(Datastore.Get(id));

    public T Get<T>(int id) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id));
    public T Get<T>(Guid id) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id));
    public T Get<T>(IdKey id) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id));

    public bool Exists(Guid id) => Datastore.Exists(id, NodeConstants.BaseNodeTypeId);
    public bool Exists<T>(Guid id) => Datastore.Exists(id, Mapper.GetNodeTypeId(typeof(T)));

    public IEnumerable<T> Get<T>(IEnumerable<int> ids) => Datastore.Get(ids).Select(Mapper.CreateObjectFromNodeData<T>);
    //public IEnumerable<T> Get<T>(IEnumerable<int> ids) => Datastore.Get(ids.Select(id => (int)id)).Select(Mapper.CreateObjectFromNodeData<T>);
    public IEnumerable<T> Get<T>(IEnumerable<Guid> ids) => Datastore.Get(ids).Select(Mapper.CreateObjectFromNodeData<T>);

    public bool TryGet<T>(Guid id, [MaybeNullWhen(false)] out T node) {
        if (Datastore.TryGet(id, out var nodeData)) {
            node = Mapper.CreateObjectFromNodeData<T>(nodeData);
            return true;
        }
        node = default;
        return false;
    }
    public bool TryGet<T>(int id, [MaybeNullWhen(false)] out T node) {
        if (Datastore.TryGet((int)id, out var nodeData)) {
            node = Mapper.CreateObjectFromNodeData<T>(nodeData);
            return true;
        }
        node = default;
        return false;
    }
    public T Get<T>(INodeData nodeData) => Mapper.CreateObjectFromNodeData<T>(nodeData);
    public IEnumerable<T> GetRelatedNodes<T>(Guid propertyId, Guid nodeId) { // used by mapper internally
        throw new NotImplementedException("GetRelated with propertyId is not implemented in NodeStore.");
    }
    public bool TryGetRelatedNode<T>(Guid propertyId, Guid nodeId, [MaybeNullWhen(false)] out T value) { // used by mapper internally
        throw new NotImplementedException("GetRelated with propertyId is not implemented in NodeStore.");
    }
    public IEnumerable<T> GetRelated<T>(NodeDataWithRelations[] node) { // used by mapper internally
        foreach (var item in node) yield return Mapper.CreateObjectFromNodeData<T>(item);
    }
    public Task<TransactionResult> ExecuteAsync(ActionModel[] actions, bool flushToDisk = false) {
        throw new NotImplementedException();
    }
    public TransactionResult Execute(Transaction transaction, bool flushToDisk = false) {
        if (transaction.Count == 0) return TransactionResult.Empty;
        try {
            transaction.PrepareRelevantPlugins();
            transaction.OnBeforeExecute();
            var result = Datastore.Execute(transaction._transactionData, flushToDisk);
            transaction.OnAfterExecute(result);
            return result;
        } catch (Exception error) {
            transaction.OnErrorExecute(error);
            throw;
        }
    }
    public void Flush() => Maintenance(MaintenanceAction.FlushDisk);
    public void Maintenance(MaintenanceAction actions) => Datastore.Maintenance(actions);
    public Transaction CreateTransaction() => new(this);

    public bool TryRequestGlobalLock(out Guid lockId, double lockDurationInMs = 1000, double maxWaitTimeInMs = 1000) {
        try {
            lockId = RequestGlobalLock(lockDurationInMs, maxWaitTimeInMs);
            return true;
        } catch {
            lockId = Guid.Empty;
            return false;
        }
    }
    public Guid RequestGlobalLock(double lockDurationInMs, double maxWaitTimeInMs) => RequestGlobalLockAsync(lockDurationInMs, maxWaitTimeInMs).Result;
    public Task<Guid> RequestGlobalLockAsync(double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => Datastore.RequestGlobalLockAsync(lockDurationInMs, maxWaitTimeInMs);
    public Task<Guid> RequestLockAsync(Guid nodeId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => Datastore.RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs);

    public Guid RequestLock(Guid nodeId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs).Result;
    public Guid RequestLock(int nodeId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs).Result;
    public Guid RequestLock(object node, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => RequestLockAsync(node, lockDurationInMs, maxWaitTimeInMs).Result;
    public Task<Guid> RequestLockAsync(int nodeId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => Datastore.RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs);
    public Task<Guid> RequestLockAsync(object node, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) {
        if (Mapper.TryGetIdGuid(node, out var guid)) return RequestLockAsync(guid, lockDurationInMs, maxWaitTimeInMs);
        if (Mapper.TryGetIdUInt(node, out var id)) return RequestLockAsync(id, lockDurationInMs, maxWaitTimeInMs);
        throw new Exception("Only nodes with Guid or int id accepted. ");
    }
    public bool TryRequestLock(Guid nodeId, out Guid lockId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) {
        try {
            lockId = RequestLock(nodeId, lockDurationInMs, maxWaitTimeInMs);
            return true;
        } catch {
            lockId = Guid.Empty;
            return false;
        }
    }
    public bool TryRequestLock(int nodeId, out Guid lockId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) {
        try {
            lockId = RequestLock(nodeId, lockDurationInMs, maxWaitTimeInMs);
            return true;
        } catch {
            lockId = Guid.Empty;
            return false;
        }
    }
    public bool TryRequestLock(object node, out Guid lockId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) {
        try {
            lockId = RequestLock(node, lockDurationInMs, maxWaitTimeInMs);
            return true;
        } catch {
            lockId = Guid.Empty;
            return false;
        }
    }

    public void RefreshLock(Guid lockId) => Datastore.RefreshLock(lockId);
    public void ReleaseLock(Guid lockId) => Datastore.ReleaseLock(lockId);

    //public void UpdateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateProperty(nodeId, expression, value), flushToDisk);
    public void UpdateProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateProperty(nodeId, propertyId, value), flushToDisk);
    public void UpdateProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateProperty(nodeId, propertyId, value), flushToDisk);
    public void UpdateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void UpdateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void UpdateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void UpdateProperty<T, V>(IEnumerable<Guid> ids, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => Execute(new Transaction(this).UpdateProperty(ids, expression, value), flushToDisk);

    public void UpdateProperties<T>(Guid nodeId, params Tuple<Expression<Func<T, object>>, object>[] propertyValuePairs) where T : notnull => Execute(new Transaction(this).UpdateProperties(nodeId, propertyValuePairs));
    //public void UpdateProperties<T>(Guid nodeId, IEnumerable<Tuple<Expression<Func<T, object>>, object>> propertyValuePairs, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).UpdateProperties(nodeId, propertyValuePairs), flushToDisk);
    //public void UpdateProperties<T>(Guid nodeId, Expression<Func<T, object>>[] expressions, object[] values, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).UpdateProperties(nodeId, expressions, values), flushToDisk);

    public void UpdateIfDifferentProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateIfDifferentProperty(nodeId, propertyId, value), flushToDisk);
    public void UpdateIfDifferentProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateIfDifferentProperty(nodeId, propertyId, value), flushToDisk);
    public void UpdateIfDifferentProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateIfDifferentProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void UpdateIfDifferentProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateIfDifferentProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void UpdateIfDifferentProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateIfDifferentProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);

    public void ResetProperty(Guid nodeId, Guid propertyId, bool flushToDisk = false) => Execute(new Transaction(this).ResetProperty(nodeId, propertyId), flushToDisk);
    public void ResetProperty(int nodeId, Guid propertyId, bool flush) => Execute(new Transaction(this).ResetProperty(nodeId, propertyId), flush);
    public void ResetProperty<T, V>(T node, Expression<Func<T, V>> expression, bool flushToDisk = false) where T : notnull where V : notnull => ResetProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, flushToDisk);
    public void ResetProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, bool flushToDisk = false) => ResetProperty(nodeId, Mapper.GetProperty(expression).Id, flushToDisk);
    public void ResetProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, bool flushToDisk = false) => ResetProperty(nodeId, Mapper.GetProperty(expression).Id, flushToDisk);
    public void AddToProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).AddToProperty(nodeId, propertyId, value), flushToDisk);
    public void AddToProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).AddToProperty(nodeId, propertyId, value), flushToDisk);
    public void AddToProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => AddToProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void AddToProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => AddToProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void AddToProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => AddToProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void MultiplyProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).MultiplyProperty(nodeId, propertyId, value), flushToDisk);
    public void MultiplyProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).MultiplyProperty(nodeId, propertyId, value), flushToDisk);
    public void MultiplyProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => MultiplyProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void MultiplyProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => MultiplyProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void MultiplyProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => MultiplyProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);

    public void ReIndex(int id) => new Transaction(this).ReIndex(id);
    public void ReIndex(Guid id) => new Transaction(this).ReIndex(id);

    public Task FileUploadAsync(Guid nodeId, Guid propertyId, IIOProvider source, string fileKey, string fileName) {
        return Datastore.FileUploadAsync(nodeId, propertyId, source, fileKey, fileName);
    }
    public Task FileUploadAsync(Guid nodeId, Guid propertyId, Stream source, string fileKey, string fileName) {
        return Datastore.FileUploadAsync(nodeId, propertyId, source, fileKey, fileName);
    }
    public Task FileDownloadAsync(Guid nodeId, Guid propertyId, Stream outStream) {
        return Datastore.FileDownloadAsync(nodeId, propertyId, outStream);
    }
    public async Task<byte[]> FileDownloadAsync(Guid nodeId, Guid propertyId) {
        using var ms = new MemoryStream();
        await FileDownloadAsync(nodeId, propertyId, ms);
        return ms.ToArray();
    }
    public Task FileDeleteAsync(Guid nodeId, Guid propertyId) {
        return Datastore.FileDeleteAsync(nodeId, propertyId);
    }

    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, string filePath, string fileKey, string fileName) {
        using var source = File.OpenRead(filePath);
        return FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, source, fileKey, fileName);
    }
    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, Stream source, string fileKey, string fileName) => FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, source, fileKey, fileName);
    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, byte[] data, string fileKey, string fileName) => FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, new MemoryStream(data), fileKey, fileName);
    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, IIOProvider source, string fileKey, string fileName) => FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, source, fileKey, fileName);
    public Task FileDownloadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, Stream outStream) => FileDownloadAsync(nodeId, Mapper.GetProperty(expression).Id, outStream);
    public Task<byte[]> FileDownloadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression) => FileDownloadAsync(nodeId, Mapper.GetProperty(expression).Id);
    public Task FileDeleteAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression) => FileDeleteAsync(nodeId, Mapper.GetProperty(expression).Id);

    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, string filePath, string fileKey, string fileName) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, filePath, fileKey, fileName);
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, Stream source, string fileKey, string fileName) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, source, fileKey, fileName);
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, byte[] data, string fileKey, string fileName) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, new MemoryStream(data), fileKey, fileName);
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, IIOProvider source, string fileKey, string fileName) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, source, fileKey, fileName);
    public Task FileDownloadAsync<T>(T node, Expression<Func<T, FileValue>> expression, Stream outStream) where T : notnull => FileDownloadAsync(Mapper.GetIdGuid(node), expression, outStream);
    public Task<byte[]> FileDownloadAsync<T>(T node, Expression<Func<T, FileValue>> expression) where T : notnull => FileDownloadAsync(Mapper.GetIdGuid(node), expression);
    public Task FileDeleteAsync<T>(T node, Expression<Func<T, FileValue>> expression) where T : notnull => FileDeleteAsync(Mapper.GetIdGuid(node), expression);

    public Task<bool> FileUploadedAndAvailableAsync(Guid nodeId, Guid propertyId) => Datastore.FileUploadedAndAvailableAsync(nodeId, propertyId);
    public Task<bool> FileUploadedAndAvailableAsync<T>(T node, Expression<Func<T, FileValue>> expression) where T : notnull => FileUploadedAndAvailableAsync(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id);

    public Task EnqueueTaskAsync(TaskData task, string? jobId = null) {
        Datastore.EnqueueTask(task, jobId);
        return Task.CompletedTask;
    }
    public void EnqueueTask(TaskData task, string? jobId = null) => Datastore.EnqueueTask(task, jobId);

    public long Timestamp => Datastore.Timestamp;
    public void Dispose() => Datastore.Dispose();

}
