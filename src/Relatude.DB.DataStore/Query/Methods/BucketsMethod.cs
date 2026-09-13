using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;

/// <summary>
/// The ids of a result and, per property asked for, which of that property's buckets each of them is
/// in: a value per bucket, or a range of values, the way a facet has them (<see cref="IBucketSource"/>).
/// Built up by the parsed chain - Buckets("A", "B") for the properties' own choice of values or
/// ranges, AddValueBucket / AddRangeBucket to choose, SetBucketOptions for the ceiling, SortBy for
/// the order the ids would come in by an indexed property - like <see cref="PivotMethod"/>, and
/// evaluated against the collection it was chained onto, or the selection of a facet clause. What a
/// picture that places and colours every node of a result needs, without one node being read.
/// </summary>
public class BucketsMethod : IExpression, IBucketBuilder {
    readonly IExpression _input;
    readonly Datamodel _dm;
    readonly List<BucketBy> _by = new();
    int _maxBuckets = BucketGroups.DefaultMaxBuckets;
    bool _includeMissing = true;
    (string Property, Guid PropertyId, bool Descending)? _sort;

    public BucketsMethod(IExpression input, Datamodel dm, IEnumerable<string> properties) {
        _input = input;
        _dm = dm;
        foreach (var property in properties) AddBucket(property, null);
    }
    public void AddBucket(string property, bool? isRange) => _by.Add(new BucketBy(property, BucketGroups.PropertyId(_dm, property), isRange));
    public void SetBucketOptions(int maxBuckets, bool includeMissing) {
        if (maxBuckets < 0) throw new ArgumentOutOfRangeException(nameof(maxBuckets), "Max buckets must be 0 (no limit) or more. ");
        _maxBuckets = maxBuckets;
        _includeMissing = includeMissing;
    }
    public void SortBy(string property, bool descending) => _sort = (property, BucketGroups.PropertyId(_dm, property), descending);

    public object Evaluate(IVariables vars) {
        var nodes = TerminalSource.Nodes(_input, vars, "Buckets");
        var ids = nodes.NodeIds.ToArray();
        // the buckets first, on the collection as it came: sorting it below gives it a new set,
        // against which nothing cached for the original could be reused
        var groups = BucketGroups.Of(nodes, _by, _maxBuckets, _includeMissing, vars.Context, "Buckets");
        // the order by the sort property's index - the same reorder OrderBy uses, so it works on the
        // selection of a facet clause too, which no OrderBy can follow - as positions into the ids;
        // a node the index has no value for comes last, in the result's own order
        int[]? order = null;
        if (_sort is { } sort && ids.Length > 0 && nodes.TryOrderByIndexes(_dm.Properties[sort.PropertyId].CodeName, sort.Descending)) {
            order = IdPositions.OrderOf(ids, nodes.NodeIds);
        }
        return new BucketsQueryResultData(ids, order, nodes.TotalCount, groups);
    }
    public override string ToString() {
        var text = _input + ".Buckets()" + BucketGroups.Clauses(_by, _maxBuckets, _includeMissing);
        if (_sort is { } sort) text += ".SortBy(" + sort.Property.ToStringLiteral() + (sort.Descending ? ", true)" : ")");
        return text;
    }
}
