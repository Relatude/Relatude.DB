using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class DateTimeProperty : ValueProperty<DateTime>, IPropertyContainsValue {
    public DateTimeProperty(DateTimePropertyModel pm, Definition def) : base(pm, def) {
        MinValue = pm.MinValue;
        MaxValue = pm.MaxValue;
        DefaultValue = pm.DefaultValue;
    }
    protected override void WriteValue(DateTime v, IAppendStream stream) => stream.WriteDateTimeUtc(v);
    protected override DateTime ReadValue(IReadStream stream) => stream.ReadDateTimeUtc();
    public override PropertyType PropertyType => PropertyType.DateTime;
    public DateTime DefaultValue;
    public DateTime MinValue = DateTime.MinValue;
    public DateTime MaxValue = DateTime.MaxValue;
    public override void ValidateValue(object value, INodeData node) {
        var v = (DateTime)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public override bool SatisfyValueRequirement(object? value1, object? value2, ValueRequirement requirement) {
        var v1 = DateTimePropertyModel.ForceValueType(value1, out _);
        var v2 = DateTimePropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            ValueRequirement.Greater => v1 > v2,
            ValueRequirement.GreaterOrEqual => v1 >= v2,
            ValueRequirement.Less => v1 < v2,
            ValueRequirement.LessOrEqual => v1 <= v2,
            _ => throw new NotSupportedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is DateTime dt1 && v2 is DateTime dt2) return dt1 == dt2;
        return false;
    }
}
/// <summary>
/// The node's own creation or change time as an indexed property of every node type, declared on the
/// base type (see NodeConstants.SystemCreatedUtcPropertyId). The value is never among the node's
/// values: the indexes (Definition.IteratePropertyIndexes) and row evaluation (NodeObjectData) both
/// read it from the node record, and it cannot be written - the database stamps it.
/// </summary>
internal sealed class SystemDateTimeProperty : DateTimeProperty {
    readonly bool _created;
    public SystemDateTimeProperty(DateTimePropertyModel pm, Definition def) : base(pm, def) {
        _created = pm.Id == NodeConstants.SystemCreatedUtcPropertyId;
    }
    // must be a pure function of the node data: index and de-index of the same node have to agree
    public DateTime ValueOf(INodeData node) {
        if (node is NodeDataRevisions revs) return valueOf(revs);
        return _created ? node.CreatedUtc : node.ChangedUtc;
    }
    // A node with revisions has a date per revision. Queries return the published ones, so they
    // decide - all of them when none is published: the first creation and the last change.
    DateTime valueOf(NodeDataRevisions revs) {
        var anyPublished = revs.Revisions.Any(r => r.RevisionType == RevisionType.Published);
        DateTime? result = null;
        foreach (var rev in revs.Revisions) {
            if (anyPublished && rev.RevisionType != RevisionType.Published) continue;
            var v = _created ? rev.CreatedUtc : rev.ChangedUtc;
            if (result == null || (_created ? v < result.Value : v > result.Value)) result = v;
        }
        return result ?? DateTime.MinValue;
    }
    public override void ValidateValue(object value, INodeData node) {
        throw new Exception("The property " + CodeName + " is the node's own " + (_created ? "creation" : "change")
            + " time. It is maintained by the database and cannot be written. ");
    }
}
