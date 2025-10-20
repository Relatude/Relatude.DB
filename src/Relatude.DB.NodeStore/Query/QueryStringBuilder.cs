using Relatude.DB.Common;
using Relatude.DB.Query.Data;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Relatude.DB.Nodes;
using Relatude.DB.Datamodels;
using System.Collections;
using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
using System.Globalization;
using Relatude.DB.Transactions;

namespace Relatude.DB.Query;
internal sealed class QueryStringBuilder {
    internal StringBuilder _sb;
    internal readonly List<Parameter> _parameters;
    internal QueryStringBuilder(NodeStore store, string sourceName, List<Parameter>? parameters = null) {
        Store = store;
        this._parameters = parameters is null ? [] : parameters;
        _sb = new StringBuilder(sourceName);
    }
    internal NodeStore Store { get; }
    internal QueryStringBuilder(NodeStore store, StringBuilder sb, List<Parameter> parameters) {
        Store = store;
        _sb = sb;
        _parameters = parameters;
    }
    void add(string method, params object[]? args) {
        _sb.Append('.');
        _sb.Append(method);
        _sb.Append('(');
        if (args != null) {
            bool first = true;
            foreach (var arg in args) {
                if (!first) _sb.Append(", ");
                else first = false;
                var pName = "Param" + _parameters.Count;
                _sb.Append(pName);
                _parameters.Add(new Parameter(pName, arg));
            }
        }
        _sb.Append(')');
    }
    internal QueryStringEvaluater Prepare() => new QueryStringEvaluater(Store, getQueryString(), _parameters);
    internal void Page(int pageIndex, int pageSize) => add("Page", pageIndex, pageSize);
    internal void Take(int count) => add("Take", count);
    internal void Skip(int offset) => add("Skip", offset);
    IEnumerable<T> toEnumerable<T>(ICollectionData data) {
        // temporary solution, should be replaced with a more efficient code
        // a collection of this type indicates that return type is a Node object, so use mapper to create the object
        if (data is IStoreNodeDataCollection coll) {
            foreach (var nd in coll.NodeValues) yield return Store.Mapper.CreateObjectFromNodeData<T>(nd);
            yield break;
        }
        // a collection of this type indicates that return type is a plain value type
        if (data is ValueCollectionData vc) {
            foreach (var f in vc.Values) yield return (T)f;
            yield break;
        }
        if (data is FacetQueryResultData facets) {
            foreach (var f in facets.Result.Values) yield return (T)f;
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
                    var values = od.GetValues(Store.Mapper.CreateObjectFromNodeData);
                    yield return (T)createAnonymousInstance(values, propNameById, ctor);
                } else if (o is IStoreNodeData no) {
                    yield return Store.Mapper.CreateObjectFromNodeData<T>(no.NodeData);
                } else if (o is IEnumerable<IStoreNodeData> os) {
                    var t = typeof(T);
                    if (t.IsArray) {
                        var tNode = typeof(T).GetElementType();
                        if (tNode == null) throw new NotSupportedException();
                        var array = Array.CreateInstance(tNode, os.Count());
                        int i = 0;
                        foreach (var nd in os) array.SetValue(Store.Mapper.CreateObjectFromNodeData(nd.NodeData), i++);
                        yield return (T)(object)array;
                    } else if (typeof(IEnumerable).IsAssignableFrom(t) && t.IsGenericType) {
                        var tNode = typeof(T).GetGenericArguments().Single();
                        var listType = typeof(List<>).MakeGenericType([tNode]);
                        var ilist = Activator.CreateInstance(listType) as IList;
                        if (ilist == null) throw new NotSupportedException();
                        foreach (var nd in os) ilist.Add(Store.Mapper.CreateObjectFromNodeData(nd.NodeData));
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
            if (p.Name == null) throw new NotSupportedException("Parameter name is null. ");
            var arg = args[propNameById[p.Name]];
            var t = p.ParameterType;
            if (arg is IEnumerable argEnum && !t.IsAssignableFrom(arg.GetType())) {
                if (t.IsArray) {
                    var vs = argEnum.ToObjectList();
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
    Task<object> toDataAsync() => Store.Datastore.QueryAsync(getQueryString(), _parameters);
    object toData() => Store.Datastore.Query(getQueryString(), _parameters);
    internal async Task<int> CountAsync() {
        add("Count");
        var result = await toDataAsync();
        return (int)result;
    }
    internal int Count() {
        add("Count");
        var result = toData();
        return (int)result;
    }
    internal QueryStringBuilder Sum() {
        add("Sum");
        return this;
    }
    internal QueryStringBuilder Sum<TSource, TResult>(Expression<Func<TSource, TResult>> expression) {
        _sb.Append(".Sum(");
        _sb.Append(expression.ToQueryString(_parameters.Count, out var parameters));
        _parameters.AddRange(parameters);
        _sb.Append(')');
        return this;
    }
    internal void OrderBy<T>(Expression<Func<T, object>> expression, bool descending) {
        _sb.Append(".OrderBy(");
        _sb.Append(expression.ToQueryString(_parameters.Count, out var parameters));
        _parameters.AddRange(parameters);
        if (descending) _sb.Append(", true");
        _sb.Append(')');
    }
    internal void SelectId() => add("SelectId");
    internal void Select(Expression expression) {
        _sb.Append(".Select(");
        // _sb.Append(expression.ToQueryString(_parameters.Count, out var parameters));
        _sb.Append("(Article c) => Param_0");
        // _parameters.AddRange(parameters);
        _parameters.AddRange([new Parameter("Param_0", 1.2)]);
        _sb.Append(')');
    }
    internal void Where<T>(Expression<Func<T, bool>> expression) {
        _sb.Append(".Where(");
        _sb.Append(expression.ToQueryString(_parameters.Count, out var parameters));
        _parameters.AddRange(parameters);
        _sb.Append(')');
    }
    internal void Where(string query) {
        if (query == null) return;
        _sb.Append(".Where(");
        _sb.Append(query);
        _sb.Append(')');
    }
    string writeValue<TProperty>(TProperty value) {
        if (value is string s) return s.ToStringLiteral();
        if (value is Guid g) return g.ToString().ToStringLiteral();
        if (value is bool b) return b.ToString().ToLower();
        if (value is DateTime dt) return dt.Ticks.ToString();
        if (value is Enum e)
            return Convert.ToInt32(e).ToString();
        throw new NotImplementedException();

    }
    internal void WhereIn<TNode, TProperty>(Expression<Func<TNode, TProperty>> vproperty, IEnumerable<TProperty> values) {
        var propertyName = Store.Mapper.GetProperty(vproperty).Id.ToString();
        if (propertyName == null) throw new NotSupportedException();
        WhereIn(propertyName, values);
    }
    internal void WhereIn<TProperty>(string propertyName, IEnumerable<TProperty> values) {
        var valueArray = "[" + string.Join(',', values.Select(writeValue)) + "]";
        _sb.Append(".WhereIn(");
        _sb.Append(propertyName.ToStringLiteral());
        _sb.Append(", ");
        _sb.Append(valueArray);
        _sb.Append(')');
    }
    internal void WhereInIds(IEnumerable<Guid> values) {
        var valueArray = "[" + string.Join(',', values.Select(v => writeValue(v))) + "]";
        _sb.Append(".WhereInIds(");
        _sb.Append(valueArray);
        _sb.Append(')');

    }
    internal void Relates(Guid relationPropertyId, Guid id) {
        Relates(relationPropertyId.ToString(), id.ToString());
    }
    internal void Relates<TNode, TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid id) {
        Relates(Store.Mapper.GetProperty(relationProperty).Id.ToString(), id.ToString());
    }
    internal void RelatesAny<TNode, TProperty>(Expression<Func<TNode, TProperty>> relationProperty, IEnumerable<Guid> ids) {
        var property = Store.Mapper.GetProperty(relationProperty).Id.ToString();
        var guidArray = "[" + string.Join(',', ids) + "]";
        if (property == null) return;
        _sb.Append(".RelatesAny(");
        _sb.Append(property.ToStringLiteral());
        _sb.Append(", ");
        _sb.Append(guidArray.ToStringLiteral());
        _sb.Append(')');
    }
    internal void Relates(string text, string id) {
        if (text == null) return;
        _sb.Append(".Relates(");
        _sb.Append(text.ToStringLiteral());
        _sb.Append(", ");
        _sb.Append(id.ToStringLiteral());
        _sb.Append(')');
    }
    internal void RelatesNot<TNode, TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid id) {
        RelatesNot(Store.Mapper.GetProperty(relationProperty).Id.ToString(), id.ToString());
    }
    internal void RelatesNot(string text, string id) {
        if (text == null) return;
        _sb.Append(".RelatesNot(");
        _sb.Append(text.ToStringLiteral());
        _sb.Append(", ");
        _sb.Append(id.ToStringLiteral());
        _sb.Append(')');
    }

    // Include and ThenInclude methods:
    List<IncludeBranch>? _branches;
    string? _branchMarker;
    internal IncludeBranch CreateBranch<T, TProperty>(Expression<Func<T, TProperty>> expression, int? top) {
        ensureIncludeIsAddedToQuery();
        var b = new IncludeBranch(Store.Mapper.GetProperty(expression).Id, top);
        if (_branches == null) _branches = new();
        _branches.Add(b);
        return b;
    }
    //internal IncludeBranch CreateBranch(Guid relationPropertyId, int? top) {
    //    ensureIncludeIsAddedToQuery();
    //    var b = new IncludeBranch(relationPropertyId, top);
    //    if (_branches == null) _branches = new();
    //    _branches.Add(b);
    //    return b;
    //}
    internal IncludeBranch CreateChildBranch<T, TProperty>(IncludeBranch parent, Expression<Func<T, TProperty>> expression, int? top) {
        return parent.ReuseOrCreateChildBranch(Store.Mapper.GetProperty(expression).Id, top);
    }
    internal void ensureIncludeIsAddedToQuery() {
        if (_branchMarker == null) {
            _branchMarker = Guid.NewGuid().ToString();
            _sb.Append(_branchMarker);
        }
    }

    internal void WhereSearch(string? text, double? semanticRatio = null, float? minimumVectorSimilarity = null, bool? orSearch = null, int? maxWordsEvaluatedWhenFuzzy = null) {
        if (string.IsNullOrEmpty(text)) return;
        _sb.Append(".WhereSearch(");
        _sb.Append(text.ToStringLiteral());
        _sb.Append(", " + (semanticRatio != null ? semanticRatio.Value.ToString(CultureInfo.InvariantCulture) : "null"));
        _sb.Append(", " + (minimumVectorSimilarity != null ? minimumVectorSimilarity.Value.ToString(CultureInfo.InvariantCulture) : "null"));
        _sb.Append(", " + (orSearch != null ? (orSearch.Value ? "true" : "false") : "null"));
        _sb.Append(", " + (maxWordsEvaluatedWhenFuzzy != null ? maxWordsEvaluatedWhenFuzzy.Value.ToString() : "null"));
        _sb.Append(')');
    }
    internal void Search(string? text, double? semanticRatio = null, float? minimumVectorSimilarity = null, bool? orSearch = null, int? maxWordsEvaluatedWhenFuzzy = null, int? maxHitsEvaluatedBeforeRanked = null) {
        if (text == null) return;
        _sb.Append(".Search(");
        _sb.Append(text.ToStringLiteral());
        _sb.Append(", " + (semanticRatio != null ? semanticRatio.Value.ToString(CultureInfo.InvariantCulture) : "null"));
        _sb.Append(", " + (minimumVectorSimilarity != null ? minimumVectorSimilarity.Value.ToString(CultureInfo.InvariantCulture) : "null"));
        _sb.Append(", " + (orSearch != null ? (orSearch.Value ? "true" : "false") : "null"));
        _sb.Append(", " + (maxWordsEvaluatedWhenFuzzy != null ? maxWordsEvaluatedWhenFuzzy.Value.ToString() : "null"));
        _sb.Append(", " + (maxHitsEvaluatedBeforeRanked != null ? maxHitsEvaluatedBeforeRanked.Value.ToString() : "null"));
        _sb.Append(')');
    }
    internal void Where(Guid id) {
        _sb.Append(".Where(\"");
        _sb.Append(id);
        _sb.Append("\")");
    }
    internal void Where(int id) {
        _sb.Append(".Where(");
        _sb.Append(id);
        _sb.Append(')');
    }
    internal void Where(IEnumerable<int> ids) {
        _sb.Append(".Where([");
        bool first = true;
        foreach (var id in ids) {
            if (!first) _sb.Append(", ");
            else first = false;
            _sb.Append(id);
        }
        _sb.Append("])");
    }
    internal void Where(IEnumerable<Guid> ids) {
        _sb.Append(".Where([");
        bool first = true;
        foreach (var id in ids) {
            if (!first) _sb.Append(", ");
            else first = false;
            _sb.Append('\"');
            _sb.Append(id);
            _sb.Append('\"');
        }
        _sb.Append("])");
    }
    string getQueryString() {
        if (_branchMarker == null || _branches == null) return _sb.ToString();
        StringBuilder include = new();
        foreach (var b in _branches) {
            foreach (var path in b.GetPaths()) {
                include.Append(".Include(\"");
                include.Append(path);
                include.Append("\")");
            }
        }
        return _sb.ToString().Replace(_branchMarker, include.ToString());
    }
    public override string ToString() => getQueryString();
    internal void WhereTypes(IEnumerable<Guid> userTypes) {
        if (userTypes.Count() == 0) return;
        add("WhereTypes", userTypes.ToArray());
    }
    internal void Update<TNode, TProperty>(Expression<Func<TNode, TProperty>> property, object newValue) {
        _sb.Append(".Update(");
        _sb.Append(Store.Mapper.GetProperty(property).Id.ToString());
        _sb.Append(", ");
        _sb.Append(newValue.ToString());
        _sb.Append(')');
    }

}

