using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class BooleanProperty : Property {
    public BooleanProperty(BooleanPropertyModel pm, Definition def) : base(pm, def) {
        DefaultValue = pm.DefaultValue;
    }
    IValueIndex<bool>? _index = null;
    Dictionary<string, IValueIndex<bool>>? _indexByCulture = null;
    public IValueIndex<bool> GetIndex(QueryContext ctx) => (Model.CultureSensitive) ? _indexByCulture![ctx.CultureCode!] : _index!;
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        if (Indexed) {
            var indexes = IndexFactory.CreateValueIndexes(store, def.Sets, this, null, write, read);
            if (Model.CultureSensitive) _index = indexes.First().Value;
            else _indexByCulture = indexes;
            Indexes.AddRange(indexes.Values);
        }
    }
    void write(bool v, IAppendStream stream) => stream.WriteBool(v);
    bool read(IReadStream stream) => stream.ReadBool();
    public bool DefaultValue;
    public IValueIndex<bool>? Index;
    public override PropertyType PropertyType => PropertyType.Boolean;
    public override IRangeIndex? ValueIndex => Index;
    public override object ForceValueType(object value, out bool changed) {
        return BooleanPropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value) { }
    public override object GetDefaultValue() => DefaultValue;
    public static object GetValue(byte[] bytes) => BitConverter.ToBoolean(bytes, 0);
    public override bool CanBeFacet() => Indexed;
    public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
        var facets = new Facets(Model);
        facets.AddValue(new FacetValue(false));
        facets.AddValue(new FacetValue(true));
        return facets;
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        foreach (var facetValue in facets.Values) {
            var v = BooleanPropertyModel.ForceValueType(facetValue.Value, out _);
            nodeIds = Index.Filter(nodeIds, IndexOperator.Equal, v);
        }
        return nodeIds;
    }
    public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        foreach (var facetValue in facets.Values) {
            var v = BooleanPropertyModel.ForceValueType(facetValue.Value, out _);
            facetValue.Count = Index.CountEqual(nodeIds, v);
        }
    }
    public bool ContainsValue() {
        throw new NotSupportedException();
    }
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
