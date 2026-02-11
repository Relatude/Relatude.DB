using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;
internal class TimeSpanProperty : ValueProperty<TimeSpan>, IPropertyContainsValue {
    public TimeSpanProperty(TimeSpanPropertyModel pm, Definition def) : base(pm, def) {
        MinValue = pm.MinValue;
        MaxValue = pm.MaxValue;
        DefaultValue = pm.DefaultValue;
    }
    protected override void WriteValue(TimeSpan v, IAppendStream stream) => stream.WriteLong(v.Ticks);
    protected override TimeSpan ReadValue(IReadStream stream) => TimeSpan.FromTicks(stream.ReadLong());
    public override PropertyType PropertyType => PropertyType.TimeSpan;
    public TimeSpan DefaultValue;
    public TimeSpan MinValue = TimeSpan.MinValue;
    public TimeSpan MaxValue = TimeSpan.MaxValue;
    public override void ValidateValue(object value) {
        var v = (TimeSpan)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public override object GetDefaultValue() => DefaultValue;
    public static object GetValue(byte[] bytes) => BitConverter.ToInt32(bytes, 0);

    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = TimeSpanPropertyModel.ForceValueType(value1, out _);
        var v2 = TimeSpanPropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            ValueRequirement.Greater => v1 > v2,
            ValueRequirement.GreaterOrEqual=> v1 >= v2,
            ValueRequirement.Less => v1 < v2,
            ValueRequirement.LessOrEqual => v1 <= v2,
            _ => throw new NotSupportedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if(v1 is TimeSpan ts1 && v2 is TimeSpan ts2) return ts1 == ts2;
        return false;
    }
}
