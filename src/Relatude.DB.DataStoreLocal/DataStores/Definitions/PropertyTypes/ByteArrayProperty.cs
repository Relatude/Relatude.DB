using System.Collections;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
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
        public override IRangeIndex? ValueIndex => null;
        public override void ValidateValue(object value) { }
        public override object GetDefaultValue() => Array.Empty<byte>();
        public override bool CanBeFacet() => false;
        public override Facets GetDefaultFacets(Facets? given) {
            throw new NotSupportedException("ByteArrayProperty cannot be used as a facet. ");
        }
        public override IdSet FilterFacets(Facets facets, IdSet nodeIds) {
            throw new NotSupportedException("ByteArrayProperty cannot be used as a facet. ");
        }
        public override void CountFacets(IdSet nodeIds, Facets facets) {
            throw new NotSupportedException("ByteArrayProperty cannot be used as a facet. ");
        }
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
