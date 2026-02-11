using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;
internal class IntegerProperty : ValueProperty<int>, IPropertyContainsValue {
    public IntegerProperty(IntegerPropertyModel pm, Definition def) : base(pm, def) {
        MinValue = pm.MinValue;
        MaxValue = pm.MaxValue;
        DefaultValue = pm.DefaultValue;
    }
    protected override void WriteValue(int v, IAppendStream stream) => stream.WriteInt(v);
    protected override int ReadValue(IReadStream stream) => stream.ReadInt();
    public override PropertyType PropertyType => PropertyType.Integer;
    public readonly int DefaultValue;
    public readonly int MinValue = int.MinValue;
    public readonly int MaxValue = int.MaxValue;
    public override void ValidateValue(object value) {
        var v = (int)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public override object GetDefaultValue() => DefaultValue;
    public static object GetValue(byte[] bytes) => BitConverter.ToInt32(bytes, 0);
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = IntegerPropertyModel.ForceValueType(value1, out _);
        var v2 = IntegerPropertyModel.ForceValueType(value2, out _);
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
        if (v1 is int i1 && v2 is int i2) return i1 == i2;
        return false;
    }
}
