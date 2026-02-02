using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes
{
    internal class RelationProperty : Property {
        public RelationProperty(RelationPropertyModel pm, Definition def) : base(pm, def) {
            RelationId = pm.RelationId;
            RelModel = pm;
        }

        public override PropertyType PropertyType => PropertyType.Relation;
        public Guid RelationId { get; }
        public RelationPropertyModel RelModel { get; }

        public override bool CanBeFacet() {
            return false;
            throw new NotImplementedException();
        }
        public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) {
            throw new NotImplementedException();
        }

        public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
            throw new NotImplementedException();
        }

        public override object ForceValueType(object value, out bool changed) {
            throw new NotImplementedException();
        }

        public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
            throw new NotImplementedException();
        }

        public override object GetDefaultValue() {
            throw new NotImplementedException();
        }

        public override void ValidateValue(object value) {
            throw new NotImplementedException();
        }

        internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
            //throw new NotImplementedException();
        }
        public override IRangeIndex? ValueIndex => null;
        public override bool AreValuesEqual(object v1, object v2) {
            throw new NotImplementedException();
        }
    }
}
