using Microsoft.CodeAnalysis.CSharp.Syntax;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Transactions;
using System;
using System.Collections;
using System.Linq.Expressions;
using static System.Runtime.InteropServices.JavaScript.JSType;
namespace Relatude.DB.Nodes;

/// <summary>
/// A batch of changes that are applied together or not at all. You build it up by calling the operation methods,
/// which only record what should happen, and then call <see cref="Execute(bool)"/> or
/// <see cref="ExecuteAsync(bool)"/> to commit. Nothing reaches the database before that, and if any single
/// operation fails the whole batch is rolled back.
/// <para>
/// Every operation method returns the transaction itself, so calls can be chained:
/// <code>store.CreateTransaction().Insert(order).AddRelation(order, o => o.Customer, customer).Execute();</code>
/// </para>
/// <para>
/// The same convenience methods exist directly on <see cref="NodeStore"/>, where each call is its own one
/// operation transaction. Use this class when several changes must succeed or fail as a unit, or when you want
/// to avoid the overhead of committing many small transactions.
/// </para>
/// <para>
/// Nodes are addressed by node object, by public <see cref="Guid"/> id or by internal <see cref="int"/> id, and
/// most operations have one overload per flavour. When a node object without an id is passed, an id is generated
/// on the spot and written back to the object, so relations to it can be recorded in the same transaction.
/// Executing clears the transaction, so the instance can be filled and executed again.
/// </para>
/// <para>A transaction is a short lived object and is not meant to be shared between threads.</para>
/// </summary>
public partial class Transaction {
    internal TransactionData _transactionData;
    /// <summary>The store this transaction belongs to and will be committed against.</summary>
    public readonly NodeStore Store;
    /// <summary>
    /// Supplied method is called before committing the transaction internally.
    /// It is called inside the inner transaction scope
    /// An exception thrown in this callback will cause the transaction to rollback and be canceled.
    /// The database is locked during the callback, so it is not recommended to do any long-running operations here.
    /// </summary>
    /// <param name="action"></param>
    public void SetCommitCallback(Action<Transaction> action) {
        _transactionData.InnerCallbackBeforeCommitting = () => action(this);
    }
    /// <summary>Creates an empty transaction against a store. <see cref="NodeStore.CreateTransaction"/> does the same.</summary>
    public Transaction(NodeStore store) {
        Store = store;
        _transactionData = new();
    }
    /// <summary>
    /// Creates a transaction that is allowed to write nodes held by the given lock. Without the exemption a write
    /// to a locked node is rejected, so pass the lock id you got from <see cref="NodeStore.RequestLock(Guid, double, double)"/>.
    /// </summary>
    public Transaction(NodeStore store, Guid lockExcemption) {
        Store = store;
        _transactionData = new() {
            LockExcemptions = [lockExcemption]
        };
    }
    /// <summary>Creates a transaction that is allowed to write nodes held by any of the given locks.</summary>
    public Transaction(NodeStore store, IEnumerable<Guid> lockExcemptions) {
        Store = store;
        _transactionData = new() {
            LockExcemptions = lockExcemptions.ToList()
        };
    }
    /// <summary>
    /// By default a transaction blocked by someone else's lock waits and retries a few times before giving up.
    /// Set this to fail immediately instead, which suits interactive code that would rather report a conflict.
    /// </summary>
    public bool NoRetriesIfLocked {
        get => _transactionData.NoRetriesIfLocked;
        set => _transactionData.NoRetriesIfLocked = value;
    }
    /// <summary>
    /// Hint that the inserted nodes are unlikely to be read back soon. They are dropped from the node
    /// cache as soon as they are written to the log, instead of filling it with the whole data set.
    /// </summary>
    public bool BulkInsert {
        get => _transactionData.BulkInsert;
        set => _transactionData.BulkInsert = value;
    }
    /// <summary>Lets this transaction write nodes held by one more lock. The lock must still be active when it is executed.</summary>
    public void AddLockExcemptions(Guid lockId) {
        if (_transactionData.LockExcemptions == null) _transactionData.LockExcemptions = [];
        _transactionData.LockExcemptions.Add(lockId);
    }
    //public Transaction Relate<T, K>(T fromNode, Expression<Func<T, ManyProperty<K>>> expression, K toNode) {
    //    return this;
    //}
    //public Transaction Relate<T, K>(T fromNode, Expression<Func<T, OneProperty<K>>> expression, K toNode) {
    //    return this;
    //}
    // ---------------------------------------------------------------------------------------------------------
    // RELATIONS
    // The relation is named by one of its ends: a relation property on your model, given as a lambda
    // (n => n.Author) or as a raw property id. The direction is worked out from that property, so you always
    // pass "from" as the node the property belongs to.
    //   AddRelation    - adds a link, throws if it already exists
    //   SetRelation    - makes sure the link exists, removing whatever the cardinality does not allow beside it
    //   RemoveRelation - removes a link, throws if it is not there
    //   ClearRelation  - removes a link if it is there, no error if it is not
    //   ClearRelations - removes every link from that node through that property
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Links two node objects. Ids are generated for nodes that do not have one yet.</summary>
    public Transaction AddRelation<T>(T fromNode, Expression<Func<T, object?>> expression, object toNode) {
        if (fromNode == null) throw new Exception("From node cannot be null. ");
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            AddRelation(fromGuid, expression!, toGuid);
        } else if (Store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && Store.Mapper.TryGetIdUInt(toNode, out var toUInt)) {
            AddRelation(fromUint, expression, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    /// <summary>Links two node objects using a raw relation property id, for code that has no compile time model types.</summary>
    public Transaction AddRelation(object fromNode, Guid propertyId, object toNode) {
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            AddRelation(fromGuid, propertyId, toGuid);
        } else if (Store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && Store.Mapper.TryGetIdUInt(toNode, out var toUInt)) {
            AddRelation(fromUint, propertyId, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    /// <summary>Links two nodes given by internal id.</summary>
    public Transaction AddRelation<T>(int idFrom, Expression<Func<T, object?>> expression, int idTo) {
        var p = getRelProp(expression);
        _transactionData.AddRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Links two nodes given by public id.</summary>
    public Transaction AddRelation<T>(Guid idFrom, Expression<Func<T, object?>> expression, Guid idTo) {
        var p = getRelProp(expression!);
        _transactionData.AddRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Links one node to several others through the same property.</summary>
    public Transaction AddRelation<T>(Guid idFrom, Expression<Func<T, object?>> expression, IEnumerable<Guid> idTos) {
        var p = getRelProp(expression);
        foreach (var idTo in idTos) _transactionData.AddRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Links two nodes by public id, using a raw relation property id.</summary>
    public Transaction AddRelation(Guid idFrom, Guid propertyId, Guid idTo) {
        var p = getRelProp(propertyId);
        _transactionData.AddRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Links two nodes by internal id, using a raw relation property id.</summary>
    public Transaction AddRelation(int idFrom, Guid propertyId, int idTo) {
        var p = getRelProp(propertyId);
        _transactionData.AddRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }

    /// <summary>
    /// Relation operations addressed by the relation itself rather than by a property on one of its ends, see
    /// <see cref="TransactionRelation"/>. Use it for symmetric relations and for relations declared as their own
    /// type, where there is no single "from" side: <c>t.Relation.Relate&lt;Friendship&gt;(a, b)</c>.
    /// </summary>
    public TransactionRelation Relation => new TransactionRelation(this);
    //public Transaction Relate<R, TFrom, TTo>(TFrom fromNode, TTo toNode) where R : IRelation<TFrom, TTo> {
    //    return this;
    //}
    //public Transaction Relate<R, TFrom, TTo>(TFrom fromNode, TTo toNode) where R : IRelation<TFrom, TTo> {
    //    return this;
    //}

    //public Transaction Relate<T>(OneOne<T> relation, T fromNode, T toNode) => relate(relation, fromNode, toNode);
    //public Transaction Relate<T>(ManyMany<T> relation, T fromNode, T toNode) => relate(relation, fromNode, toNode);
    //public Transaction Relate<TFrom, TTo>(OneToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode) => relate(relation, fromNode, toNode);
    //public Transaction Relate<TFrom, TTo>(OneToOne<TFrom, TTo> relation, TFrom fromNode, TTo toNode) => relate(relation, fromNode, toNode);
    //public Transaction Relate<TFrom, TTo>(ManyToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode) => relate(relation, fromNode, toNode);

    /// <summary>Makes sure two node objects are linked, replacing any link the cardinality does not allow beside it.</summary>
    public Transaction SetRelation<T>(object fromNode, Expression<Func<T, object?>> expression, object toNode) {
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            SetRelation(fromGuid, expression, toGuid);
        } else if (Store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toUInt)) {
            SetRelation(fromUint, expression, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    /// <summary>Sets the link between two node objects using a raw relation property id.</summary>
    public Transaction SetRelation(object fromNode, Guid propertyId, object toNode) {
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            SetRelation(propertyId, fromGuid, (object)toGuid);
        } else if (Store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toUInt)) {
            SetRelation(fromUint, propertyId, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    /// <summary>Sets the link between two nodes given by internal id.</summary>
    public Transaction SetRelation<T>(int idFrom, Expression<Func<T, object?>> expression, int idTo) {
        var p = getRelProp(expression);
        _transactionData.SetRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Sets the link between two nodes given by public id. Guid.Empty as target clears the relation instead.</summary>
    public Transaction SetRelation<T>(Guid idFrom, Expression<Func<T, object?>> expression, Guid idTo) {
        if (idTo == Guid.Empty) {
            ClearRelations(idFrom, expression);
            return this;
        }
        if (idFrom == Guid.Empty) {
            throw new Exception("Source node id cannot be empty. ");
        }
        var p = getRelProp(expression);
        _transactionData.SetRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Sets a link by public id using a raw relation property id. Guid.Empty as target clears the relation instead.</summary>
    public Transaction SetRelation(Guid idFrom, Guid propertyId, Guid idTo) {
        if (idTo == Guid.Empty) {
            ClearRelations(idFrom, propertyId);
            return this;
        }
        if (idFrom == Guid.Empty) {
            throw new Exception("Source node id cannot be empty. ");
        }
        var p = getRelProp(propertyId);
        _transactionData.SetRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Sets a link by internal id using a raw relation property id. 0 as target clears the relation instead.</summary>
    public Transaction SetRelation(int idFrom, Guid propertyId, int idTo) {
        if (idTo == 0) {
            ClearRelations(idFrom, propertyId);
            return this;
        }
        if (idFrom == 0) {
            throw new Exception("Source node id cannot be 0. ");
        }
        var p = getRelProp(propertyId);
        _transactionData.SetRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Links a node to each of the given node objects. Existing links to other nodes are left in place.</summary>
    public Transaction SetRelation<T>(object fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> toNodes) {
        foreach (var to in toNodes) SetRelation(fromNode, expression, to);
        return this;
    }
    /// <summary>Links a node to each of the given public ids. Existing links to other nodes are left in place.</summary>
    public Transaction SetRelation<T>(object fromNode, Expression<Func<T, object?>> expression, IEnumerable<Guid> toNodeIds) {
        var pId = getRelProp(expression).Id;
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)) {
            foreach (var to in toNodeIds) SetRelation(fromGuid, pId, to);
        } else if (Store.Mapper.TryGetIdUInt(fromNode, out var fromUint)) {
            throw new Exception("Both source and target must currently use same datatype for id. Could be improved later. ");
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    /// <summary>Links a node to each of the given internal ids. Source and targets must use the same kind of id.</summary>
    public Transaction SetRelation<T>(object fromNode, Expression<Func<T, object?>> expression, IEnumerable<int> toNodeIds) {
        var pId = getRelProp(expression).Id;
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)) {
            throw new Exception("Both source and target must currently use same datatype for id. Could be improved later. ");
        } else if (Store.Mapper.TryGetIdUInt(fromNode, out var fromUint)) {
            foreach (var to in toNodeIds) SetRelation(fromUint, pId, to);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }

    /// <summary>Removes the link between two node objects. The transaction fails if there is no such link.</summary>
    public Transaction RemoveRelation<T>(object fromNode, Expression<Func<T, object?>> expression, object toNode) {
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            RemoveRelation(fromGuid, expression, toGuid);
        } else if (Store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toUInt)) {
            RemoveRelation(fromUint, expression, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    /// <summary>Removes the link between two nodes given by internal id.</summary>
    public Transaction RemoveRelation<T>(int idFrom, Expression<Func<T, object?>> expression, int idTo) {
        var p = getRelProp(expression);
        _transactionData.RemoveRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Removes the link between two nodes given by public id.</summary>
    public Transaction RemoveRelation<T>(Guid idFrom, Expression<Func<T, object?>> expression, Guid idTo) {
        var p = getRelProp(expression);
        _transactionData.RemoveRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Removes the links from one node to each of the given targets.</summary>
    public Transaction RemoveRelation<T>(Guid idFrom, Expression<Func<T, object?>> expression, IEnumerable<Guid> idTos) {
        var p = getRelProp(expression);
        foreach (var idTo in idTos) _transactionData.RemoveRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Removes a link by public id, using a raw relation property id.</summary>
    public Transaction RemoveRelation(Guid idFrom, Guid propertyId, Guid idTo) {
        var p = getRelProp(propertyId);
        _transactionData.RemoveRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Removes a link by internal id, using a raw relation property id.</summary>
    public Transaction RemoveRelation(int idFrom, Guid propertyId, int idTo) {
        var p = getRelProp(propertyId);
        _transactionData.RemoveRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }

    /// <summary>Removes the link between two node objects if it exists. Unlike RemoveRelation, a missing link is not an error.</summary>
    public Transaction ClearRelation<T>(object fromNode, Expression<Func<T, object?>> expression, object toNode) {
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            ClearRelation(fromGuid, expression, toGuid);
        } else if (Store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && Store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toUInt)) {
            ClearRelation(fromUint, expression, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    /// <summary>Removes the link between two nodes given by internal id, if it exists.</summary>
    public Transaction ClearRelation<T>(int idFrom, Expression<Func<T, object?>> expression, int idTo) {
        var p = getRelProp(expression);
        _transactionData.ClearRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Removes the link between two nodes given by public id, if it exists.</summary>
    public Transaction ClearRelation<T>(Guid idFrom, Expression<Func<T, object?>> expression, Guid idTo) {
        var p = getRelProp(expression);
        _transactionData.ClearRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Removes a link by internal id if it exists, using a raw relation property id.</summary>
    public Transaction ClearRelation(int idFrom, Guid propertyId, int idTo) {
        var p = getRelProp(propertyId);
        _transactionData.ClearRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Removes a link by public id if it exists, using a raw relation property id.</summary>
    public Transaction ClearRelation(Guid idFrom, Guid propertyId, Guid idTo) {
        var p = getRelProp(propertyId);
        _transactionData.ClearRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    /// <summary>Unlinks everything this node relates to through the given property.</summary>
    public Transaction ClearRelations<T>(object fromNode, Expression<Func<T, object?>> expression) {
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)) {
            ClearRelations(fromGuid, expression);
        } else if (Store.Mapper.TryGetIdUInt(fromNode, out var fromUint)) {
            ClearRelations(fromUint, expression);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    /// <summary>Unlinks everything the node with this internal id relates to through the given property.</summary>
    public Transaction ClearRelations<T>(int idFrom, Expression<Func<T, object?>> expression) {
        var p = getRelProp(expression);
        if (p.FromTargetToSource) {
            _transactionData.ClearRelationsWithTarget(p.RelationId, idFrom);
        } else {
            _transactionData.ClearRelationsWithSource(p.RelationId, idFrom);
        }
        return this;
    }
    /// <summary>Unlinks everything the node with this public id relates to through the given property.</summary>
    public Transaction ClearRelations<T>(Guid idFrom, Expression<Func<T, object?>> expression) {
        var p = getRelProp(expression);
        if (p.FromTargetToSource) {
            _transactionData.ClearRelationsWithTarget(p.RelationId, idFrom);
        } else {
            _transactionData.ClearRelationsWithSource(p.RelationId, idFrom);
        }
        return this;
    }
    /// <summary>Unlinks everything the node relates to through the property, addressed by public id and raw property id.</summary>
    public Transaction ClearRelations(Guid idFrom, Guid propertyId) {
        var p = getRelProp(propertyId);
        if (p.FromTargetToSource) {
            _transactionData.ClearRelationsWithTarget(p.RelationId, idFrom);
        } else {
            _transactionData.ClearRelationsWithSource(p.RelationId, idFrom);
        }
        return this;
    }
    /// <summary>Unlinks everything the node relates to through the property, addressed by internal id and raw property id.</summary>
    public Transaction ClearRelations(int idFrom, Guid propertyId) {
        var p = getRelProp(propertyId);
        if (p.FromTargetToSource) {
            _transactionData.ClearRelationsWithTarget(p.RelationId, idFrom);
        } else {
            _transactionData.ClearRelationsWithSource(p.RelationId, idFrom);
        }
        return this;
    }

    // ---------------------------------------------------------------------------------------------------------
    // SINGLE PROPERTY UPDATES
    // Change one property of a node without reading and writing the whole node, which is cheaper and avoids
    // overwriting changes someone else made to the other properties in the meantime.
    //   UpdateProperty      - an alias for UpdateIfDifferentProperty: the write is skipped if the value is equal
    //   ForceUpdateProperty - writes without comparing first
    //   ResetProperty       - puts the property back to the default value from the data model
    //   AddToProperty       - adds to the current value in one atomic step: numbers add up, text is concatenated
    //                         and arrays get the values appended
    //   MultiplyProperty    - multiplies the current numeric value in one atomic step
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Writes one property of a node object if the value differs from the stored one.</summary>
    public Transaction UpdateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value) => UpdateIfDifferentProperty(node, expression, value);
    /// <summary>Writes one property of the node with this public id if the value differs.</summary>
    public Transaction UpdateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value) => UpdateIfDifferentProperty(nodeId, expression, value);
    /// <summary>Writes one property of the node with this public id if the value differs, for weakly typed values.</summary>
    public Transaction UpdateProperty<T>(Guid nodeId, Expression<Func<T, object>> expression, object value) => UpdateIfDifferentProperty(nodeId, expression, value);
    /// <summary>Writes the same property value on many nodes, skipping those that already have it.</summary>
    public Transaction UpdateProperty<T, V>(IEnumerable<Guid> ids, Expression<Func<T, V>> expression, V value) => UpdateIfDifferentProperty(ids, expression, value);
    /// <summary>Writes one property of the node with this internal id if the value differs.</summary>
    public Transaction UpdateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value) => UpdateIfDifferentProperty(nodeId, expression, value);
    /// <summary>Writes one property addressed by raw property id if the value differs.</summary>
    public Transaction UpdateProperty(Guid nodeId, Guid propertyId, object value) => UpdateIfDifferentProperty(nodeId, propertyId, value);
    /// <summary>Writes one property of the node with this internal id, addressed by raw property id, if the value differs.</summary>
    public Transaction UpdateProperty(int nodeId, Guid propertyId, object value) => UpdateIfDifferentProperty(nodeId, propertyId, value);
    /// <summary>Writes several properties of one node, each given as a property lambda paired with its value.</summary>
    public Transaction UpdateProperties<T>(Guid nodeId, IEnumerable<Tuple<Expression<Func<T, object?>>, object>> propertyValuePairs) => UpdateIfDifferentProperties(nodeId, propertyValuePairs);
    /// <summary>Writes several properties of one node, given as parallel arrays of property lambdas and values.</summary>
    public Transaction UpdateProperties<T>(Guid nodeId, Expression<Func<T, object?>>[] expressions, object[] values) => UpdateIfDifferentProperties(nodeId, expressions, values);

    /// <summary>Sets the display name, the built in label used by the admin UI and by automatic address generation.</summary>
    public Transaction UpdateDisplayName(object node, string value) => UpdateIfDifferentDisplayName(node, value);
    /// <summary>Sets the display name of the node with this public id.</summary>
    public Transaction UpdateDisplayName(Guid nodeId, string value) => UpdateIfDifferentDisplayName(nodeId, value);
    /// <summary>Sets the display name of the node with this internal id.</summary>
    public Transaction UpdateDisplayName(int nodeId, string value) => UpdateIfDifferentDisplayName(nodeId, value);
    /// <summary>Sets the address (URL path) of the node with this public id. The store adjusts it if it is taken.</summary>
    public Transaction UpdateAddress(Guid nodeId, string value) => UpdateIfDifferentAddress(nodeId, value);
    /// <summary>Sets the address of the node with this internal id.</summary>
    public Transaction UpdateAddress(int nodeId, string value) => UpdateIfDifferentAddress(nodeId, value);
    /// <summary>Sets the address of the node addressed by a key holding either kind of id.</summary>
    public Transaction UpdateAddress(NodeKey node, string value) => UpdateIfDifferentAddress(node, value);
    /// <summary>Sets the address of the node this object represents.</summary>
    public Transaction UpdateAddress(object node, string value) => UpdateIfDifferentAddress(node, value);
    /// <summary>Turns automatic address generation on or off. While on, the address follows the display name.</summary>
    public Transaction UpdateAutoAddress(object node, bool value) => UpdateIfDifferentAutoAddress(node, value);
    /// <summary>Turns automatic address generation on or off for the node with this public id.</summary>
    public Transaction UpdateAutoAddress(Guid nodeId, bool value) => UpdateIfDifferentAutoAddress(nodeId, value);
    /// <summary>Turns automatic address generation on or off for the node with this internal id.</summary>
    public Transaction UpdateAutoAddress(int nodeId, bool value) => UpdateIfDifferentAutoAddress(nodeId, value);

    /// <summary>Writes one property of a node object without comparing to the stored value first.</summary>
    public Transaction ForceUpdateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return ForceUpdateProperty(nodeId, expression, value);
        } else if (Store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return ForceUpdateProperty(nodeIdUint, expression, value);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    /// <summary>Writes one property of the node with this public id without comparing first. The value cannot be null.</summary>
    public Transaction ForceUpdateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.ForceUpdateProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Writes one property without comparing first, for weakly typed values.</summary>
    public Transaction ForceUpdateProperty<T>(Guid nodeId, Expression<Func<T, object?>> expression, object value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.ForceUpdateProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Writes the same property value on many nodes without comparing first.</summary>
    public Transaction ForceUpdateProperty<T, V>(IEnumerable<Guid> ids, Expression<Func<T, V>> expression, V value) {
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        if (value == null) throw new Exception("Value cannot be null. ");
        foreach (var id in ids) _transactionData.ForceUpdateProperty(id, propertyId, value);
        return this;
    }
    /// <summary>Writes one property of the node with this internal id without comparing first.</summary>
    public Transaction ForceUpdateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.ForceUpdateProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Writes one property addressed by raw property id without comparing first.</summary>
    public Transaction ForceUpdateProperty(Guid nodeId, Guid propertyId, object value) {
        _transactionData.ForceUpdateProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Writes one property of the node with this internal id, by raw property id, without comparing first.</summary>
    public Transaction ForceUpdateProperty(int nodeId, Guid propertyId, object value) {
        _transactionData.ForceUpdateProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Writes several properties of one node without comparing first, given as property and value pairs.</summary>
    public Transaction ForceUpdateProperties<T>(Guid nodeId, IEnumerable<Tuple<Expression<Func<T, object?>>, object>> propertyValuePairs) {
        var propertyIds = propertyValuePairs.Select(tuple => Store.Mapper.GetProperty(tuple.Item1).Id).ToArray();
        var values = propertyValuePairs.Select(tuple => tuple.Item2).ToArray();
        _transactionData.ForceUpdateProperties(nodeId, propertyIds, values);
        return this;
    }
    /// <summary>Writes several properties of one node without comparing first, given as parallel arrays.</summary>
    public Transaction ForceUpdateProperties<T>(Guid nodeId, Expression<Func<T, object?>>[] expressions, object[] values) {
        var propertyIds = expressions.Select(expression => Store.Mapper.GetProperty(expression).Id).ToArray();
        _transactionData.ForceUpdateProperties(nodeId, propertyIds, values);
        return this;
    }

    /// <summary>Sets the display name of a node object, unless it already has that value.</summary>
    public Transaction UpdateIfDifferentDisplayName(object node, string value) {
        return UpdateIfDifferentProperty(node, NodeConstants.SystemDisplayNamePropertyId, value);
    }
    /// <summary>Sets the display name of the node with this public id, unless it already has that value.</summary>
    public Transaction UpdateIfDifferentDisplayName(Guid nodeId, string value) {
        return UpdateIfDifferentProperty(nodeId, NodeConstants.SystemDisplayNamePropertyId, value);
    }
    /// <summary>Sets the display name of the node with this internal id, unless it already has that value.</summary>
    public Transaction UpdateIfDifferentDisplayName(int nodeId, string value) {
        return UpdateIfDifferentProperty(nodeId, NodeConstants.SystemDisplayNamePropertyId, value);
    }
    /// <summary>Sets the address of a node object, unless it already has that value.</summary>
    public Transaction UpdateIfDifferentAddress(object node, string value) {
        return UpdateIfDifferentProperty(node, NodeConstants.SystemAddressPropertyId, value);
    }
    /// <summary>Sets the address of the node with this public id, unless it already has that value.</summary>
    public Transaction UpdateIfDifferentAddress(Guid nodeId, string value) {
        return UpdateIfDifferentProperty(nodeId, NodeConstants.SystemAddressPropertyId, value);
    }
    /// <summary>Sets the address of the node addressed by a key holding either kind of id.</summary>
    public Transaction UpdateIfDifferentAddress(NodeKey key, string value) {
        if (key.HasGuid) {
            return UpdateIfDifferentProperty(key.Guid, NodeConstants.SystemAddressPropertyId, value);
        } else if (key.HasInt) {
            return UpdateIfDifferentProperty(key.Int, NodeConstants.SystemAddressPropertyId, value);
        } else {
            throw new Exception("Key must have either Guid or int id. ");
        }
    }
    /// <summary>Sets the address of the node with this internal id, unless it already has that value.</summary>
    public Transaction UpdateIfDifferentAddress(int nodeId, string value) {
        return UpdateIfDifferentProperty(nodeId, NodeConstants.SystemAddressPropertyId, value);
    }
    /// <summary>Turns automatic address generation on or off for a node object, unless it is already so.</summary>
    public Transaction UpdateIfDifferentAutoAddress(object node, bool value) {
        return UpdateIfDifferentProperty(node, NodeConstants.SystemAutoAddressPropertyId, value);
    }
    /// <summary>Turns automatic address generation on or off for the node with this public id.</summary>
    public Transaction UpdateIfDifferentAutoAddress(Guid nodeId, bool value) {
        return UpdateIfDifferentProperty(nodeId, NodeConstants.SystemAutoAddressPropertyId, value);
    }
    /// <summary>Turns automatic address generation on or off for the node with this internal id.</summary>
    public Transaction UpdateIfDifferentAutoAddress(int nodeId, bool value) {
        return UpdateIfDifferentProperty(nodeId, NodeConstants.SystemAutoAddressPropertyId, value);
    }


    /// <summary>Writes one property of a node object, skipping the write if the stored value is already equal.</summary>
    public Transaction UpdateIfDifferentProperty<T, V>(T node, Expression<Func<T, V>> expression, V value) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return UpdateIfDifferentProperty(nodeId, expression, value);
        } else if (Store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return UpdateIfDifferentProperty(nodeIdUint, expression, value);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    /// <summary>Writes one property of a node object, addressed by raw property id, if the value differs.</summary>
    public Transaction UpdateIfDifferentProperty(object node, Guid propertyId, object value) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return UpdateIfDifferentProperty(nodeId, propertyId, value);
        } else if (Store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return UpdateIfDifferentProperty(nodeIdUint, propertyId, value);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    /// <summary>Writes one property of the node with this public id if the value differs. The value cannot be null.</summary>
    public Transaction UpdateIfDifferentProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.UpdateIfDifferentProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Writes one property of the node with this internal id if the value differs.</summary>
    public Transaction UpdateIfDifferentProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.UpdateIfDifferentProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Writes one property addressed by public id and raw property id, if the value differs.</summary>
    public Transaction UpdateIfDifferentProperty(Guid nodeId, Guid propertyId, object value) {
        _transactionData.UpdateIfDifferentProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Writes the same property value on many nodes, skipping those that already have it.</summary>
    public Transaction UpdateIfDifferentProperty<T, V>(IEnumerable<Guid> ids, Expression<Func<T, V>> expression, V value) {
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        if (value == null) throw new Exception("Value cannot be null. ");
        foreach (var id in ids) _transactionData.UpdateIfDifferentProperty(id, propertyId, value);
        return this;
    }
    /// <summary>Writes one property addressed by internal id and raw property id, if the value differs.</summary>
    public Transaction UpdateIfDifferentProperty(int nodeId, Guid propertyId, object value) {
        _transactionData.UpdateIfDifferentProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Writes several properties of one node, each given as a property lambda paired with its value.</summary>
    public Transaction UpdateIfDifferentProperties<T>(Guid nodeId, IEnumerable<Tuple<Expression<Func<T, object?>>, object>> propertyValuePairs) {
        var propertyIds = propertyValuePairs.Select(tuple => Store.Mapper.GetProperty(tuple.Item1).Id).ToArray();
        var values = propertyValuePairs.Select(tuple => tuple.Item2).ToArray();
        _transactionData.UpdateIfDifferentProperties(nodeId, propertyIds, values);
        return this;
    }
    /// <summary>Writes several properties of one node, given as parallel arrays of property lambdas and values.</summary>
    public Transaction UpdateIfDifferentProperties<T>(Guid nodeId, Expression<Func<T, object?>>[] expressions, object[] values) {
        var propertyIds = expressions.Select(expression => Store.Mapper.GetProperty(expression).Id).ToArray();
        _transactionData.UpdateIfDifferentProperties(nodeId, propertyIds, values);
        return this;
    }

    /// <summary>Puts a property of a node object back to the default value defined in the data model.</summary>
    public Transaction ResetProperty<T, V>(T node, Expression<Func<T, V>> expression) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return ResetProperty(nodeId, expression);
        } else if (Store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return ResetProperty(nodeIdUint, expression);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    /// <summary>Puts a property of the node with this public id back to its default value.</summary>
    public Transaction ResetProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression) {
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.ResetProperty(nodeId, propertyId);
        return this;
    }
    /// <summary>Puts a property of the node with this internal id back to its default value.</summary>
    public Transaction ResetProperty<T, V>(int nodeId, Expression<Func<T, V>> expression) {
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.ResetProperty(nodeId, propertyId);
        return this;
    }
    /// <summary>Puts a property back to its default value, addressed by public id and raw property id.</summary>
    public Transaction ResetProperty(Guid nodeId, Guid propertyId) {
        _transactionData.ResetProperty(nodeId, propertyId);
        return this;
    }
    /// <summary>Puts a property back to its default value, addressed by internal id and raw property id.</summary>
    public Transaction ResetProperty(int nodeId, Guid propertyId) {
        _transactionData.ResetProperty(nodeId, propertyId);
        return this;
    }
    /// <summary>Adds to the current value of a property on a node object, in one atomic read modify write step.</summary>
    public Transaction AddToProperty<T, V>(T node, Expression<Func<T, V>> expression, object value) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return AddToProperty(nodeId, expression, value);
        } else if (Store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return AddToProperty(nodeIdUint, expression, value);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    /// <summary>Adds to the current value of a property, addressed by public id and raw property id.</summary>
    public Transaction AddToProperty(Guid nodeId, Guid propertyId, object value) {
        _transactionData.AddToProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Adds to the current value of a property, addressed by internal id and raw property id.</summary>
    public Transaction AddToProperty(int nodeId, Guid propertyId, object value) {
        _transactionData.AddToProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Adds to the current value of a property on the node with this public id, for instance to increment a counter.</summary>
    public Transaction AddToProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, object value) {
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.AddToProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Adds to the current value of a property on the node with this internal id.</summary>
    public Transaction AddToProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, object value) {
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.AddToProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Multiplies the current numeric value of a property on a node object, in one atomic step.</summary>
    public Transaction MultiplyProperty<T, V>(T node, Expression<Func<T, V>> expression, object value) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return MultiplyProperty(nodeId, expression, value);
        } else if (Store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return MultiplyProperty(nodeIdUint, expression, value);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    /// <summary>Multiplies the current value of a property on the node with this public id.</summary>
    public Transaction MultiplyProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, object value) {
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.MultiplyProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Multiplies the current value of a property on the node with this internal id.</summary>
    public Transaction MultiplyProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, object value) {
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.MultiplyProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Multiplies the current value of a property, addressed by public id and raw property id.</summary>
    public Transaction MultiplyProperty(Guid nodeId, Guid propertyId, object value) {
        _transactionData.MultiplyProperty(nodeId, propertyId, value);
        return this;
    }
    /// <summary>Multiplies the current value of a property, addressed by internal id and raw property id.</summary>
    public Transaction MultiplyProperty(int nodeId, Guid propertyId, object value) {
        _transactionData.MultiplyProperty(nodeId, propertyId, value);
        return this;
    }

    /// <summary>Changes the node this object represents into the model type T, keeping its id and relations.</summary>
    public Transaction ChangeType<T>(object nodeId) {
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(nodeId, out var id)) {
            return ChangeType<T>(id);
        } else if (Store.Mapper.TryGetIdUInt(nodeId, out var idUint)) {
            return ChangeType<T>(idUint);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    /// <summary>Changes the node with this public id into the model type T.</summary>
    public Transaction ChangeType<T>(Guid nodeId) {
        var nodeTypeId = Store.Mapper.GetNodeTypeId(typeof(T));
        ChangeType(nodeId, nodeTypeId);
        return this;
    }
    /// <summary>Changes the node type, given by its id in the data model. Properties the new type lacks are dropped.</summary>
    public Transaction ChangeType(Guid nodeId, Guid nodeTypeId) {
        _transactionData.ChangeType(nodeId, nodeTypeId);
        return this;
    }
    /// <summary>Changes the node type of the node with this internal id.</summary>
    public Transaction ChangeType(int nodeId, Guid nodeTypeId) {
        _transactionData.ChangeType(nodeId, nodeTypeId);
        return this;
    }

    /// <summary>Rebuilds the search, vector and value indexes for the node with this internal id.</summary>
    public Transaction ReIndex(int nodeId) {
        _transactionData.ReIndex(nodeId);
        return this;
    }
    /// <summary>Rebuilds the search, vector and value indexes for one node, for instance after an analyzer change.</summary>
    public Transaction ReIndex(Guid nodeId) {
        _transactionData.ReIndex(nodeId);
        return this;
    }

    // ---------------------------------------------------------------------------------------------------------
    // VALIDATION
    // A validation is a precondition, not a change: when the transaction runs, the stored value is compared with
    // the one given and the whole transaction is rejected if the requirement does not hold. This gives you
    // optimistic concurrency without a lock, for instance "only book this seat if it is still free".
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Requires a property of a node object to satisfy the given comparison, or the transaction is rejected.</summary>
    public Transaction ValidateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, ValueRequirement requirement = ValueRequirement.Equal) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return ValidateProperty(nodeId, expression, value, requirement);
        } else if (Store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return ValidateProperty(nodeIdUint, expression, value, requirement);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    /// <summary>Requires a property of the node with this public id to satisfy the given comparison.</summary>
    public Transaction ValidateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, ValueRequirement requirement = ValueRequirement.Equal) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.ValidateProperty(nodeId, propertyId, requirement, value);
        return this;
    }
    /// <summary>Requires a property of the node with this internal id to satisfy the given comparison.</summary>
    public Transaction ValidateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, ValueRequirement requirement = ValueRequirement.Equal) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = Store.Mapper.GetProperty(expression).Id;
        _transactionData.ValidateProperty(nodeId, propertyId, requirement, value);
        return this;
    }
    /// <summary>Requires a property to satisfy the given comparison, addressed by public id and raw property id.</summary>
    public Transaction ValidateProperty(Guid nodeId, Guid propertyId, object value, ValueRequirement requirement = ValueRequirement.Equal) {
        _transactionData.ValidateProperty(nodeId, propertyId, requirement, value);
        return this;
    }
    /// <summary>Requires a property to satisfy the given comparison, addressed by internal id and raw property id.</summary>
    public Transaction ValidateProperty(int nodeId, Guid propertyId, object value, ValueRequirement requirement = ValueRequirement.Equal) {
        _transactionData.ValidateProperty(nodeId, propertyId, requirement, value);
        return this;
    }

    // ---------------------------------------------------------------------------------------------------------
    // REVISIONS
    // A node normally holds one version of its content. Enabling revisions turns it into a set of versions, each
    // with a revision id, a RevisionType (Published, Preliminary, Archived, awaiting approval, ...) and a culture.
    // Only the Published revision of a culture is indexed and returned by normal queries, so this is what drives
    // drafts, preview, approval and archive workflows. See NodeStore.GetRevisions to read them back.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Turns a plain node into a revision aware one, its current content becoming the first revision.</summary>
    public Transaction EnableRevisions(Guid nodeId, Guid? newRevisionId = null) {
        _transactionData.EnableRevisions(nodeId, newRevisionId);
        return this;
    }
    /// <summary>Enables revisions for the node with this internal id.</summary>
    public Transaction EnableRevisions(int nodeId, Guid? newRevisionId = null) {
        _transactionData.EnableRevisions(nodeId, newRevisionId);
        return this;
    }
    /// <summary>Enables revisions and reports the id given to the first revision, needed to address it later.</summary>
    public Transaction EnableRevisions(Guid nodeId, out Guid newRevisionId) {
        newRevisionId = Guid.NewGuid();
        _transactionData.EnableRevisions(nodeId, newRevisionId);
        return this;
    }
    /// <summary>Enables revisions for the node with this internal id and reports the id of the first revision.</summary>
    public Transaction EnableRevisions(int nodeId, out Guid newRevisionId) {
        newRevisionId = Guid.NewGuid();
        _transactionData.EnableRevisions(nodeId, newRevisionId);
        return this;
    }
    /// <summary>Collapses a revision aware node back to a plain one, keeping the given revision and dropping the rest.</summary>
    public Transaction DisableRevisions(Guid nodeId, Guid? revisionIdToKeep = null) {
        _transactionData.DisableRevisions(nodeId, revisionIdToKeep);
        return this;
    }
    /// <summary>Collapses the node with this internal id back to a single version.</summary>
    public Transaction DisableRevisions(int nodeId, Guid? revisionIdToKeep = null) {
        _transactionData.DisableRevisions(nodeId, revisionIdToKeep);
        return this;
    }
    /// <summary>Writes meta values, such as author or workflow state, on one specific revision of a node.</summary>
    public Transaction UpdateMeta(Guid nodeId, Guid revisionId, KeyValuePair<string, object>[] metaProperties) {
        _transactionData.UpdateMeta(nodeId, revisionId, metaProperties);
        return this;
    }
    /// <summary>Writes meta values on one revision of the node with this internal id.</summary>
    public Transaction UpdateMeta(int nodeId, Guid revisionId, KeyValuePair<string, object>[] metaProperties) {
        _transactionData.UpdateMeta(nodeId, revisionId, metaProperties);
        return this;
    }
    /// <summary>Writes meta values on the node itself, for nodes that do not use revisions.</summary>
    public Transaction UpdateMeta(Guid nodeId, KeyValuePair<string, object>[] metaProperties) {
        _transactionData.UpdateMeta(nodeId, metaProperties);
        return this;
    }
    /// <summary>Writes meta values on the node with this internal id.</summary>
    public Transaction UpdateMeta(int nodeId, KeyValuePair<string, object>[] metaProperties) {
        _transactionData.UpdateMeta(nodeId, metaProperties);
        return this;
    }
    /// <summary>Writes one named meta value on a specific revision of a node.</summary>
    public Transaction UpdateMeta(Guid id, Guid revisionId, string propertyName, object value) => UpdateMeta(id, revisionId, [new(propertyName, value)]);
    /// <summary>Writes one named meta value on a specific revision of the node with this internal id.</summary>
    public Transaction UpdateMeta(int id, Guid revisionId, string propertyName, object value) => UpdateMeta(id, revisionId, [new(propertyName, value)]);
    /// <summary>Writes one named meta value on the node.</summary>
    public Transaction UpdateMeta(Guid id, string propertyName, object value) => UpdateMeta(id, [new(propertyName, value)]);
    /// <summary>Writes one named meta value on the node with this internal id.</summary>
    public Transaction UpdateMeta(int id, string propertyName, object value) => UpdateMeta(id, [new(propertyName, value)]);

    /// <summary>Deletes one revision of a node, leaving the other revisions and the node itself in place.</summary>
    public Transaction DeleteRevision(Guid nodeId, Guid revisionId) {
        _transactionData.DeleteRevision(nodeId, revisionId);
        return this;
    }
    /// <summary>Deletes one revision of the node with this internal id.</summary>
    public Transaction DeleteRevision(int nodeId, Guid revisionId) {
        _transactionData.DeleteRevision(nodeId, revisionId);
        return this;
    }
    /// <summary>Copies an existing revision into a new one of the given type and culture, for instance a draft from the published version.</summary>
    public Transaction CreateRevision(Guid nodeId, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId = null, Guid? cultureId = null) {
        _transactionData.CreateRevision(nodeId, sourceRevisionId, revisionType, newRevisionId, cultureId);
        return this;
    }
    /// <summary>Creates a new revision of the node with this internal id, copied from an existing revision.</summary>
    public Transaction CreateRevision(int nodeId, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId = null, Guid? cultureId = null) {
        _transactionData.CreateRevision(nodeId, sourceRevisionId, revisionType, newRevisionId, cultureId);
        return this;
    }
    /// <summary>Creates a new revision for a culture given by code, for instance to start a translation.</summary>
    public Transaction CreateRevision(Guid nodeId, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId, string? newCultureCode) {
        _transactionData.CreateRevision(nodeId, sourceRevisionId, revisionType, newRevisionId, newCultureCode);
        return this;
    }
    /// <summary>Creates a new revision for a culture given by code, for the node with this internal id.</summary>
    public Transaction CreateRevision(int nodeId, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId, string? newCultureCode) {
        _transactionData.CreateRevision(nodeId, sourceRevisionId, revisionType, newRevisionId, newCultureCode);
        return this;
    }
    /// <summary>Creates a new revision and reports the id it was given, so you can address it later in the same code.</summary>
    public Transaction CreateRevision(Guid nodeId, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, Guid? cultureId = null) {
        newRevisionId = Guid.NewGuid();
        _transactionData.CreateRevision(nodeId, sourceRevisionId, revisionType, newRevisionId, cultureId);
        return this;
    }
    /// <summary>Creates a new revision of the node with this internal id and reports the id it was given.</summary>
    public Transaction CreateRevision(int nodeId, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, Guid? cultureId = null) {
        newRevisionId = Guid.NewGuid();
        _transactionData.CreateRevision(nodeId, sourceRevisionId, revisionType, newRevisionId, cultureId);
        return this;
    }
    /// <summary>Creates a new revision for a culture given by code and reports the id it was given.</summary>
    public Transaction CreateRevision(Guid nodeId, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, string? newCultureCode) {
        newRevisionId = Guid.NewGuid();
        _transactionData.CreateRevision(nodeId, sourceRevisionId, revisionType, newRevisionId, newCultureCode);
        return this;
    }
    /// <summary>Creates a new revision for a culture code on the node with this internal id, reporting the new id.</summary>
    public Transaction CreateRevision(int nodeId, Guid sourceRevisionId, RevisionType revisionType, out Guid newRevisionId, string? newCultureCode) {
        newRevisionId = Guid.NewGuid();
        _transactionData.CreateRevision(nodeId, sourceRevisionId, revisionType, newRevisionId, newCultureCode);
        return this;
    }

    /// <summary>Moves a revision to another state, which is how content is published, archived or binned.</summary>
    public Transaction ChangeRevisionType(Guid nodeId, Guid revisionId, RevisionType newRevisionType) {
        _transactionData.ChangeRevisionType(nodeId, revisionId, newRevisionType);
        return this;
    }
    /// <summary>Moves a revision of the node with this internal id to another state.</summary>
    public Transaction ChangeRevisionType(int nodeId, Guid revisionId, RevisionType newRevisionType) {
        _transactionData.ChangeRevisionType(nodeId, revisionId, newRevisionType);
        return this;
    }
    /// <summary>Reassigns a revision to another culture, given by culture node id.</summary>
    public Transaction ChangeRevisionCulture(Guid nodeId, Guid revisionId, Guid newCultureId) {
        _transactionData.ChangeRevisionCulture(nodeId, revisionId, newCultureId);
        return this;
    }
    /// <summary>Reassigns a revision of the node with this internal id to another culture.</summary>
    public Transaction ChangeRevisionCulture(int nodeId, Guid revisionId, Guid newCultureId) {
        _transactionData.ChangeRevisionCulture(nodeId, revisionId, newCultureId);
        return this;
    }
    /// <summary>Reassigns a revision to another culture, given by culture code such as "nb-NO".</summary>
    public Transaction ChangeRevisionCulture(Guid nodeId, Guid revisionId, string newCultureCode) {
        _transactionData.ChangeRevisionCulture(nodeId, revisionId, newCultureCode);
        return this;
    }
    /// <summary>Reassigns a revision of the node with this internal id to another culture code.</summary>
    public Transaction ChangeRevisionCulture(int nodeId, Guid revisionId, string newCultureCode) {
        _transactionData.ChangeRevisionCulture(nodeId, revisionId, newCultureCode);
        return this;
    }

    // helpers, to aid directionality of relations:
    int source(int from, RelationPropertyModel p, int to) => p.FromTargetToSource ? to : from;
    int target(int from, RelationPropertyModel p, int to) => p.FromTargetToSource ? from : to;
    Guid source(Guid from, RelationPropertyModel p, Guid to) => p.FromTargetToSource ? to : from;
    Guid target(Guid from, RelationPropertyModel p, Guid to) => p.FromTargetToSource ? from : to;
    RelationPropertyModel getRelProp<T>(Expression<Func<T, object?>> expression) => getRelProp(Store.Mapper.GetProperty(expression).Id);
    RelationPropertyModel getRelProp(Guid propertyId) {
        if (!Store.Datastore.Datamodel.Properties.TryGetValue(propertyId, out var property)) {
            throw new Exception("Property with id " + propertyId + " is not part of the datamodel. ");
        }
        if (property is not RelationPropertyModel relationProperty) throw new Exception("Only relation properties accepted. ");
        return relationProperty;
    }
    // ---------------------------------------------------------------------------------------------------------
    // INSERT
    // Insert is an alias for InsertOrFail, which fails if a node with the same id already exists, while
    // InsertIfNotExists quietly skips those. Unless ignoreRelated is true, node objects hanging off the inserted
    // node's reference properties are inserted as well, if they are not already stored, and the relations are
    // recorded in the same transaction. cultureCode picks which language variant the values belong to, and
    // revisionType which revision is written (Published unless stated).
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Inserts several nodes. Same as <see cref="InsertOrFail(IEnumerable{object}, string?, RevisionType?, bool)"/>.</summary>
    public Transaction Insert(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool ignoreRelated = false) => InsertOrFail(nodes, cultureCode, revisionType, ignoreRelated);
    /// <summary>Inserts one node, failing if its id already exists. An id is generated if the node has none.</summary>
    public Transaction Insert(object node, string? cultureCode = null, RevisionType? revisionType = null, bool ignoreRelated = false) => InsertOrFail(node, out _, cultureCode, revisionType, ignoreRelated);
    /// <summary>Inserts one node and reports the public id it was given, which you can use for relations in the same transaction.</summary>
    public Transaction Insert(object node, out Guid id, string? cultureCode = null, RevisionType? revisionType = null, bool ignoreRelated = false) => InsertOrFail(node, out id, cultureCode, revisionType, ignoreRelated);
    /// <summary>Inserts several nodes, failing if any of their ids already exists.</summary>
    public Transaction InsertOrFail(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool ignoreRelated = false) {
        foreach (var n in nodes) InsertOrFail(n, cultureCode, revisionType, ignoreRelated);
        return this;
    }
    /// <summary>Inserts one node, failing if its id already exists.</summary>
    public Transaction InsertOrFail(object node, string? cultureCode = null, RevisionType? revisionType = null, bool ignoreRelated = false) {
        return InsertOrFail(node, out _, cultureCode, revisionType, ignoreRelated);
    }
    /// <summary>Inserts one node, failing if its id already exists, and reports the public id it was given.</summary>
    public Transaction InsertOrFail(object node, out Guid id, string? cultureCode = null, RevisionType? revisionType = null, bool ignoreRelated = false) {
        return _insertOrFail(node, out id, ignoreRelated, null, cultureCode, revisionType);
    }
    /// <summary>Inserts the nodes that are not already stored and skips the rest. Handy for idempotent seeding.</summary>
    public Transaction InsertIfNotExists(IEnumerable<object> nodes, string? cultureCode = null, RevisionType? revisionType = null, bool ignoreRelated = false) {
        foreach (var n in nodes) InsertIfNotExists(n, cultureCode, revisionType, ignoreRelated);
        return this;
    }
    /// <summary>Inserts the node unless a node with the same id is already stored.</summary>
    public Transaction InsertIfNotExists(object node, string? cultureCode = null, RevisionType? revisionType = null, bool ignoreRelated = false) {
        return InsertIfNotExists(node, out _, cultureCode, revisionType, ignoreRelated);
    }
    /// <summary>Inserts the node unless it is already stored, and reports its public id either way.</summary>
    public Transaction InsertIfNotExists(object node, out Guid id, string? cultureCode = null, RevisionType? revisionType = null, bool ignoreRelated = false) {
        return _insertIfNotExists(node, out id, ignoreRelated, [], cultureCode, revisionType); // when last parameter is not null, it will force InsertIfNotExists
    }
    private Transaction _insertOrFail(object node, out Guid id, bool ignoreRelated, Dictionary<object, Guid>? inserted, string? cultureCode, RevisionType? revisionType) {
        return _insert(node, out id, ignoreRelated, inserted, false, cultureCode, revisionType);
    }
    private Transaction _insertIfNotExists(object node, out Guid id, bool ignoreRelated, Dictionary<object, Guid>? inserted, string? cultureCode, RevisionType? revisionType) {
        return _insert(node, out id, ignoreRelated, inserted, true, cultureCode, revisionType);
    }
    private Transaction _insert(object node, out Guid id, bool ignoreRelated, Dictionary<object, Guid>? inserted, bool insertIfNotExists, string? cultureCode, RevisionType? revisionType) {
        // // when last parameter (inserted) is not null, it will force InsertIfNotExists
        if (inserted != null && inserted.TryGetValue(node, out id)) return this;

        var related = ignoreRelated ? null : new RelatedCollection();
        Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out _);
        var nodeData = Store.Mapper.CreateNodeDataFromObject(node, related, null);
        id = nodeData.Id;
        if (inserted == null) { // root node, insert or fail depending on insertIfNotExists flag
            if (insertIfNotExists) {
                _transactionData.InsertIfNotExists(nodeData, cultureCode, revisionType);
            } else {
                _transactionData.InsertOrFail(nodeData, cultureCode, revisionType);
            }
        } else {  // any child node is InsertIfNotExists as children are always only added if new
            _transactionData.InsertIfNotExists(nodeData, cultureCode, revisionType);
        }
        if (related == null) return this; // means ignoreRelated was true or no related found
        inserted ??= [];
        inserted.Add(node, id);
        foreach (var single in related.Singles) {
            _insertOrFail(single.To, out var idTo, ignoreRelated, inserted, cultureCode, revisionType);
            SetRelation(id, single.PropertyId, idTo);
        }
        foreach (var multiple in related.Multiples) {
            foreach (var to in multiple.Tos) {
                _insertOrFail(to, out var idTo, ignoreRelated, inserted, cultureCode, revisionType);
                SetRelation(id, multiple.PropertyId, idTo);
            }
        }
        return this;
    }

    // ---------------------------------------------------------------------------------------------------------
    // UPSERT AND UPDATE
    // Upsert inserts the node if its id is unknown and updates it otherwise. Update requires the node to exist.
    // Both write all properties of the node object you hand in and leave its relations untouched. The Force
    // variants skip the comparison with the stored node: cheaper when you know it changed, wasteful when it did not.
    // Related node objects are ignored here, unlike on insert.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Inserts or overwrites several nodes without comparing them to the stored versions first.</summary>
    public Transaction ForceUpsert(IEnumerable<object> nodes) {
        foreach (var n in nodes) ForceUpsert(n);
        return this;
    }
    /// <summary>Inserts or overwrites a node without comparing it to the stored version first.</summary>
    public Transaction ForceUpsert(object node) {
        Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out _);
        _transactionData.ForceUpsert(Store.Mapper.CreateNodeDataFromObject(node, null, null));
        return this;
    }
    /// <summary>Inserts or updates several nodes, skipping the ones that have not changed.</summary>
    public Transaction Upsert(IEnumerable<object> nodes) {
        foreach (var n in nodes) Upsert(n);
        return this;
    }
    /// <summary>Inserts the node, or updates it if its id is already known. Unchanged nodes are not rewritten.</summary>
    public Transaction Upsert(object node) {
        Store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out _);
        _transactionData.Upsert(Store.Mapper.CreateNodeDataFromObject(node, null, null));
        return this;
    }

    /// <summary>Writes back all properties of the node. Fails if it no longer exists.</summary>
    public Transaction Update(object node) => UpdateOrFail(node);
    /// <summary>Writes back all properties of several nodes. Fails if any of them no longer exists.</summary>
    public Transaction Update(IEnumerable node) => UpdateOrFail(node);
    /// <summary>Writes back the node, failing if it no longer exists. Nothing is written if nothing changed.</summary>
    public Transaction UpdateOrFail(object node) {
        _transactionData.UpdateOrFail(Store.Mapper.CreateNodeDataFromObject(node, null, null));
        return this;
    }
    /// <summary>Writes back the node, or does nothing if it has been deleted in the meantime.</summary>
    public Transaction UpdateIfExists(object node) {
        _transactionData.UpdateIfExists(Store.Mapper.CreateNodeDataFromObject(node, null, null));
        return this;
    }
    /// <summary>Writes the node without checking whether anything actually changed.</summary>
    public Transaction ForceUpdate(object node) {
        _transactionData.ForceUpdateNode(Store.Mapper.CreateNodeDataFromObject(node, null, null));
        return this;
    }
    /// <summary>Writes back several nodes, failing if any of them no longer exists.</summary>
    public Transaction UpdateOrFail(IEnumerable node) {
        foreach (var n in node) UpdateOrFail(n);
        return this;
    }
    /// <summary>Writes back the nodes that still exist and silently skips the rest.</summary>
    public Transaction UpdateIfExists(IEnumerable node) {
        foreach (var n in node) UpdateIfExists(n);
        return this;
    }
    /// <summary>Writes several nodes without comparing them to the stored versions first.</summary>
    public Transaction ForceUpdate(IEnumerable node) {
        foreach (var n in node) ForceUpdate(n);
        return this;
    }

    // ---------------------------------------------------------------------------------------------------------
    // DELETE
    // Delete is an alias for DeleteOrFail, which fails if the node is not there, while DeleteIfExists ignores it.
    // Deleting a node also removes every relation pointing to or from it, as part of the same transaction.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Deletes the node with this public id, failing if it does not exist.</summary>
    public Transaction DeleteOrFail(Guid nodeGuid) {
        _transactionData.DeleteOrFail(nodeGuid);
        return this;
    }
    /// <summary>Deletes these nodes, failing if any of them does not exist.</summary>
    public Transaction DeleteOrFail(IEnumerable<Guid> nodeGuids) {
        foreach (var g in nodeGuids) DeleteOrFail(g);
        return this;
    }
    /// <summary>Deletes the node with this internal id, failing if it does not exist.</summary>
    public Transaction DeleteOrFail(int id) {
        _transactionData.DeleteOrFail(id);
        return this;
    }
    /// <summary>Deletes these nodes by internal id, failing if any of them does not exist.</summary>
    public Transaction DeleteOrFail(IEnumerable<int> ids) {
        foreach (var id in ids) DeleteOrFail(id);
        return this;
    }
    /// <summary>Deletes the node if it is there, otherwise does nothing.</summary>
    public Transaction DeleteIfExists(Guid nodeGuid) {
        _transactionData.DeleteIfExists(nodeGuid);
        return this;
    }
    /// <summary>Deletes the nodes that exist and ignores the ids that do not.</summary>
    public Transaction DeleteIfExists(IEnumerable<Guid> nodeGuids) {
        foreach (var g in nodeGuids) DeleteIfExists(g);
        return this;
    }
    /// <summary>Deletes the nodes that exist and ignores the internal ids that do not.</summary>
    public Transaction DeleteIfExists(IEnumerable<int> ids) {
        foreach (var id in ids) DeleteIfExists(id);
        return this;
    }
    /// <summary>Deletes the node with this internal id if it is there, otherwise does nothing.</summary>
    public Transaction DeleteIfExists(int id) {
        _transactionData.DeleteIfExists(id);
        return this;
    }
    /// <summary>Deletes the node with this internal id. Fails if it does not exist.</summary>
    public Transaction Delete(int id) => DeleteOrFail(id);
    /// <summary>Deletes the node with this public id. Fails if it does not exist.</summary>
    public Transaction Delete(Guid id) => DeleteOrFail(id);
    /// <summary>Deletes all these nodes. Fails if any of them does not exist.</summary>
    public Transaction Delete(IEnumerable<Guid> nodeGuids) {
        foreach (var g in nodeGuids) DeleteOrFail(g);
        return this;
    }
    /// <summary>Deletes all these nodes by internal id. Fails if any of them does not exist.</summary>
    public Transaction Delete(IEnumerable<int> ids) {
        foreach (var id in ids) DeleteOrFail(id);
        return this;
    }

    /// <summary>
    /// How many operations are queued. Zero means executing does nothing. Note that one call can add more than one
    /// operation, for instance an insert that also stores related nodes.
    /// </summary>
    public int Count => _transactionData.Actions.Count;
}
