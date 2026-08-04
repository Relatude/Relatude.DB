using Relatude.DB.Datamodels.Properties;
using System.Linq.Expressions;
namespace Relatude.DB.Nodes;

// Reordering of relation lists. All operations reorder the ordered list of items related to the from node.
// Multi item moves behave like list UIs: the selection keeps its internal order and compacts against the
// ends of the list. Positions are clamped, moving past the top or bottom never throws.
public partial class Transaction {

    // MoveRelation: moves items by offset places, negative = towards the top, positive = towards the bottom
    public Transaction MoveRelation<T>(T fromNode, Expression<Func<T, object?>> expression, object item, int offset) => MoveRelation(fromNode, expression, [item], offset);
    public Transaction MoveRelation<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items, int offset) {
        if (fromNode == null) throw new Exception("From node cannot be null. ");
        if (tryGetGuids(fromNode, items, out var fromGuid, out var itemGuids)) {
            MoveRelation(fromGuid, expression, itemGuids, offset);
        } else if (tryGetInts(fromNode, items, out var fromInt, out var itemInts)) {
            MoveRelation(fromInt, expression, itemInts, offset);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction MoveRelation<T>(Guid idFrom, Expression<Func<T, object?>> expression, Guid item, int offset) => MoveRelation(idFrom, expression, [item], offset);
    public Transaction MoveRelation<T>(Guid idFrom, Expression<Func<T, object?>> expression, IEnumerable<Guid> items, int offset) {
        var p = getRelProp(expression);
        _transactionData.MoveRelation(p.RelationId, idFrom, items.ToArray(), offset, p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelation<T>(int idFrom, Expression<Func<T, object?>> expression, int item, int offset) => MoveRelation(idFrom, expression, [item], offset);
    public Transaction MoveRelation<T>(int idFrom, Expression<Func<T, object?>> expression, IEnumerable<int> items, int offset) {
        var p = getRelProp(expression);
        _transactionData.MoveRelation(p.RelationId, idFrom, items.ToArray(), offset, p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelation(Guid idFrom, Guid propertyId, Guid item, int offset) => MoveRelation(idFrom, propertyId, [item], offset);
    public Transaction MoveRelation(Guid idFrom, Guid propertyId, IEnumerable<Guid> items, int offset) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelation(p.RelationId, idFrom, items.ToArray(), offset, p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelation(int idFrom, Guid propertyId, int item, int offset) => MoveRelation(idFrom, propertyId, [item], offset);
    public Transaction MoveRelation(int idFrom, Guid propertyId, IEnumerable<int> items, int offset) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelation(p.RelationId, idFrom, items.ToArray(), offset, p.FromTargetToSource);
        return this;
    }

    // MoveRelationToTop
    public Transaction MoveRelationToTop<T>(T fromNode, Expression<Func<T, object?>> expression, object item) => MoveRelationToTop(fromNode, expression, [item]);
    public Transaction MoveRelationToTop<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items) {
        if (fromNode == null) throw new Exception("From node cannot be null. ");
        if (tryGetGuids(fromNode, items, out var fromGuid, out var itemGuids)) {
            MoveRelationToTop(fromGuid, expression, itemGuids);
        } else if (tryGetInts(fromNode, items, out var fromInt, out var itemInts)) {
            MoveRelationToTop(fromInt, expression, itemInts);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction MoveRelationToTop<T>(Guid idFrom, Expression<Func<T, object?>> expression, Guid item) => MoveRelationToTop(idFrom, expression, [item]);
    public Transaction MoveRelationToTop<T>(Guid idFrom, Expression<Func<T, object?>> expression, IEnumerable<Guid> items) {
        var p = getRelProp(expression);
        _transactionData.MoveRelationToTop(p.RelationId, idFrom, items.ToArray(), p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationToTop<T>(int idFrom, Expression<Func<T, object?>> expression, int item) => MoveRelationToTop(idFrom, expression, [item]);
    public Transaction MoveRelationToTop<T>(int idFrom, Expression<Func<T, object?>> expression, IEnumerable<int> items) {
        var p = getRelProp(expression);
        _transactionData.MoveRelationToTop(p.RelationId, idFrom, items.ToArray(), p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationToTop(Guid idFrom, Guid propertyId, Guid item) => MoveRelationToTop(idFrom, propertyId, [item]);
    public Transaction MoveRelationToTop(Guid idFrom, Guid propertyId, IEnumerable<Guid> items) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelationToTop(p.RelationId, idFrom, items.ToArray(), p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationToTop(int idFrom, Guid propertyId, int item) => MoveRelationToTop(idFrom, propertyId, [item]);
    public Transaction MoveRelationToTop(int idFrom, Guid propertyId, IEnumerable<int> items) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelationToTop(p.RelationId, idFrom, items.ToArray(), p.FromTargetToSource);
        return this;
    }

    // MoveRelationToBottom
    public Transaction MoveRelationToBottom<T>(T fromNode, Expression<Func<T, object?>> expression, object item) => MoveRelationToBottom(fromNode, expression, [item]);
    public Transaction MoveRelationToBottom<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items) {
        if (fromNode == null) throw new Exception("From node cannot be null. ");
        if (tryGetGuids(fromNode, items, out var fromGuid, out var itemGuids)) {
            MoveRelationToBottom(fromGuid, expression, itemGuids);
        } else if (tryGetInts(fromNode, items, out var fromInt, out var itemInts)) {
            MoveRelationToBottom(fromInt, expression, itemInts);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction MoveRelationToBottom<T>(Guid idFrom, Expression<Func<T, object?>> expression, Guid item) => MoveRelationToBottom(idFrom, expression, [item]);
    public Transaction MoveRelationToBottom<T>(Guid idFrom, Expression<Func<T, object?>> expression, IEnumerable<Guid> items) {
        var p = getRelProp(expression);
        _transactionData.MoveRelationToBottom(p.RelationId, idFrom, items.ToArray(), p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationToBottom<T>(int idFrom, Expression<Func<T, object?>> expression, int item) => MoveRelationToBottom(idFrom, expression, [item]);
    public Transaction MoveRelationToBottom<T>(int idFrom, Expression<Func<T, object?>> expression, IEnumerable<int> items) {
        var p = getRelProp(expression);
        _transactionData.MoveRelationToBottom(p.RelationId, idFrom, items.ToArray(), p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationToBottom(Guid idFrom, Guid propertyId, Guid item) => MoveRelationToBottom(idFrom, propertyId, [item]);
    public Transaction MoveRelationToBottom(Guid idFrom, Guid propertyId, IEnumerable<Guid> items) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelationToBottom(p.RelationId, idFrom, items.ToArray(), p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationToBottom(int idFrom, Guid propertyId, int item) => MoveRelationToBottom(idFrom, propertyId, [item]);
    public Transaction MoveRelationToBottom(int idFrom, Guid propertyId, IEnumerable<int> items) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelationToBottom(p.RelationId, idFrom, items.ToArray(), p.FromTargetToSource);
        return this;
    }

    // MoveRelationBefore: moves items to a contiguous block just before the anchor item
    public Transaction MoveRelationBefore<T>(T fromNode, Expression<Func<T, object?>> expression, object item, object anchor) => MoveRelationBefore(fromNode, expression, [item], anchor);
    public Transaction MoveRelationBefore<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items, object anchor) {
        if (fromNode == null) throw new Exception("From node cannot be null. ");
        if (tryGetGuids(fromNode, items, out var fromGuid, out var itemGuids) && Store.Mapper.TryGetIdGuidAndCreateIfPossible(anchor, out var anchorGuid)) {
            MoveRelationBefore(fromGuid, expression, itemGuids, anchorGuid);
        } else if (tryGetInts(fromNode, items, out var fromInt, out var itemInts) && Store.Mapper.TryGetIdUInt(anchor, out var anchorInt)) {
            MoveRelationBefore(fromInt, expression, itemInts, anchorInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction MoveRelationBefore<T>(Guid idFrom, Expression<Func<T, object?>> expression, Guid item, Guid anchor) => MoveRelationBefore(idFrom, expression, [item], anchor);
    public Transaction MoveRelationBefore<T>(Guid idFrom, Expression<Func<T, object?>> expression, IEnumerable<Guid> items, Guid anchor) {
        var p = getRelProp(expression);
        _transactionData.MoveRelationBefore(p.RelationId, idFrom, items.ToArray(), anchor, p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationBefore<T>(int idFrom, Expression<Func<T, object?>> expression, int item, int anchor) => MoveRelationBefore(idFrom, expression, [item], anchor);
    public Transaction MoveRelationBefore<T>(int idFrom, Expression<Func<T, object?>> expression, IEnumerable<int> items, int anchor) {
        var p = getRelProp(expression);
        _transactionData.MoveRelationBefore(p.RelationId, idFrom, items.ToArray(), anchor, p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationBefore(Guid idFrom, Guid propertyId, Guid item, Guid anchor) => MoveRelationBefore(idFrom, propertyId, [item], anchor);
    public Transaction MoveRelationBefore(Guid idFrom, Guid propertyId, IEnumerable<Guid> items, Guid anchor) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelationBefore(p.RelationId, idFrom, items.ToArray(), anchor, p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationBefore(int idFrom, Guid propertyId, int item, int anchor) => MoveRelationBefore(idFrom, propertyId, [item], anchor);
    public Transaction MoveRelationBefore(int idFrom, Guid propertyId, IEnumerable<int> items, int anchor) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelationBefore(p.RelationId, idFrom, items.ToArray(), anchor, p.FromTargetToSource);
        return this;
    }

    // MoveRelationAfter: moves items to a contiguous block just after the anchor item
    public Transaction MoveRelationAfter<T>(T fromNode, Expression<Func<T, object?>> expression, object item, object anchor) => MoveRelationAfter(fromNode, expression, [item], anchor);
    public Transaction MoveRelationAfter<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> items, object anchor) {
        if (fromNode == null) throw new Exception("From node cannot be null. ");
        if (tryGetGuids(fromNode, items, out var fromGuid, out var itemGuids) && Store.Mapper.TryGetIdGuidAndCreateIfPossible(anchor, out var anchorGuid)) {
            MoveRelationAfter(fromGuid, expression, itemGuids, anchorGuid);
        } else if (tryGetInts(fromNode, items, out var fromInt, out var itemInts) && Store.Mapper.TryGetIdUInt(anchor, out var anchorInt)) {
            MoveRelationAfter(fromInt, expression, itemInts, anchorInt);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction MoveRelationAfter<T>(Guid idFrom, Expression<Func<T, object?>> expression, Guid item, Guid anchor) => MoveRelationAfter(idFrom, expression, [item], anchor);
    public Transaction MoveRelationAfter<T>(Guid idFrom, Expression<Func<T, object?>> expression, IEnumerable<Guid> items, Guid anchor) {
        var p = getRelProp(expression);
        _transactionData.MoveRelationAfter(p.RelationId, idFrom, items.ToArray(), anchor, p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationAfter<T>(int idFrom, Expression<Func<T, object?>> expression, int item, int anchor) => MoveRelationAfter(idFrom, expression, [item], anchor);
    public Transaction MoveRelationAfter<T>(int idFrom, Expression<Func<T, object?>> expression, IEnumerable<int> items, int anchor) {
        var p = getRelProp(expression);
        _transactionData.MoveRelationAfter(p.RelationId, idFrom, items.ToArray(), anchor, p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationAfter(Guid idFrom, Guid propertyId, Guid item, Guid anchor) => MoveRelationAfter(idFrom, propertyId, [item], anchor);
    public Transaction MoveRelationAfter(Guid idFrom, Guid propertyId, IEnumerable<Guid> items, Guid anchor) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelationAfter(p.RelationId, idFrom, items.ToArray(), anchor, p.FromTargetToSource);
        return this;
    }
    public Transaction MoveRelationAfter(int idFrom, Guid propertyId, int item, int anchor) => MoveRelationAfter(idFrom, propertyId, [item], anchor);
    public Transaction MoveRelationAfter(int idFrom, Guid propertyId, IEnumerable<int> items, int anchor) {
        var p = getRelProp(propertyId);
        _transactionData.MoveRelationAfter(p.RelationId, idFrom, items.ToArray(), anchor, p.FromTargetToSource);
        return this;
    }

    // SetRelationOrder: reorders the whole list to match the given ids, which must contain exactly the currently related ids
    public Transaction SetRelationOrder<T>(T fromNode, Expression<Func<T, object?>> expression, IEnumerable<object> itemsInOrder) {
        if (fromNode == null) throw new Exception("From node cannot be null. ");
        if (tryGetGuids(fromNode, itemsInOrder, out var fromGuid, out var itemGuids)) {
            SetRelationOrder(fromGuid, expression, itemGuids);
        } else if (tryGetInts(fromNode, itemsInOrder, out var fromInt, out var itemInts)) {
            SetRelationOrder(fromInt, expression, itemInts);
        } else {
            throw new Exception("Only nodes with Guid or int id accepted. ");
        }
        return this;
    }
    public Transaction SetRelationOrder<T>(Guid idFrom, Expression<Func<T, object?>> expression, IEnumerable<Guid> itemsInOrder) {
        var p = getRelProp(expression);
        _transactionData.SetRelationOrder(p.RelationId, idFrom, itemsInOrder.ToArray(), p.FromTargetToSource);
        return this;
    }
    public Transaction SetRelationOrder<T>(int idFrom, Expression<Func<T, object?>> expression, IEnumerable<int> itemsInOrder) {
        var p = getRelProp(expression);
        _transactionData.SetRelationOrder(p.RelationId, idFrom, itemsInOrder.ToArray(), p.FromTargetToSource);
        return this;
    }
    public Transaction SetRelationOrder(Guid idFrom, Guid propertyId, IEnumerable<Guid> itemsInOrder) {
        var p = getRelProp(propertyId);
        _transactionData.SetRelationOrder(p.RelationId, idFrom, itemsInOrder.ToArray(), p.FromTargetToSource);
        return this;
    }
    public Transaction SetRelationOrder(int idFrom, Guid propertyId, IEnumerable<int> itemsInOrder) {
        var p = getRelProp(propertyId);
        _transactionData.SetRelationOrder(p.RelationId, idFrom, itemsInOrder.ToArray(), p.FromTargetToSource);
        return this;
    }

    bool tryGetGuids(object fromNode, IEnumerable<object> items, out Guid fromGuid, out Guid[] itemGuids) {
        itemGuids = [];
        if (!Store.Mapper.TryGetIdGuidAndCreateIfPossible(fromNode, out fromGuid)) return false;
        var list = new List<Guid>();
        foreach (var item in items) {
            if (!Store.Mapper.TryGetIdGuidAndCreateIfPossible(item, out var g)) return false;
            list.Add(g);
        }
        itemGuids = [.. list];
        return true;
    }
    bool tryGetInts(object fromNode, IEnumerable<object> items, out int fromId, out int[] itemIds) {
        itemIds = [];
        if (!Store.Mapper.TryGetIdUInt(fromNode, out fromId)) return false;
        var list = new List<int>();
        foreach (var item in items) {
            if (!Store.Mapper.TryGetIdUInt(item, out var id)) return false;
            list.Add(id);
        }
        itemIds = [.. list];
        return true;
    }
}

// Raw relation id based reordering. The owner is the node whose ordered list is changed. By default the
// owner's list of targets is reordered, pass reorderSourcesOfTarget = true to reorder the source list of
// a target instead (only meaningful for many to many relations).
public partial class TransactionRelation {
    public Transaction Move(Guid relationId, int owner, int item, int offset, bool reorderSourcesOfTarget = false) => Move(relationId, owner, [item], offset, reorderSourcesOfTarget);
    public Transaction Move(Guid relationId, int owner, IEnumerable<int> items, int offset, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelation(relationId, owner, items.ToArray(), offset, reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction Move(Guid relationId, Guid owner, Guid item, int offset, bool reorderSourcesOfTarget = false) => Move(relationId, owner, [item], offset, reorderSourcesOfTarget);
    public Transaction Move(Guid relationId, Guid owner, IEnumerable<Guid> items, int offset, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelation(relationId, owner, items.ToArray(), offset, reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction Move<R>(int owner, int item, int offset, bool reorderSourcesOfTarget = false) where R : IRelation => Move(transaction.Store.Mapper.GetRelationId<R>(), owner, item, offset, reorderSourcesOfTarget);
    public Transaction Move<R>(int owner, IEnumerable<int> items, int offset, bool reorderSourcesOfTarget = false) where R : IRelation => Move(transaction.Store.Mapper.GetRelationId<R>(), owner, items, offset, reorderSourcesOfTarget);
    public Transaction Move<R>(Guid owner, Guid item, int offset, bool reorderSourcesOfTarget = false) where R : IRelation => Move(transaction.Store.Mapper.GetRelationId<R>(), owner, item, offset, reorderSourcesOfTarget);
    public Transaction Move<R>(Guid owner, IEnumerable<Guid> items, int offset, bool reorderSourcesOfTarget = false) where R : IRelation => Move(transaction.Store.Mapper.GetRelationId<R>(), owner, items, offset, reorderSourcesOfTarget);

    public Transaction MoveToTop(Guid relationId, int owner, int item, bool reorderSourcesOfTarget = false) => MoveToTop(relationId, owner, [item], reorderSourcesOfTarget);
    public Transaction MoveToTop(Guid relationId, int owner, IEnumerable<int> items, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelationToTop(relationId, owner, items.ToArray(), reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction MoveToTop(Guid relationId, Guid owner, Guid item, bool reorderSourcesOfTarget = false) => MoveToTop(relationId, owner, [item], reorderSourcesOfTarget);
    public Transaction MoveToTop(Guid relationId, Guid owner, IEnumerable<Guid> items, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelationToTop(relationId, owner, items.ToArray(), reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction MoveToTop<R>(int owner, int item, bool reorderSourcesOfTarget = false) where R : IRelation => MoveToTop(transaction.Store.Mapper.GetRelationId<R>(), owner, item, reorderSourcesOfTarget);
    public Transaction MoveToTop<R>(int owner, IEnumerable<int> items, bool reorderSourcesOfTarget = false) where R : IRelation => MoveToTop(transaction.Store.Mapper.GetRelationId<R>(), owner, items, reorderSourcesOfTarget);
    public Transaction MoveToTop<R>(Guid owner, Guid item, bool reorderSourcesOfTarget = false) where R : IRelation => MoveToTop(transaction.Store.Mapper.GetRelationId<R>(), owner, item, reorderSourcesOfTarget);
    public Transaction MoveToTop<R>(Guid owner, IEnumerable<Guid> items, bool reorderSourcesOfTarget = false) where R : IRelation => MoveToTop(transaction.Store.Mapper.GetRelationId<R>(), owner, items, reorderSourcesOfTarget);

    public Transaction MoveToBottom(Guid relationId, int owner, int item, bool reorderSourcesOfTarget = false) => MoveToBottom(relationId, owner, [item], reorderSourcesOfTarget);
    public Transaction MoveToBottom(Guid relationId, int owner, IEnumerable<int> items, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelationToBottom(relationId, owner, items.ToArray(), reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction MoveToBottom(Guid relationId, Guid owner, Guid item, bool reorderSourcesOfTarget = false) => MoveToBottom(relationId, owner, [item], reorderSourcesOfTarget);
    public Transaction MoveToBottom(Guid relationId, Guid owner, IEnumerable<Guid> items, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelationToBottom(relationId, owner, items.ToArray(), reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction MoveToBottom<R>(int owner, int item, bool reorderSourcesOfTarget = false) where R : IRelation => MoveToBottom(transaction.Store.Mapper.GetRelationId<R>(), owner, item, reorderSourcesOfTarget);
    public Transaction MoveToBottom<R>(int owner, IEnumerable<int> items, bool reorderSourcesOfTarget = false) where R : IRelation => MoveToBottom(transaction.Store.Mapper.GetRelationId<R>(), owner, items, reorderSourcesOfTarget);
    public Transaction MoveToBottom<R>(Guid owner, Guid item, bool reorderSourcesOfTarget = false) where R : IRelation => MoveToBottom(transaction.Store.Mapper.GetRelationId<R>(), owner, item, reorderSourcesOfTarget);
    public Transaction MoveToBottom<R>(Guid owner, IEnumerable<Guid> items, bool reorderSourcesOfTarget = false) where R : IRelation => MoveToBottom(transaction.Store.Mapper.GetRelationId<R>(), owner, items, reorderSourcesOfTarget);

    public Transaction MoveBefore(Guid relationId, int owner, int item, int anchor, bool reorderSourcesOfTarget = false) => MoveBefore(relationId, owner, [item], anchor, reorderSourcesOfTarget);
    public Transaction MoveBefore(Guid relationId, int owner, IEnumerable<int> items, int anchor, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelationBefore(relationId, owner, items.ToArray(), anchor, reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction MoveBefore(Guid relationId, Guid owner, Guid item, Guid anchor, bool reorderSourcesOfTarget = false) => MoveBefore(relationId, owner, [item], anchor, reorderSourcesOfTarget);
    public Transaction MoveBefore(Guid relationId, Guid owner, IEnumerable<Guid> items, Guid anchor, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelationBefore(relationId, owner, items.ToArray(), anchor, reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction MoveBefore<R>(int owner, int item, int anchor, bool reorderSourcesOfTarget = false) where R : IRelation => MoveBefore(transaction.Store.Mapper.GetRelationId<R>(), owner, item, anchor, reorderSourcesOfTarget);
    public Transaction MoveBefore<R>(int owner, IEnumerable<int> items, int anchor, bool reorderSourcesOfTarget = false) where R : IRelation => MoveBefore(transaction.Store.Mapper.GetRelationId<R>(), owner, items, anchor, reorderSourcesOfTarget);
    public Transaction MoveBefore<R>(Guid owner, Guid item, Guid anchor, bool reorderSourcesOfTarget = false) where R : IRelation => MoveBefore(transaction.Store.Mapper.GetRelationId<R>(), owner, item, anchor, reorderSourcesOfTarget);
    public Transaction MoveBefore<R>(Guid owner, IEnumerable<Guid> items, Guid anchor, bool reorderSourcesOfTarget = false) where R : IRelation => MoveBefore(transaction.Store.Mapper.GetRelationId<R>(), owner, items, anchor, reorderSourcesOfTarget);

    public Transaction MoveAfter(Guid relationId, int owner, int item, int anchor, bool reorderSourcesOfTarget = false) => MoveAfter(relationId, owner, [item], anchor, reorderSourcesOfTarget);
    public Transaction MoveAfter(Guid relationId, int owner, IEnumerable<int> items, int anchor, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelationAfter(relationId, owner, items.ToArray(), anchor, reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction MoveAfter(Guid relationId, Guid owner, Guid item, Guid anchor, bool reorderSourcesOfTarget = false) => MoveAfter(relationId, owner, [item], anchor, reorderSourcesOfTarget);
    public Transaction MoveAfter(Guid relationId, Guid owner, IEnumerable<Guid> items, Guid anchor, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.MoveRelationAfter(relationId, owner, items.ToArray(), anchor, reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction MoveAfter<R>(int owner, int item, int anchor, bool reorderSourcesOfTarget = false) where R : IRelation => MoveAfter(transaction.Store.Mapper.GetRelationId<R>(), owner, item, anchor, reorderSourcesOfTarget);
    public Transaction MoveAfter<R>(int owner, IEnumerable<int> items, int anchor, bool reorderSourcesOfTarget = false) where R : IRelation => MoveAfter(transaction.Store.Mapper.GetRelationId<R>(), owner, items, anchor, reorderSourcesOfTarget);
    public Transaction MoveAfter<R>(Guid owner, Guid item, Guid anchor, bool reorderSourcesOfTarget = false) where R : IRelation => MoveAfter(transaction.Store.Mapper.GetRelationId<R>(), owner, item, anchor, reorderSourcesOfTarget);
    public Transaction MoveAfter<R>(Guid owner, IEnumerable<Guid> items, Guid anchor, bool reorderSourcesOfTarget = false) where R : IRelation => MoveAfter(transaction.Store.Mapper.GetRelationId<R>(), owner, items, anchor, reorderSourcesOfTarget);

    public Transaction SetOrder(Guid relationId, int owner, IEnumerable<int> itemsInOrder, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.SetRelationOrder(relationId, owner, itemsInOrder.ToArray(), reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction SetOrder(Guid relationId, Guid owner, IEnumerable<Guid> itemsInOrder, bool reorderSourcesOfTarget = false) {
        transaction._transactionData.SetRelationOrder(relationId, owner, itemsInOrder.ToArray(), reorderSourcesOfTarget);
        return transaction;
    }
    public Transaction SetOrder<R>(int owner, IEnumerable<int> itemsInOrder, bool reorderSourcesOfTarget = false) where R : IRelation => SetOrder(transaction.Store.Mapper.GetRelationId<R>(), owner, itemsInOrder, reorderSourcesOfTarget);
    public Transaction SetOrder<R>(Guid owner, IEnumerable<Guid> itemsInOrder, bool reorderSourcesOfTarget = false) where R : IRelation => SetOrder(transaction.Store.Mapper.GetRelationId<R>(), owner, itemsInOrder, reorderSourcesOfTarget);
}
