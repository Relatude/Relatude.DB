using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class BooleanProperty : ValueProperty<bool> {
    public BooleanProperty(BooleanPropertyModel pm, Definition def) : base(pm, def) {
        DefaultValue = pm.DefaultValue;
    }
    protected override void WriteValue(bool v, IAppendStream stream) => stream.WriteBool(v);
    protected override bool ReadValue(IReadStream stream) => stream.ReadBool();
    public bool DefaultValue;
    public override PropertyType PropertyType => PropertyType.Boolean;
    public override void ValidateValue(object value) { }
    public override object GetDefaultValue() => DefaultValue;
    public static object GetValue(byte[] bytes) => BitConverter.ToBoolean(bytes, 0);
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = BooleanPropertyModel.ForceValueType(value1, out _);
        var v2 = BooleanPropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            _ => throw new NotSupportedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if( v1 is bool b1 && v2 is bool b2 ) return b1 == b2;
        return false;
    }
}
