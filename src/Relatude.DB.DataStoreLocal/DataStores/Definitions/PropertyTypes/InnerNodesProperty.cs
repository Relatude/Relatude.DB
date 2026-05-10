using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class EmbeddedProperty : Property {
    public EmbeddedProperty(PropertyModel pm, Definition def) : base(pm, def) {
    }
    public override PropertyType PropertyType => PropertyType.Embedded;
    public override object ForceValueType(object value, out bool changed) {
        if (value is IInnerNodeDataMap ndm) {
            changed = false;
            return ndm;
        }
        changed = true;
        return null!;
    }
    public override void ValidateValue(object value) {
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
    }
    public override bool AreValuesEqual(object v1, object v2) {
        throw new NotImplementedException();
    }
}
