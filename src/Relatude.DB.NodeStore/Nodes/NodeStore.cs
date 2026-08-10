
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
using Relatude.DB.Web;
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

/// <summary>
/// Fluent builder for a <see cref="NodeStore"/> that sees the data through a different <see cref="QueryContext"/>.
/// Each method returns a new builder with one setting changed, and <see cref="Create"/> hands back the store.
/// No data is copied: the new store shares all indexes and caches with the original, only the reading context
/// (culture, visibility, user) differs. Start from <see cref="NodeStore.Context"/>:
/// <code>var norwegian = store.Context.Culture("nb-NO").Create();</code>
/// </summary>
public class DbContext(NodeStore store) {
    /// <summary>Reads text and other culture dependent values in the given culture. Null selects the store default.</summary>
    public DbContext Culture(string? cultureCode) => change(store.QueryContext.Culture(cultureCode));
    /// <summary>Includes nodes flagged as hidden. They are left out of query results by default.</summary>
    public DbContext Hidden(bool includeHidden = true) => change(store.QueryContext.Hidden(includeHidden));
    /// <summary>When a node has no content in the requested culture, falls back to another culture rather than skipping the node.</summary>
    public DbContext CultureFallbacks(bool includeFallbacks = true) => change(store.QueryContext.CultureFallbacks(includeFallbacks));
    /// <summary>Reads as the built in master admin user, so no permission or visibility filtering is applied.</summary>
    public DbContext Admin() => change(store.QueryContext.Admin());
    DbContext change(QueryContext queryContext) => new DbContext(store.NewStoreWithDifferentContext(queryContext));
    /// <summary>Returns the <see cref="NodeStore"/> configured by the preceding calls. Cheap: it is a thin wrapper around the original store.</summary>
    public NodeStore Create() => store;
}
/// <summary>
/// The main entry point to a Relatude database, and the object oriented layer on top of the raw
/// <see cref="IDataStore"/>. It maps your own C# classes and interfaces ("nodes") to and from the store:
/// use the <c>Query</c> and <c>Get</c> methods to read, and the mutation methods (Insert, Update, Delete,
/// AddRelation, ...) or a <see cref="Transaction"/> to write.
/// <para>
/// Every mutation method here builds a <see cref="Transaction"/> containing a single operation and commits it
/// immediately. To apply several changes atomically (all of them or none), call <see cref="CreateTransaction"/>,
/// queue the operations on it and finish with <see cref="Transaction.Execute(bool)"/>.
/// </para>
/// <para>
/// A node can be addressed in three ways, and most methods have one overload per flavour:
/// by its public <see cref="Guid"/> id (stable across databases and restarts, the normal choice),
/// by its internal <see cref="int"/> id (faster and smaller, but only meaningful inside one database),
/// or by the node object itself (the id is read from it, and generated if it does not have one yet).
/// </para>
/// <para>
/// The <c>flushToDisk</c> argument that most mutation methods accept controls durability: when true the write
/// ahead log is flushed to disk before the call returns, so the change survives a power loss or process kill.
/// When false (the default) the change is committed in memory and to the log buffer, and written to disk by the
/// store's normal flushing policy. Bulk loads are much faster with false.
/// </para>
/// <para>
/// A NodeStore is meant to be long lived and shared by the whole application, normally registered as a singleton.
/// Disposing it closes the underlying data store.
/// </para>
/// </summary>
public class NodeStore : IDisposable {

    /// <summary>The underlying low level store. Use it for advanced operations that this class does not expose.</summary>
    public readonly IDataStore Datastore;
    /// <summary>Entry point for getting a store that reads in another culture, as another user, or with hidden nodes included. See <see cref="DbContext"/>.</summary>
    public DbContext Context => new DbContext(this);
    /// <summary>The context (culture, user, visibility) that this store reads with unless a query overrides it.</summary>
    public QueryContext QueryContext => Datastore.QueryContext;
    /// <summary>Translates between node objects and the store's internal node data, and resolves property expressions to property ids.</summary>
    public readonly NodeMapper Mapper;
    /// <summary>The AI engine used for vector embeddings and semantic search, as configured for this database.</summary>
    public AIEngine AI => Datastore.AI;
    internal List<INodeTransactionPlugin>? _transactionPlugins = null;
    internal List<INodeTransactionPlugin> TransactionPlugins {
        get {
            if (_transactionPlugins == null) _transactionPlugins = new();
            return _transactionPlugins;
        }
    }

    /// <summary>
    /// Registers a plugin that is called before and after every transaction touching the node types it declares
    /// an interest in. Typical uses are validation, auditing and derived values. Must be called while the store
    /// is still closed, i.e. during start up, otherwise an exception is thrown.
    /// </summary>
    public void RegisterTransactionPlugin(INodeTransactionPlugin plugin) {
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        if (Datastore.State == DataStoreState.Open) {
            throw new InvalidOperationException("Cannot register transaction plugin after the store is opened. ");
        }
        TransactionPlugins.Add(plugin);
        plugin.Database = this;
    }
    /// <summary>Registers a runner that executes background jobs of a given kind, see <see cref="EnqueueTask"/>.</summary>
    public void RegisterRunner(ITaskRunner runner) => Datastore.RegisterRunner(runner);
    /// <summary>
    /// Replaces the default reading context of the whole store. This affects every query that does not pass its own
    /// context. To change the context for one piece of code only, use <see cref="Context"/> instead.
    /// </summary>
    public void SetQueryContext(QueryContext qx) => Datastore.SetDefaultQueryContext(qx);

    internal NodeStore NewStoreWithDifferentContext(QueryContext ctx) {
        return new NodeStore(new DataStoreSession(ctx, Datastore), Mapper, TransactionPlugins);
    }

    private NodeStore(DataStoreSession datastore, NodeMapper mapper, List<INodeTransactionPlugin> plugins) {
        Datastore = datastore;
        Mapper = mapper;
        _transactionPlugins = plugins;
    }
    /// <summary>
    /// Wraps a data store and builds the object mapping layer for it. On the first run the mapper implementations for
    /// your model interfaces are generated and compiled to a DLL; later runs load that DLL from the index folder, so
    /// construction is cheap unless the data model changed. Normally you do not call this yourself: the server
    /// setup (<c>AddRelatudeDB</c>) creates the store for you.
    /// </summary>
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
    /// <summary>Whether the database is Closed, Opening, Open, Closing, in Error or Disposed. Reads and writes require Open.</summary>
    public DataStoreState State => Datastore.State;

    /// <summary>
    /// Creates a new node instance in memory only. T may be an interface from your model: a class implementing it is
    /// generated at start up. Nothing is stored until you insert the object.
    /// </summary>
    public T Create<T>() => Mapper.NewObjectFromType<T>();
    /// <summary>
    /// Creates a node, inserts it, and returns it. The callback receives the new node together with the open
    /// transaction, so you can add relations or other operations that are committed together with the insert.
    /// </summary>
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
    /// <summary>Creates and inserts a node with a caller chosen public id. Fails if that id is already taken.</summary>
    public T CreateAndInsert<T>(Action<T>? setProperties, Guid id) where T : notnull => CreateAndInsert<T>(setProperties, new NodeKey(id));
    /// <summary>Creates and inserts a node with a caller chosen internal id. Fails if that id is already taken.</summary>
    public T CreateAndInsert<T>(Action<T>? setProperties, int id) where T : notnull => CreateAndInsert<T>(setProperties, new NodeKey(id));
    /// <summary>
    /// Creates a node, lets the callback fill in its properties, inserts it and returns it.
    /// Pass an id to control the key of the new node, otherwise one is generated.
    /// </summary>
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

