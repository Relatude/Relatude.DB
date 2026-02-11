using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class DateTimeOffsetProperty : ValueProperty<DateTimeOffset> {
    public DateTimeOffsetProperty(DateTimeOffsetPropertyModel pm, Definition def) : base(pm, def) {
        MinValue = pm.MinValue;
        MaxValue = pm.MaxValue;
        DefaultValue = pm.DefaultValue;
    }
    protected override void WriteValue(DateTimeOffset v, IAppendStream stream) => stream.WriteDateTimeOffset(v);
    protected override DateTimeOffset ReadValue(IReadStream stream) => stream.ReadDateTimeOffset();
    public override PropertyType PropertyType => PropertyType.DateTimeOffset;
    public DateTimeOffset DefaultValue;
    public DateTimeOffset MinValue = DateTimeOffset.MinValue;
    public DateTimeOffset MaxValue = DateTimeOffset.MaxValue;
    public override void ValidateValue(object value) {
        var v = (DateTimeOffset)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public override object GetDefaultValue() => DefaultValue;
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = DateTimeOffsetPropertyModel.ForceValueType(value1, out _);
        var v2 = DateTimeOffsetPropertyModel.ForceValueType(value2, out _);
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
        if (v1 is DateTimeOffset dt1 && v2 is DateTimeOffset dt2) return dt1 == dt2;
        return false;
    }
}
