using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Datamodels;
using Relatude.DB.Query;

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
}
public interface IManyProperty : IRelationProperty {
    IEnumerable<object> GetIncludedData();
}
public class OneProperty<T>() : IOneProperty {
    NodeStore? store;
    Guid parentId;
    Guid propertyId;
    INodeData? nodeData;
    bool? isSet = false;
    public void Initialize(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet) {
        this.store = store;
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
        if (store!.Datastore.TryGetRelatedNodeFromPropertyId(propertyId, parentId, out nodeData)) {
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
            value = store!.Get<T>(nodeData);
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
        return store!.Get<T>(nodeData);
    }

}
public class ManyProperty<T>() : IManyProperty {
    int? _count;
    NodeStore? store;
    Guid parentId;
    Guid propertyId;
    INodeData[]? nodeDatas;
    public void Initialize(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) {
        this.store = store;
        this.parentId = parentId;
        this.propertyId = propertyId;
        this.nodeDatas = nodeDatas;
    }
    public bool IsSet() => Count() > 0;
    public int Count() {
        if (_count.HasValue) return _count.Value;
        if (nodeDatas is not null) return (_count = nodeDatas.Length).Value;
        return (_count = store!.Datastore.GetRelatedCountFromPropertyId(propertyId, parentId)).Value;
    }
    public IEnumerable<T> Get() {
        if (_count.HasValue && _count == 0) return [];
        if (nodeDatas is null) nodeDatas = store!.Datastore.GetRelatedNodesFromPropertyId(propertyId, parentId);
        return nodeDatas.Select(n => store!.Get<T>(n));
    }

    public IQueryOfNodes<T, T> Query() => store!.QueryRelated<T>(propertyId, parentId);
    public IQueryOfNodes<T, T> Query(Guid id) => store!.QueryRelated<T>(propertyId, parentId).Where(id);
    public IQueryOfNodes<T, T> Query(IEnumerable<Guid> ids) => store!.QueryRelated<T>(propertyId, parentId).Where(ids);
    public IQueryOfNodes<T, T> Query(int id) => store!.QueryRelated<T>(propertyId, parentId).Where(id);
    public IQueryOfNodes<T, T> Query(IEnumerable<int> ids) => store!.QueryRelated<T>(propertyId, parentId).Where(ids);

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
            yield return store!.Get<T>(nodeData)!;
        }
    }
}

public interface IRelation { }
public interface IOneOne : IRelation { }
public interface IOneToOne : IRelation { }
public interface IOneToMany : IRelation { }
public interface IManyMany : IRelation { }
public interface IManyToMany : IRelation { }

public class OneOne<T> : IOneOne {
    public class One() : OneProperty<T>() {
        public readonly static OneProperty<T> Empty = new();
    }
}
public class OneToOne<TLeft, TRight> : IOneToOne {
    public class Left() : OneProperty<TLeft>() {
        public readonly static Left Empty = new();
    }
    public class Right() : OneProperty<TRight>() {
        public readonly static Right Empty = new();
    }
}
public class OneToMany<TLeft, TRight> : IOneToMany {
    public class Left() : OneProperty<TLeft>() {
        public readonly static Left Empty = new();
    }
    public class Right() : ManyProperty<TRight>() {
        public readonly static Right Empty = new();
    }
}
public class ManyMany<T> : IManyMany {
    public class Many() : ManyProperty<T>() {
        public readonly static Many Empty = new();
    }
}
public class ManyToMany<TLeft, TRight> : IManyToMany {
    public class Left() : ManyProperty<TLeft>() {
        public readonly static Left Empty = new();
    }
    public class Right() : ManyProperty<TRight>() {
        public readonly static Right Empty = new();
    }
}

public class OneOne<T, TRelationSelfReference> : IOneOne
    where TRelationSelfReference : OneOne<T, TRelationSelfReference> {
    public class One() : OneProperty<T>() {
        public readonly static One Empty = new();
    }
}
public class OneToOne<TLeft, TRight, TRelationSelfReference> : IOneToOne
     where TRelationSelfReference : OneToOne<TLeft, TRight, TRelationSelfReference> {
    public class Left() : OneProperty<TLeft>() {
        public readonly static Left Empty = new();
    }
    public class Right() : OneProperty<TRight>() {
        public readonly static Right Empty = new();
    }
}
public class OneToMany<TLeft, TRight, TRelationSelfReference> : IOneToMany
where TRelationSelfReference : OneToMany<TLeft, TRight, TRelationSelfReference> {
    public class Left() : OneProperty<TLeft>() {
        public readonly static Left Empty = new();
    }
    public class Right() : ManyProperty<TRight>() {
        public readonly static Right Empty = new();
    }
}
public class ManyMany<T, TRelationSelfReference> : IManyMany
    where TRelationSelfReference : ManyMany<T, TRelationSelfReference> {
    public class Many() : ManyProperty<T>() {
        public readonly static Many Empty = new();
    }
}
public class ManyToMany<TLeft, TRight, TRelationSelfReference> : IManyToMany
    where TRelationSelfReference : ManyToMany<TLeft, TRight, TRelationSelfReference> {
    public class Left() : ManyProperty<TLeft>() {
        public readonly static Left Empty = new();
    }
    public class Right() : ManyProperty<TRight>() {
        public readonly static Right Empty = new();
    }
}




//// TRelationSelfReference is there to ensure datamodel analysis based on reflection 
//// and separate two relations refering to the same type combination.

//public class OneOne<T, TRelationSelfReference> : IOneOne
//    where TRelationSelfReference : OneOne<T, TRelationSelfReference> {
//    public class Node : OneProperty<T> { }
//    public readonly static Node Empty = new();
//}
//public class OneToOne<TFrom, TTo, TRelationSelfReference> : IOneToOne
//    where TRelationSelfReference : OneToOne<TFrom, TTo, TRelationSelfReference> {
//    public class FromNode : OneProperty<TFrom> { }
//    public class ToNode : OneProperty<TTo> { }
//    public readonly static FromNode EmptyFrom = new();
//    public readonly static ToNode EmptyTo = new();
//}
//public class OneToMany<TFrom, TTo, TRelationSelfReference> : IOneToMany
//    where TRelationSelfReference : OneToMany<TFrom, TTo, TRelationSelfReference> {
//    public class FromNode : OneProperty<TFrom> { }
//    public class ToNodes : ManyProperty<TTo> { }
//    public readonly static FromNode EmptyFrom = new();
//    public readonly static ToNodes EmptyTo = new();
//}
//public class ManyMany<T, TRelationSelfReference> : IManyMany
//    where TRelationSelfReference : ManyMany<T, TRelationSelfReference> {
//    public class Nodes : ManyProperty<T> { }
//    public readonly static Nodes Empty = new();
//}

///// <summary>
///// Manyto Many relation with self reference.
///// </summary>
///// <typeparam name="TFrom">Relation source type</typeparam>
///// <typeparam name="TTo">Relation target type</typeparam>
///// <typeparam name="TRelationSelfReference">A self reference to resolve ambiguity when multiple relations use the same combination of types</typeparam>
//public class ManyToMany<TFrom, TTo, TRelationSelfReference> : IManyToMany
//    where TRelationSelfReference : ManyToMany<TFrom, TTo, TRelationSelfReference> {
//    public class FromNodes : ManyProperty<TFrom> { }
//    public class ToNodes : ManyProperty<TTo> { }
//    public readonly static FromNodes EmptyFrom = new();
//    public readonly static ToNodes EmptyTo = new();

//}
