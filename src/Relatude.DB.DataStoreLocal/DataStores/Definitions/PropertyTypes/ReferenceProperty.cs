using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class ReferenceProperty : ValueProperty<Guid> {
    public ReferenceProperty(ReferencePropertyModel pm, Definition def) : base(pm, def) {
        DefaultValue = pm.DefaultValue;
    }
    protected override void WriteValue(Guid v, IAppendStream stream) => stream.WriteGuid(v);
    protected override Guid ReadValue(IReadStream stream) => stream.ReadGuid();
    public Guid DefaultValue;
    public override PropertyType PropertyType => PropertyType.Reference;
    public override void ValidateValue(object value) { }
    public static object GetValue(byte[] bytes) => new Guid(bytes);
    public override bool SatisfyValueRequirement(object? value1, object? value2, ValueRequirement requirement) {
        var v1 = (Guid)value1!;
        var v2 = (Guid)value2!;
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            _ => throw new NotSupportedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is Guid g1 && v2 is Guid g2) return g1 == g2;
        return false;
    }
}

