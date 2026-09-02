using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using System.Reflection;
using Relatude.DB.Nodes;
using System.Collections;

namespace Relatude.DB.Query;
internal sealed class QueryStringEvaluater {
    readonly string _query;
    readonly NodeStore _store;
    readonly IEnumerable<Parameter> _parameters;
    // The context this query was started with, if any. Null means read with the store default.
    // It has to be carried all the way to the store: it is what the access control, culture and
    // visibility filtering is evaluated against.
    readonly QueryContext? _ctx;
    internal QueryStringEvaluater(NodeStore store, string query, IEnumerable<Parameter> parameters, QueryContext? ctx = null) {
        _store = store;
        _query = query;
        _parameters = parameters;
        _ctx = ctx;
    }
    internal async Task<object?> EvaluateForJsonAsync() {
        var data = await toDataAsync();
        if (data is PivotQueryResultData pivot) { // before the collection branch: a pivot has no rows to enumerate
            PivotNodeValueMapper.MapNodeDataValues(pivot.Result, _store); // relation group values: node data -> typed node objects
            return pivot.Result;
        }
        if (data is ICollectionData coll) {
            var values = toEnumerable<object?>(coll);
            if (data is FacetQueryResultData facet) {
                FacetNodeValueMapper.MapNodeDataValues(facet, _store); // relation facet values: node data -> typed node objects
                return new ResultSetFacetsNotEnumerable<object?>(values, facet);
            } else {
                return new ResultSetNotEnumerable<object?>(values, coll.Count, coll.TotalCount, coll.PageIndexUsed, coll.PageSizeUsed, coll.DurationMs, false, 0);
            }
        } else if (data is ISearchQueryResultData search) {
            var hitValues = search.Hits.Select(h => new SearchResultHit<object?>(_store.Mapper.CreateObjectFromNodeData(h.NodeData, null), h.Score, h.Sample));
            return new ResultSetNotEnumerable<object?>(hitValues, search.Count, search.TotalCount, search.PageIndexUsed, search.PageSizeUsed, search.DurationMs, search.Capped, search.InnerSearchTimeMs);
        } else if (data is IGraphPathResultData path) {
            var pathNodes = path.Nodes.Select(n => _store.Mapper.CreateObjectFromNodeData(n, null)).ToArray();
            return new GraphPathResult<object?>(path.Found, [.. path.NodeIds], pathNodes, path.DurationMs);
        } else {
            return data;
        }
    }

    internal async Task<ResultSet<T?>> EvaluateSetAsync<T>() {
        var data = (ICollectionData)(await toDataAsync())!;
        var enumerable = toEnumerable<T?>(data);
        if (data is FacetQueryResultData facet) {
            FacetNodeValueMapper.MapNodeDataValues(facet, _store); // relation facet values: node data -> typed node objects
            return new ResultSetFacets<T?>(enumerable, facet);
        } else {
            return new ResultSet<T?>(enumerable, data.Count, data.TotalCount, data.PageIndexUsed, data.PageSizeUsed, data.DurationMs, data.DurationMs);
        }
    }
    internal ResultSet<T?> EvaluateSet<T>() {
        var data = (ICollectionData)toData()!;
        var enumerable = toEnumerable<T>(data);
        if (data is FacetQueryResultData facet) {
            FacetNodeValueMapper.MapNodeDataValues(facet, _store); // relation facet values: node data -> typed node objects
            return new ResultSetFacets<T?>(enumerable, facet);
        } else {
            return new ResultSet<T?>(enumerable, data.Count, data.TotalCount, data.PageIndexUsed, data.PageSizeUsed, data.DurationMs, data.DurationMs);
        }
    }

    internal async Task<T> EvaluateValueAsync<T>() {
        var data = await toDataAsync();
        if (data is T rt) return rt;
        throw new NotSupportedException();
    }
    internal T EvaluateValue<T>() {
        var data = toData();
        if (data is T rt) return rt;
        throw new NotSupportedException();
    }

