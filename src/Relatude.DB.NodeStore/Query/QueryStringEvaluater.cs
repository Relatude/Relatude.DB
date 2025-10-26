using Relatude.DB.Query.Data;
using System.Reflection;
using Relatude.DB.Nodes;
using System.Collections;

namespace Relatude.DB.Query;
internal sealed class QueryStringEvaluater {
    readonly string _query;
    readonly NodeStore _store;
    readonly IEnumerable<Parameter> _parameters;
    internal QueryStringEvaluater(NodeStore store, string query, IEnumerable<Parameter> parameters) {
        _store = store;
        _query = query;
        _parameters = parameters;
    }
    internal async Task<object?> EvaluateForJsonAsync() {
        var data = await toDataAsync();
        if (data is ICollectionData coll) {
            var values = toEnumerable<object>(coll);
            if (data is FacetQueryResultData facet) {
                return new ResultSetFacetsNotEnumerable<object>(values, facet);
            } else {
                return new ResultSetNotEnumerable<object>(values, coll.Count, coll.TotalCount, coll.PageIndexUsed, coll.PageSizeUsed, coll.DurationMs, false);
            }
        } else if (data is ISearchQueryResultData search) {
            var hitValues = search.Hits.Select(h => new SearchResultHit<object>(_store.Mapper.CreateObjectFromNodeData(h.NodeData), h.Score, h.Sample));
            return new ResultSetNotEnumerable<object>(hitValues, search.Count, search.TotalCount, search.PageIndexUsed, search.PageSizeUsed, search.DurationMs, search.Capped);
        } else {
            return data;
        }
    }

    internal async Task<ResultSet<T>> EvaluateSetAsync<T>() {
        var data = (ICollectionData)(await toDataAsync())!;
        var enumerable = toEnumerable<T>(data);
        if (data is FacetQueryResultData facet) {
            return new ResultSetFacets<T>(enumerable, facet);
        } else {
            return new ResultSet<T>(enumerable, data.Count, data.TotalCount, data.PageIndexUsed, data.PageSizeUsed, data.DurationMs);
        }
    }
    internal ResultSet<T> EvaluateSet<T>() {
        var data = (ICollectionData)toData()!;
        var enumerable = toEnumerable<T>(data);
        if (data is FacetQueryResultData facet) {
            return new ResultSetFacets<T>(enumerable, facet);
        } else {
            return new ResultSet<T>(enumerable, data.Count, data.TotalCount, data.PageIndexUsed, data.PageSizeUsed, data.DurationMs);
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

    IEnumerable<T> toEnumerable<T>(ICollectionData data) {
        // temporary solution, should be replaced with a more efficient code
        // a collection of this type indicates that return type is a Node object, so use mapper to create the object
        if (data is IStoreNodeDataCollection coll) {
            foreach (var nd in coll.NodeValues) yield return _store.Mapper.CreateObjectFromNodeData<T>(nd);
            yield break;
        }
        // a collection of this type indicates that return type is a plain value type
        if (data is ValueCollectionData vc) {
            foreach (var f in vc.Values) yield return (T)f;
            yield break;
        }
        if (data is FacetQueryResultData facets) {
            foreach (var f in facets.Result.NodeValues) yield return _store.Mapper.CreateObjectFromNodeData<T>(f);
            yield break;
        }
        // a collection of this type is more complicated...
        if (data is ObjectCollection oc) {
            ConstructorInfo? ctor = null;
            var n = 0;
            Dictionary<string, int>? propNameById = null;
            // room for optimazation here...
            foreach (var o in oc.Objects) {
                if (o is ObjectData od) {
                    if (ctor == null) ctor = typeof(T).GetConstructors().Single();
                    if (propNameById == null) propNameById = ctor.GetParameters().ToDictionary(p => p.Name == null ? "" : p.Name, p => n++);
                    var values = od.GetValues(_store.Mapper.CreateObjectFromNodeData);
                    yield return (T)createAnonymousInstance(values!, propNameById, ctor);
                } else if (o is IStoreNodeData no) {
                    yield return _store.Mapper.CreateObjectFromNodeData<T>(no.NodeData);
                } else if (o is IEnumerable<IStoreNodeData> os) {
                    var t = typeof(T);
                    if (t.IsArray) {
                        var tNode = typeof(T).GetElementType();
                        if (tNode == null) throw new NotSupportedException();
                        var array = Array.CreateInstance(tNode, os.Count());
                        int i = 0;
                        foreach (var nd in os) array.SetValue(_store.Mapper.CreateObjectFromNodeData(nd.NodeData), i++);
                        yield return (T)(object)array;
                    } else if (typeof(IEnumerable).IsAssignableFrom(t) && t.IsGenericType) {
                        var tNode = typeof(T).GetGenericArguments().Single();
                        var listType = typeof(List<>).MakeGenericType([tNode]);
                        var ilist = Activator.CreateInstance(listType) as IList;
                        if (ilist == null) throw new NotSupportedException();
                        foreach (var nd in os) ilist.Add(_store.Mapper.CreateObjectFromNodeData(nd.NodeData));
                        yield return (T)ilist;
                    } else {
                        throw new NotSupportedException();
                    }
                } else {
                    yield return (T)o;
                }
            }
            yield break;
        }
        throw new NotSupportedException();
    }

    static object createAnonymousInstance(Tuple<string, object>[] values, Dictionary<string, int> propNameById, ConstructorInfo ctor) {
        // temporary solution, should be replaced with a more efficient code
        object[] args = new object[values.Length];
        foreach (var v in values) args[propNameById[v.Item1]] = v.Item2;
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
    Task<object?> toDataAsync() => _store.Datastore.QueryAsync(_query, _parameters);
    object? toData() => _store.Datastore.Query(_query, _parameters);
}

