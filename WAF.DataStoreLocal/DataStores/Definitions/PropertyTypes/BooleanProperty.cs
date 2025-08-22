using WAF.AI;
using WAF.Common;
using WAF.Datamodels.Properties;
using WAF.DataStores.Indexes;
using WAF.DataStores.Sets;
using WAF.IO;
using WAF.Transactions;

namespace WAF.DataStores.Definitions.PropertyTypes;

internal class BooleanProperty : Property {
    public BooleanProperty(BooleanPropertyModel pm, Definition def) : base(pm, def) {
        DefaultValue = pm.DefaultValue;
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, IAIProvider? ai) {
        if (Indexed) {
            Index = IndexFactory.CreateValueIndex(store, def.Sets, this, null, write, read);
            Indexes.Add(Index);
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
    public override Facets GetDefaultFacets(Facets? given) {
        var facets = new Facets(Model);
        facets.AddValue(new FacetValue(false));
        facets.AddValue(new FacetValue(true));
        return facets;
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        foreach (var facetValue in facets.Values) {
            var v = BooleanPropertyModel.ForceValueType(facetValue.Value, out _);
            nodeIds = Index.Filter(nodeIds, IndexOperator.Equal, v);
        }
        return nodeIds;
    }
    public override void CountFacets(IdSet nodeIds, Facets facets) {
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
