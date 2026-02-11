using Relatude.DB.AI;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes {
    internal class ByteArrayProperty : Property {
        public ByteArrayProperty(ByteArrayPropertyModel pm, Definition def) : base(pm, def) {
        }
        internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        }
        public override PropertyType PropertyType => PropertyType.ByteArray;
        public override object ForceValueType(object value, out bool changed) {
            return ByteArrayPropertyModel.ForceValueType(value, out changed);
        }
        public override void ValidateValue(object value) { }
        public override object GetDefaultValue() => Array.Empty<byte>();
        public override bool AreValuesEqual(object v1, object v2) {
            var b1 = ByteArrayPropertyModel.ForceValueType(v1, out _);
            var b2 = ByteArrayPropertyModel.ForceValueType(v2, out _);
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
