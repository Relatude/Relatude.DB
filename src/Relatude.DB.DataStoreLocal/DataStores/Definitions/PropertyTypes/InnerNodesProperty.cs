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
    public override void ValidateValue(object value, INodeData node) {
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
    }
    public override bool AreValuesEqual(object v1, object v2) {
        IInnerNodeDataMap? ndm1 = v1 as IInnerNodeDataMap;
        IInnerNodeDataMap? ndm2 = v2 as IInnerNodeDataMap;
        if (ndm1 == null && ndm2 == null) return true;
        if (ndm1 == null || ndm2 == null) return false;
        if (ndm1.Count != ndm2.Count) return false;
        var values1 = ndm1.ToList();
        var values2 = ndm2.ToList();
        for (var i = 0; i < values1.Count; i++) {
            var node1 = values1[i];
            var node2 = values2[i];
            var props1 = node1.Values.ToArray();
            var props2 = node2.Values.ToArray();
            for (var j = 0; j < props1.Length; j++) {
                var p1 = props1[j];
                var p2 = props2[j];
                if (p1.PropertyId != p2.PropertyId) return false;
                var propDef = Definition.Properties[p1.PropertyId];
                if (!propDef.AreValuesEqual(p1.Value, p2.Value)) return false;
            }
        }
        return true;
    }
}
