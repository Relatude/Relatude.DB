using Relatude.DB.Transactions;
namespace Relatude.DB.Nodes;
public partial class TransactionRelation(Transaction transaction) {
    public Transaction Relate(Guid relationId, int from, int to) {
        transaction._transactionData.AddRelation(relationId, from, to);
        return transaction;
    }
    public Transaction Relate(Guid relationId, Guid from, Guid to) {
        transaction._transactionData.AddRelation(relationId, from, to);
        return transaction;
    }
    public Transaction Relate(Guid relationId, Guid from, int to) {
        var action = new RelationAction(RelationOperation.Add, relationId);
        action.SourceGuid = from;
        action.Target = to;
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction Relate(Guid relationId, int from, Guid to) {
        var action = new RelationAction(RelationOperation.Add, relationId);
        action.Source = from;
        action.TargetGuid = to;
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction Relate(Guid relationId, object from, object to) {
        var action = new RelationAction(RelationOperation.Add, relationId);
        var mapper = transaction.Store.Mapper;
        if (!mapper.TryGetIdGuid(from, out action.SourceGuid)) {
            if (!mapper.TryGetIdUInt(from, out action.Source))
                throw new Exception("Unable to get id for 'from' node. Only nodes with Guid or int id accepted. ");
        }
        if (!mapper.TryGetIdGuid(to, out action.TargetGuid)) {
            if (!mapper.TryGetIdUInt(to, out action.Target))
                throw new Exception("Unable to get id for 'to' node. Only nodes with Guid or int id accepted. ");
        }
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction Relate<R>(int from, int to) where R : IRelation => Relate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Relate<R>(Guid from, Guid to) where R : IRelation => Relate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Relate<R>(Guid from, int to) where R : IRelation => Relate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Relate<R>(int from, Guid to) where R : IRelation => Relate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Relate<R>(object from, object to) where R : IRelation => Relate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Relate<R, TFrom, TTo>(TFrom from, TTo to) where R : IRelation<TFrom, TTo> => Relate<R>(from!, to!);
    public Transaction UnRelate(Guid relationId, int from, int to) {
        transaction._transactionData.RemoveRelation(relationId, from, to);
        return transaction;
    }
    public Transaction UnRelate(Guid relationId, Guid from, Guid to) {
        transaction._transactionData.RemoveRelation(relationId, from, to);
        return transaction;
    }
    public Transaction UnRelate(Guid relationId, Guid from, int to) {
        var action = new RelationAction(RelationOperation.Remove, relationId);
        action.SourceGuid = from;
        action.Target = to;
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction UnRelate(Guid relationId, int from, Guid to) {
        var action = new RelationAction(RelationOperation.Remove, relationId);
        action.Source = from;
        action.TargetGuid = to;
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction UnRelate(Guid relationId, object from, object to) {
        var action = new RelationAction(RelationOperation.Remove, relationId);
        var mapper = transaction.Store.Mapper;
        if (!mapper.TryGetIdGuid(from, out action.SourceGuid)) {
            if (!mapper.TryGetIdUInt(from, out action.Source))
                throw new Exception("Unable to get id for 'from' node. Only nodes with Guid or int id accepted. ");
        }
        if (!mapper.TryGetIdGuid(to, out action.TargetGuid)) {
            if (!mapper.TryGetIdUInt(to, out action.Target))
                throw new Exception("Unable to get id for 'to' node. Only nodes with Guid or int id accepted. ");
        }
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction UnRelate<R>(int from, int to) where R : IRelation => UnRelate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction UnRelate<R>(Guid from, Guid to) where R : IRelation => UnRelate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction UnRelate<R>(Guid from, int to) where R : IRelation => UnRelate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction UnRelate<R>(int from, Guid to) where R : IRelation => UnRelate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction UnRelate<R>(object from, object to) where R : IRelation => UnRelate(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction UnRelate<R, TFrom, TTo>(TFrom from, TTo to) where R : IRelation<TFrom, TTo> => UnRelate<R>(from!, to!);
    public Transaction Set(Guid relationId, int from, int to) {
        transaction._transactionData.SetRelation(relationId, from, to);
        return transaction;
    }
    public Transaction Set(Guid relationId, Guid from, Guid to) {
        transaction._transactionData.SetRelation(relationId, from, to);
        return transaction;
    }
    public Transaction Set(Guid relationId, Guid from, int to) {
        var action = new RelationAction(RelationOperation.Set, relationId);
        action.SourceGuid = from;
        action.Target = to;
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction Set(Guid relationId, int from, Guid to) {
        var action = new RelationAction(RelationOperation.Set, relationId);
        action.Source = from;
        action.TargetGuid = to;
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction Set(Guid relationId, object from, object to) {
        var action = new RelationAction(RelationOperation.Set, relationId);
        var mapper = transaction.Store.Mapper;
        if (!mapper.TryGetIdGuid(from, out action.SourceGuid)) {
            if (!mapper.TryGetIdUInt(from, out action.Source))
                throw new Exception("Unable to get id for 'from' node. Only nodes with Guid or int id accepted. ");
        }
        if (!mapper.TryGetIdGuid(to, out action.TargetGuid)) {
            if (!mapper.TryGetIdUInt(to, out action.Target))
                throw new Exception("Unable to get id for 'to' node. Only nodes with Guid or int id accepted. ");
        }
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction Set<R>(int from, int to) where R : IRelation => Set(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Set<R>(Guid from, Guid to) where R : IRelation => Set(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Set<R>(Guid from, int to) where R : IRelation => Set(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Set<R>(int from, Guid to) where R : IRelation => Set(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Set<R>(object from, object to) where R : IRelation => Set(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Set<R, TFrom, TTo>(TFrom from, TTo to) where R : IRelation<TFrom, TTo> => Set<R>(from!, to!);
    public Transaction Clear(Guid relationId, int from, int to) {
        transaction._transactionData.ClearRelation(relationId, from, to);
        return transaction;
    }
    public Transaction Clear(Guid relationId, Guid from, Guid to) {
        transaction._transactionData.ClearRelation(relationId, from, to);
        return transaction;
    }
    public Transaction Clear(Guid relationId, Guid from, int to) { 
        var action = new RelationAction(RelationOperation.Clear, relationId);
        action.SourceGuid = from;
        action.Target = to;
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction Clear(Guid relationId, int from, Guid to) {
        var action = new RelationAction(RelationOperation.Clear, relationId);
        action.Source = from;
        action.TargetGuid = to;
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction Clear(Guid relationId, object from, object to) {
        var action = new RelationAction(RelationOperation.Clear, relationId);
        var mapper = transaction.Store.Mapper;
        if (!mapper.TryGetIdGuid(from, out action.SourceGuid)) {
            if (!mapper.TryGetIdUInt(from, out action.Source))
                throw new Exception("Unable to get id for 'from' node. Only nodes with Guid or int id accepted. ");
        }
        if (!mapper.TryGetIdGuid(to, out action.TargetGuid)) {
            if (!mapper.TryGetIdUInt(to, out action.Target))
                throw new Exception("Unable to get id for 'to' node. Only nodes with Guid or int id accepted. ");
        }
        transaction._transactionData.Add(action);
        return transaction;
    }
    public Transaction Clear<R>(int from, int to) where R : IRelation => Clear(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Clear<R>(Guid from, Guid to) where R : IRelation => Clear(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Clear<R>(Guid from, int to) where R : IRelation => Clear(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Clear<R>(int from, Guid to) where R : IRelation => Clear(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Clear<R>(object from, object to) where R : IRelation => Clear(transaction.Store.Mapper.GetRelationId<R>(), from, to);
    public Transaction Clear<R, TFrom, TTo>(TFrom from, TTo to) where R : IRelation<TFrom, TTo> => Clear<R>(from!, to!);
    public Transaction ClearFrom(Guid relationId, int from) {
        transaction._transactionData.ClearRelationsWithSource(relationId, from);
        return transaction;
    }
    public Transaction ClearFrom(Guid relationId, Guid from) {
        transaction._transactionData.ClearRelationsWithSource(relationId, from);
        return transaction;
    }
    public Transaction ClearFrom<R>(int from) where R : IRelation => ClearFrom(transaction.Store.Mapper.GetRelationId<R>(), from);
    public Transaction ClearFrom<R>(Guid from) where R : IRelation => ClearFrom(transaction.Store.Mapper.GetRelationId<R>(), from);
    public Transaction ClearTo(Guid relationId, int to) {
        transaction._transactionData.ClearRelationsWithTarget(relationId, to);
        return transaction;
    }
    public Transaction ClearTo(Guid relationId, Guid to) {
        transaction._transactionData.ClearRelationsWithTarget(relationId, to);
        return transaction;
    }
    public Transaction ClearTo<R>(int to) where R : IRelation => ClearTo(transaction.Store.Mapper.GetRelationId<R>(), to);
    public Transaction ClearTo<R>(Guid to) where R : IRelation => ClearTo(transaction.Store.Mapper.GetRelationId<R>(), to);
    public Transaction ClearAll(Guid relationId) {
        transaction._transactionData.ClearRelationsWithAny(relationId);
        return transaction;
    }
    public Transaction ClearAll<R>() where R : IRelation => ClearAll(transaction.Store.Mapper.GetRelationId<R>());
}
