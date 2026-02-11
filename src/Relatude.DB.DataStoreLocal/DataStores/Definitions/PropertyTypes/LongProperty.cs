using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;
internal class LongProperty : ValueProperty<long>, IPropertyContainsValue {
    public LongProperty(LongPropertyModel pm, Definition def) : base(pm, def) {
        MinValue = pm.MinValue;
        MaxValue = pm.MaxValue;
        DefaultValue = pm.DefaultValue;
    }
    protected override void WriteValue(long v, IAppendStream stream) => stream.WriteLong(v);
    protected override long ReadValue(IReadStream stream) => stream.ReadLong();
    public override PropertyType PropertyType => PropertyType.Long;
    public long DefaultValue;
    public long MinValue = long.MinValue;
    public long MaxValue = long.MaxValue;
    public override void ValidateValue(object value) {
        var v = (long)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public override object GetDefaultValue() => DefaultValue;
    public static object GetValue(byte[] bytes) => BitConverter.ToInt32(bytes, 0);
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = LongPropertyModel.ForceValueType(value1, out _);
        var v2 = LongPropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            ValueRequirement.Less => v1 < v2,
            ValueRequirement.LessOrEqual => v1 <= v2,
            ValueRequirement.Greater => v1 > v2,
            ValueRequirement.GreaterOrEqual => v1 >= v2,
            _ => throw new NotImplementedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is long l1 && v2 is long l2) return l1 == l2;
        return false;
    }
}
