using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;
internal class GuidProperty : Property, IPropertyContainsValue {
    public GuidProperty(GuidPropertyModel pm, Definition def) : base(pm, def) {
        DefaultValue = pm.DefaultValue;
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        if (Indexed) {
            Index = IndexFactory.CreateValueIndex(store, def.Sets, this, null, write, read);
            Indexes.Add(Index);
        }
    }
    void write(Guid v, IAppendStream stream) => stream.WriteGuid(v);
    Guid read(IReadStream stream) => stream.ReadGuid();
    public override PropertyType PropertyType => PropertyType.Long;
    public Guid DefaultValue;
    public IValueIndex<Guid>? Index;
    public override object ForceValueType(object value, out bool changed) {
        return GuidPropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value) {
    }
    public override IRangeIndex? ValueIndex => null;
    public override object GetDefaultValue() => DefaultValue;
    public static object GetValue(byte[] bytes) => BitConverter.ToInt32(bytes, 0);
    public bool ContainsValue(object value) {
        if (Index == null) throw new Exception("Index is null. ");
        return Index.ContainsValue((Guid)value);
    }
    public override bool CanBeFacet() => false;
    public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
        throw new NotSupportedException("GuidProperty cannot be used as a facet. ");
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
        throw new NotSupportedException("GuidProperty cannot be used as a facet. ");
    }
    public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) {
        throw new NotSupportedException("GuidProperty cannot be used as a facet. ");
    }
    public override IdSet WhereIn(IdSet ids, IEnumerable<object?> values, QueryContext ctx) {
        if (Index == null) throw new NullReferenceException("Property is not indexed. ");
        return Index.FilterInValues(ids, values.Cast<Guid>().ToList());
    }
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = GuidPropertyModel.ForceValueType(value1, out _);
        var v2 = GuidPropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            _ => throw new NotSupportedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is Guid guid1 && v2 is Guid guid2) return guid1 == guid2;
        return false;
    }
}
