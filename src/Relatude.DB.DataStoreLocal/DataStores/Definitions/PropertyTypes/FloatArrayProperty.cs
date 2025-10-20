using Relatude.DB.AI;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes {
    internal class FloatArrayProperty : Property {
        bool _isSystemVectorIndexPropertyId = false;
        public FloatArrayProperty(FloatArrayPropertyModel pm, Definition def) : base(pm, def) {
            _isSystemVectorIndexPropertyId = pm.Id == NodeConstants.SystemVectorIndexPropertyId;
        }
        public SemanticIndex? Index { get; private set; }
        internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
            if (Indexed && ai != null) {
                Index = new SemanticIndex(def.Sets, Id.ToString(), ai, store);
                Indexes.Add(Index);
            }
        }
        public override PropertyType PropertyType => PropertyType.FloatArray;
        public override object ForceValueType(object value, out bool changed) {
            return FloatArrayPropertyModel.ForceValueType(value, out changed);
        }
        public override bool IsNodeRelevantForIndex(INodeData node, IIndex index) {
            if (!_isSystemVectorIndexPropertyId) return true;
            return Definition.NodeTypes[node.NodeType].Model.SemanticIndex!.Value;
        }
        public override void ValidateValue(object value) { }
        public override object GetDefaultValue() => Array.Empty<float>();
        public override bool AreValuesEqual(object v1, object v2) {
            var b1 = FloatArrayPropertyModel.ForceValueType(v1, out _);
            var b2 = FloatArrayPropertyModel.ForceValueType(v2, out _);
            if (b1 == null && b2 == null) return true;
            if (b1 == null || b2 == null) return false;
            if (b1.Length != b2.Length) return false;
            for (int i = 0; i < b1.Length; i++) {
                if (b1[i] != b2[i]) return false;
            }
            return true;
        }
    }
}
