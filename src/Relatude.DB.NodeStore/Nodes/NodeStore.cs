
using Relatude.DB.AI;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.Native.Models;
using Relatude.DB.Query;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Xml.Linq;


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

public class DbContext(NodeStore store) {
    public DbContext Culture(string? cultureCode) => change(store.QueryContext.Culture(cultureCode));
    public DbContext Hidden(bool includeHidden = true) => change(store.QueryContext.Hidden(includeHidden));
    public DbContext CultureFallbacks(bool includeFallbacks = true) => change(store.QueryContext.CultureFallbacks(includeFallbacks));
    public DbContext Admin() => change(store.QueryContext.Admin());
    DbContext change(QueryContext queryContext) => new DbContext(store.NewStoreWithDifferentContext(queryContext));
    public NodeStore Create() => store;
}
public class NodeStore : IDisposable {

    public readonly IDataStore Datastore;
    public DbContext Context => new DbContext(this);
    public QueryContext QueryContext => Datastore.QueryContext;
    public readonly NodeMapper Mapper;
    public AIEngine AI => Datastore.AI;
    internal List<INodeTransactionPlugin>? _transactionPlugins = null;
    internal List<INodeTransactionPlugin> TransactionPlugins {
        get {
            if (_transactionPlugins == null) _transactionPlugins = new();
            return _transactionPlugins;
        }
    }

    public void RegisterTransactionPlugin(INodeTransactionPlugin plugin) {
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        if (Datastore.State == DataStoreState.Open) {
            throw new InvalidOperationException("Cannot register transaction plugin after the store is opened. ");
        }
        TransactionPlugins.Add(plugin);
        plugin.Database = this;
    }
    public void RegisterRunner(ITaskRunner runner) => Datastore.RegisterRunner(runner);
    public void SetQueryContext(QueryContext qx) => Datastore.SetDefaultQueryContext(qx);

    internal NodeStore NewStoreWithDifferentContext(QueryContext ctx) {
        return new NodeStore(new DataStoreSession(ctx, Datastore), Mapper, TransactionPlugins);
    }