    // ---------------------------------------------------------------------------------------------------------
    // INSERT
    // Insert is an alias for InsertOrFail, which throws if a node with the same id already exists.
    // InsertIfNotExists quietly skips nodes whose id is taken. Unless ignoreRelated is true, node objects
    // referenced by the inserted node are inserted too (if not already stored) and the relations are set.
    // cultureCode says which language variant the values belong to, revisionType which revision is written
    // (Published unless stated). All of them return a TransactionResult carrying the new state id of the store.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Inserts one node. Throws if its id already exists.</summary>
    public TransactionResult Insert(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).Insert(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Inserts one node and reports the public id it was given, which is useful when the node did not have one.</summary>
    public TransactionResult Insert(object node, out Guid id, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).Insert(node, out id, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Inserts many nodes in one atomic transaction. If any one of them fails, none of them are stored.</summary>
    public TransactionResult Insert(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).Insert(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Inserts one node, throwing if a node with the same id already exists. Same as <see cref="Insert(object, string?, RevisionType?, bool, bool)"/>.</summary>
    public TransactionResult InsertOrFail(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).InsertOrFail(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Inserts many nodes atomically, throwing if any of their ids already exists.</summary>
    public TransactionResult InsertOrFail(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).InsertOrFail(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Inserts one node, doing nothing if a node with the same id is already stored. Handy for idempotent seeding.</summary>
    public TransactionResult InsertIfNotExists(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).InsertIfNotExists(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Inserts the nodes that are not already stored, skipping the rest.</summary>
    public TransactionResult InsertIfNotExists(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this).InsertIfNotExists(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);

    /// <summary>
    /// Insert for data loads: identical to <see cref="Insert(IEnumerable{object}, string?, RevisionType?, bool, bool)"/>,
    /// but the nodes leave the node cache as soon as they are written to the log, so a large load does
    /// not leave the whole data set resident in memory.
    /// </summary>
    public TransactionResult BulkInsert(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => Execute(new Transaction(this) { BulkInsert = true }.Insert(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Awaitable version of <see cref="BulkInsert"/>, for loading large data sets without filling the node cache.</summary>
    public Task<TransactionResult> BulkInsertAsync(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this) { BulkInsert = true }.Insert(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);

    /// <summary>Awaitable insert of one node. Throws if its id already exists.</summary>
    public Task<TransactionResult> InsertAsync(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).Insert(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Awaitable insert of many nodes in one atomic transaction.</summary>
    public Task<TransactionResult> InsertAsync(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).Insert(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Awaitable insert of one node, throwing if its id already exists.</summary>
    public Task<TransactionResult> InsertOrFailAsync(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).InsertOrFail(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Awaitable insert of many nodes, throwing if any of their ids already exists.</summary>
    public Task<TransactionResult> InsertOrFailAsync(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).InsertOrFail(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Awaitable insert of one node, doing nothing if it is already stored.</summary>
    public Task<TransactionResult> InsertIfNotExistsAsync(object node, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).InsertIfNotExists(node, cultureCode, revisionType, ignoreRelated), flushToDisk);
    /// <summary>Awaitable insert of the nodes that are not already stored, skipping the rest.</summary>
    public Task<TransactionResult> InsertIfNotExistsAsync(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool flushToDisk = false, bool ignoreRelated = false)
        => ExecuteAsync(new Transaction(this).InsertIfNotExists(nodes, cultureCode, revisionType, ignoreRelated), flushToDisk);

    // ---------------------------------------------------------------------------------------------------------
    // UPDATE (whole node)
    // These write back all properties of the node object you hand in, identified by the id it carries.
    // Relations are not touched, use the relation methods for those. Three flavours:
    //   Update / UpdateOrFail - throws if the node no longer exists, and skips the write if nothing changed
    //   UpdateIfExists        - does nothing if the node no longer exists
    //   ForceUpdate           - writes without comparing first: cheaper when you know it changed, wasteful if not
    // To change a single property without reading and writing the whole node, use UpdateProperty instead.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Writes back all properties of the node. Throws if it no longer exists, does nothing if nothing changed.</summary>
    public TransactionResult Update(object node, bool flushToDisk = false) => Execute(new Transaction(this).Update(node), flushToDisk);
    /// <summary>Writes back several nodes in one atomic transaction. Throws if any of them no longer exists.</summary>
    public TransactionResult Update<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).Update(nodes), flushToDisk);
    /// <summary>Writes back the node, or does nothing if it has been deleted in the meantime.</summary>
    public TransactionResult UpdateIfExists(object node, bool flushToDisk = false) => Execute(new Transaction(this).UpdateIfExists(node), flushToDisk);
    /// <summary>Writes back the nodes that still exist and silently skips the rest.</summary>
    public TransactionResult UpdateIfExists<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).UpdateIfExists(nodes), flushToDisk);
    /// <summary>Writes back the node, throwing if it no longer exists. Same as <see cref="Update(object, bool)"/>.</summary>
    public TransactionResult UpdateOrFail(object node, bool flushToDisk = false) => Execute(new Transaction(this).UpdateOrFail(node), flushToDisk);
    /// <summary>Writes back the nodes, throwing if any of them no longer exists.</summary>
    public TransactionResult UpdateOrFail<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).UpdateOrFail(nodes), flushToDisk);
    /// <summary>Writes the node without checking whether anything actually changed. Use when you know it did.</summary>
    public TransactionResult ForceUpdate(object node, bool flushToDisk = false) => Execute(new Transaction(this).ForceUpdate(node), flushToDisk);
    /// <summary>Writes the nodes without comparing them to the stored versions first.</summary>
    public TransactionResult ForceUpdate<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ForceUpdate(nodes), flushToDisk);

    /// <summary>Awaitable <see cref="Update(object, bool)"/>.</summary>
    public Task<TransactionResult> UpdateAsync(object node, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Update(node), flushToDisk);
    /// <summary>Awaitable update of several nodes in one atomic transaction.</summary>
    public Task<TransactionResult> UpdateAsync<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => ExecuteAsync(new Transaction(this).Update(nodes), flushToDisk);
    /// <summary>Awaitable update that ignores a node that has been deleted in the meantime.</summary>
    public Task<TransactionResult> UpdateIfExistsAsync(object node, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).UpdateIfExists(node), flushToDisk);
    /// <summary>Awaitable update of the nodes that still exist, skipping the rest.</summary>
    public Task<TransactionResult> UpdateIfExistsAsync<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => ExecuteAsync(new Transaction(this).UpdateIfExists(nodes), flushToDisk);
    /// <summary>Awaitable update that throws if the node no longer exists.</summary>
    public Task<TransactionResult> UpdateOrFailAsync(object node, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).UpdateOrFail(node), flushToDisk);
    /// <summary>Awaitable update that throws if any of the nodes no longer exists.</summary>
    public Task<TransactionResult> UpdateOrFailAsync<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => ExecuteAsync(new Transaction(this).UpdateOrFail(nodes), flushToDisk);
    /// <summary>Awaitable update that skips the comparison with the stored node.</summary>
    public Task<TransactionResult> ForceUpdateAsync(object node, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).ForceUpdate(node), flushToDisk);
    /// <summary>Awaitable update of several nodes that skips the comparison with the stored nodes.</summary>
    public Task<TransactionResult> ForceUpdateAsync<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => ExecuteAsync(new Transaction(this).ForceUpdate(nodes), flushToDisk);


    /// <summary>
    /// Deletes every node of type T matching the predicate. The matching ids are queried first and then deleted,
    /// so this is two round trips and not isolated from concurrent writes.
    /// </summary>
    public void DeleteMany<T>(Expression<Func<T, bool>> expression, bool flushDisk = false) {
        var ids = Query<T>().Where(expression).SelectId().Execute();
        DeleteIfExists(ids, flushDisk);
    }
    /// <summary>
    /// Deletes every node of type T, by default including nodes of types inheriting from it.
    /// Pass includeDescendants false to delete only nodes of exactly this type.
    /// </summary>
    public void DeleteMany<T>(bool includeDescendants = true, bool flushDisk = false) {
        var ids = Query<T>().WhereTypes([typeof(T)], includeDescendants).SelectId().Execute();
        DeleteIfExists(ids, flushDisk);
    }

    // ---------------------------------------------------------------------------------------------------------
    // DELETE
    // Delete is an alias for DeleteOrFail, which throws if the node is not there. DeleteIfExists ignores it.
    // Deleting a node also removes every relation pointing to or from it, in the same transaction.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Deletes the node this object represents. Throws if it is already gone.</summary>
    public TransactionResult Delete(object node, bool flushToDisk = false) => Execute(new Transaction(this).Delete(Mapper.GetIdGuid(node)), flushToDisk);
    /// <summary>Deletes the node with this internal id. Throws if it does not exist.</summary>
    public TransactionResult Delete(int id, bool flushToDisk = false) => Execute(new Transaction(this).Delete(id), flushToDisk);
    /// <summary>Deletes all these nodes in one atomic transaction. Throws if any of them does not exist.</summary>
    public TransactionResult Delete(IEnumerable<int> ids, bool flushToDisk = false) => Execute(new Transaction(this).Delete(ids), flushToDisk);
    /// <summary>Deletes the node with this internal id, throwing if it does not exist.</summary>
    public TransactionResult DeleteOrFail(int id, bool flushToDisk = false) => Execute(new Transaction(this).DeleteOrFail(id), flushToDisk);
    /// <summary>Deletes these nodes, throwing if any of them does not exist.</summary>
    public TransactionResult DeleteOrFail(IEnumerable<int> ids, bool flushToDisk = false) => Execute(new Transaction(this).DeleteOrFail(ids), flushToDisk);
    /// <summary>Deletes the node if it is there, otherwise does nothing.</summary>
    public TransactionResult DeleteIfExists(int id, bool flushToDisk = false) => Execute(new Transaction(this).DeleteIfExists(id), flushToDisk);
    /// <summary>Deletes the nodes that exist and ignores the ids that do not.</summary>
    public TransactionResult DeleteIfExists(IEnumerable<int> ids, bool flushToDisk = false) => Execute(new Transaction(this).DeleteIfExists(ids), flushToDisk);

    /// <summary>Deletes the node with this public id. Throws if it does not exist.</summary>
    public TransactionResult Delete(Guid id, bool flushToDisk = false) => Execute(new Transaction(this).Delete(id), flushToDisk);
    /// <summary>Deletes all these nodes in one atomic transaction. Throws if any of them does not exist.</summary>
    public TransactionResult Delete(IEnumerable<Guid> ids, bool flushToDisk = false) => Execute(new Transaction(this).Delete(ids), flushToDisk);
    /// <summary>Deletes the node with this public id, throwing if it does not exist.</summary>
    public TransactionResult DeleteOrFail(Guid id, bool flushToDisk = false) => Execute(new Transaction(this).DeleteOrFail(id), flushToDisk);
    /// <summary>Deletes these nodes, throwing if any of them does not exist.</summary>
    public TransactionResult DeleteOrFail(IEnumerable<Guid> ids, bool flushToDisk = false) => Execute(new Transaction(this).DeleteOrFail(ids), flushToDisk);
    /// <summary>Deletes the node if it is there, otherwise does nothing.</summary>
    public TransactionResult DeleteIfExists(Guid id, bool flushToDisk = false) => Execute(new Transaction(this).DeleteIfExists(id), flushToDisk);
    /// <summary>Deletes the nodes that exist and ignores the ids that do not.</summary>
    public TransactionResult DeleteIfExists(IEnumerable<Guid> ids, bool flushToDisk = false) => Execute(new Transaction(this).DeleteIfExists(ids), flushToDisk);

    /// <summary>Awaitable delete by public id. Throws if the node does not exist.</summary>
    public Task<TransactionResult> DeleteAsync(Guid id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Delete(id), flushToDisk);
    /// <summary>Awaitable delete of several nodes by public id, in one atomic transaction.</summary>
    public Task<TransactionResult> DeleteAsync(IEnumerable<Guid> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Delete(ids), flushToDisk);
    /// <summary>Awaitable delete by public id that throws if the node does not exist.</summary>
    public Task<TransactionResult> DeleteOrFailAsync(Guid id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteOrFail(id), flushToDisk);
    /// <summary>Awaitable delete that throws if any of the nodes does not exist.</summary>
    public Task<TransactionResult> DeleteOrFailAsync(IEnumerable<Guid> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteOrFail(ids), flushToDisk);
    /// <summary>Awaitable delete that does nothing if the node is already gone.</summary>
    public Task<TransactionResult> DeleteIfExistsAsync(Guid id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteIfExists(id), flushToDisk);
    /// <summary>Awaitable delete of the nodes that exist, ignoring the ids that do not.</summary>
    public Task<TransactionResult> DeleteIfExistsAsync(IEnumerable<Guid> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteIfExists(ids), flushToDisk);

    /// <summary>Awaitable delete by internal id. Throws if the node does not exist.</summary>
    public Task<TransactionResult> DeleteAsync(int id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Delete(id), flushToDisk);
    /// <summary>Awaitable delete of several nodes by internal id, in one atomic transaction.</summary>
    public Task<TransactionResult> DeleteAsync(IEnumerable<int> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).Delete(ids), flushToDisk);
    /// <summary>Awaitable delete by internal id that throws if the node does not exist.</summary>
    public Task<TransactionResult> DeleteOrFailAsync(int id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteOrFail(id), flushToDisk);
    /// <summary>Awaitable delete that throws if any of the nodes does not exist.</summary>
    public Task<TransactionResult> DeleteOrFailAsync(IEnumerable<int> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteOrFail(ids), flushToDisk);
    /// <summary>Awaitable delete that does nothing if the node is already gone.</summary>
    public Task<TransactionResult> DeleteIfExistsAsync(int id, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteIfExists(id), flushToDisk);
    /// <summary>Awaitable delete of the nodes that exist, ignoring the ids that do not.</summary>
    public Task<TransactionResult> DeleteIfExistsAsync(IEnumerable<int> ids, bool flushToDisk = false) => ExecuteAsync(new Transaction(this).DeleteIfExists(ids), flushToDisk);

    // ---------------------------------------------------------------------------------------------------------
    // UPSERT: insert the node if its id is unknown, otherwise update it. Upsert compares with the stored node
    // and skips the write when nothing changed, ForceUpsert always writes. Related node objects are ignored.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Inserts or overwrites the node without comparing it to the stored version first.</summary>
    public TransactionResult ForceUpsert(object node, bool flushToDisk = false) => Execute(new Transaction(this).ForceUpsert(node), flushToDisk);
    /// <summary>Inserts or overwrites several nodes without comparing them to the stored versions first.</summary>
    public TransactionResult ForceUpsert<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ForceUpsert(nodes), flushToDisk);
    /// <summary>Inserts the node, or updates it if its id is already known. Unchanged nodes are not rewritten.</summary>
    public TransactionResult Upsert(object node, bool flushToDisk = false) => Execute(new Transaction(this).Upsert(node), flushToDisk);
    /// <summary>Inserts or updates several nodes in one atomic transaction.</summary>
    public TransactionResult Upsert<T>(IEnumerable<T> nodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).Upsert(nodes), flushToDisk);

    // ---------------------------------------------------------------------------------------------------------
    // RELATIONS
    // A relation is identified by one of its two ends: the relation property on your model, given either as a
    // lambda (n => n.Author) or as the raw property id. Which end is "from" follows from the property you name,
    // the direction is handled for you.
    //   AddRelation    - adds a link, throws if it is already there
    //   SetRelation    - makes sure the link exists, removing whatever the cardinality does not allow beside it
    //                    (for a one to many that means the old parent link is dropped). Does nothing if already set
    //   RemoveRelation - removes one link, throws if it is not there
    //   ClearRelation  - removes one link if it is there, no error if it is not
    //   ClearRelations - removes every link from that node through that property
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Links two node objects through the given relation property. Throws if the link already exists.</summary>
    public TransactionResult AddRelation<T>(T fromNode, Expression<Func<T, object?>> expression, object toNode, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromNode, expression, toNode), flushToDisk);
    /// <summary>Links two nodes given by internal id. Throws if the link already exists.</summary>
    public TransactionResult AddRelation<T>(int fromId, Expression<Func<T, object?>> expression, int toId, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, expression, toId), flushToDisk);
    /// <summary>Links two nodes given by public id. Throws if the link already exists.</summary>
    public TransactionResult AddRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, expression!, toId), flushToDisk);
    /// <summary>Links one node to several others through the same property, in one transaction.</summary>
    public TransactionResult AddRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, expression, toIds), flushToDisk);
    /// <summary>Links two nodes using the raw relation property id, for code that works without the model types.</summary>
    public TransactionResult AddRelation(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, propertyId, toId), flushToDisk);
    /// <summary>Links two nodes by internal id using the raw relation property id.</summary>
    public TransactionResult AddRelation(int fromId, Guid propertyId, int toId, bool flushToDisk = false) => Execute(new Transaction(this).AddRelation(fromId, propertyId, toId), flushToDisk);

    //public TransactionResult Relate<T>(OneOne<T> relation, T fromNode, T toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);
    //public TransactionResult Relate<T>(ManyMany<T> relation, T fromNode, T toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);
    //public TransactionResult Relate<TFrom, TTo>(OneToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);
    //public TransactionResult Relate<TFrom, TTo>(OneToOne<TFrom, TTo> relation, TFrom fromNode, TTo toNode, bool flushToDisk = false) => throw new NotImplementedException();
    //public TransactionResult Relate<TFrom, TTo>(ManyToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode, bool flushToDisk = false) => Execute(new Transaction(this).Relate(relation, fromNode, toNode), flushToDisk);

    /// <summary>Removes the link between two node objects. Throws if there is no such link.</summary>
    public TransactionResult RemoveRelation<T>(T fromNode, Expression<Func<T, object?>> expression, object toNode, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).RemoveRelation(fromNode, expression, toNode), flushToDisk);
    /// <summary>Removes the link between two nodes given by public id. Throws if there is no such link.</summary>
    public TransactionResult RemoveRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, Guid toId, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).RemoveRelation(fromId, expression, toId), flushToDisk);
    /// <summary>Removes the links from one node to several others, in one transaction. Throws if any of them is missing.</summary>
    public TransactionResult RemoveRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).RemoveRelation(fromId, expression, toIds), flushToDisk);
    /// <summary>Removes a link using the raw relation property id. Throws if there is no such link.</summary>
    public TransactionResult RemoveRelation(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).RemoveRelation(fromId, propertyId, toId), flushToDisk);
    /// <summary>Removes a link between nodes given by internal id, using the raw relation property id.</summary>
    public TransactionResult RemoveRelation(int fromId, Guid propertyId, int toId, bool flushToDisk = false) => Execute(new Transaction(this).RemoveRelation(fromId, propertyId, toId), flushToDisk);

    /// <summary>Makes sure the two node objects are linked, replacing any link the cardinality does not allow beside it.</summary>
    public TransactionResult SetRelation<T>(T fromNode, Expression<Func<T, object?>> expression, object toNode, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).SetRelation(fromNode, expression, toNode), flushToDisk);
    /// <summary>Sets the link between two nodes given by public id. Passing Guid.Empty as the target clears the relation instead.</summary>
    public TransactionResult SetRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, expression, toId), flushToDisk);
    /// <summary>Sets the link between two nodes given by internal id. Passing 0 as the target clears the relation instead.</summary>
    public TransactionResult SetRelation<T>(int fromId, Expression<Func<T, object?>> expression, int toId, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, expression, toId), flushToDisk);
    /// <summary>Sets a link using the raw relation property id.</summary>
    public TransactionResult SetRelation(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, propertyId, toId), flushToDisk);
    /// <summary>Adds links from one node to each of the given node objects. Existing links to others are left alone, see <see cref="ClearAndSetRelation{T}(T, Expression{Func{T, object?}}, IEnumerable{object}, bool)"/> to replace the whole list.</summary>
    public TransactionResult SetRelation<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> toNodes, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).SetRelation(fromNode, expression, toNodes), flushToDisk);
    /// <summary>Adds links from one node to each of the given public ids, leaving links to other nodes alone.</summary>
    public TransactionResult SetRelation<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).SetRelation(fromNode, expression, toIds), flushToDisk);
    /// <summary>Adds links from one node to each of the given internal ids. Source and targets must use the same kind of id.</summary>
    public TransactionResult SetRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<int> toIds, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, expression, toIds), flushToDisk);
    /// <summary>Adds links from one node to each of the given internal ids.</summary>
    public TransactionResult SetRelation<T>(int fromId, Expression<Func<T, object?>> expression, IEnumerable<int> toIds, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, expression, toIds), flushToDisk);
    /// <summary>Adds links to several targets using the raw relation property id.</summary>
    public TransactionResult SetRelation(Guid fromId, Guid propertyId, IEnumerable<Guid> toIds, bool flushToDisk = false) => Execute(new Transaction(this).SetRelation(fromId, propertyId, toIds), flushToDisk);

    // ClearAndSetRelation replaces the whole list in one transaction: everything currently related through the
    // property is unlinked first, then the given nodes are linked. This is the natural fit for "save this picker".

    /// <summary>Replaces all nodes related through the property with exactly the given node objects.</summary>
    public TransactionResult ClearAndSetRelation<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> toNodes, bool flushToDisk = false) where T : notnull
        => Execute(new Transaction(this).ClearRelations(fromNode, expression).SetRelation(fromNode, expression, toNodes), flushToDisk);
    /// <summary>Replaces all nodes related through the property with exactly the given public ids.</summary>
    public TransactionResult ClearAndSetRelation<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<Guid> toIds, bool flushToDisk = false) where T : notnull
        => Execute(new Transaction(this).ClearRelations(fromNode, expression).SetRelation(fromNode, expression, toIds), flushToDisk);
    /// <summary>Replaces all nodes related to the given source through the property with exactly the given internal ids.</summary>
    public TransactionResult ClearAndSetRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<int> toIds, bool flushToDisk = false) where T : notnull
        => Execute(new Transaction(this).ClearRelations(fromId, expression).SetRelation(fromId, expression, toIds), flushToDisk);
    /// <summary>Replaces all nodes related to the given source through the property with exactly the given internal ids.</summary>
    public TransactionResult ClearAndSetRelation<T>(int fromId, Expression<Func<T, object?>> expression, IEnumerable<int> toIds, bool flushToDisk = false) where T : notnull
        => Execute(new Transaction(this).ClearRelations(fromId, expression).SetRelation(fromId, expression, toIds), flushToDisk);
    /// <summary>Replaces the related nodes using the raw relation property id.</summary>
    public TransactionResult ClearAndSetRelation(Guid fromId, Guid propertyId, IEnumerable<Guid> toIds, bool flushToDisk = false)
        => Execute(new Transaction(this).ClearRelations(fromId, propertyId).SetRelation(fromId, propertyId, toIds), flushToDisk);

    /// <summary>Removes the link between the two node objects if it exists. Unlike RemoveRelation it does not complain if it does not.</summary>
    public TransactionResult ClearRelation<T>(T fromNode, Expression<Func<T, object?>> expression, object toNode, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelation(fromNode, expression, toNode), flushToDisk);
    /// <summary>Removes the link between two nodes given by public id, if it exists.</summary>
    public TransactionResult ClearRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, Guid toId, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelation(fromId, expression, toId), flushToDisk);
    /// <summary>Removes the link between a node given by internal id and a node given by public id, if it exists.</summary>
    public TransactionResult ClearRelation<T>(int fromId, Expression<Func<T, object?>> expression, Guid toId, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelation(fromId, expression, toId), flushToDisk);
    /// <summary>Removes a link if it exists, using the raw relation property id.</summary>
    public TransactionResult ClearRelation(Guid fromId, Guid propertyId, Guid toId, bool flushToDisk = false) => Execute(new Transaction(this).ClearRelation(fromId, propertyId, toId), flushToDisk);
    /// <summary>Removes a link between nodes given by internal id if it exists, using the raw relation property id.</summary>
    public TransactionResult ClearRelation(int fromId, Guid propertyId, int toId, bool flushToDisk = false) => Execute(new Transaction(this).ClearRelation(fromId, propertyId, toId), flushToDisk);
    /// <summary>Unlinks everything this node relates to through the given property.</summary>
    public TransactionResult ClearRelations<T>(T fromNode, Expression<Func<T, object?>> expression, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelations(fromNode, expression), flushToDisk);
    /// <summary>Unlinks everything the node with this internal id relates to through the given property.</summary>
    public TransactionResult ClearRelations<T>(int fromId, Expression<Func<T, object?>> expression, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelations(fromId, expression), flushToDisk);
    /// <summary>Unlinks everything the node with this public id relates to through the given property.</summary>
    public TransactionResult ClearRelations<T>(Guid fromId, Expression<Func<T, object?>> expression, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).ClearRelations(fromId, expression), flushToDisk);
    /// <summary>Unlinks everything the node relates to through the given property, addressed by raw property id.</summary>
    public TransactionResult ClearRelations(Guid fromId, Guid propertyId, bool flushToDisk = false) => Execute(new Transaction(this).ClearRelations(fromId, propertyId), flushToDisk);

    // ---------------------------------------------------------------------------------------------------------
    // RELATION ORDERING
    // The nodes related to a node through one property form an ordered list, and that is the order queries return
    // them in. These methods change that order without adding or removing anything. They behave like a list UI:
    // a multi item move keeps the internal order of the selection and compacts it, positions are clamped so moving
    // past either end never throws, and every item must already be related or the transaction fails.
    // Offsets are negative towards the top of the list and positive towards the bottom.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Moves one related node up or down the list by offset places.</summary>
    public TransactionResult MoveRelation<T>(T fromNode, Expression<Func<T, object?>> expression, object item, int offset, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelation(fromNode, expression, item, offset), flushToDisk);
    /// <summary>Moves a selection of related nodes up or down the list by offset places, keeping their internal order.</summary>
    public TransactionResult MoveRelation<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items, int offset, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelation(fromNode, expression, items, offset), flushToDisk);
    /// <summary>Moves one related node by offset places, both nodes given by public id.</summary>
    public TransactionResult MoveRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, Guid item, int offset, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelation(fromId, expression, item, offset), flushToDisk);
    /// <summary>Moves several related nodes by offset places, all given by public id.</summary>
    public TransactionResult MoveRelation<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<Guid> items, int offset, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelation(fromId, expression, items, offset), flushToDisk);
    /// <summary>Moves one related node by offset places, using the raw relation property id.</summary>
    public TransactionResult MoveRelation(Guid fromId, Guid propertyId, Guid item, int offset, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelation(fromId, propertyId, item, offset), flushToDisk);
    /// <summary>Moves several related nodes by offset places, using the raw relation property id.</summary>
    public TransactionResult MoveRelation(Guid fromId, Guid propertyId, IEnumerable<Guid> items, int offset, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelation(fromId, propertyId, items, offset), flushToDisk);
    /// <summary>Moves one related node to the top of the list.</summary>
    public TransactionResult MoveRelationToTop<T>(T fromNode, Expression<Func<T, object?>> expression, object item, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelationToTop(fromNode, expression, item), flushToDisk);
    /// <summary>Moves a selection of related nodes to the top of the list, keeping their internal order.</summary>
    public TransactionResult MoveRelationToTop<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelationToTop(fromNode, expression, items), flushToDisk);
    /// <summary>Moves one related node to the top of the list, both nodes given by public id.</summary>
    public TransactionResult MoveRelationToTop<T>(Guid fromId, Expression<Func<T, object?>> expression, Guid item, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationToTop(fromId, expression, item), flushToDisk);
    /// <summary>Moves several related nodes to the top of the list, all given by public id.</summary>
    public TransactionResult MoveRelationToTop<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<Guid> items, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationToTop(fromId, expression, items), flushToDisk);
    /// <summary>Moves one related node to the top of the list, using the raw relation property id.</summary>
    public TransactionResult MoveRelationToTop(Guid fromId, Guid propertyId, Guid item, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationToTop(fromId, propertyId, item), flushToDisk);
    /// <summary>Moves several related nodes to the top of the list, using the raw relation property id.</summary>
    public TransactionResult MoveRelationToTop(Guid fromId, Guid propertyId, IEnumerable<Guid> items, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationToTop(fromId, propertyId, items), flushToDisk);
    /// <summary>Moves one related node to the bottom of the list.</summary>
    public TransactionResult MoveRelationToBottom<T>(T fromNode, Expression<Func<T, object?>> expression, object item, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelationToBottom(fromNode, expression, item), flushToDisk);
    /// <summary>Moves a selection of related nodes to the bottom of the list, keeping their internal order.</summary>
    public TransactionResult MoveRelationToBottom<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelationToBottom(fromNode, expression, items), flushToDisk);
    /// <summary>Moves one related node to the bottom of the list, both nodes given by public id.</summary>
    public TransactionResult MoveRelationToBottom<T>(Guid fromId, Expression<Func<T, object?>> expression, Guid item, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationToBottom(fromId, expression, item), flushToDisk);
    /// <summary>Moves several related nodes to the bottom of the list, all given by public id.</summary>
    public TransactionResult MoveRelationToBottom<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<Guid> items, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationToBottom(fromId, expression, items), flushToDisk);
    /// <summary>Moves one related node to the bottom of the list, using the raw relation property id.</summary>
    public TransactionResult MoveRelationToBottom(Guid fromId, Guid propertyId, Guid item, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationToBottom(fromId, propertyId, item), flushToDisk);
    /// <summary>Moves several related nodes to the bottom of the list, using the raw relation property id.</summary>
    public TransactionResult MoveRelationToBottom(Guid fromId, Guid propertyId, IEnumerable<Guid> items, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationToBottom(fromId, propertyId, items), flushToDisk);
    /// <summary>Moves one related node to the position just before the anchor node in the list.</summary>
    public TransactionResult MoveRelationBefore<T>(T fromNode, Expression<Func<T, object?>> expression, object item, object anchor, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelationBefore(fromNode, expression, item, anchor), flushToDisk);
    /// <summary>Moves a selection of related nodes into one block just before the anchor node.</summary>
    public TransactionResult MoveRelationBefore<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items, object anchor, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelationBefore(fromNode, expression, items, anchor), flushToDisk);
    /// <summary>Moves one related node just before the anchor, all nodes given by public id.</summary>
    public TransactionResult MoveRelationBefore<T>(Guid fromId, Expression<Func<T, object?>> expression, Guid item, Guid anchor, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationBefore(fromId, expression, item, anchor), flushToDisk);
    /// <summary>Moves several related nodes into one block just before the anchor, all given by public id.</summary>
    public TransactionResult MoveRelationBefore<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<Guid> items, Guid anchor, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationBefore(fromId, expression, items, anchor), flushToDisk);
    /// <summary>Moves one related node just before the anchor, using the raw relation property id.</summary>
    public TransactionResult MoveRelationBefore(Guid fromId, Guid propertyId, Guid item, Guid anchor, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationBefore(fromId, propertyId, item, anchor), flushToDisk);
    /// <summary>Moves several related nodes just before the anchor, using the raw relation property id.</summary>
    public TransactionResult MoveRelationBefore(Guid fromId, Guid propertyId, IEnumerable<Guid> items, Guid anchor, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationBefore(fromId, propertyId, items, anchor), flushToDisk);
    /// <summary>Moves one related node to the position just after the anchor node in the list.</summary>
    public TransactionResult MoveRelationAfter<T>(T fromNode, Expression<Func<T, object?>> expression, object item, object anchor, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelationAfter(fromNode, expression, item, anchor), flushToDisk);
    /// <summary>Moves a selection of related nodes into one block just after the anchor node.</summary>
    public TransactionResult MoveRelationAfter<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items, object anchor, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).MoveRelationAfter(fromNode, expression, items, anchor), flushToDisk);
    /// <summary>Moves one related node just after the anchor, all nodes given by public id.</summary>
    public TransactionResult MoveRelationAfter<T>(Guid fromId, Expression<Func<T, object?>> expression, Guid item, Guid anchor, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationAfter(fromId, expression, item, anchor), flushToDisk);
    /// <summary>Moves several related nodes into one block just after the anchor, all given by public id.</summary>
    public TransactionResult MoveRelationAfter<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<Guid> items, Guid anchor, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationAfter(fromId, expression, items, anchor), flushToDisk);
    /// <summary>Moves one related node just after the anchor, using the raw relation property id.</summary>
    public TransactionResult MoveRelationAfter(Guid fromId, Guid propertyId, Guid item, Guid anchor, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationAfter(fromId, propertyId, item, anchor), flushToDisk);
    /// <summary>Moves several related nodes just after the anchor, using the raw relation property id.</summary>
    public TransactionResult MoveRelationAfter(Guid fromId, Guid propertyId, IEnumerable<Guid> items, Guid anchor, bool flushToDisk = false) => Execute(new Transaction(this).MoveRelationAfter(fromId, propertyId, items, anchor), flushToDisk);
    /// <summary>Rewrites the whole order of the list. The nodes given must be exactly the ones currently related, in the wanted order.</summary>
    public TransactionResult SetRelationOrder<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> itemsInOrder, bool flushToDisk = false) where T : notnull => Execute(new Transaction(this).SetRelationOrder(fromNode, expression, itemsInOrder), flushToDisk);
    /// <summary>Rewrites the whole order of the list, given by public id. Must list exactly the currently related nodes.</summary>
    public TransactionResult SetRelationOrder<T>(Guid fromId, Expression<Func<T, object?>> expression, IEnumerable<Guid> itemsInOrder, bool flushToDisk = false) => Execute(new Transaction(this).SetRelationOrder(fromId, expression, itemsInOrder), flushToDisk);
    /// <summary>Rewrites the whole order of the list, using the raw relation property id.</summary>
    public TransactionResult SetRelationOrder(Guid fromId, Guid propertyId, IEnumerable<Guid> itemsInOrder, bool flushToDisk = false) => Execute(new Transaction(this).SetRelationOrder(fromId, propertyId, itemsInOrder), flushToDisk);

    /// <summary>Rebuilds the search, vector and value indexes for one node. Useful after a model or analyzer change.</summary>
    public TransactionResult ReIndex(Guid id, bool flushToDisk = false) => Execute(new Transaction(this).ReIndex(id), flushToDisk);
    /// <summary>Rebuilds the indexes for the node with this internal id.</summary>
    public TransactionResult ReIndex(int id, bool flushToDisk = false) => Execute(new Transaction(this).ReIndex(id), flushToDisk);

    // ---------------------------------------------------------------------------------------------------------
    // REVISIONS
    // A node normally holds one version of its content. Enabling revisions turns it into a set of versions, each
    // with a revision id, a RevisionType (Published, Preliminary, Archived, Binned, awaiting approval, ...) and a
    // culture. Only the Published revision of a culture is indexed and returned by normal queries: the others are
    // reached through a QueryContext that asks for them. This is what drives editorial workflows such as drafts,
    // preview, approval and archives. Node meta data (created, changed, author, and your own key/value pairs)
    // lives per revision, which is what the UpdateMeta methods write.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Converts a plain node into a revision aware node, its current content becoming the first revision.</summary>
    public TransactionResult EnableRevisions(Guid id, Guid? newRevisionId = null, bool flushToDisk = false) => Execute(new Transaction(this).EnableRevisions(id, newRevisionId), flushToDisk);
    /// <summary>Enables revisions for the node with this internal id.</summary>
    public TransactionResult EnableRevisions(int id, Guid? newRevisionId = null, bool flushToDisk = false) => Execute(new Transaction(this).EnableRevisions(id, newRevisionId), flushToDisk);
    /// <summary>Enables revisions and reports the id given to the first revision, which you need to address it later.</summary>
    public TransactionResult EnableRevisions(Guid id, out Guid newRevisionId, bool flushToDisk = false) => Execute(new Transaction(this).EnableRevisions(id, out newRevisionId), flushToDisk);
    /// <summary>Enables revisions for the node with this internal id and reports the id of the first revision.</summary>
    public TransactionResult EnableRevisions(int id, out Guid newRevisionId, bool flushToDisk = false) => Execute(new Transaction(this).EnableRevisions(id, out newRevisionId), flushToDisk);

    /// <summary>Collapses a revision aware node back to a plain one. All revisions except the one to keep are discarded, and the id may be omitted only if there is a single revision left.</summary>
    public TransactionResult DisableRevisions(Guid id, Guid? revisionIdToKeep = null, bool flushToDisk = false) => Execute(new Transaction(this).DisableRevisions(id, revisionIdToKeep), flushToDisk);
    /// <summary>Collapses the node with this internal id back to a single version.</summary>
    public TransactionResult DisableRevisions(int id, Guid? revisionIdToKeep = null, bool flushToDisk = false) => Execute(new Transaction(this).DisableRevisions(id, revisionIdToKeep), flushToDisk);

    /// <summary>Writes meta values (for instance author or workflow state) on one specific revision of a node.</summary>
    public TransactionResult UpdateMeta(Guid id, Guid revisionId, KeyValuePair<string, object>[] metaProperties, bool flushToDisk = false) => Execute(new Transaction(this).UpdateMeta(id, revisionId, metaProperties), flushToDisk);
    /// <summary>Writes meta values on one revision of the node with this internal id.</summary>
    public TransactionResult UpdateMeta(int id, Guid revisionId, KeyValuePair<string, object>[] metaProperties, bool flushToDisk = false) => Execute(new Transaction(this).UpdateMeta(id, revisionId, metaProperties), flushToDisk);
    /// <summary>Writes one named meta value on one revision of a node.</summary>
    public TransactionResult UpdateMeta(Guid id, Guid revisionId, string propertyName, object value, bool flushToDisk = false) => UpdateMeta(id, revisionId, [new(propertyName, value)], flushToDisk);
    /// <summary>Writes one named meta value on one revision of the node with this internal id.</summary>
    public TransactionResult UpdateMeta(int id, Guid revisionId, string propertyName, object value, bool flushToDisk = false) => UpdateMeta(id, revisionId, [new(propertyName, value)], flushToDisk);

    /// <summary>Writes meta values on the node itself, for nodes that do not use revisions.</summary>
    public TransactionResult UpdateMeta(Guid id, KeyValuePair<string, object>[] metaProperties, bool flushToDisk = false) => Execute(new Transaction(this).UpdateMeta(id, metaProperties), flushToDisk);
    /// <summary>Writes meta values on the node with this internal id.</summary>
    public TransactionResult UpdateMeta(int id, KeyValuePair<string, object>[] metaProperties, bool flushToDisk = false) => Execute(new Transaction(this).UpdateMeta(id, metaProperties), flushToDisk);
    /// <summary>Writes one named meta value on the node.</summary>
    public TransactionResult UpdateMeta(Guid id, string propertyName, object value, bool flushToDisk = false) => UpdateMeta(id, [new(propertyName, value)], flushToDisk);
    /// <summary>Writes one named meta value on the node with this internal id.</summary>
    public TransactionResult UpdateMeta(int id, string propertyName, object value, bool flushToDisk = false) => UpdateMeta(id, [new(propertyName, value)], flushToDisk);

    /// <summary>Deletes one revision of a node. The other revisions and the node itself are untouched.</summary>
    public TransactionResult DeleteRevision(Guid id, Guid revisionId, bool flushToDisk = false) => Execute(new Transaction(this).DeleteRevision(id, revisionId), flushToDisk);
    /// <summary>Deletes one revision of the node with this internal id.</summary>
    public TransactionResult DeleteRevision(int id, Guid revisionId, bool flushToDisk = false) => Execute(new Transaction(this).DeleteRevision(id, revisionId), flushToDisk);
    /// <summary>Copies an existing revision into a new one of the given type and culture, for instance a draft taken from the published version. Revisions are enabled automatically if they were not.</summary>
    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId = null, Guid? cultureId = null, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, newRevisionId, cultureId), flushToDisk);
    /// <summary>Creates a new revision of the node with this internal id, copied from an existing revision.</summary>
    public TransactionResult CreateRevision(int id, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId = null, Guid? cultureId = null, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, newRevisionId, cultureId), flushToDisk);
    /// <summary>Creates a new revision for a culture given by code, for instance to start a translation from the published version.</summary>
    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId, string? cultureCode, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, newRevisionId, cultureCode), flushToDisk);
    /// <summary>Creates a new revision for a culture given by code, for the node with this internal id.</summary>
    public TransactionResult CreateRevision(int id, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId, string? cultureCode, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, newRevisionId, cultureCode), flushToDisk);

    /// <summary>Creates a new revision and reports the id it was given, which you need in order to address it later.</summary>
    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, Guid? cultureId = null, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, cultureId), flushToDisk);
    /// <summary>Creates a new revision in the same culture as its source and reports the id it was given.</summary>
    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, Guid.Empty), default);
    /// <summary>Creates a new revision of the node with this internal id and reports the id it was given.</summary>
    public TransactionResult CreateRevision(int id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, Guid? cultureId = null, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, cultureId), flushToDisk);
    /// <summary>Creates a new revision for a culture given by code and reports the id it was given.</summary>
    public TransactionResult CreateRevision(Guid id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, string? cultureCode, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, cultureCode), flushToDisk);
    /// <summary>Creates a new revision for a culture given by code, for the node with this internal id, and reports the new id.</summary>
    public TransactionResult CreateRevision(int id, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, string? cultureCode, bool flushToDisk = false)
        => Execute(new Transaction(this).CreateRevision(id, sourceRevisionId, revisionType, out newRevisionId, cultureCode), flushToDisk);

    /// <summary>
    /// Returns every revision of a node, each as the mapped node object paired with its meta data (revision id,
    /// type, culture, timestamps). Use it to build a version history or a workflow overview.
    /// </summary>
    public NodeAndMeta<T>[] GetRevisions<T>(Guid id) {
        var revisions = Datastore.GetRevisions(id);
        return revisions.Select(r => new NodeAndMeta<T>(Mapper.CreateObjectFromNodeData<T>(r, null), r)).ToArray();
    }
    /// <summary>Moves a revision to another state, which is how content is published, archived or binned.</summary>
    public TransactionResult ChangeRevisionType(Guid id, Guid revisionId, RevisionType newRevisionType, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionType(id, revisionId, newRevisionType), flushToDisk);
    /// <summary>Moves a revision of the node with this internal id to another state.</summary>
    public TransactionResult ChangeRevisionType(int id, Guid revisionId, RevisionType newRevisionType, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionType(id, revisionId, newRevisionType), flushToDisk);
    /// <summary>Reassigns a revision to another culture, given by culture node id.</summary>
    public TransactionResult ChangeRevisionCulture(Guid id, Guid revisionId, Guid newCultureId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionCulture(id, revisionId, newCultureId), flushToDisk);
    /// <summary>Reassigns a revision of the node with this internal id to another culture.</summary>
    public TransactionResult ChangeRevisionCulture(int id, Guid revisionId, Guid newCultureId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionCulture(id, revisionId, newCultureId), flushToDisk);
    /// <summary>Reassigns a revision to another culture, given by culture code such as "nb-NO".</summary>
    public TransactionResult ChangeRevisionCulture(Guid id, Guid revisionId, string newCultureCode, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionCulture(id, revisionId, newCultureCode), flushToDisk);
    /// <summary>Reassigns a revision of the node with this internal id to another culture code.</summary>
    public TransactionResult ChangeRevisionCulture(int id, Guid revisionId, string newCultureCode, bool flushToDisk = false) => Execute(new Transaction(this).ChangeRevisionCulture(id, revisionId, newCultureCode), flushToDisk);

    /// <summary>
    /// Changes the node type of an existing node while keeping its id and its relations. Properties that the new
    /// type does not have are dropped, so this is a lossy operation.
    /// </summary>
    public void ChangeType(Guid id, Guid newTypeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType(id, newTypeId), flushToDisk);
    /// <summary>Changes the node type of the node with this internal id.</summary>
    public void ChangeType(int id, Guid newTypeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType(id, newTypeId), flushToDisk);
    /// <summary>Changes the node this object represents into the model type T.</summary>
    public void ChangeType<T>(object node, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType<T>(node), flushToDisk);
    /// <summary>Changes the node with this public id into the model type T.</summary>
    public void ChangeType<T>(Guid nodeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType<T>(nodeId), flushToDisk);
    /// <summary>Changes the node with this internal id into the model type T.</summary>
    public void ChangeType<T>(int nodeId, bool flushToDisk = false) => Execute(new Transaction(this).ChangeType<T>(nodeId), flushToDisk);

    /// <summary>Awaitable single node lookup by public id. Throws if the node does not exist, see TryGet for a softer version.</summary>
    public async Task<T> GetAsync<T>(Guid id) => Mapper.CreateObjectFromNodeData<T>(await Datastore.GetAsync(id), null);
    /// <summary>Runs a transaction you have built yourself. Everything in it is applied atomically.</summary>
    public Task<TransactionResult> ExecuteAsync(Transaction transaction, bool flushToDisk = false) => Datastore.ExecuteAsync(transaction._transactionData, flushToDisk);

    // ---------------------------------------------------------------------------------------------------------
    // QUERIES
    // Query returns a builder that you extend with Where, WhereSearch, WhereRelates, OrderBy, Page, Include,
    // Facets and so on, and finish with Execute, ExecuteFirst, Count and friends. Nothing runs until then.
    // Pass a QueryContext to read in another culture or as another user for this query only.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Starts a query over all nodes, regardless of type.</summary>
    public IQueryOfNodes<object, object> Query(QueryContext? ctx = null) => new QueryOfNodes<object, object>(this, ctx);
    /// <summary>Starts a query narrowed to the single node this object represents. Useful as a starting point for traversals.</summary>
    public IQueryOfNodes<T, T> Query<T>(T node, QueryContext? ctx = null) where T : notnull
        => Query<T>(Mapper.GetIdKey(node), ctx);
    /// <summary>Starts a query over the nodes of a type given by its id in the data model, for code that has no compile time type.</summary>
    public IQueryOfNodes<object, object> QueryType(Guid nodeTypeId, QueryContext? ctx = null) => new QueryOfNodes<object, object>(this, ctx, Datastore.Datamodel.NodeTypes[nodeTypeId].CodeName);
    /// <summary>Starts a query over the nodes of a type given by name, for code that has no compile time type.</summary>
    public IQueryOfNodes<object, object> QueryType(string typeName, QueryContext? ctx = null) => new QueryOfNodes<object, object>(this, ctx, typeName);

    /// <summary>
    /// Parses and runs a query written in the textual query language and returns the result ready for JSON
    /// serialisation. This is what the HTTP query endpoint uses. Parameters are passed separately, never inlined.
    /// </summary>
    public Task<object?> EvaluateForJsonAsync(string query, List<Parameter> parameters, QueryContext? ctx = null) {
        return new QueryStringBuilder(this, ctx, query, parameters).Prepare().EvaluateForJsonAsync();
    }
    /// <summary>Starts a query for the single node of type T with this public id.</summary>
    public IQueryOfNodes<T, T> Query<T>(Guid id, QueryContext? ctx = null)
        => new QueryOfNodes<T, T>(this, ctx).Where("a => a." + Datastore.Datamodel.NodeTypes[Mapper.GetNodeTypeId(typeof(T))].NameOfPublicIdProperty + " == \"" + id + "\"");
    /// <summary>Starts a query for the single node of type T with this internal id.</summary>
    public IQueryOfNodes<T, T> Query<T>(int id, QueryContext? ctx = null)
        => new QueryOfNodes<T, T>(this, ctx).Where("a => a." + Datastore.Datamodel.NodeTypes[Mapper.GetNodeTypeId(typeof(T))].NameOfInternalIdProperty + " == " + id + "");
    /// <summary>Starts a query for one node addressed by a key that may hold either kind of id.</summary>
    public IQueryOfNodes<T, T> Query<T>(NodeKey id, QueryContext? ctx = null) => id.Int == 0 ? Query<T>(id.Guid) : Query<T>(id.Int);
    /// <summary>Starts a query over all nodes of type T, including types inheriting from it. The usual entry point for reading.</summary>
    public IQueryOfNodes<T, T> Query<T>(QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx);
    /// <summary>Starts a query limited to the given public ids. Ids that do not exist are simply not returned.</summary>
    public IQueryOfNodes<T, T> Query<T>(IEnumerable<Guid> ids, QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx).WhereInIds(ids);
    /// <summary>Starts a query over nodes of type T filtered by a lambda, shorthand for Query&lt;T&gt;().Where(...).</summary>
    public IQueryOfNodes<T, T> Query<T>(Expression<Func<T, bool>> expression, QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx).Where(expression);
    /// <summary>Starts a query over the nodes related to a given node through a relation property id.</summary>
    public IQueryOfNodes<T, T> QueryRelated<T>(Guid propertyId, Guid nodeId, QueryContext? ctx = null) => new QueryOfNodes<T, T>(this, ctx).WhereRelates(propertyId, nodeId);

    /// <summary>Returns true if the two nodes are linked through the given relation property.</summary>
    public bool RelationExists<T>(Guid fromId, Expression<Func<T, object>> expression, Guid toId, QueryContext? ctx = null) => Query<T>(fromId, ctx).WhereRelates<T, object>(expression, toId).Count() > 0;
    /// <summary>Finds one shortest path (breadth first, unweighted) between two nodes over a relation. </summary>
    public GraphPathResult<T> ShortestPath<T, TProperty>(Guid fromNodeId, Expression<Func<T, TProperty>> relationProperty, Guid toNodeId, int maxLevel = 1000, GraphDirection direction = GraphDirection.Default, int? maxVisited = null, QueryContext? ctx = null)
        => Query<T>(ctx).ShortestPath(relationProperty, fromNodeId, toNodeId, maxLevel, direction, maxVisited).Execute();
    /// <summary>Writes everything buffered in the write ahead log to disk and waits until it is there.</summary>
    public Task FlushAsync() => Datastore.MaintenanceAsync(MaintenanceAction.FlushDisk);
    /// <summary>
    /// Runs one or more housekeeping actions: flushing to disk, clearing or persisting caches, truncating the log,
    /// garbage collecting and so on. The flags can be combined. Some of them are expensive and block writers.
    /// </summary>
    public Task MaintenanceAsync(MaintenanceAction options) => Datastore.MaintenanceAsync(options);


    /// <summary>Number of nodes in the database, of any type.</summary>
    public long Count() => Query<object>().Count();
    /// <summary>Number of nodes of type T, including types inheriting from it.</summary>
    public long Count<T>() => Query<T>().Count();

    // ---------------------------------------------------------------------------------------------------------
    // GET: direct lookup by id, much cheaper than a query when you already know which node you want.
    // The Get methods throw if the node is missing, the TryGet methods return false instead.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Reads one node by internal id, as its mapped model type. Throws if it does not exist.</summary>
    public object Get(int id) => Mapper.CreateObjectFromNodeData(Datastore.Get(id), null);
    /// <summary>Reads one node by public id, as its mapped model type. Throws if it does not exist.</summary>
    public object Get(Guid id) => Mapper.CreateObjectFromNodeData(Datastore.Get(id), null);
    /// <summary>Reads one node by a key holding either kind of id. Throws if it does not exist.</summary>
    public object Get(NodeKey id) => Mapper.CreateObjectFromNodeData(Datastore.Get(id), null);
    /// <summary>Maps raw node data that you already hold into a node object, without touching the store.</summary>
    public object Get(INodeDataExternal nodeData) => Get<object>(nodeData);

    /// <summary>Re-reads the node this object represents, giving you a fresh copy of the stored values.</summary>
    public T Get<T>(T node) where T : notnull => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(Mapper.GetIdGuid(node)), null);
    /// <summary>Reads the node with this internal id as type T. Throws if it does not exist or is another type.</summary>
    public T Get<T>(int id) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id), null);
    /// <summary>Reads the node with this public id as type T. Throws if it does not exist or is another type.</summary>
    public T Get<T>(Guid id) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id), null);
    /// <summary>Reads the node addressed by a key holding either kind of id, as type T.</summary>
    public T Get<T>(NodeKey id) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id), null);

    /// <summary>Reads the node with this internal id in the given culture, for instance "nb-NO".</summary>
    public T Get<T>(int id, string cultureCode) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureCode)), null);
    /// <summary>Reads the node with this public id in the given culture, for instance "nb-NO".</summary>
    public T Get<T>(Guid id, string cultureCode) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureCode)), null);
    /// <summary>Reads the node addressed by key in the given culture.</summary>
    public T Get<T>(NodeKey id, string cultureCode) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureCode)), null);
    /// <summary>Reads the node with this internal id in the culture identified by its culture node id.</summary>
    public T Get<T>(int id, Guid cultureId) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureId)), null);
    /// <summary>Reads the node with this public id in the culture identified by its culture node id.</summary>
    public T Get<T>(Guid id, Guid cultureId) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureId)), null);
    /// <summary>Reads the node addressed by key in the culture identified by its culture node id.</summary>
    public T Get<T>(NodeKey id, Guid cultureId) => Mapper.CreateObjectFromNodeData<T>(Datastore.Get(id, QueryContext.Culture(cultureId)), null);

    /// <summary>The CLR type a node is mapped to, without reading the whole node. Throws if the node does not exist.</summary>
    public Type GetNodeType(Guid nodeId) => Mapper.GetNodeType(Datastore.GetNodeType(nodeId));
    /// <summary>The CLR type a node is mapped to, or false if the node does not exist or its type is not in the model.</summary>
    public bool TryGetNodeType(Guid nodeId, [MaybeNullWhen(false)] out Type type) {
        if (Datastore.TryGetNodeType(nodeId, out var typeId)
         && Mapper.TryGetNodeType(typeId, out var t)) {
            type = t;
            return true;
        }
        type = null;
        return false;
    }
    /// <summary>True if a node with this public id exists at all.</summary>
    public bool Exists(Guid id) => Datastore.ExistsAndIsType(id, NodeConstants.BaseNodeTypeId);
    /// <summary>True if a node with this public id exists and is of type T, or a type inheriting from it.</summary>
    public bool Exists<T>(Guid id) => Datastore.ExistsAndIsType(id, Mapper.GetNodeTypeId(typeof(T)));

    /// <summary>Reads many nodes by internal id in one go, which is much faster than one call each. Lazily evaluated.</summary>
    public IEnumerable<T> Get<T>(IEnumerable<int> ids) => Datastore.Get(ids).Select(n => Mapper.CreateObjectFromNodeData<T>(n, null));
    /// <summary>Reads many nodes by public id in one go, which is much faster than one call each. Lazily evaluated.</summary>
    public IEnumerable<T> Get<T>(IEnumerable<Guid> ids) => Datastore.Get(ids).Select(n => Mapper.CreateObjectFromNodeData<T>(n, null));

    /// <summary>Reads a node by public id, returning false rather than throwing when it does not exist.</summary>
    public bool TryGet(Guid id, [MaybeNullWhen(false)] out object node) => TryGet<object>(id, out node);
    /// <summary>Reads a node by public id as type T, returning false rather than throwing when it does not exist.</summary>
    public bool TryGet<T>(Guid id, [MaybeNullWhen(false)] out T node) {
        if (Datastore.TryGet(id, out var nodeData)) {
            node = Mapper.CreateObjectFromNodeData<T>(nodeData, null);
            return true;
        }
        node = default;
        return false;
    }
    /// <summary>Reads a node by internal id as type T, returning false rather than throwing when it does not exist.</summary>
    public bool TryGet<T>(int id, [MaybeNullWhen(false)] out T node) {
        if (Datastore.TryGet((int)id, out var nodeData)) {
            node = Mapper.CreateObjectFromNodeData<T>(nodeData, null);
            return true;
        }
        node = default;
        return false;
    }
    /// <summary>Maps raw node data you already hold into a node object of type T, without touching the store.</summary>
    public T Get<T>(INodeDataExternal nodeData) => Mapper.CreateObjectFromNodeData<T>(nodeData, null);
    /// <summary>Not implemented. Related nodes are read through queries or through the relation properties on the node objects.</summary>
    public IEnumerable<T> GetRelatedNodes<T>(Guid propertyId, Guid nodeId) { // used by mapper internally
        throw new NotImplementedException("GetRelated with propertyId is not implemented in NodeStore.");
    }
    /// <summary>Not implemented. Related nodes are read through queries or through the relation properties on the node objects.</summary>
    public bool TryGetRelatedNode<T>(Guid propertyId, Guid nodeId, [MaybeNullWhen(false)] out T value) { // used by mapper internally
        throw new NotImplementedException("GetRelated with propertyId is not implemented in NodeStore.");
    }
    /// <summary>Maps preloaded related node data into node objects. Called by the generated mappers, rarely by application code.</summary>
    public IEnumerable<T> GetRelated<T>(NodeDataWithRelations[] node) { // used by mapper internally
        foreach (var item in node) yield return Mapper.CreateObjectFromNodeData<T>(item, null);
    }
    /// <summary>Resolves a plain reference property to its node, or default when it is unset or the target has been deleted. Called by the generated mappers.</summary>
    public T? GetReferencedNodeOrDefault<T>(Guid id) { // used by mapper internally, for plain-typed reference properties
        if (id == Guid.Empty) return default;
        if (!Datastore.TryGet(id, out var nodeData)) return default; // target deleted: skip
        if (Get(nodeData) is T node) return node;
        return default;
    }
    /// <summary>Resolves a multi reference property to its nodes, skipping ids whose target has been deleted. Called by the generated mappers.</summary>
    public IEnumerable<T> GetReferencedNodes<T>(Guid[]? ids) { // used by mapper internally, for plain-typed references properties
        if (ids == null) yield break;
        foreach (var id in ids) {
            if (id == Guid.Empty) continue;
            if (!Datastore.TryGet(id, out var nodeData)) continue; // target deleted: skip
            if (Get(nodeData) is T node) yield return node;
        }
    }

    /// <summary>Reads a single property value addressed by node and property, without loading the whole node. False if it is not there.</summary>
    public bool TryGetValue<T>(PropertyPath path, [MaybeNullWhen(false)] out T value) => Datastore.TryGetValue(path, out value);
    /// <summary>Reads a single property value addressed by node and property, without loading the whole node. Throws if it is not there.</summary>
    public T GetValue<T>(PropertyPath path) => Datastore.GetValue<T>(path);
    /// <summary>Reads a single property value from a textual property path.</summary>
    public T GetValue<T>(string path) => Datastore.GetValue<T>(PropertyPath.Parse(path));

    // ---------------------------------------------------------------------------------------------------------
    // ADDRESSES: a node can have a unique, human readable address, which is the path part of its URL. Addresses
    // are generated from the display name unless you set one, and are how the web layer resolves a request.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Looks up which node an address belongs to. False if no node has that address.</summary>
    public bool TryGetIdFromAddress(string address, [MaybeNullWhen(false)] out Guid nodeId) {
        return Datastore.TryGetNodeIdFromAddress(address, out nodeId);
    }
    /// <summary>Looks up the node an address belongs to, and the culture that address is registered for.</summary>
    public bool TryGetIdFromAddress(string address, [MaybeNullWhen(false)] out Guid nodeId, [MaybeNullWhen(false)] out string cultureCode) {
        return Datastore.TryGetNodeIdFromAddress(address, out nodeId, out cultureCode);
    }
    /// <summary>Looks up the internal id of the node an address belongs to.</summary>
    public bool TryGetIdFromAddress(string address, [MaybeNullWhen(false)] out int nodeId) {
        return Datastore.TryGetNodeIdFromAddress(address, out nodeId);
    }
    /// <summary>Looks up the internal id of the node an address belongs to, and the culture of that address.</summary>
    public bool TryGetIdFromAddress(string address, [MaybeNullWhen(false)] out int nodeId, [MaybeNullWhen(false)] out string cultureCode) {
        return Datastore.TryGetNodeIdFromAddress(address, out nodeId, out cultureCode);
    }
    /// <summary>Reads the node an address points to, in one step. This is the normal way to serve a page request.</summary>
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

    /// <summary>Not implemented.</summary>
    public Task<TransactionResult> ExecuteAsync(ActionModel[] actions, bool flushToDisk = false) {
        throw new NotImplementedException();
    }
    /// <summary>
    /// Commits a transaction: all of its operations are applied together or none of them are. Registered
    /// transaction plugins are notified before and after, and on failure. An empty transaction is a no-op.
    /// </summary>
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
    /// <summary>Writes everything buffered in the write ahead log to disk and waits until it is there.</summary>
    public void Flush() => Maintenance(MaintenanceAction.FlushDisk);
    /// <summary>Runs one or more housekeeping actions, see <see cref="MaintenanceAsync"/>. The flags can be combined.</summary>
    public void Maintenance(MaintenanceAction actions) => Datastore.Maintenance(actions);
    /// <summary>
    /// Starts a transaction you fill with operations and then commit with <see cref="Transaction.Execute(bool)"/>.
    /// Use it whenever several changes must succeed or fail together. Nothing is written until it is executed.
    /// </summary>
    public Transaction CreateTransaction() => new(this);

    // ---------------------------------------------------------------------------------------------------------
    // LOCKS
    // A write lock reserves a node (or the whole store) for the holder: transactions from anyone else that touch
    // it are rejected and retried, unless they carry the lock id as an exemption (see Transaction.AddLockExcemptions).
    // Locks expire on their own after lockDurationInMs, so a crashed client cannot block the database forever.
    // Refresh a lock to extend it and always release it when done. This is meant for editing sessions and other
    // long lived work, single transactions are already atomic on their own.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Tries to lock the whole store for writing, returning false instead of throwing if the wait times out.</summary>
    public bool TryRequestGlobalLock(out Guid lockId, double lockDurationInMs = 1000, double maxWaitTimeInMs = 1000) {
        try {
            lockId = RequestGlobalLock(lockDurationInMs, maxWaitTimeInMs);
            return true;
        } catch {
            lockId = Guid.Empty;
            return false;
        }
    }
    /// <summary>Locks the whole store for writing and returns the lock id. Blocks until granted or the wait times out.</summary>
    public Guid RequestGlobalLock(double lockDurationInMs, double maxWaitTimeInMs) => RequestGlobalLockAsync(lockDurationInMs, maxWaitTimeInMs).Result;
    /// <summary>Awaitable global write lock. Throws if it cannot be granted within maxWaitTimeInMs.</summary>
    public Task<Guid> RequestGlobalLockAsync(double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => Datastore.RequestGlobalLockAsync(lockDurationInMs, maxWaitTimeInMs);
    /// <summary>Awaitable write lock on one node, given by public id. Throws if it cannot be granted in time.</summary>
    public Task<Guid> RequestLockAsync(Guid nodeId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => Datastore.RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs);

    /// <summary>Locks one node for writing and returns the lock id. Blocks until granted or the wait times out.</summary>
    public Guid RequestLock(Guid nodeId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs).Result;
    /// <summary>Locks the node with this internal id for writing and returns the lock id.</summary>
    public Guid RequestLock(int nodeId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs).Result;
    /// <summary>Locks the node this object represents for writing and returns the lock id.</summary>
    public Guid RequestLock(object node, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => RequestLockAsync(node, lockDurationInMs, maxWaitTimeInMs).Result;
    /// <summary>Awaitable write lock on the node with this internal id.</summary>
    public Task<Guid> RequestLockAsync(int nodeId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) => Datastore.RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs);
    /// <summary>Awaitable write lock on the node this object represents.</summary>
    public Task<Guid> RequestLockAsync(object node, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) {
        if (Mapper.TryGetIdGuid(node, out var guid)) return RequestLockAsync(guid, lockDurationInMs, maxWaitTimeInMs);
        if (Mapper.TryGetIdUInt(node, out var id)) return RequestLockAsync(id, lockDurationInMs, maxWaitTimeInMs);
        throw new Exception("Only nodes with Guid or int id accepted. ");
    }
    /// <summary>Tries to lock one node, returning false instead of throwing if it is busy.</summary>
    public bool TryRequestLock(Guid nodeId, out Guid lockId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) {
        try {
            lockId = RequestLock(nodeId, lockDurationInMs, maxWaitTimeInMs);
            return true;
        } catch {
            lockId = Guid.Empty;
            return false;
        }
    }
    /// <summary>Tries to lock the node with this internal id, returning false instead of throwing if it is busy.</summary>
    public bool TryRequestLock(int nodeId, out Guid lockId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) {
        try {
            lockId = RequestLock(nodeId, lockDurationInMs, maxWaitTimeInMs);
            return true;
        } catch {
            lockId = Guid.Empty;
            return false;
        }
    }
    /// <summary>Tries to lock the node this object represents, returning false instead of throwing if it is busy.</summary>
    public bool TryRequestLock(object node, out Guid lockId, double lockDurationInMs = 10000, double maxWaitTimeInMs = 10000) {
        try {
            lockId = RequestLock(node, lockDurationInMs, maxWaitTimeInMs);
            return true;
        } catch {
            lockId = Guid.Empty;
            return false;
        }
    }

    /// <summary>Extends a lock by its original duration. Call it periodically during long work so the lock does not expire.</summary>
    public void RefreshLock(Guid lockId) => Datastore.RefreshLock(lockId);
    /// <summary>Releases a lock immediately, instead of waiting for it to expire. Always do this when the work is done.</summary>
    public void ReleaseLock(Guid lockId) => Datastore.ReleaseLock(lockId);

    // ---------------------------------------------------------------------------------------------------------
    // SINGLE PROPERTY UPDATES
    // These change one property without reading and writing the whole node, which is both cheaper and safer when
    // several parts of the application write to the same node.
    //   UpdateProperty / UpdateIfDifferentProperty - writes only when the value actually differs (the same method)
    //   ForceUpdateProperty                        - writes without comparing first
    //   ResetProperty                              - puts the property back to its default value
    //   AddToProperty / MultiplyProperty           - read modify write in one atomic step, so parallel counters
    //                                                do not lose updates. Add means numeric addition, string
    //                                                concatenation or appending to an array, depending on the type
    // The property is named either by a lambda (n => n.Title) or by its raw property id.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Writes one property, addressed by raw property id, if the value differs from the stored one.</summary>
    public void UpdateProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Writes one property of the node with this internal id, if the value differs.</summary>
    public void UpdateProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Writes one property of the node this object represents, naming it with a lambda such as n =&gt; n.Title.</summary>
    public void UpdateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Writes one property of the node with this public id, naming it with a lambda.</summary>
    public void UpdateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Writes one property of the node with this internal id, naming it with a lambda.</summary>
    public void UpdateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Writes the same property value on many nodes in one atomic transaction.</summary>
    public void UpdateProperty<T, V>(IEnumerable<Guid> ids, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => Execute(new Transaction(this).UpdateProperty(ids, expression, value), flushToDisk);
    /// <summary>Writes several properties of one node in a single transaction, each given as a property lambda and a value.</summary>
    public void UpdateProperties<T>(Guid nodeId, params Tuple<Expression<Func<T, object?>>, object>[] propertyValuePairs) where T : notnull => Execute(new Transaction(this).UpdateProperties(nodeId, propertyValuePairs));

    /// <summary>Sets the display name, the built in label used by the admin UI and by automatic address generation.</summary>
    public void UpdateDisplayName(Guid nodeId, string newDisplayName, bool flushToDisk = false) => Execute(new Transaction(this).UpdateDisplayName(nodeId, newDisplayName), flushToDisk);
    /// <summary>Sets the display name of the node with this internal id.</summary>
    public void UpdateDisplayName(int nodeId, string newDisplayName, bool flushToDisk = false) => Execute(new Transaction(this).UpdateDisplayName(nodeId, newDisplayName), flushToDisk);
    /// <summary>Sets the display name of the node this object represents.</summary>
    public void UpdateDisplayName(object node, string newDisplayName, bool flushToDisk = false) => Execute(new Transaction(this).UpdateDisplayName(node, newDisplayName), flushToDisk);
    /// <summary>Sets the address (URL path) of a node. If it is taken, the store adjusts it, see the overload reporting the result.</summary>
    public void UpdateAddress(Guid nodeId, string newAddress, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAddress(nodeId, newAddress), flushToDisk);
    /// <summary>Sets the address of the node with this internal id.</summary>
    public void UpdateAddress(int nodeId, string newAddress, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAddress(nodeId, newAddress), flushToDisk);
    /// <summary>Sets the address of the node addressed by a key holding either kind of id.</summary>
    public void UpdateAddress(NodeKey key, string newAddress, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAddress(key, newAddress), flushToDisk);
    /// <summary>Sets the address of the node this object represents.</summary>
    public void UpdateAddress(object node, string newAddress, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAddress(node, newAddress), flushToDisk);

    /// <summary>Turns automatic address generation on or off for a node. While on, the address follows the display name.</summary>
    public void UpdateAutoAddress(object node, bool value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAutoAddress(node, value), flushToDisk);
    /// <summary>Turns automatic address generation on or off for the node with this public id.</summary>
    public void UpdateAutoAddress(Guid nodeId, bool value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAutoAddress(nodeId, value), flushToDisk);
    /// <summary>Turns automatic address generation on or off for the node with this internal id.</summary>
    public void UpdateAutoAddress(int nodeId, bool value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateAutoAddress(nodeId, value), flushToDisk);

    /// <summary>
    /// Sets the address and reports what the node actually ended up with, since the store may have to adjust an
    /// address that is already in use. newAddressGenerated is true when the stored address equals the one asked for.
    /// </summary>
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
    /// <summary>Sets the address of the node with this internal id and reports the address it actually ended up with.</summary>
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
    /// <summary>Sets the address of the node addressed by key and reports the address it actually ended up with.</summary>
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
    /// <summary>
    /// Sets the address of the node this object represents. didChange is true when the store had to adjust the
    /// address you asked for, and changedAddress then holds the one that was actually stored.
    /// </summary>
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

    /// <summary>Writes one property, addressed by raw property id, without comparing to the stored value first.</summary>
    public void ForceUpdateProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).ForceUpdateProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Writes one property of the node with this internal id, without comparing first.</summary>
    public void ForceUpdateProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).ForceUpdateProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Writes one property of the node this object represents, without comparing first.</summary>
    public void ForceUpdateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => ForceUpdateProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Writes one property of the node with this public id, without comparing first.</summary>
    public void ForceUpdateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => ForceUpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Writes one property of the node with this internal id, without comparing first.</summary>
    public void ForceUpdateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => ForceUpdateProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Writes the same property value on many nodes without comparing first.</summary>
    public void ForceUpdateProperty<T, V>(IEnumerable<Guid> ids, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => Execute(new Transaction(this).ForceUpdateProperty(ids, expression, value), flushToDisk);
    /// <summary>Writes several properties of one node without comparing first.</summary>
    public void ForceUpdateProperties<T>(Guid nodeId, params Tuple<Expression<Func<T, object?>>, object>[] propertyValuePairs) where T : notnull => Execute(new Transaction(this).ForceUpdateProperties(nodeId, propertyValuePairs));

    /// <summary>Writes one property only if the value differs from the stored one. Same as <see cref="UpdateProperty(Guid, Guid, object, bool)"/>.</summary>
    public void UpdateIfDifferentProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateIfDifferentProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Writes one property of the node with this internal id only if the value differs.</summary>
    public void UpdateIfDifferentProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).UpdateIfDifferentProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Writes one property of the node this object represents only if the value differs.</summary>
    public void UpdateIfDifferentProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateIfDifferentProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Writes one property of the node with this public id only if the value differs.</summary>
    public void UpdateIfDifferentProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateIfDifferentProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Writes one property of the node with this internal id only if the value differs.</summary>
    public void UpdateIfDifferentProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => UpdateIfDifferentProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);

    /// <summary>Puts a property back to the default value defined for it in the data model.</summary>
    public void ResetProperty(Guid nodeId, Guid propertyId, bool flushToDisk = false) => Execute(new Transaction(this).ResetProperty(nodeId, propertyId), flushToDisk);
    /// <summary>Puts a property of the node with this internal id back to its default value.</summary>
    public void ResetProperty(int nodeId, Guid propertyId, bool flush) => Execute(new Transaction(this).ResetProperty(nodeId, propertyId), flush);
    /// <summary>Puts a property of the node this object represents back to its default value.</summary>
    public void ResetProperty<T, V>(T node, Expression<Func<T, V>> expression, bool flushToDisk = false) where T : notnull where V : notnull => ResetProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, flushToDisk);
    /// <summary>Puts a property of the node with this public id back to its default value.</summary>
    public void ResetProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, bool flushToDisk = false) => ResetProperty(nodeId, Mapper.GetProperty(expression).Id, flushToDisk);
    /// <summary>Puts a property of the node with this internal id back to its default value.</summary>
    public void ResetProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, bool flushToDisk = false) => ResetProperty(nodeId, Mapper.GetProperty(expression).Id, flushToDisk);
    /// <summary>Adds to the current value of a property in one atomic step: numbers add up, text is concatenated, arrays get the values appended.</summary>
    public void AddToProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).AddToProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Adds to the current value of a property on the node with this internal id.</summary>
    public void AddToProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).AddToProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Adds to the current value of a property on the node this object represents, for instance to increment a counter.</summary>
    public void AddToProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => AddToProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Adds to the current value of a property on the node with this public id.</summary>
    public void AddToProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => AddToProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Adds to the current value of a property on the node with this internal id.</summary>
    public void AddToProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => AddToProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Multiplies the current numeric value of a property in one atomic step.</summary>
    public void MultiplyProperty(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).MultiplyProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Multiplies the current value of a property on the node with this internal id.</summary>
    public void MultiplyProperty(int nodeId, Guid propertyId, object value, bool flushToDisk = false) => Execute(new Transaction(this).MultiplyProperty(nodeId, propertyId, value), flushToDisk);
    /// <summary>Multiplies the current value of a property on the node this object represents.</summary>
    public void MultiplyProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => MultiplyProperty(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Multiplies the current value of a property on the node with this public id.</summary>
    public void MultiplyProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => MultiplyProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);
    /// <summary>Multiplies the current value of a property on the node with this internal id.</summary>
    public void MultiplyProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, bool flushToDisk = false) where T : notnull where V : notnull => MultiplyProperty(nodeId, Mapper.GetProperty(expression).Id, value, flushToDisk);

    // These two win overload resolution over ReIndex(id, flushToDisk) for a single argument call, so they have to
    // do the same thing. They simply forward, discarding the result.
    /// <summary>Rebuilds the indexes for the node with this internal id, without flushing to disk.</summary>
    public void ReIndex(int id) => ReIndex(id, false);
    /// <summary>Rebuilds the indexes for the node with this public id, without flushing to disk.</summary>
    public void ReIndex(Guid id) => ReIndex(id, false);


    // ---------------------------------------------------------------------------------------------------------
    // FILES
    // A file lives in a FileValue property on a node, so it is addressed by the pair (node id, property id).
    // The bytes are kept in the configured file store (local disk, S3, ...) and never in the node itself. Uploading
    // writes the bytes and updates the FileValue on the node, so you do not update the node yourself afterwards.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Streams the stored file into the given stream.</summary>
    public Task FileDownloadAsync(Guid nodeId, Guid propertyId, Stream outStream) {
        return Datastore.FileDownloadAsync(new(nodeId, propertyId), outStream);
    }
    /// <summary>Reads the whole stored file into memory. Prefer the streaming overload for large files.</summary>
    public async Task<byte[]> FileDownloadAsync(Guid nodeId, Guid propertyId) {
        using var ms = new MemoryStream();
        await FileDownloadAsync(nodeId, propertyId, ms);
        return ms.ToArray();
    }
    /// <summary>Deletes the file held by a file property and clears the value on the node.</summary>
    public Task FileDeleteAsync(Guid nodeId, Guid propertyId) => Datastore.FileDeleteAsync(new(nodeId, propertyId));
    /// <summary>Deletes the file held by the file property named by the lambda.</summary>
    public Task FileDeleteAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression) => FileDeleteAsync(nodeId, Mapper.GetProperty(expression).Id);

    /// <summary>Uploads a file that already lives in another IO provider, copying it into the file store.</summary>
    public Task<FileValue> FileUploadAsync(Guid nodeId, Guid propertyId, IIOProvider source, string sourceFileKey, string? fileName = null) => Datastore.FileUploadAsync(new(nodeId, propertyId), source, sourceFileKey, fileName);
    /// <summary>Uploads from a stream, for instance straight from an HTTP request body, and returns the stored file value.</summary>
    public Task<FileValue> FileUploadAsync(Guid nodeId, Guid propertyId, Stream source, string fileName) => Datastore.FileUploadAsync(new(nodeId, propertyId), source, fileName);
    /// <summary>Uploads a file from the local file system into the file property named by the lambda.</summary>
    public async Task<FileValue> FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, string filePath, string? newFileName = null) {
        using var stream = File.OpenRead(filePath);
        newFileName = newFileName ?? Path.GetFileName(filePath);
        return await FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, stream, newFileName);
    }
    /// <summary>Uploads a local file into an existing FileValue. The node must already be stored, since the value carries the property path.</summary>
    public async Task<FileValue> FileUploadAsync(FileValue file, string localFilePath, string? fileName = null) {
        if (file.PropertyPath == null) throw new Exception("File cannot be uploaded as node is not yet inserted to the database. ");
        using var stream = File.OpenRead(localFilePath);
        return await Datastore.FileUploadAsync(file.PropertyPath, stream, fileName ?? Path.GetFileName(localFilePath));
    }

    /// <summary>Uploads from a stream into the file property named by the lambda.</summary>
    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, Stream source, string fileName) => FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, source, fileName);
    /// <summary>Uploads a byte array into the file property named by the lambda.</summary>
    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, byte[] data, string fileName) => FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, new MemoryStream(data), fileName);
    /// <summary>Uploads a file from another IO provider into the file property named by the lambda.</summary>
    public Task FileUploadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, IIOProvider source, string sourceFileKey, string? fileName = null) => FileUploadAsync(nodeId, Mapper.GetProperty(expression).Id, source, sourceFileKey, fileName);

    /// <summary>Uploads a local file into a file property of the node this object represents.</summary>
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, string filePath, string? fileName = null) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, filePath, fileName);
    /// <summary>Uploads from a stream into a file property of the node this object represents.</summary>
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, Stream source, string fileName) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, source, fileName);
    /// <summary>Uploads a byte array into a file property of the node this object represents.</summary>
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, byte[] data, string fileName) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, new MemoryStream(data), fileName);
    /// <summary>Uploads a file from another IO provider into a file property of the node this object represents.</summary>
    public Task FileUploadAsync<T>(T node, Expression<Func<T, FileValue>> expression, IIOProvider source, string sourceFileKey, string? fileName = null) where T : notnull => FileUploadAsync(Mapper.GetIdGuid(node), expression, source, sourceFileKey, fileName);

    /// <summary>Streams the file held by the property named by the lambda into the given stream.</summary>
    public Task FileDownloadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression, Stream outStream) => FileDownloadAsync(nodeId, Mapper.GetProperty(expression).Id, outStream);
    /// <summary>Reads the file held by the property named by the lambda into memory.</summary>
    public Task<byte[]> FileDownloadAsync<T>(Guid nodeId, Expression<Func<T, FileValue>> expression) => FileDownloadAsync(nodeId, Mapper.GetProperty(expression).Id);
    /// <summary>Streams a file belonging to the node this object represents into the given stream.</summary>
    public Task FileDownloadAsync<T>(T node, Expression<Func<T, FileValue>> expression, Stream outStream) where T : notnull => FileDownloadAsync(Mapper.GetIdGuid(node), expression, outStream);
    /// <summary>Reads a file belonging to the node this object represents into memory.</summary>
    public Task<byte[]> FileDownloadAsync<T>(T node, Expression<Func<T, FileValue>> expression) where T : notnull => FileDownloadAsync(Mapper.GetIdGuid(node), expression);
    /// <summary>
    /// Downloads a stored file and writes it to a local path, creating or overwriting that file, and returns the
    /// file value that was read. 
    /// </summary>
    public async Task<FileValue> FileDownloadAsync(FileValue file, string localFilePath) {
        if (file.PropertyPath == null) throw new Exception("File cannot be downloaded as node is not yet inserted to the database. ");
        using var stream = File.Create(localFilePath);
        return await Datastore.FileDownloadAsync(file.PropertyPath, stream);
    }
    /// <summary>Opens a readable stream for a stored file. The bytes are pumped in as they arrive, so reading can start before the transfer is complete.</summary>
    public Task<Stream> OpenFileDownloadStreamAsync(FileValue file) {
        if (file.PropertyPath == null) throw new Exception("File cannot be downloaded as node is not yet inserted to the database. ");
        var stream = new WriteToReadStream();
        _ = Datastore.FileDownloadAsync(file.PropertyPath, stream)
            .ContinueWith(t => stream.Complete(t.IsFaulted ? t.Exception : null));
        return Task.FromResult<Stream>(stream);
    }
    /// <summary>Deletes a file belonging to the node this object represents.</summary>
    public Task FileDeleteAsync<T>(T node, Expression<Func<T, FileValue>> expression) where T : notnull => FileDeleteAsync(Mapper.GetIdGuid(node), expression);

    /// <summary>True when the bytes really are in the file store. Useful after an upload to a remote store that completes in the background.</summary>
    public Task<bool> FileUploadedAndAvailableAsync(Guid nodeId, Guid propertyId) => Datastore.IsFileUploadedAndAvailableAsync(new(nodeId, propertyId));
    /// <summary>True when the file of this node property really is in the file store.</summary>
    public Task<bool> FileUploadedAndAvailableAsync<T>(T node, Expression<Func<T, FileValue>> expression) where T : notnull => FileUploadedAndAvailableAsync(Mapper.GetIdGuid(node), Mapper.GetProperty(expression).Id);

    // Multipart upload, for files too large to send in one request: initiate once, append chunks in order,
    // then finalize. Cancel to throw away what has been uploaded so far. Not every file store supports it,
    // check with FileStoreSupportsMultipartUploads first.

    /// <summary>Starts a chunked upload to a file property and returns the id used for the following calls.</summary>
    public Task<Guid> InitiateMultipartUploadAsync(PropertyPath propertyPath, string fileName, QueryContext? ctx = null) => Datastore.InitiateMultipartUploadAsync(propertyPath, fileName, ctx);
    /// <summary>Starts a chunked upload into an existing file value. The node must already be stored.</summary>
    public Task<Guid> InitiateMultipartUploadAsync(FileValue fileValue, string fileName, QueryContext? ctx = null) => Datastore.InitiateMultipartUploadAsync(fileValue.PropertyPath!, fileName, ctx);
    /// <summary>Appends the next chunk of a chunked upload. Chunks must be appended in order.</summary>
    public Task AppendMultipartUploadAsync(Guid fileId, byte[] data, int length) => Datastore.AppendMultipartUploadAsync(fileId, data, length);
    /// <summary>
    /// Completes a chunked upload, writes the file value onto the node and returns it. Registered transaction
    /// plugins interested in the node type get their upload callback here.
    /// </summary>
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
    /// <summary>Abandons a chunked upload and throws away the chunks uploaded so far.</summary>
    public Task CancelMultipartUploadAsync(Guid fileId) => Datastore.CancelMultipartUpload(fileId);
    /// <summary>True if the file store behind this property can take chunked uploads.</summary>
    public bool FileStoreSupportsMultipartUploads(PropertyPath propertyPath) => Datastore.FileStoreSupportsMultipartUploads(propertyPath);
    /// <summary>True if the file store behind this file value can take chunked uploads.</summary>
    public bool FileStoreSupportsMultipartUploads(FileValue fileValue) => Datastore.FileStoreSupportsMultipartUploads(fileValue.PropertyPath!);

    // ---------------------------------------------------------------------------------------------------------
    // URLS AND FILE STREAMS
    // GetUrl builds the URL the web layer serves a node or a file on, and TryParseUrl is the reverse. A file URL
    // can carry a FileAdjustment, which is a requested variant of the file such as a resized image or a converted
    // format. Variants are produced by background conversion, so a request may have to wait for one, which is what
    // the maxWait arguments and the IsFileReady and conversion methods are about.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The URL of the node this object represents, based on its address.</summary>
    public string GetUrl(object node, bool absolute = false, QueryContext? ctx = null) => GetUrl(Mapper.GetIdKey(node), absolute, ctx);
    /// <summary>The URL of the node with this public id. Pass absolute to include scheme and host.</summary>
    public string GetUrl(Guid nodeId, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(new NodePath(nodeId), absolute, ctx);
    /// <summary>The URL of the node with this internal id.</summary>
    public string GetUrl(int nodeId, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(new NodePath(nodeId), absolute, ctx);
    /// <summary>The URL of the node addressed by a key holding either kind of id.</summary>
    public string GetUrl(NodeKey key, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(new NodePath(key), absolute, ctx);
    /// <summary>The URL of a node path, which can also point at a node inside an embedded structure.</summary>
    public string GetUrl(NodePath node, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(node, absolute, ctx);
    /// <summary>The URL of a specific variant of a file, for instance a thumbnail of an image.</summary>
    public string GetUrl(FileValue fileValue, FileAdjustment adj, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(fileValue.PropertyPath!, adj, absolute, ctx);
    /// <summary>The URL of a file as it was uploaded.</summary>
    public string GetUrl(FileValue fileValue, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(fileValue.PropertyPath!, absolute, ctx);
    /// <summary>The URL of a file variant, addressed by property path.</summary>
    public string GetUrl(PropertyPath propertyPath, FileAdjustment adj, bool absolute = false, QueryContext? ctx = null) => Datastore.GetUrl(propertyPath, adj, absolute, ctx);

    /// <summary>Takes a URL apart into what it points at: node, file property and requested variant. False if it is not one of ours.</summary>
    public bool TryParseUrl(string url, [MaybeNullWhen(false)] out UrlKeys result, QueryContext? ctx = null) => Datastore.TryParseUrl(url, out result, ctx);
    /// <summary>Resolves a URL all the way to the content to serve: node data, or a file stream with content type and file name.</summary>
    public bool TryParseUrlForContent(string url, [MaybeNullWhen(false)] out UrlContent result, int maxWaitMs = -1, QueryContext? ctx = null) => Datastore.TryParseUrlForContent(url, out result, maxWaitMs, ctx);

    /// <summary>Opens the file a URL points at. maxWait is how long to wait, in ms, for a variant that is still being produced.</summary>
    public Task<Stream> GetFileStream(string url, int maxWait, QueryContext? ctx = null) => Datastore.GetFileStream(url, maxWait, ctx);
    /// <summary>Opens the file a URL points at, and reports whether it is the finished variant or a stand in while conversion runs.</summary>
    public Task<StateAndStream> GetFileStreamAndState(string url, int maxWait = -1, QueryContext? ctx = null) => Datastore.GetFileStreamAndState(url, maxWait, ctx);
    /// <summary>Opens the original file stored in this file property.</summary>
    public Task<Stream> GetFileStream(PropertyPath propertyPath, QueryContext? ctx = null) => Datastore.GetFileStream(propertyPath, ctx);
    /// <summary>Opens a variant of the file, waiting up to maxWait ms if it has to be produced first.</summary>
    public Task<Stream> GetFileStream(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null) => Datastore.GetFileStream(propertyPath, adj, maxWait, ctx);
    /// <summary>Opens a variant of the file and reports whether it is ready or still being produced.</summary>
    public Task<StateAndStream> GetFileStreamAndState(PropertyPath propertyPath, FileAdjustment adj, int maxWait = -1, QueryContext? ctx = null)
        => Datastore.GetFileStreamAndState(propertyPath, adj, maxWait, ctx);
    /// <summary>Opens the file and hands back its file value as well, so you get name, size and content type in the same call.</summary>
    public Task<StreamAndValue> GetFileStreamAndValue(PropertyPath propertyPath, QueryContext? ctx = null)
        => Datastore.GetFileStreamAndValue(propertyPath, ctx);
    /// <summary>
    /// Progress of the conversion producing a file variant, for progress bars and diagnostics. False when no
    /// conversion is known. Pass queueConversionIfNotRequested to start one that has not been asked for yet.
    /// </summary>
    public bool TryGetConversionInfo(PropertyPath propertyPath, FileAdjustment adj, bool queueConversionIfNotRequested, [MaybeNullWhen(false)] out FileConversionProgressInfo progressInfo, QueryContext? ctx = null)
        => Datastore.TryGetConversionInfo(propertyPath, adj, queueConversionIfNotRequested, out progressInfo, ctx);
    /// <summary>True when a file variant is ready to serve. Pass requestIfNot to queue the conversion when it is not.</summary>
    public bool IsFileReady(PropertyPath propertyPath, FileAdjustment adj, bool requestIfNot, QueryContext? ctx = null) => Datastore.IsFileReady(propertyPath, adj, requestIfNot, ctx);
    /// <summary>Queues the conversion producing a file variant unless it is already done or queued. Returns at once.</summary>
    public void EnsureConversionRequested(PropertyPath propertyPath, FileAdjustment adj, QueryContext? ctx = null) => Datastore.EnsureConversionRequested(propertyPath, adj, ctx);
    /// <summary>The file conversions running or queued right now, for progress reporting and diagnostics.</summary>
    public FileConversions GetRunningConversions(QueryContext? ctx = null) => Datastore.GetConversions(ctx);

    /// <summary>
    /// Puts a job on the store's background task queue, to be picked up by a registered runner. Pass a jobId to
    /// group related tasks. Returns as soon as it is queued, it does not wait for the work.
    /// </summary>
    public Task EnqueueTaskAsync(TaskData task, string? jobId = null) {
        Datastore.EnqueueTask(task, jobId);
        return Task.CompletedTask;
    }
    /// <summary>Puts a job on the store's background task queue, to be picked up by a registered runner.</summary>
    public void EnqueueTask(TaskData task, string? jobId = null) => Datastore.EnqueueTask(task, jobId);

    /// <summary>
    /// The state id of the store, which increases with every committed transaction. Comparing it before and after
    /// tells you whether anything changed, which is handy for caching.
    /// </summary>
    public long Timestamp => Datastore.Timestamp;

    /// <summary>Closes the underlying data store. Only dispose the store when the application is shutting down.</summary>
    public virtual void Dispose() => Datastore.Dispose();

    /// <summary>
    /// Makes sure the given cultures exist as culture nodes, creating the missing ones and correcting codes that
    /// have changed. Native and English names are filled in from the .NET culture. Call it at start up, the store
    /// must be open. An unknown culture code throws.
    /// </summary>
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
    /// <summary>Makes sure culture nodes exist for the given culture codes, creating the ones that are missing.</summary>
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