    IEnumerable<T?> toEnumerable<T>(ICollectionData data) {
        // temporary solution, should be replaced with a more efficient code
        // a collection of this type indicates that return type is a Node object, so use mapper to create the object
        if (data is IStoreNodeDataCollection coll) {
            foreach (var nd in coll.NodeValues) yield return _store.Mapper.CreateObjectFromNodeData<T>(nd, null);
            yield break;
        }
        // a collection of this type indicates that return type is a plain value type
        if (data is ValueCollectionData vc) {
            foreach (var f in vc.Values) yield return (T)f;
            yield break;
        }
        if (data is FacetQueryResultData facets) {
            foreach (var f in facets.Result.NodeValues) yield return _store.Mapper.CreateObjectFromNodeData<T>(f, null);
            yield break;
        }
        // a collection of this type is more complicated...
        if (data is ObjectCollection oc) {
            // a projection has to be materialised into something. A caller with a type to fill - an
            // anonymous type from the typed API, a record - gets it constructed; a caller asking for object
            // (JSON results and untyped queries) has no such type, and gets the members as a bag instead.
            var asBag = typeof(T) == typeof(object);
            ConstructorInfo? ctor = null;
            Dictionary<string, int>? argIndexByName = null;
            // room for optimazation here...
            foreach (var o in oc.Objects) {
                if (o is ObjectData od) {
                    var values = od.GetValues(n => _store.Mapper.CreateObjectFromNodeData(n, null));
                    if (asBag) {
                        yield return (T)(object)createValueBag(values);
                        continue;
                    }
                    if (ctor == null) {
                        ctor = projectionConstructor(typeof(T));
                        var parameters = ctor.GetParameters();
                        argIndexByName = new Dictionary<string, int>(parameters.Length);
                        for (var i = 0; i < parameters.Length; i++) argIndexByName[parameters[i].Name ?? string.Empty] = i;
                    }
                    yield return (T)createAnonymousInstance(values, argIndexByName!, ctor);
                } else if (o is IStoreNodeData no) {
                    yield return _store.Mapper.CreateObjectFromNodeData<T>(no.NodeData, null);
                } else if (o is IEnumerable<IStoreNodeData> os) {
                    var t = typeof(T);
                    if (t == typeof(object)) { // no type to fill, as above: the mapped nodes are the value
                        yield return (T)(object)os.Select(nd => _store.Mapper.CreateObjectFromNodeData(nd.NodeData, null)).ToList();
                    } else if (t.IsArray) {
                        var tNode = typeof(T).GetElementType();
                        if (tNode == null) throw new NotSupportedException();
                        var array = Array.CreateInstance(tNode, os.Count());
                        int i = 0;
                        foreach (var nd in os) array.SetValue(_store.Mapper.CreateObjectFromNodeData(nd.NodeData, null), i++);
                        yield return (T)(object)array;
                    } else if (typeof(IEnumerable).IsAssignableFrom(t) && t.IsGenericType) {
                        var tNode = typeof(T).GetGenericArguments().Single();
                        var listType = typeof(List<>).MakeGenericType([tNode]);
                        var ilist = Activator.CreateInstance(listType) as IList;
                        if (ilist == null) throw new NotSupportedException();
                        foreach (var nd in os) ilist.Add(_store.Mapper.CreateObjectFromNodeData(nd.NodeData, null));
                        yield return (T)ilist;
                    } else {
                        throw new NotSupportedException();
                    }
                } else {
                    yield return (T?)o;
                }
            }
            yield break;
        }
        throw new NotSupportedException();
    }

    /// <summary>
    /// The projected members of one row, by name. This is what a projection becomes when the caller has no
    /// type to construct: it serializes as a JSON object, and reads as a dictionary from code.
    /// </summary>
    static Dictionary<string, object?> createValueBag(Tuple<string, object?>[] values) {
        var bag = new Dictionary<string, object?>(values.Length);
        foreach (var v in values) bag[v.Item1] = v.Item2; // indexer, not Add: a projection may repeat a name
        return bag;
    }
    /// <summary>The constructor a projected row is built with, with an explanation when there is none to use.</summary>
    static ConstructorInfo projectionConstructor(Type type) {
        var ctors = type.GetConstructors();
        var problem = ctors.Length switch {
            0 => "it has no public constructor",
            1 when ctors[0].GetParameters().Length == 0 => "its only constructor takes no arguments",
            1 => null,
            _ => "it has more than one public constructor",
        };
        if (problem != null) {
            throw new NotSupportedException("A projected query (Select(x => new { ... })) cannot be returned as "
                + type.FullName + " because " + problem + ". Project into an anonymous type or a record whose "
                + "constructor takes the projected members, or read the result as object to get the members by name.");
        }
        return ctors[0];
    }
    static object createAnonymousInstance(Tuple<string, object?>[] values, Dictionary<string, int> propNameById, ConstructorInfo ctor) {
        // temporary solution, should be replaced with a more efficient code
        object?[] args = new object?[ctor.GetParameters().Length];
        foreach (var v in values) {
            if (!propNameById.TryGetValue(v.Item1, out var index)) {
                throw new NotSupportedException("The projected member \"" + v.Item1 + "\" has no matching constructor "
                    + "argument on " + ctor.DeclaringType?.FullName + ". Project into an anonymous type or a record "
                    + "with the same members, or read the result as object to get the members by name.");
            }
            args[index] = v.Item2;
        }
        foreach (var p in ctor.GetParameters()) {
            if (p.Name == null) throw new NotSupportedException("Parameter name is null.");
            var arg = args[propNameById[p.Name]];
            var t = p.ParameterType;
            if (arg is IEnumerable argEnum && !t.IsAssignableFrom(arg.GetType())) {
                if (t.IsArray) {
                    var vs = argEnum.Cast<object>().ToList();
                    var pType = p.ParameterType.GetElementType();
                    if (pType == null) throw new NotSupportedException();
                    var array = Array.CreateInstance(pType, vs.Count);
                    for (int i = 0; i < vs.Count; i++) array.SetValue(vs[i], i);
                    args[propNameById[p.Name]] = array;
                } else if (typeof(IEnumerable).IsAssignableFrom(t) && t.IsGenericType) {
                    args[propNameById[p.Name]] = ((IEnumerable<object>)arg).ToList();
                    var tNode = t.GetGenericArguments().Single();
                    var listType = typeof(List<>).MakeGenericType([tNode]);
                    var ilist = Activator.CreateInstance(listType) as IList;
                    if (ilist == null) throw new NotSupportedException();
                    foreach (var nd in argEnum) ilist.Add(nd);
                    args[propNameById[p.Name]] = ilist;
                } else {
                    throw new NotSupportedException();
                }
            }
        }
        return ctor.Invoke(args);
    }
    Task<object?> toDataAsync() => _store.Datastore.QueryAsync(_query, _parameters, _ctx);
    object? toData() => _store.Datastore.Query(_query, _parameters, _ctx);
}

