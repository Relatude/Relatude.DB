using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;

/// <summary>
/// Where the nodes of a result are, by a geo coordinate property of theirs (<see cref="ICoordinateSource"/>):
/// the id and the position of every node that has one. AddValueBucket / AddRangeBucket chain onto
/// it to bucket the result by other properties as <see cref="BucketsMethod"/> does, so the points
/// can be coloured by a value in the same answer. Evaluated against the collection it was chained
/// onto, or the selection of a facet clause; no node is read.
/// </summary>
public class CoordinatesMethod : IExpression, IBucketBuilder {
    readonly IExpression _input;
    readonly Datamodel _dm;
    readonly string _property;
    readonly Guid _propertyId;
    readonly List<BucketBy> _by = new();
    int _maxBuckets = BucketGroups.DefaultMaxBuckets;
    bool _includeMissing = true;

    public CoordinatesMethod(IExpression input, Datamodel dm, string property) {
        _input = input;
        _dm = dm;
        _property = property;
        _propertyId = BucketGroups.PropertyId(dm, property);
        var type = dm.Properties[_propertyId].PropertyType;
        if (type != PropertyType.GeoCoordinate)
            throw new Exception("Coordinates() needs a GeoCoordinate property to place the nodes by; \"" + dm.Properties[_propertyId].CodeName + "\" holds " + type + ". ");
    }
    public void AddBucket(string property, bool? isRange) => _by.Add(new BucketBy(property, BucketGroups.PropertyId(_dm, property), isRange));
    public void SetBucketOptions(int maxBuckets, bool includeMissing) {
        if (maxBuckets < 0) throw new ArgumentOutOfRangeException(nameof(maxBuckets), "Max buckets must be 0 (no limit) or more. ");
        _maxBuckets = maxBuckets;
        _includeMissing = includeMissing;
    }

    public object Evaluate(IVariables vars) {
        var nodes = TerminalSource.Nodes(_input, vars, "Coordinates");
        if (nodes is not ICoordinateSource source) throw new Exception("Coordinates() cannot read the positions of this collection without reading its nodes. ");
        var located = source.Coordinates(_propertyId, vars.Context);
        // the buckets hold every node of the collection, located or not; a consumer placing the
        // points keeps the ones it has a place for
        var groups = BucketGroups.Of(nodes, _by, _maxBuckets, _includeMissing, vars.Context, "Coordinates");
        return new CoordinatesQueryResultData(_propertyId, located, nodes.Count, nodes.TotalCount, groups);
    }
    public override string ToString() => _input + ".Coordinates(" + _property.ToStringLiteral() + ")" + BucketGroups.Clauses(_by, _maxBuckets, _includeMissing);
}
