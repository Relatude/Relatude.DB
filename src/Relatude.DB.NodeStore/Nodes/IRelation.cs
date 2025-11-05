using Relatude.DB.Datamodels;
using Relatude.DB.Query;
using System.Diagnostics.CodeAnalysis;
using System.Transactions;

namespace Relatude.DB.Nodes;

public interface IRelationProperty {
    bool IsSet();
    int Count();
    bool Contains(int id);
    bool Contains(Guid id);
    bool HasIncludedData();
}
public interface IOneProperty : IRelationProperty {
    object? GetIncludedData();
    void Initialize(NodeStore store, int parent__Id, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet);
}
public interface IManyProperty : IRelationProperty {
    IEnumerable<object> GetIncludedData();
    void Initialize(NodeStore store, int parent__Id, Guid parentId, Guid propertyId, INodeData[]? nodeDatas);
}
public interface IOneProperty<T> : IOneProperty {
}
public interface IManyProperty<T> : IManyProperty {
}
public class OneProperty<T>() : IOneProperty<T> {
    NodeStore store = null!;
    Guid parentId;
    int parent__Id;
    Guid propertyId;
    INodeData? nodeData;
    bool? isSet = false;
    public void Initialize(NodeStore store, int parent__Id, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet) {
        this.store = store;
        this.parent__Id = parent__Id;
        this.parentId = parentId;
        this.propertyId = propertyId;
        this.nodeData = nodeData;
        this.isSet = isSet;
    }
    public bool IsSet() => (isSet.HasValue) ? isSet.Value : tryGet(out _);
    public int Count() => IsSet() ? 1 : 0;
    public T Get() {
        if (TryGet(out T? value)) return value;
        throw new Exception($"Relation {store?.Datastore.Datamodel.Properties[propertyId].CodeName} is not set and empty. ");
    }
    bool tryGet([MaybeNullWhen(false)] out INodeData value) {
        if (isSet.HasValue) {
            if (isSet.Value) {
                value = nodeData!; // _node is guaranteed to be not null if _isSet is true
                return true;
            }
            value = default;
            return false;
        }
        if (store.Datastore.TryGetRelatedNodeFromPropertyId(propertyId, parentId, out nodeData)) {
            isSet = true;
            value = nodeData;
            return true;
        }
        isSet = false;
        value = default;
        return false;
    }
    public bool TryGet([MaybeNullWhen(false)] out T value) {
        if (tryGet(out INodeData? nodeData)) {
            value = store.Get<T>(nodeData);
            return true;
        }
        value = default;
        return false;
    }
    public bool Contains(int id) => tryGet(out var nodeData) ? nodeData.__Id == id : false;
    public bool Contains(Guid id) => tryGet(out var nodeData) ? nodeData.Id == id : false;

    public bool HasIncludedData() => isSet.HasValue;
    public object? GetIncludedData() {
        if (!isSet.HasValue) throw new Exception("No included data. ");
        if (nodeData == null) return null;
        return store.Get<T>(nodeData);
    }

}
public class ManyProperty<T>() : IManyProperty<T> {
    int? _count;
    NodeStore store = null!;
    int parent__Id;
    Guid parentId;
    Guid propertyId;
    INodeData[]? nodeDatas;
    public void Initialize(NodeStore store, int parent__Id, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) {
        this.store = store;
        this.parent__Id = parent__Id;
        this.parentId = parentId;
        this.parent__Id = parent__Id;
        this.propertyId = propertyId;
        this.nodeDatas = nodeDatas;
    }
    public bool IsSet() => Count() > 0;
    public int Count() {
        if (_count.HasValue) return _count.Value;
        if (nodeDatas is not null) return (_count = nodeDatas.Length).Value;
        return (_count = store.Datastore.GetRelatedCountFromPropertyId(propertyId, parentId)).Value;
    }
    public IEnumerable<T> Get() {
        if (_count.HasValue && _count == 0) return [];
        if (nodeDatas is null) nodeDatas = store.Datastore.GetRelatedNodesFromPropertyId(propertyId, parentId);
        return nodeDatas.Select(n => store.Get<T>(n));
    }

    public IQueryOfNodes<T, T> Query() => store.QueryRelated<T>(propertyId, parentId);
    public IQueryOfNodes<T, T> Query(Guid id) => store.QueryRelated<T>(propertyId, parentId).Where(id);
    public IQueryOfNodes<T, T> Query(IEnumerable<Guid> ids) => store.QueryRelated<T>(propertyId, parentId).Where(ids);
    public IQueryOfNodes<T, T> Query(int id) => store.QueryRelated<T>(propertyId, parentId).Where(id);
    public IQueryOfNodes<T, T> Query(IEnumerable<int> ids) => store.QueryRelated<T>(propertyId, parentId).Where(ids);

    public void Relate(T node) => store.Relate(parentId, propertyId, store.Mapper.GetIdGuid(node!));
    public void Relate(Guid id) => store.Relate(parentId, propertyId, id);
    public void Relate(int id) => store.Relate(parent__Id, propertyId, id);

