using Relatude.DB.AI;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes {
    internal class FloatArrayProperty : Property {
        bool _isSystemVectorIndexPropertyId = false;
        public FloatArrayProperty(FloatArrayPropertyModel pm, Definition def) : base(pm, def) {
            _isSystemVectorIndexPropertyId = pm.Id == NodeConstants.SystemVectorIndexPropertyId;
        }
        SemanticIndex? _index;
        Dictionary<string, SemanticIndex>? _indexByCulture;
        public SemanticIndex GetIndex(QueryContext ctx) {
            if (Model.CultureSensitive) {
                if (_indexByCulture is null) throw new Exception("The property " + CodeName + " is culture sensitive but no indexes by culture were initialized. ");
                if (ctx.CultureCode is null) throw new Exception("The property " + CodeName + " is culture sensitive but the query context does not have a culture code. ");
                if (_indexByCulture!.TryGetValue(ctx.CultureCode!, out var index)) return index;
                throw new Exception("The property " + CodeName + " is culture sensitive but no index was found for culture code " + ctx.CultureCode + ". ");
            } else {
                if (_index is null) throw new Exception("The property " + CodeName + " is not culture sensitive but no index was initialized. ");
                return _index;
            }
        }
        internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
            if (Indexed && ai != null) {
                var indexes = IndexFactory.CreateSemanticIndexes(store, ai, this, null);
                if (indexes.Count == 0) throw new Exception("No indexes were created for the property " + CodeName + ". ");
                if (!Model.CultureSensitive) _index = indexes.First().Value;
                else _indexByCulture = indexes;
                Indexes.AddRange(indexes.Values);

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
