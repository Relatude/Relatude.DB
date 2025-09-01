using System;
using System.Collections;
using System.Linq.Expressions;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Transactions;
namespace Relatude.DB.Nodes;
public sealed partial class Transaction {
    internal TransactionData _transactionData;
    readonly NodeStore _store;
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
    public Transaction(NodeStore store) {
        _store = store;
        _transactionData = new();
    }
    public Transaction(NodeStore store, Guid lockExcemption) {
        _store = store;
        _transactionData = new() {
            LockExcemptions = [lockExcemption]
        };
    }
    public Transaction(NodeStore store, IEnumerable<Guid> lockExcemptions) {
        _store = store;
        _transactionData = new() {
            LockExcemptions = lockExcemptions.ToList()
        };
    }
    public void AddLockExcemptions(Guid lockId) {
        if (_transactionData.LockExcemptions == null) _transactionData.LockExcemptions = [];
        _transactionData.LockExcemptions.Add(lockId);
    }
    public Transaction Relate<T>(T fromNode, Expression<Func<T, object>> expression, object toNode) {
        if (fromNode == null) throw new Exception("From node cannot be null. ");
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            Relate(fromGuid, expression, toGuid);
        } else if (_store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && _store.Mapper.TryGetIdUInt(toNode, out var toUInt)) {
            Relate(fromUint, expression, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction Relate(object fromNode, Guid propertyId, object toNode) {
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            Relate(fromGuid, propertyId, toGuid);
        } else if (_store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && _store.Mapper.TryGetIdUInt(toNode, out var toUInt)) {
            Relate(fromUint, propertyId, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction Relate<T>(int idFrom, Expression<Func<T, object>> expression, int idTo) {
        var p = getRelProp(expression);
        _transactionData.AddRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction Relate<T>(Guid idFrom, Expression<Func<T, object>> expression, Guid idTo) {
        var p = getRelProp(expression);
        _transactionData.AddRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction Relate<T>(Guid idFrom, Expression<Func<T, object>> expression, IEnumerable<Guid> idTos) {
        var p = getRelProp(expression);
        foreach (var idTo in idTos) _transactionData.AddRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction Relate(Guid idFrom, Guid propertyId, Guid idTo) {
        var p = getRelProp(propertyId);
        _transactionData.AddRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }


    Transaction relate<R>(R relation, object? fromNode, object? toNode) {
        var relationId = _store.Mapper.GetRelationId<R>();
        var fromGuid = _store.Mapper.GetIdGuidOrCreate(fromNode);
        var toGuid = _store.Mapper.GetIdGuidOrCreate(toNode);
        _transactionData.AddRelation(relationId, fromGuid, toGuid);
        return this;
    }
    public Transaction Relate<T>(OneOne<T> relation, T fromNode, T toNode) => relate(relation, fromNode, toNode);
    public Transaction Relate<T, TRelationSelfReference>(OneOne<T, TRelationSelfReference> relation, T fromNode, T toNode)
        where TRelationSelfReference : OneOne<T, TRelationSelfReference> => relate(relation, fromNode, toNode);
    public Transaction Relate<T>(ManyMany<T> relation, T fromNode, T toNode) => relate(relation, fromNode, toNode);
    public Transaction Relate<T, TRelationSelfReference>(ManyMany<T, TRelationSelfReference> relation, T fromNode, T toNode)
        where TRelationSelfReference : ManyMany<T, TRelationSelfReference> => relate(relation, fromNode, toNode);
    public Transaction Relate<TFrom, TTo>(OneToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode) => relate(relation, fromNode, toNode);
    public Transaction Relate<TFrom, TTo, TRelationSelfReference>(OneToMany<TFrom, TTo, TRelationSelfReference> relation, TFrom fromNode, TTo toNode)
        where TRelationSelfReference : OneToMany<TFrom, TTo, TRelationSelfReference> => relate(relation, fromNode, toNode);
    public Transaction Relate<TFrom, TTo>(OneToOne<TFrom, TTo> relation, TFrom fromNode, TTo toNode) => relate(relation, fromNode, toNode);
    public Transaction Relate<TFrom, TTo, TRelationSelfReference>(OneToOne<TFrom, TTo, TRelationSelfReference> relation, TFrom fromNode, TTo toNode)
        where TRelationSelfReference : OneToOne<TFrom, TTo, TRelationSelfReference> => relate(relation, fromNode, toNode);
    public Transaction Relate<TFrom, TTo>(ManyToMany<TFrom, TTo> relation, TFrom fromNode, TTo toNode) => relate(relation, fromNode, toNode);
    public Transaction Relate<TFrom, TTo, TRelationSelfReference>(ManyToMany<TFrom, TTo, TRelationSelfReference> relation, TFrom fromNode, TTo toNode)
        where TRelationSelfReference : ManyToMany<TFrom, TTo, TRelationSelfReference> => relate(relation, fromNode, toNode);

    public Transaction SetRelation<T>(object fromNode, Expression<Func<T, object>> expression, object toNode) {
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            SetRelation(expression, fromGuid, toGuid);
        } else if (_store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toUInt)) {
            SetRelation(fromUint, expression, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction SetRelation(object fromNode, Guid propertyId, object toNode) {
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            SetRelation(propertyId, fromGuid, (object)toGuid);
        } else if (_store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toUInt)) {
            SetRelation(fromUint, propertyId, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction SetRelation<T>(int idFrom, Expression<Func<T, object>> expression, int idTo) {
        var p = getRelProp(expression);
        _transactionData.SetRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction SetRelation<T>(Guid idFrom, Expression<Func<T, object>> expression, Guid idTo) {
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
    public Transaction SetRelation<T>(object fromNode, Expression<Func<T, object>> expression, IEnumerable<object> toNodes) {
        foreach (var to in toNodes) SetRelation(fromNode, expression, to);
        return this;
    }
    public Transaction SetRelation<T>(object fromNode, Expression<Func<T, object>> expression, IEnumerable<Guid> toNodeIds) {
        var pId = getRelProp(expression).Id;
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)) {
            foreach (var to in toNodeIds) SetRelation(fromGuid, pId, to);
        } else if (_store.Mapper.TryGetIdUInt(fromNode, out var fromUint)) {
            throw new Exception("Both source and target must currently use same datatype for id. Could be improved later. ");
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction SetRelation<T>(object fromNode, Expression<Func<T, object>> expression, IEnumerable<int> toNodeIds) {
        var pId = getRelProp(expression).Id;
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)) {
            throw new Exception("Both source and target must currently use same datatype for id. Could be improved later. ");
        } else if (_store.Mapper.TryGetIdUInt(fromNode, out var fromUint)) {
            foreach (var to in toNodeIds) SetRelation(fromUint, pId, to);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }

    public Transaction UnRelate<T>(object fromNode, Expression<Func<T, object>> expression, object toNode) {
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            UnRelate(fromGuid, expression, toGuid);
        } else if (_store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toUInt)) {
            UnRelate(fromUint, expression, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction UnRelate<T>(int idFrom, Expression<Func<T, object>> expression, int idTo) {
        var p = getRelProp(expression);
        _transactionData.RemoveRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction UnRelate<T>(Guid idFrom, Expression<Func<T, object>> expression, Guid idTo) {
        var p = getRelProp(expression);
        _transactionData.RemoveRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction UnRelate<T>(Guid idFrom, Expression<Func<T, object>> expression, IEnumerable<Guid> idTos) {
        var p = getRelProp(expression);
        foreach (var idTo in idTos) _transactionData.RemoveRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction UnRelate(Guid idFrom, Guid propertyId, Guid idTo) {
        var p = getRelProp(propertyId);
        _transactionData.RemoveRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }

    public Transaction ClearRelation<T>(object fromNode, Expression<Func<T, object>> expression, object toNode) {
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)
            && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toGuid)) {
            ClearRelation(fromGuid, expression, toGuid);
        } else if (_store.Mapper.TryGetIdUInt(fromNode, out var fromUint)
              && _store.Mapper.TryGetIdGuidAndCreateIfPossible(toNode, out var toUInt)) {
            ClearRelation(fromUint, expression, toUInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction ClearRelation<T>(int idFrom, Expression<Func<T, object>> expression, int idTo) {
        var p = getRelProp(expression);
        _transactionData.ClearRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction ClearRelation<T>(Guid idFrom, Expression<Func<T, object>> expression, Guid idTo) {
        var p = getRelProp(expression);
        _transactionData.ClearRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction ClearRelation(Guid idFrom, Guid propertyId, Guid idTo) {
        var p = getRelProp(propertyId);
        _transactionData.ClearRelation(p.RelationId, source(idFrom, p, idTo), target(idFrom, p, idTo));
        return this;
    }
    public Transaction ClearRelations<T>(object fromNode, Expression<Func<T, object>> expression) {
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out var fromGuid)) {
            ClearRelations(fromGuid, expression);
        } else if (_store.Mapper.TryGetIdUInt(fromNode, out var fromUint)) {
            ClearRelations(fromUint, expression);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction ClearRelations<T>(int idFrom, Expression<Func<T, object>> expression) {
        var p = getRelProp(expression);
        if (p.FromTargetToSource) {
            _transactionData.ClearRelationsWithTarget(p.RelationId, idFrom);
        } else {
            _transactionData.ClearRelationsWithSource(p.RelationId, idFrom);
        }
        return this;
    }
    public Transaction ClearRelations<T>(Guid idFrom, Expression<Func<T, object>> expression) {
        var p = getRelProp(expression);
        if (p.FromTargetToSource) {
            _transactionData.ClearRelationsWithTarget(p.RelationId, idFrom);
        } else {
            _transactionData.ClearRelationsWithSource(p.RelationId, idFrom);
        }
        return this;
    }
    public Transaction ClearRelations(Guid idFrom, Guid propertyId) {
        var p = getRelProp(propertyId);
        if (p.FromTargetToSource) {
            _transactionData.ClearRelationsWithTarget(p.RelationId, idFrom);
        } else {
            _transactionData.ClearRelationsWithSource(p.RelationId, idFrom);
        }
        return this;
    }
    public Transaction ClearRelations(int idFrom, Guid propertyId) {
        var p = getRelProp(propertyId);
        if (p.FromTargetToSource) {
            _transactionData.ClearRelationsWithTarget(p.RelationId, idFrom);
        } else {
            _transactionData.ClearRelationsWithSource(p.RelationId, idFrom);
        }
        return this;
    }
    public Transaction UpdateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return UpdateProperty(nodeId, expression, value);
        } else if (_store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return UpdateProperty(nodeIdUint, expression, value);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    public Transaction UpdateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.UpdateProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction UpdateProperty<T>(Guid nodeId, Expression<Func<T, object>> expression, object value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.UpdateProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction UpdateProperties<T>(Guid nodeId, IEnumerable<Tuple<Expression<Func<T, object>>, object>> propertyValuePairs) {
        var propertyIds = propertyValuePairs.Select(tuple => _store.Mapper.GetProperty(tuple.Item1).Id).ToArray();
        var values = propertyValuePairs.Select(tuple => tuple.Item2).ToArray();
        _transactionData.UpdateProperties(nodeId, propertyIds, values);
        return this;
    }
    public Transaction UpdateProperties<T>(Guid nodeId, Expression<Func<T, object>>[] expressions, object[] values) {
        var propertyIds = expressions.Select(expression => _store.Mapper.GetProperty(expression).Id).ToArray();
        _transactionData.UpdateProperties(nodeId, propertyIds, values);
        return this;
    }
    public Transaction UpdateProperty<T, V>(IEnumerable<Guid> ids, Expression<Func<T, V>> expression, V value) {
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        if (value == null) throw new Exception("Value cannot be null. ");
        foreach (var id in ids) _transactionData.UpdateProperty(id, propertyId, value);
        return this;
    }
    public Transaction UpdateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.UpdateProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction UpdateProperty(Guid nodeId, Guid propertyId, object value) {
        _transactionData.UpdateProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction UpdateProperty(int nodeId, Guid propertyId, object value) {
        _transactionData.UpdateProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction UpdateIfDifferentProperty<T, V>(T node, Expression<Func<T, V>> expression, V value) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return UpdateIfDifferentProperty(nodeId, expression, value);
        } else if (_store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return UpdateIfDifferentProperty(nodeIdUint, expression, value);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    public Transaction UpdateIfDifferentProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.UpdateIfDifferentProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction UpdateIfDifferentProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.UpdateIfDifferentProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction UpdateIfDifferentProperty(Guid nodeId, Guid propertyId, object value) {
        _transactionData.UpdateIfDifferentProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction UpdateIfDifferentProperty(int nodeId, Guid propertyId, object value) {
        _transactionData.UpdateIfDifferentProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction ResetProperty<T, V>(T node, Expression<Func<T, V>> expression) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return ResetProperty(nodeId, expression);
        } else if (_store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return ResetProperty(nodeIdUint, expression);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    public Transaction ResetProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression) {
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.ResetProperty(nodeId, propertyId);
        return this;
    }
    public Transaction ResetProperty<T, V>(int nodeId, Expression<Func<T, V>> expression) {
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.ResetProperty(nodeId, propertyId);
        return this;
    }
    public Transaction ResetProperty(Guid nodeId, Guid propertyId) {
        _transactionData.ResetProperty(nodeId, propertyId);
        return this;
    }
    public Transaction ResetProperty(int nodeId, Guid propertyId) {
        _transactionData.ResetProperty(nodeId, propertyId);
        return this;
    }
    public Transaction AddToProperty<T, V>(T node, Expression<Func<T, V>> expression, object value) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return AddToProperty(nodeId, expression, value);
        } else if (_store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return AddToProperty(nodeIdUint, expression, value);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    public Transaction AddToProperty(Guid nodeId, Guid propertyId, object value) {
        _transactionData.AddToProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction AddToProperty(int nodeId, Guid propertyId, object value) {
        _transactionData.AddToProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction AddToProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, object value) {
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.AddToProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction AddToProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, object value) {
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.AddToProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction MultiplyProperty<T, V>(T node, Expression<Func<T, V>> expression, object value) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return MultiplyProperty(nodeId, expression, value);
        } else if (_store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return MultiplyProperty(nodeIdUint, expression, value);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    public Transaction MultiplyProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, object value) {
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.MultiplyProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction MultiplyProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, object value) {
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.MultiplyProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction MultiplyProperty(Guid nodeId, Guid propertyId, object value) {
        _transactionData.MultiplyProperty(nodeId, propertyId, value);
        return this;
    }
    public Transaction MultiplyProperty(int nodeId, Guid propertyId, object value) {
        _transactionData.MultiplyProperty(nodeId, propertyId, value);
        return this;
    }

    public Transaction ChangeType<T>(object nodeId) {
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(nodeId, out var id)) {
            return ChangeType<T>(id);
        } else if (_store.Mapper.TryGetIdUInt(nodeId, out var idUint)) {
            return ChangeType<T>(idUint);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    public Transaction ChangeType<T>(Guid nodeId) {
        var nodeTypeId = _store.Mapper.GetNodeTypeId(typeof(T));
        ChangeType(nodeId, nodeTypeId);
        return this;
    }
    public Transaction ChangeType(Guid nodeId, Guid nodeTypeId) {
        _transactionData.ChangeType(nodeId, nodeTypeId);
        return this;
    }
    public Transaction ChangeType(int nodeId, Guid nodeTypeId) {
        _transactionData.ChangeType(nodeId, nodeTypeId);
        return this;
    }

    public Transaction ReIndex(int nodeId) {
        _transactionData.ReIndex(nodeId);
        return this;
    }
    public Transaction ReIndex(Guid nodeId) {
        _transactionData.ReIndex(nodeId);
        return this;
    }

    public Transaction ValidateProperty<T, V>(T node, Expression<Func<T, V>> expression, V value, ValueRequirement requirement = ValueRequirement.Equal) {
        if (node == null) throw new Exception("Node cannot be null. ");
        if (_store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out var nodeId)) {
            return ValidateProperty(nodeId, expression, value, requirement);
        } else if (_store.Mapper.TryGetIdUInt(node, out var nodeIdUint)) {
            return ValidateProperty(nodeIdUint, expression, value, requirement);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
    }
    public Transaction ValidateProperty<T, V>(Guid nodeId, Expression<Func<T, V>> expression, V value, ValueRequirement requirement = ValueRequirement.Equal) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.ValidateProperty(nodeId, propertyId, requirement, value);
        return this;
    }
    public Transaction ValidateProperty<T, V>(int nodeId, Expression<Func<T, V>> expression, V value, ValueRequirement requirement = ValueRequirement.Equal) {
        if (value == null) throw new Exception("Value cannot be null. ");
        var propertyId = _store.Mapper.GetProperty(expression).Id;
        _transactionData.ValidateProperty(nodeId, propertyId, requirement, value);
        return this;
    }
    public Transaction ValidateProperty(Guid nodeId, Guid propertyId, object value, ValueRequirement requirement = ValueRequirement.Equal) {
        _transactionData.ValidateProperty(nodeId, propertyId, requirement, value);
        return this;
    }
    public Transaction ValidateProperty(int nodeId, Guid propertyId, object value, ValueRequirement requirement = ValueRequirement.Equal) {
        _transactionData.ValidateProperty(nodeId, propertyId, requirement, value);
        return this;
    }

    // helpers:
    int source(int from, RelationPropertyModel p, int to) => p.FromTargetToSource ? to : from;
    int target(int from, RelationPropertyModel p, int to) => p.FromTargetToSource ? from : to;
    Guid source(Guid from, RelationPropertyModel p, Guid to) => p.FromTargetToSource ? to : from;
    Guid target(Guid from, RelationPropertyModel p, Guid to) => p.FromTargetToSource ? from : to;
    RelationPropertyModel getRelProp<T>(Expression<Func<T, object>> expression) => getRelProp(_store.Mapper.GetProperty(expression).Id);
    RelationPropertyModel getRelProp(Guid propertyId) {
        if (!_store.Datastore.Datamodel.Properties.TryGetValue(propertyId, out var property)) {
            throw new Exception("Property with id " + propertyId + " is not part of the datamodel. ");
        }
        if (property is not RelationPropertyModel relationProperty) throw new Exception("Only relation properties accepted. ");
        return relationProperty;
    }

    public Transaction Insert(IEnumerable nodes, bool ignoreRelated = false) {
        foreach (var n in nodes) Insert(n, ignoreRelated);
        return this;
    }
    public Transaction Insert(object node, bool ignoreRelated = false) {
        return Insert(node, out _, ignoreRelated);
    }
    public Transaction Insert(object node, out Guid id, bool ignoreRelated = false) {
        return _insert(node, out id, ignoreRelated);
    }
    private Transaction _insert(object node, out Guid id, bool ignoreRelated, Dictionary<object, Guid>? inserted = null) {
        // // when last parameter (inserted) is not null, it will force InsertIfNotExists
        if (inserted != null && inserted.TryGetValue(node, out id)) return this;

        var related = ignoreRelated ? null : new RelatedCollection();
        _store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out _);
        var nodeData = _store.Mapper.CreateNodeDataFromObject(node, related);
        id = nodeData.Id;
        if (inserted == null) _transactionData.InsertNode(nodeData); // force add on first insert, causing exception if node already exists
        else _transactionData.InsertIfNotExists(nodeData); // only add child if new, if they do exists do not add or update, just traverse relations
        if (related == null) return this;
        inserted ??= [];
        inserted.Add(node, id);
        foreach (var single in related.Singles) {
            _insert(single.To, out var idTo, ignoreRelated, inserted);
            SetRelation(id, single.PropertyId, idTo);
        }
        foreach (var multiple in related.Multiples) {
            foreach (var to in multiple.Tos) {
                _insert(to, out var idTo, ignoreRelated, inserted);
                SetRelation(id, multiple.PropertyId, idTo);
            }
        }
        return this;
    }

    public Transaction InsertIfNotExists(IEnumerable nodes, bool ignoreRelated = false) {
        foreach (var n in nodes) InsertIfNotExists(n, ignoreRelated);
        return this;
    }
    public Transaction InsertIfNotExists(object node, bool ignoreRelated = false) {
        return InsertIfNotExists(node, out _, ignoreRelated);
    }
    public Transaction InsertIfNotExists(object node, out Guid id, bool ignoreRelated = false) {
        return _insert(node, out id, ignoreRelated, []); // when last parameter is not null, it will force InsertIfNotExists
    }


    public Transaction ForceUpsert(object node) {
        _store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out _);
        _transactionData.ForceUpsertNode(_store.Mapper.CreateNodeDataFromObject(node, null));
        return this;
    }
    public Transaction Upsert(object node) {
        _store.Mapper.TryGetIdGuidAndCreateIfPossible(node, out _);
        _transactionData.UpsertNode(_store.Mapper.CreateNodeDataFromObject(node, null));
        return this;
    }
    public Transaction ForceUpdate(object node) {
        _transactionData.ForceUpdateNode(_store.Mapper.CreateNodeDataFromObject(node, null));
        return this;
    }
    public Transaction Update(object node) {
        _transactionData.UpdateNode(_store.Mapper.CreateNodeDataFromObject(node, null));
        return this;
    }
    public Transaction DeleteOrFail(Guid nodeGuid) {
        _transactionData.DeleteNodeOrFail(nodeGuid);
        return this;
    }
    public Transaction DeleteOrFail(int id) {
        _transactionData.DeleteNodeOrFail(id);
        return this;
    }
    public Transaction Delete(Guid nodeGuid) {
        _transactionData.DeleteNode(nodeGuid);
        return this;
    }
    public Transaction Delete(int id) {
        _transactionData.DeleteNode(id);
        return this;
    }

    public int Count => _transactionData.Actions.Count;
}