    public void Relate(T node, Transaction transaction) => transaction.Relate(parentId, propertyId, transaction.Store.Mapper.GetIdGuid(node!));
    public void Relate(Guid id, Transaction transaction) => transaction.Relate(parentId, propertyId, id);
    public void Relate(int id, Transaction transaction) => transaction.Relate(parent__Id, propertyId, id);

    public void UnRelate(T node) => store.UnRelate(parentId, propertyId, store.Mapper.GetIdGuid(node!));
    public void UnRelate(Guid id) => store.UnRelate(parentId, propertyId, id);
    public void UnRelate(int id) => store.UnRelate(parent__Id, propertyId, id);

    public void UnRelate(T node, Transaction transaction) => transaction.UnRelate(parentId, propertyId, transaction.Store.Mapper.GetIdGuid(node!));
    public void UnRelate(Guid id, Transaction transaction) => transaction.UnRelate(parentId, propertyId, id);
    public void UnRelate(int id, Transaction transaction) => transaction.UnRelate(parent__Id, propertyId, id);

    public void ClearRelation(T node) => store.ClearRelation(parentId, propertyId, store.Mapper.GetIdGuid(node!));
    public void ClearRelation(Guid id) => store.ClearRelation(parentId, propertyId, id);
    public void ClearRelation(int id) => store.ClearRelation(parent__Id, propertyId, id);
    public void ClearAllRelation() => store.ClearRelation(parentId, propertyId, Guid.Empty);

    public void ClearRelation(T node, Transaction transaction) => transaction.ClearRelation(parentId, propertyId, transaction.Store.Mapper.GetIdGuid(node!));
    public void ClearRelation(Guid id, Transaction transaction) => transaction.ClearRelation(parentId, propertyId, id);
    public void ClearRelation(int id, Transaction transaction) => transaction.ClearRelation(parent__Id, propertyId, id);
    public void ClearAllRelation(Transaction transaction) => transaction.ClearRelation(parentId, propertyId, Guid.Empty);

    public bool Contains(int id) {
        if (_count.HasValue && _count == 0) return false;
        if (nodeDatas is not null) return nodeDatas.Any(n => n.__Id == id);
        return Query(id).Count() > 0;
    }
    public bool Contains(Guid id) {
        if (_count.HasValue && _count == 0) return false;
        if (nodeDatas is not null) return nodeDatas.Any(n => n.Id == id);
        return Query(id).Count() > 0;
    }
    public bool HasIncludedData() => nodeDatas is not null;
    public IEnumerable<object> GetIncludedData() {
        if (nodeDatas is null) throw new Exception("No included data. ");
        foreach (var nodeData in nodeDatas) {
            yield return store.Get<T>(nodeData)!;
        }
    }
}

public interface IRelation { }
public interface IOneOne : IRelation { }
public interface IOneToOne : IRelation { }
public interface IOneToMany : IRelation { }
public interface IManyMany : IRelation { }
public interface IManyToMany : IRelation { }

/// <summary>
/// Symmetric one-to-one relation
/// Example: Spouse relation
/// If one spouse is related to the other, the other is automatically related to the first.
/// One spouse can be related to only one other spouse. Effectively, this creates a pair.
/// </summary>
public class OneOne<TOne> : IOneOne {
    public class One() : OneProperty<TOne>() { }
}
/// <summary>
/// Directional one-to-one relation
/// Example: Husband to wife relation
/// If the husband is related to the wife, the wife is automatically related back to the husband.
/// One husband can be related to only one wife, and one wife can be related to only one husband.
/// </summary>
public class OneToOne<TOneFrom, TOneTo> : IOneToOne {
    public class OneFrom() : OneProperty<TOneFrom>() { }
    public class OneTo() : OneProperty<TOneTo>() { }
}
/// <summary>
/// Directional one-to-many relation
/// Example: Parent to children relation
/// If the parent is related to the children, the children are automatically related back to the parent.
/// One parent can be related to many children, but each child is related to only one parent.
/// </summary>
public class OneToMany<TOne, TMany> : IOneToMany {
    public class One() : OneProperty<TOne>() { }
    public class Many() : ManyProperty<TMany>() { }
}
/// <summary>
/// Symmetric many-to-many relation
/// Example: Friends relation
/// If one friend is related to another, the other is automatically related back to the first.
/// One friend can be related to many friends and vice versa.
/// </summary>
public class ManyMany<TMany> : IManyMany {
    public class Many() : ManyProperty<TMany>() { }
}
/// <summary>
/// Directional many-to-many relation
/// Example: Teachers to students relation
/// If a teacher is related to students, the students are automatically related back to the teacher.
/// Many teachers can be related to many students and vice versa.
/// </summary>
public class ManyToMany<TManyFrom, TManyTo> : IManyToMany {
    public class ManyFrom() : ManyProperty<TManyFrom>() { }
    public class ManyTo() : ManyProperty<TManyTo>() { }
}