    private NodeStore(DataStoreSession datastore, NodeMapper mapper, List<INodeTransactionPlugin> plugins) {
        Datastore = datastore;
        Mapper = mapper;
        _transactionPlugins = plugins;
    }
    public NodeStore(IDataStore datastore) {
        Datastore = datastore;
        var sw = Stopwatch.StartNew();
        datastore.Datamodel.EnsureInitalization();
        if (_transactionPlugins != null) foreach (var plugin in _transactionPlugins) plugin.Database = this;
        var interfaceClasses = InterfaceGen.GetImplementations(datastore.Datamodel);
        var mappers = MapperGen.GenerateValueMappers(datastore.Datamodel);
        var code = interfaceClasses.Concat(mappers).ToList();
        var totalCode = string.Join("\n", code.Select(c => c.code));
        ulong codeHash = 0;
        foreach (var c in code) codeHash ^= c.code.XXH64Hash();
        var fileKey = datastore.FileKeys.MapperDll_GetFileKey(codeHash);
        foreach (var f in datastore.FileKeys.MapperDll_GetAllFileKeys(datastore.IOIndex)) {
            if (f != fileKey) datastore.IOIndex.DeleteFileIfItExists(f);
        }
        byte[] dll;
        if (datastore.IOIndex.DoesNotExistsOrIsEmpty(fileKey)) {
            Stopwatch sw2 = Stopwatch.StartNew();
            dll = Compiler.BuildDll(code, datastore.Datamodel);
            datastore.LogInfo("Recompiled mapper DLL in " + sw2.ElapsedMilliseconds.To1000N() + "ms.");
            datastore.IOIndex.WriteAllBytes(fileKey, dll);
        } else {
            dll = datastore.IOIndex.ReadAllBytes(fileKey);
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
        return node; // Get<T>(Mapper.GetIdGuidOrCreate(node!));
    }
    public T CreateAndInsert<T>(Action<T>? setProperties, Guid id) where T : notnull => CreateAndInsert<T>(setProperties, new NodeKey(id));
    public T CreateAndInsert<T>(Action<T>? setProperties, int id) where T : notnull => CreateAndInsert<T>(setProperties, new NodeKey(id));
    public T CreateAndInsert<T>(Action<T>? setProperties = null, NodeKey? id = null) where T : notnull {
        var node = Mapper.NewObjectFromType<T>(id);
        var t = CreateTransaction();
        t.Insert(node);
        if (setProperties != null) {
            setProperties.Invoke(node);
            t.Update(node);
        }
        t.Execute();
        return node; // Get<T>(Mapper.GetIdGuidOrCreate(node!));
    }

    public TransactionResult Insert(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).Insert(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public TransactionResult Insert(object node, out Guid id, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).Insert(node, out id, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public TransactionResult Insert(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).Insert(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public TransactionResult InsertOrFail(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).InsertOrFail(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public TransactionResult InsertOrFail(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).InsertOrFail(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public TransactionResult InsertIfNotExists(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).InsertIfNotExists(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public TransactionResult InsertIfNotExists(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).InsertIfNotExists(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);

    public Task<TransactionResult> InsertAsync(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).Insert(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertAsync(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).Insert(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertOrFailAsync(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).InsertOrFail(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertOrFailAsync(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).InsertOrFail(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertIfNotExistsAsync(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).InsertIfNotExists(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    public Task<TransactionResult> InsertIfNotExistsAsync(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).InsertIfNotExists(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);

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

    public TransactionResult Delete(object node, bool flushToDisk = false) => Execute(new Transaction(this).Delete(Mapper.GetIdGuid(node)), flushToDisk);
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

    public TransactionResult AddRelation<T>(T fromNode, Expression<Func<T, object>> expression, object toNode, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromNode, expression, toNode), flushToDisk);
    public TransactionResult AddRelation<T>(int fromId, Expression<Func<T, object>> expression, int toId, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, expression, toId), flushToDisk);
    public TransactionResult AddRelation<T>(Guid fromId, Expression<Func<T, object>> expression, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, expression!, toId), flushToDisk);
    public TransactionResult AddRelation<T>(Guid fromId, Expression<Func<T, object>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, expression, toIds), flushToDisk);
    public TransactionResult AddRelation(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, propertyId, toId), flushToDisk);
    public TransactionResult AddRelation(int fromId, Guid propertyId, int toId, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, propertyId, toId), flushToDisk);

    //public TransactionResult Relate<T>(OneOne<T> relation, T fromNode, T toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);
    //public TransactionResult Relate<T>(ManyMany<T> relation, T fromNode, T toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);
    //public TransactionResult Relate<TFrom, TTo>(OneToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);
    //public TransactionResult Relate<TFrom, TTo>(OneToOne<TFrom, TTo> relation, TFrom fromNode, TTo toNode, bool flushToDisk = false) => throw new NotImplementedException();
    //public TransactionResult Relate<TFrom, TTo>(ManyToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);

    public TransactionResult RemoveRelation<T>(T fromNode, Expression<Func<T, object>> expression, object toNode, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).RemoveRelation(fromNode, expression, toNode), flushToDisk);
    public TransactionResult RemoveRelation<T>(Guid fromId, Expression<Func<T, object>> expression, Guid toId, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).RemoveRelation(fromId, expression, toId), flushToDisk);
    public TransactionResult RemoveRelation<T>(Guid fromId, Expression<Func<T, object>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).RemoveRelation(fromId, expression, toIds), flushToDisk);
    public TransactionResult RemoveRelation(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).RemoveRelation(fromId, propertyId, toId), flushToDisk);
    public TransactionResult RemoveRelation(int fromId, Guid propertyId, int toId, bool flushToDisk = false) => Execute(new Transaction(this).RemoveRelation(fromId, propertyId, toId), flushToDisk);

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

    public TransactionResult EnableRevisions(Guid id, Guid? newRevisionId = null, bool flushToDisk = false) => Execute(new Transaction(this).EnableRevisions(id, newRevisionId), flushToDisk);
    public TransactionResult EnableRevisions(int id, Guid? newRevisionId = null, bool flushToDisk = false) => Execute(new Transaction(this).EnableRevisions(id, newRevisionId), flushToDisk);
    public TransactionResult EnableRevisions(Guid id, out Guid newRevisionId, bool flushToDisk = false) => Execute(new Transaction(this).EnableRevisions(id, out newRevisionId), flushToDisk);
    public TransactionResult EnableRevisions(int id, out Guid newRevisionId, bool flushToDisk = false) => Execute(new Transaction(this).EnableRevisions(id, out newRevisionId), flushToDisk);

    public TransactionResult DisableRevisions(Guid id, Guid? revisionIdToKeep = null, bool flushToDisk = false) => Execute(new Transaction(this).DisableRevisions(id, revisionIdToKeep), flushToDisk);
    public TransactionResult DisableRevisions(int id, Guid? revisionIdToKeep = null, bool flushToDisk = false) => Execute(new Transaction(this).DisableRevisions(id, revisionIdToKeep), flushToDisk);

    public TransactionResult UpdateMeta(Guid id, Guid revisionId, KeyValuePair<string, object>[] metaProperties, bool flushToDisk = false) => Execute(new Transaction(this).UpdateMeta(id, revisionId, metaProperties), flushToDisk);
    public TransactionResult UpdateMeta(int id, Guid revisionId, KeyValuePair<string, object>[] metaProperties, bool flushToDisk = false) => Execute(new Transaction(this).UpdateMeta(id, revisionId, metaProperties), flushToDisk);
    public TransactionResult UpdateMeta(Guid id, Guid revisionId, string propertyName, object value, bool flushToDisk = false) => UpdateMeta(id, revisionId, [new(propertyName, value)], flushToDisk);
    public TransactionResult UpdateMeta(int id, Guid revisionId, string propertyName, object value, bool flushToDisk = false) => UpdateMeta(id, revisionId, [new(propertyName, value)], flushToDisk);

    public TransactionResult UpdateMeta(Guid id, KeyValuePair<string, object>[] metaProperties, bool flushToDisk = false) => Execute(new Transaction(this).UpdateMeta(id, metaProperties), flushToDisk);
    public TransactionResult UpdateMeta(int id, KeyValuePair<string, object>[] metaProperties, bool flushToDisk = false) => Execute(new Transaction(this).UpdateMeta(id, metaProperties), flushToDisk);
    public TransactionResult UpdateMeta(Guid id, string propertyName, object value, bool flushToDisk = false) => UpdateMeta(id, [new(propertyName, value)], flushToDisk);
    public TransactionResult UpdateMeta(int id, string propertyName, object value, bool flushToDisk = false) => UpdateMeta(id, [new(propertyName, value)], flushToDisk);

    public TransactionResult DeleteRevision(Guid id, Guid revisionId, bool flushToDisk = false) => Execute(new Transaction(this).DeleteRevision(id, revisionId), flushToDisk);
    public TransactionResult DeleteRevision(int id, Guid revisionId, bool flushToDisk = false) => Execute(new Transaction(this).DeleteRevision(id, revisionId), flushToDisk);
    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId = null, Guid? cultureId = null, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, newRevisionId, cultureId), flushToDisk);
    public TransactionResult CreateRevision(int id, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId = null, Guid? cultureId = null, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, newRevisionId, cultureId), flushToDisk);
    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId, string? cultureCode, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, newRevisionId, cultureCode), flushToDisk);
    public TransactionResult CreateRevision(int id, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId, string? cultureCode, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, newRevisionId, cultureCode), flushToDisk);

    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, Guid? cultureId = null, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, cultureId), flushToDisk);
    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, Guid.Empty), default);
    public TransactionResult CreateRevision(int id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, Guid? cultureId = null, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, cultureId), flushToDisk);
    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, string? cultureCode, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, cultureCode), flushToDisk);
    public TransactionResult CreateRevision(int id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, string? cultureCode, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, cultureCode), flushToDisk);

    public NodeAndMeta<T>[] GetRevisions<T>(Guid id) {
        var revisions = Datastore.GetRevisions(id);
        return revisions.Select(r => new NodeAndMeta<T>(Mapper.CreateObjectFromNodeData<T>(r, null), r)).ToArray();
    }
    public TransactionResult ChangeRevisionType(Guid id, Guid revisionId, RevisionType newRevisionType, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionType(id, revisionId, newRevisionType), flushToDisk);
    public TransactionResult ChangeRevisionType(int id, Guid revisionId, RevisionType newRevisionType, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionType(id, revisionId, newRevisionType), flushToDisk);
    public TransactionResult ChangeRevisionCulture(Guid id, Guid revisionId, Guid newCultureId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionCulture(id, revisionId, newCultureId), flushToDisk);
    public TransactionResult ChangeRevisionCulture(int id, Guid revisionId, Guid newCultureId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionCulture(id, revisionId, newCultureId), flushToDisk);
    public TransactionResult ChangeRevisionCulture(Guid id, Guid revisionId, string newCultureCode, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionCulture(id, revisionId, newCultureCode), flushToDisk);
    public TransactionResult ChangeRevisionCulture(int id, Guid revisionId, string newCultureCode, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionCulture(id, revisionId, newCultureCode), flushToDisk);

    public void ChangeType(Guid id, Guid newTypeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType(id, newTypeId), flushToDisk);
    public void ChangeType(int id, Guid newTypeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType(id, newTypeId), flushToDisk);
    public void ChangeType<T>(object node, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType<T>(node), flushToDisk);
    public void ChangeType<T>(Guid nodeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType<T>(nodeId), flushToDisk);
    public void ChangeType<T>(int nodeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType<T>(nodeId), flushToDisk);

    public async Task<T> GetAsync<T>(Guid id) => Mapper.CreateObjectFromNodeData<T>(await Datastore.GetAsync(id), null);
    public Task<TransactionResult> ExecuteAsync(Transaction transaction, bool flushToDisk = false) => Datastore.ExecuteAsync(transaction._transactionData, flushToDisk);
    public IQueryOfNodes<object, object> Query(QueryContext? ctx = null) => new QueryOfNodes<object, object>(this, ctx);
    public IQueryOfNodes<object, object> QueryType(Guid nodeTypeId, QueryContext? ctx = null) => new QueryOfNodes<object, object>(this, ctx, Datastore.Datamodel.NodeTypes[nodeTypeId].CodeName);
    public IQueryOfNodes<object, object> QueryType(string typeName, QueryContext? ctx = null) => new QueryOfNodes<object, object>(this, ctx, typeName);

    public Task<object?> EvaluateForJsonAsync(string query, List<Parameter> parameters, QueryContext? ctx = null) {
        return new QueryStringBuilder(this, ctx, query, parameters).Prepare().EvaluateForJsonAsync();
    }
    public IQueryOfNodes<T, T> Query<T>(Guid id, QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx).Where("a => a." + Datastore.Datamodel.NodeTypes[Mapper.GetNodeTypeId(typeof(T))].NameOfPublicIdProperty + " == \"" + id + "\"");
    public IQueryOfNodes<T, T> Query<T>(int id, QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx).Where("a => a." + Datastore.Datamodel.NodeTypes[Mapper.GetNodeTypeId(typeof(T))].NameOfInternalIdProperty + " == " + id + "");
    public IQueryOfNodes<T, T> Query<T>(NodeKey id, QueryContext? ctx = null) => id.Int == 0 ? Query<T>(id.Guid) : Query<T>(id.Int);
    public IQueryOfNodes<T, T> Query<T>(QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx);
    public IQueryOfNodes<T, T> Query<T>(IEnumerable<Guid> ids, QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx).WhereInIds(ids);
    public IQueryOfNodes<T, T> Query<T>(Expression<Func<T, bool>> expression, QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx).Where(expression);
    public IQueryOfNodes<T, T> QueryRelated<T>(Guid propertyId, Guid nodeId, QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx).WhereRelates(propertyId, nodeId);

    public bool RelationExists<T>(Guid fromId, Expression<Func<T, object>> expression, Guid toId, QueryContext? ctx = null) => Query<T>(fromId, ctx).WhereRelates<T, object>(expression, toId).Count() > 0;
    public Task FlushAsync() => Datastore.MaintenanceAsync(MaintenanceAction.FlushDisk);
    public Task MaintenanceAsync(MaintenanceAction options) => Datastore.MaintenanceAsync(options);


    public long Count() => Query<object>().Count();
    public long Count<T>() => Query<T>().Count();

    public object Get(int id) => Mapper.CreateObjectFromNodeData(Datastore.Get(id), null);
    public object Get(Guid id) => Mapper.CreateObjectFromNodeData(Datastore.Get(id), null);
    public object Get(NodeKey id) => Mapper.CreateObjectFromNodeData(Datastore.Get(id), null);

    public T Get<T>(T node) where T : notnull => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(Mapper.GetIdGuid(node)), null);
    public T Get<T>(int id) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id), null);
    public T Get<T>(Guid id) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id), null);
    public T Get<T>(NodeKey id) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id), null);

    public T Get<T>(int id, string cultureCode) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureCode)), null);
    public T Get<T>(Guid id, string cultureCode) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureCode)), null);
    public T Get<T>(NodeKey id, string cultureCode) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureCode)), null);
    public T Get<T>(int id, Guid cultureId) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureId)), null);
    public T Get<T>(Guid id, Guid cultureId) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureId)), null);
    public T Get<T>(NodeKey id, Guid cultureId) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureId)), null);

    public Type GetNodeType(Guid nodeId) => Mapper.GetNodeType(Datastore.GetNodeType(nodeId));
    public bool TryGetNodeType(Guid nodeId, [MaybeNullWhen(false)] out Type type) {
        if (Datastore.TryGetNodeType(nodeId, out var typeId)
         && Mapper.TryGetNodeType(typeId, out var t)) {
            type = t;
            return true;
        }
        type = null;
        return false;
    }
    public bool Exists(Guid id) => Datastore.ExistsAndIsType(id, NodeConstants.BaseNodeTypeId);
    public bool Exists<T>(Guid id) => Datastore.ExistsAndIsType(id, Mapper.GetNodeTypeId(typeof(T)));

    public IEnumerable<T> Get<T>(IEnumerable<int> ids) => Datastore.Get(ids).Select(n => Mapper.CreateObjectFromNodeData<T>(n, null));
    public IEnumerable<T> Get<T>(IEnumerable<Guid> ids) => Datastore.Get(ids).Select(n => Mapper.CreateObjectFromNodeData<T>(n, null));

    public bool TryGet(Guid id, [MaybeNullWhen(false)] out object node) => TryGet<object>(id, out node);
    public bool TryGet<T>(Guid id, [MaybeNullWhen(false)] out T node) {
        if (Datastore.TryGet(id, out var nodeData)) {
            node = Mapper.CreateObjectFromNodeData<T>(nodeData, null);
            return true;
        }
        node = default;
        return false;
    }
    public bool TryGet<T>(int id, [MaybeNullWhen(false)] out T node) {
        if (Datastore.TryGet((int)id, out var nodeData)) {
            node = Mapper.CreateObjectFromNodeData<T>(nodeData, null);
            return true;
        }
        node = default;
        return false;
    }
    public T Get<T>(INodeDataExternal nodeData) => Mapper.CreateObjectFromNodeData<T>(nodeData, null);
    public IEnumerable<T> GetRelatedNodes<T>(Guid propertyId, Guid nodeId) { // used by mapper internally
        throw new NotImplementedException("GetRelated with propertyId is not implemented in NodeStore.");
    }
    public bool TryGetRelatedNode<T>(Guid propertyId, Guid nodeId, [MaybeNullWhen(false)] out T value) { // used by mapper internally
        throw new NotImplementedException("GetRelated with propertyId is not implemented in NodeStore.");
    }
    public IEnumerable<T> GetRelated<T>(NodeDataWithRelations[] node) { // used by mapper internally
        foreach (var item in node) yield return Mapper.CreateObjectFromNodeData<T>(item, null);
    }

    public bool TryGetValue<T>(PropertyPath path, [MaybeNullWhen(false)] out T value) => Datastore.TryGetValue(path, out value);
    public T GetValue<T>(PropertyPath path) => Datastore.GetValue<T>(path);
    public T GetValue<T>(string path) => Datastore.GetValue<T>(PropertyPath.Parse(path));

    public bool TryGetIdFromAddress(string address, [MaybeNullWhen(false)] out Guid nodeId) {
        return Datastore.TryGetNodeIdFromAddress(address, out nodeId);
    }
    public bool TryGetIdFromAddress(string address, [MaybeNullWhen(false)] out Guid nodeId, [MaybeNullWhen(false)] out string cultureCode) {
        return Datastore.TryGetNodeIdFromAddress(address, out nodeId, out cultureCode);
    }
    public bool TryGetIdFromAddress(string address, [MaybeNullWhen(false)] out int nodeId) {
        return Datastore.TryGetNodeIdFromAddress(address, out nodeId);
    }
    public bool TryGetIdFromAddress(string address, [MaybeNullWhen(false)] out int nodeId, [MaybeNullWhen(false)] out string cultureCode) {
        return Datastore.TryGetNodeIdFromAddress(address, out nodeId, out cultureCode);
    }
    public bool TryGetFromAddress<T>(string address, [MaybeNullWhen(false)] out T node) {
        if (Datastore.TryGetNodeDataFromAddress(address, out var nodeData)) {
            node = Mapper.CreateObjectFromNodeData<T>(nodeData, null);
            return true;
        }
        node = default;
        return false;
    }
    //public bool TryGetFromUrl<T>(string address, [MaybeNullWhen(false)] out T node) {
    //    return false;
    //}

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

    public void UpdateProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateProperty(nodeId, propertyId, value), flushToDisk);
    public void UpdateProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateProperty(nodeId, propertyId, value), flushToDisk);
    public void UpdateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void UpdateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void UpdateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void UpdateProperty<T, V>(IEnumerable<Guid> ids, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => Execute(new Transaction(this).UpdateProperty(ids, expression, value), flushToDisk);
    public void UpdateProperties<T>(Guid nodeId, params Tuple<Expression<Func<T, object>>, object>[] propertyValuePairs) where T : notnull => Execute(new Transaction(this).UpdateProperties(nodeId, propertyValuePairs));

    public void UpdateDisplayName(Guid nodeId, string newDisplayName, bool flushToDisk = false) => Execute(new Transaction(this).UpdateDisplayName(nodeId, newDisplayName), flushToDisk);
    public void UpdateDisplayName(int nodeId, string newDisplayName, bool flushToDisk = false) => Execute(new Transaction(this).UpdateDisplayName(nodeId, newDisplayName), flushToDisk);
    public void UpdateDisplayName(object node, string newDisplayName, bool flushToDisk = false) => Execute(new Transaction(this).UpdateDisplayName(node, newDisplayName), flushToDisk);
    public void UpdateAddress(Guid nodeId, string newAddress, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAddress(nodeId, newAddress), flushToDisk);
    public void UpdateAddress(int nodeId, string newAddress, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAddress(nodeId, newAddress), flushToDisk);
    public void UpdateAddress(NodeKey key, string newAddress, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAddress(key, newAddress), flushToDisk);
    public void UpdateAddress(object node, string newAddress, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAddress(node, newAddress), flushToDisk);

    public void UpdateAutoAddress(object node, bool value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAutoAddress(node, value), flushToDisk);
    public void UpdateAutoAddress(Guid nodeId, bool value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAutoAddress(nodeId, value), flushToDisk);
    public void UpdateAutoAddress(int nodeId, bool value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAutoAddress(nodeId, value), flushToDisk);

    public void UpdateAddress(Guid nodeId, string newAddress, out string? generatedAddress, out bool newAddressGenerated, bool flushToDisk = false) {
        UpdateAddress(nodeId, newAddress, flushToDisk);
        if (Datastore.TryGetAddress(nodeId, out var address)) {
            generatedAddress = address;
            newAddressGenerated = address == newAddress;
        } else {
            generatedAddress = null;
            newAddressGenerated = false;
        }
    }
    public void UpdateAddress(int nodeId, string newAddress, out string? generatedAddress, out bool newAddressGenerated, bool flushToDisk = false) {
        UpdateAddress(nodeId, newAddress, flushToDisk);
        if (Datastore.TryGetAddress(nodeId, out var address)) {
            generatedAddress = address;
            newAddressGenerated = address == newAddress;
        } else {
            generatedAddress = null;
            newAddressGenerated = false;
        }
    }
    public void UpdateAddress(NodeKey key, string newAddress, out string? generatedAddress, out bool newAddressGenerated, bool flushToDisk = false) {
        UpdateAddress(key, newAddress, flushToDisk);
        if (Datastore.TryGetAddress(key, out var address)) {
            generatedAddress = address;
            newAddressGenerated = address == newAddress;
        } else {
            generatedAddress = null;
            newAddressGenerated = false;
        }
    }
    public void UpdateAddress(object node, string wantedAddress, out bool didChange, out string? changedAddress, bool flushToDisk = false) {
        UpdateAddress(node, wantedAddress, flushToDisk);
        var key = Mapper.GetIdKey(node);
        if (Datastore.TryGetAddress(key, out var address)) {
            changedAddress = address;
            didChange = address != wantedAddress;
        } else {
            changedAddress = null;
            didChange = false;
        }
    }

    public void ForceUpdateProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).ForceUpdateProperty(nodeId, propertyId, value), flushToDisk);
    public void ForceUpdateProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).ForceUpdateProperty(nodeId, propertyId, value), flushToDisk);
    public void ForceUpdateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => ForceUpdateProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void ForceUpdateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => ForceUpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void ForceUpdateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => ForceUpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    public void ForceUpdateProperty<T, V>(IEnumerable<Guid> ids, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => Execute(new Transaction(this).ForceUpdateProperty(ids, expression, value), flushToDisk);
    public void ForceUpdateProperties<T>(Guid nodeId, params Tuple<Expression<Func<T, object>>, object>[] propertyValuePairs) where T : notnull => Execute(new Transaction(this).ForceUpdateProperties(nodeId, propertyValuePairs));

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


    // FILES:

    public Task FileDownloadAsync(Guid nodeId, Guid propertyId, Stream outStream) {
        return Datastore.FileDownloadAsync(new(nodeId, propertyId), outStream);
    }
    public async Task<byte[]> FileDownloadAsync(Guid nodeId, Guid propertyId) {
        using var ms = new MemoryStream();
        await FileDownloadAsync(nodeId, propertyId, ms);
        return ms.ToArray();
    }
    public Task FileDeleteAsync(Guid nodeId, Guid propertyId) => Datastore.FileDeleteAsync(new(nodeId, propertyId));
    public Task FileDeleteAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression) => FileDeleteAsync(nodeId, Mapper.GetProperty(expression).Id);

    public Task<FileValue> FileUploadAsync(Guid nodeId, Guid propertyId, IIOProvider source, string sourceFileKey, string? fileName = null) => Datastore.FileUploadAsync(new(nodeId, propertyId), source, sourceFileKey, fileName);
    public Task<FileValue> FileUploadAsync(Guid nodeId, Guid propertyId, Stream source, string fileName) => Datastore.FileUploadAsync(new(nodeId, propertyId), source, fileName);
    public async Task<FileValue> FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, string filePath, string? newFileName = null) {
        using var stream = File.OpenRead(filePath);
        newFileName = newFileName ?? Path.GetFileName(filePath);
        return await FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, stream, newFileName);
    }
    public async Task<FileValue> FileUploadAsync(FileValue file, string localFilePath, string? fileName = null) {
        if (file.PropertyPath == null) throw new Exception("File cannot be uploaded as node is not yet inserted to the database. ");
        using var stream = File.OpenRead(localFilePath);
        return await Datastore.FileUploadAsync(file.PropertyPath, stream, fileName ?? Path.GetFileName(localFilePath));
    }

    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, Stream source, string fileName) => FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, source, fileName);
    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, byte[] data, string fileName) => FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, new MemoryStream(data), fileName);
    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, IIOProvider source, string sourceFileKey, string? fileName = null) => FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, source, sourceFileKey, fileName);

    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, string filePath, string? fileName = null) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, filePath, fileName);
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, Stream source, string fileName) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, source, fileName);
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, byte[] data, string fileName) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, new MemoryStream(data), fileName);
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, IIOProvider source, string sourceFileKey, string? fileName = null) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, source, sourceFileKey, fileName);

    public Task FileDownloadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, Stream outStream) => FileDownloadAsync(nodeId, Mapper.GetProperty(expression).Id, outStream);
    public Task<byte[]> FileDownloadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression) => FileDownloadAsync(nodeId, Mapper.GetProperty(expression).Id);
    public Task FileDownloadAsync<T>(T node, Expression<Func<T, FileValue>> expression, Stream outStream) where T : notnull => FileDownloadAsync(Mapper.GetIdGuid(node), expression, outStream);
    public Task<byte[]> FileDownloadAsync<T>(T node, Expression<Func<T, FileValue>> expression) where T : notnull => FileDownloadAsync(Mapper.GetIdGuid(node), expression);
    public async Task<FileValue> FileDownloadAsync(FileValue file, string localFilePath, string? fileName = null) {
        if (file.PropertyPath == null) throw new Exception("File cannot be uploaded as node is not yet inserted to the database. ");
        using var stream = File.OpenRead(localFilePath);
        return await Datastore.FileDownloadAsync(file.PropertyPath, stream);
    }
    public Task<Stream> OpenFileDownloadStreamAsync(FileValue file) {
        if (file.PropertyPath == null) throw new Exception("File cannot be downloaded as node is not yet inserted to the database. ");
        var stream = new WriteToReadStream();
        _ = Datastore.FileDownloadAsync(file.PropertyPath, stream)
            .ContinueWith(t => stream.Complete(t.IsFaulted ? t.Exception : null));
        return Task.FromResult<Stream>(stream);
    }
    public Task FileDeleteAsync<T>(T node, Expression<Func<T, FileValue>> expression) where T : notnull => FileDeleteAsync(Mapper.GetIdGuid(node), expression);

    public Task<bool> FileUploadedAndAvailableAsync(Guid nodeId, Guid propertyId) => Datastore.IsFileUploadedAndAvailableAsync(new(nodeId, propertyId));
    public Task<bool> FileUploadedAndAvailableAsync<T>(T node, Expression<Func<T, FileValue>> expression) where T : notnull => FileUploadedAndAvailableAsync(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id);

    public Task<Guid> InitiateMultipartUploadAsync(PropertyPath propertyPath, string fileName, QueryContext? ctx = null) => Datastore.InitiateMultipartUploadAsync(propertyPath, fileName, ctx);
    public Task<Guid> InitiateMultipartUploadAsync(FileValue fileValue, string fileName, QueryContext? ctx = null) => Datastore.InitiateMultipartUploadAsync(fileValue.PropertyPath!, fileName, ctx);
    public Task AppendMultipartUploadAsync(Guid fileId, byte[] data, int length) => Datastore.AppendMultipartUploadAsync(fileId, data, length);
    public async Task<FileValue> FinalizeMultipartUploadAsync(Guid fileId, int? maxWaitForMetaUpdate = null, QueryContext? ctx = null) {
        var fv = await Datastore.FinalizeMultipartUploadAsync(fileId, maxWaitForMetaUpdate, ctx);
        if (_transactionPlugins != null) {
            if (TryGet(fv.PropertyPath!.NodePath.NodeKey.Guid, out var node)) {
                foreach (var plugin in _transactionPlugins) {
                    if (plugin.IsTypeRelevantForUploadAction(Mapper.GetNodeTypeId(node.GetType()))) {
                        plugin.OnAfterFileUpload(fv, node);
                    }
                }
            }
        }
        return fv;
    }
    public Task CancelMultipartUploadAsync(Guid fileId) => Datastore.CancelMultipartUpload(fileId);
    public bool FileStoreSupportsMultipartUploads(PropertyPath propertyPath) => Datastore.FileStoreSupportsMultipartUploads(propertyPath);
    public bool FileStoreSupportsMultipartUploads(FileValue fileValue) => Datastore.FileStoreSupportsMultipartUploads(fileValue.PropertyPath!);

    // URL AND FILE STREAMS:
    public string GetUrl(object node, bool absolute = false, QueryContext? ctx = null) => GetUrl(Mapper.GetIdKey(node), absolute, ctx);
    public string GetUrl(Guid nodeId, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(new NodePath(nodeId), absolute, ctx);
    public string GetUrl(int nodeId, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(new NodePath(nodeId), absolute, ctx);
    public string GetUrl(NodeKey key, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(new NodePath(key), absolute, ctx);
    public string GetUrl(NodePath node, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(node, absolute, ctx);
    public string GetUrl(FileValue fileValue, FileAdjustment adj, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(fileValue.PropertyPath!, adj, absolute, ctx);
    public string GetUrl(FileValue fileValue, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(fileValue.PropertyPath!, absolute, ctx);
    public string GetUrl(PropertyPath propertyPath, FileAdjustment adj, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(propertyPath, adj, absolute, ctx);
    public Task<Stream> GetFileStream(string url, int maxWait, QueryContext? ctx = null) => Datastore.GetFileStream(url, maxWait, ctx);
    public Task<StateAndStream> GetFileStreamAndState(string url, int maxWait = -1, QueryContext? ctx = null) => Datastore.GetFileStreamAndState(url, maxWait, ctx);
    public Task<Stream> GetFileStream(PropertyPath propertyPath, QueryContext? ctx = null) => Datastore.GetFileStream(propertyPath, ctx);
    public Task<Stream> GetFileStream(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null) => Datastore.GetFileStream(propertyPath, adj, maxWait, ctx);
    public Task<StateAndStream> GetFileStreamAndState(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null) => Datastore.GetFileStreamAndState(propertyPath, adj, maxWait, ctx);
    public bool TryGetConversionInfo(PropertyPath propertyPath, FileAdjustment adj, bool queueConversionIfNotRequested, [MaybeNullWhen(false)] out FileConversionProgressInfo progressInfo, QueryContext? ctx = null)
        => TryGetConversionInfo(propertyPath, adj, queueConversionIfNotRequested, out progressInfo, ctx);
    public bool IsFileReady(PropertyPath propertyPath, FileAdjustment adj, bool requestIfNot, QueryContext? ctx = null) => Datastore.IsFileReady(propertyPath, adj, requestIfNot, ctx);
    public void EnsureConversionRequested(PropertyPath propertyPath, FileAdjustment adj, QueryContext? ctx = null) => Datastore.EnsureConversionRequested(propertyPath, adj, ctx);
    public FileConversions GetRunningConversions(QueryContext? ctx = null) => Datastore.GetConversions(ctx);

    public Task EnqueueTaskAsync(TaskData task, string? jobId = null) {
        Datastore.EnqueueTask(task, jobId);
        return Task.CompletedTask;
    }
    public void EnqueueTask(TaskData task, string? jobId = null) => Datastore.EnqueueTask(task, jobId);

    public long Timestamp => Datastore.Timestamp;

    public virtual void Dispose() => Datastore.Dispose();

    public void EnsureCultures(SystemCulture[] cultures) {
        if (State != DataStoreState.Open) throw new Exception("DataStore must be open to ensure cultures.");
        var existing = Query<ISystemCulture>().Execute();
        var toCreate = cultures.Where(c => existing.All(ec => ec.Id != c.Id));
        var toUpdate = cultures.Where(c => existing.Any(ec => ec.Id == c.Id && ec.CultureCode != c.Code));
        foreach (var cult in toCreate) {
            CreateAndInsert<ISystemCulture>(newCult => {
                newCult.CultureCode = cult.Code;
                try {
                    var cultureInfo = new CultureInfo(cult.Code);
                    newCult.NativeName = cultureInfo.NativeName;
                    newCult.EnglishName = cultureInfo.EnglishName;
                } catch {
                    throw new Exception("Invalid culture code: " + cult.Code);
                }
            }, cult.Id);
        }
        foreach (var cult in toUpdate) {
            UpdateProperty<ISystemCulture, string>(cult.Id, c => c.CultureCode, cult.Code);
        }
    }
    public void EnsureCultures(string[] cultureCodes) {
        if (State != DataStoreState.Open) throw new Exception("DataStore must be open to ensure cultures.");
        var existing = Query<ISystemCulture>().Execute();
        var toCreate = cultureCodes.Except(existing.Select(c => c.CultureCode));
        foreach (var cultureCode in toCreate) {
            CreateAndInsert<ISystemCulture>(c => {
                c.CultureCode = cultureCode;
                try {
                    var cultureInfo = new CultureInfo(cultureCode);
                    c.NativeName = cultureInfo.NativeName;
                    c.EnglishName = cultureInfo.EnglishName;
                } catch {
                    throw new Exception("Invalid culture code: " + cultureCode);
                }
            });
        }
    }

}
