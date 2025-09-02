using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Datamodels;
using Relatude.DB.Demo.Models;
using Relatude.DB.Query;

namespace Relatude.DB.Nodes;

public interface IRelationProperty {
    bool IsSet();
    int Count();
    bool Contains(int id);
    bool Contains(Guid id);
    bool HasIncludedData { get; }
}
public interface IOneProperty : IRelationProperty {
    object? IncludedData { get; }
}
public interface IManyProperty : IRelationProperty {
    IEnumerable<object> IncludedData { get; }
}

public class OneProperty<T>(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet) : IOneProperty {
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

    //IncludedData? _included;
    //public IncludedData Included => _included == null ? (_included = new(store, nodeData, isSet)) : _included;
    public bool HasIncludedData => isSet.HasValue;
    //public class IncludedData(NodeStore? store, INodeData? nodeData, bool? isSet) {
    //    public bool? IsSet => isSet;
    //    public T? Node => nodeData == null ? default : store!.Get<T>(nodeData);
    //}
    public object? IncludedData {
        get {
            if (!isSet.HasValue) throw new Exception("No included data. ");
            if (nodeData == null) return null;
            return store!.Get<T>(nodeData);
        }
    }

}
public class ManyProperty<T>(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) : IManyProperty {
    int? _count;
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

    //IncludedData? _included;
    //public IncludedData Included => _included == null ? (_included = new(store, nodeDatas)) : _included;
    public bool HasIncludedData => nodeDatas is not null;
    //public class IncludedData(NodeStore? store, INodeData[]? nodeDatasIncluded) {
    //    public int? Count => nodeDatasIncluded?.Length;
    //    public IEnumerable<T>? Nodes => nodeDatasIncluded?.Select(n => store!.Get<T>(n));
    //}
    public IEnumerable<object> IncludedData {
        get {
            if (nodeDatas is null) throw new Exception("No included data. ");
            foreach (var nodeData in nodeDatas) {
                yield return store!.Get<T>(nodeData)!;
            }
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
    public class Node(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet)
        : OneProperty<T>(store, parentId, propertyId, nodeData, isSet) { }
    public readonly static OneProperty<T> Empty = new(null, Guid.Empty, Guid.Empty, null, false);
}
public class OneToOne<TFrom, TTo> : IOneToOne {
    public class FromNode(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet)
        : OneProperty<TFrom>(store, parentId, propertyId, nodeData, isSet) { }
    public class ToNode(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet)
        : OneProperty<TTo>(store, parentId, propertyId, nodeData, isSet) { }
    public readonly static FromNode EmptyFrom = new(null, Guid.Empty, Guid.Empty, null, false);
    public readonly static ToNode EmptyTo = new(null, Guid.Empty, Guid.Empty, null, false);
}
public class OneToMany<TFrom, TTo> : OneToManyRelation<TFrom, TTo> { }
public class OneToManyRelation<TFrom, TTo> : IOneToMany {
    public static readonly OneToMany<TFrom, TTo> OneToMany = new();
    public class FromNode(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet)
        : OneProperty<TFrom>(store, parentId, propertyId, nodeData, isSet) { }
    public class ToNodes(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) : ManyProperty<TTo>(store, parentId, propertyId, nodeDatas) { }
    public readonly static FromNode EmptyFrom = new(null, Guid.Empty, Guid.Empty, null, false);
    public readonly static ToNodes EmptyTo = new(null, Guid.Empty, Guid.Empty, null);
}
public class ManyMany<T> : IManyMany {
    public class Nodes(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) : ManyProperty<T>(store, parentId, propertyId, nodeDatas) { }
    public readonly static Nodes Empty = new(null, Guid.Empty, Guid.Empty, null);
}
public class ManyToMany<TFrom, TTo> : IManyToMany {
    public class FromNodes(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) : ManyProperty<TFrom>(store, parentId, propertyId, nodeDatas) { }
    public class ToNodes(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) : ManyProperty<TTo>(store, parentId, propertyId, nodeDatas) { }
    public readonly static FromNodes EmptyFrom = new(null, Guid.Empty, Guid.Empty, null);
    public readonly static ToNodes EmptyTo = new(null, Guid.Empty, Guid.Empty, null);
}

public class OneOne<T, TRelationSelfReference> : IOneOne
    where TRelationSelfReference : OneOne<T, TRelationSelfReference> {
    public class Node(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet)
        : OneProperty<T>(store, parentId, propertyId, nodeData, isSet) { }
    public readonly static Node Empty = new(null, Guid.Empty, Guid.Empty, null, false);
}
public class OneToOne<TFrom, TTo, TRelationSelfReference> : IOneToOne
     where TRelationSelfReference : OneToOne<TFrom, TTo, TRelationSelfReference> {
    public class FromNode(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet)
        : OneProperty<TFrom>(store, parentId, propertyId, nodeData, isSet) { }
    public class ToNode(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet)
        : OneProperty<TTo>(store, parentId, propertyId, nodeData, isSet) { }
    public readonly static FromNode EmptyFrom = new(null, Guid.Empty, Guid.Empty, null, false);
    public readonly static ToNode EmptyTo = new(null, Guid.Empty, Guid.Empty, null, false);
}
public class OneToMany<TFrom, TTo, TRelationSelfReference> : OneToManyRelation<TFrom, TTo, TRelationSelfReference>
    where TRelationSelfReference : OneToMany<TFrom, TTo, TRelationSelfReference> {
}
public class OneToManyRelation<TFrom, TTo, TRelationSelfReference> : IOneToMany
where TRelationSelfReference : OneToMany<TFrom, TTo, TRelationSelfReference> {
    public static readonly OneToMany<TFrom, TTo, TRelationSelfReference> OneToMany = new();
    public class FromNode(NodeStore? store, Guid parentId, Guid propertyId, INodeData? nodeData, bool? isSet)
        : OneProperty<TFrom>(store, parentId, propertyId, nodeData, isSet) { }
    public class ToNodes(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) : ManyProperty<TTo>(store, parentId, propertyId, nodeDatas) { }
    public readonly static FromNode EmptyFrom = new(null, Guid.Empty, Guid.Empty, null, false);
    public readonly static ToNodes EmptyTo = new(null, Guid.Empty, Guid.Empty, null);
}
public class ManyMany<T, TRelationSelfReference> : IManyMany
    where TRelationSelfReference : ManyMany<T, TRelationSelfReference> {
    public class Nodes(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) : ManyProperty<T>(store, parentId, propertyId, nodeDatas) { }
    public readonly static Nodes Empty = new(null, Guid.Empty, Guid.Empty, null);
}
public class ManyToMany<TFrom, TTo, TRelationSelfReference> : IManyToMany
    where TRelationSelfReference : ManyToMany<TFrom, TTo, TRelationSelfReference> {
    public class FromNodes(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) : ManyProperty<TFrom>(store, parentId, propertyId, nodeDatas) { }
    public class ToNodes(NodeStore? store, Guid parentId, Guid propertyId, INodeData[]? nodeDatas) : ManyProperty<TTo>(store, parentId, propertyId, nodeDatas) { }
    public readonly static FromNodes EmptyFrom = new(null, Guid.Empty, Guid.Empty, null);
    public readonly static ToNodes EmptyTo = new(null, Guid.Empty, Guid.Empty, null);
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
