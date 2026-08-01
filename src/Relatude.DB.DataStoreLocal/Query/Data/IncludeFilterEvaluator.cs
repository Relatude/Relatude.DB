using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Data;
/// <summary>
/// Evaluates the filter of one include branch against candidate related node ids.
/// Index expressible filter parts are evaluated once as a set over all nodes of the related type
/// (cached in the SetRegister); remaining parts are evaluated per node, memoized.
/// Created per query execution, used inside the store read lock.
/// </summary>
internal sealed class IncludeFilterEvaluator {
    readonly DataStoreLocal _db;
    readonly QueryContext _ctx;
    readonly Metrics _metrics;
    IdSet? _nativeKeep; // ids of the related type passing the index expressible filter parts
    List<BoundIncludeFilter>? _rowFilters; // filter parts that need per node evaluation
    Dictionary<int, bool>? _rowMemo;

    IncludeFilterEvaluator(DataStoreLocal db, QueryContext ctx, Metrics metrics) {
        _db = db;
        _ctx = ctx;
        _metrics = metrics;
    }
    public static IncludeFilterEvaluator? Create(IncludeBranch branch, DataStoreLocal db, QueryContext ctx, Metrics metrics) {
        if (branch.Filter == null) return null;
        List<BoundIncludeFilter> filters = [];
        flatten(branch.Filter, filters);
        if (filters.Count == 0) return null;
        var evaluator = new IncludeFilterEvaluator(db, ctx, metrics);
        evaluator.prepare(branch.PropertyId, filters);
        return evaluator;
    }
    static void flatten(IIncludeFilter filter, List<BoundIncludeFilter> into) {
        if (filter is BoundIncludeFilter b) into.Add(b);
        else if (filter is CompositeIncludeFilter c) foreach (var f in c.Filters) flatten(f, into);
        else throw new Exception("Unknown include filter type: " + filter.GetType().FullName);
    }
    void prepare(Guid propertyId, List<BoundIncludeFilter> filters) {
        var def = _db._definition;
        var prop = def.Datamodel.Properties[propertyId];
        if (prop is not RelationPropertyModel relProp) {
            // reference properties have no single well known related type, so no index fast path:
            _rowFilters = filters;
            return;
        }
        // try to evaluate as much as possible with indexes, over all context visible nodes of the related type:
        var nodeType = def.NodeTypes[relProp.NodeTypeOfRelated];
        var allOfType = def.GetAllIdsForType(relProp.NodeTypeOfRelated, _ctx);
        var coll = new NodeCollectionData(_db, _ctx, _metrics, allOfType, nodeType, null);
        foreach (var filter in filters) {
            var scope = filter.Vars.CreateScope();
            scope.DeclarerAndSetConstant(filter.Lambda.Parameters.Single(), coll);
            var filtered = coll.FilterAsMuchAsPossibleUsingIndexes(scope, filter.Lambda.Body, out var remainingFilter);
            if (!ReferenceEquals(filtered, coll) && filtered is NodeCollectionData filteredColl) {
                _nativeKeep = _nativeKeep == null ? filteredColl.Ids : def.Sets.Intersection(_nativeKeep, filteredColl.Ids);
            }
            if (remainingFilter != null) {
                (_rowFilters ??= []).Add(new BoundIncludeFilter(new LambdaExpression(filter.Lambda.Parameters, remainingFilter), filter.Vars));
            }
        }
    }
    public bool Keep(int id) {
        if (_nativeKeep != null && !_nativeKeep.Has(id)) return false;
        if (_rowFilters == null) return true;
        _rowMemo ??= [];
        if (_rowMemo.TryGetValue(id, out var keep)) return keep;
        keep = evaluateRowFilters(id);
        _rowMemo[id] = keep;
        return keep;
    }
    bool evaluateRowFilters(int id) {
        var def = _db._definition;
        var inner = _db._nodes.Get([id], ref _metrics.DiskReads, ref _metrics.NodesReadFromDisk);
        var outer = _db.ToOuter(inner, _ctx);
        if (outer.Length == 0) return false;
        var node = outer[0];
        var typeModel = def.Datamodel.NodeTypes[node.NodeType];
        var row = new NodeObjectData(_db, node, def, typeModel.AllPropertyIdsByName, _ctx);
        foreach (var filter in _rowFilters!) {
            var scope = filter.Vars.CreateScope();
            var parameterName = filter.Lambda.Parameters.Single();
            scope.Declare(parameterName);
            scope.Set(parameterName, row);
            var result = filter.Lambda.Evaluate(scope);
            if (result is not bool b || !b) return false;
        }
        return true;
    }
}
