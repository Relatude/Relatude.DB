using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes {
    internal class FileProperty : Property {
        public FileProperty(FilePropertyModel pm, Definition def) : base(pm, def) {
        }
        internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        }
        public override PropertyType PropertyType => PropertyType.ByteArray;
        public override object ForceValueType(object value, out bool changed) {
            return FilePropertyModel.ForceValueType(value, out changed);
        }
        public override object TransformFromOuterToInnerValue(object value, INodeData? oldNodeData) {
            if (value is not FileValue fileValue) throw new Exception("Value is not a file value. ");
            if (oldNodeData == null) return FileValue.Empty; // is insert, so must be empty|
            if (oldNodeData.TryGetValue(Id, out var oldFileValue) && oldFileValue is FileValue oldFv) {
                return FileValue.CreateMerge(oldFv, fileValue); // stripping storage id etc, that is not allowed to be changed
            } else {
                // Old node data does not contain a file value, so only allow empty
                return FileValue.Empty;
            }
        }
        public override IRangeIndex? ValueIndex => null;
        public override void ValidateValue(object value) { }
        public override object GetDefaultValue() => Array.Empty<byte>();
        public static object GetValue(byte[] bytes) => BitConverter.ToBoolean(bytes, 0);
        public override bool CanBeFacet() => false;
        public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
            throw new NotSupportedException("ByteArrayProperty cannot be used as a facet. ");
        }
        public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
            throw new NotSupportedException("ByteArrayProperty cannot be used as a facet. ");
        }
        public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) {
            throw new NotSupportedException("ByteArrayProperty cannot be used as a facet. ");
        }
        public override bool AreValuesEqual(object v1, object v2) {
            var b1 = FilePropertyModel.ForceValueType(v1, out _);
            var b2 = FilePropertyModel.ForceValueType(v2, out _);
            return FileValue.AreValuesEqual(b1, b2);
        }
    }
}
