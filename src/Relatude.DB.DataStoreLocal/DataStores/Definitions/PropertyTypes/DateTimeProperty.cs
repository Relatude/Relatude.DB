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
    public override void ValidateValue(object value) {
        var v = (DateTime)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public override object GetDefaultValue() => DefaultValue;
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
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
