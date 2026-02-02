using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions {
    public interface IPropertyContainsValue {
        public bool ContainsValue(object value);
    }
    internal abstract class Property {
        static int _idCnt = 0;
        public int __Id_transient;  // stateless
        public Property(PropertyModel pm, Definition def) {
            Id = pm.Id;
            __Id_transient = Interlocked.Increment(ref _idCnt);
            Model = pm;
            CodeName = pm.CodeName;
            ReadAccess = pm.ReadAccess;
            WriteAccess = pm.WriteAccess;
            Indexed = pm.Indexed || pm.UniqueValues;
            if (pm is IPropertyModelUniqueContraints pmuv) UniqueValues = pmuv.UniqueValues;
            Indexes = [];
            Definition = def;
        }
        public bool Indexed { get; }
        public virtual bool TryReorder(IdSet unsorted, bool descending, [MaybeNullWhen(false)] out IdSet sorted) {
            sorted = null;
            return false;
        }
        public readonly PropertyModel Model;
        internal abstract void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai);
        public static Property Create(PropertyModel pm, Definition def) {
            if (pm is BooleanPropertyModel b) return new BooleanProperty(b, def);
            if (pm is ByteArrayPropertyModel bt) return new ByteArrayProperty(bt, def);
            if (pm is IntegerPropertyModel i) return new IntegerProperty(i, def);
            if (pm is LongPropertyModel l) return new LongProperty(l, def);
            if (pm is DecimalPropertyModel de) return new DecimalProperty(de, def);
            if (pm is DoublePropertyModel d) return new DoubleProperty(d, def);
            if (pm is FloatPropertyModel f) return new FloatProperty(f, def);
            if (pm is GuidPropertyModel g) return new GuidProperty(g, def);
            if (pm is DateTimePropertyModel dt) return new DateTimeProperty(dt, def);
            if (pm is DateTimeOffsetPropertyModel dto) return new DateTimeOffsetProperty(dto, def);
            if (pm is TimeSpanPropertyModel t) return new TimeSpanProperty(t, def);
            if (pm is StringPropertyModel p) return new StringProperty(p, def);
            if (pm is StringArrayPropertyModel pa) return new StringArrayProperty(pa, def);
            if (pm is RelationPropertyModel ra) return new RelationProperty(ra, def);
            if (pm is FilePropertyModel fa) return new FileProperty(fa, def);
            if (pm is FloatArrayPropertyModel far) return new FloatArrayProperty(far, def);
            throw new Exception("Unknown property type. ");
        }
        public abstract object ForceValueType(object value, out bool changed);
        public abstract void ValidateValue(object value);

        public virtual bool CanBeFacet() => false;
        public virtual void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) => throw new NotSupportedException();
        public virtual IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) => throw new NotSupportedException();
        public virtual Facets GetDefaultFacets(Facets? given, QueryContext ctx) => throw new NotSupportedException();

        readonly public Definition Definition;
        readonly public Guid Id;
        readonly public string CodeName;
        readonly public Guid ReadAccess;
        readonly public Guid WriteAccess;
        readonly public bool UniqueValues;
        internal List<IIndex> Indexes { get; }

        public abstract PropertyType PropertyType { get; }

        public abstract object GetDefaultValue();
        public void CompressMemory() {
            foreach (var item in Indexes) item.CompressMemory();
        }
        public virtual IRangeIndex? ValueIndex { get { return null; } }
        public virtual IdSet FilterRanges(IdSet set, object from, object to, QueryContext ctx) {
            if (ValueIndex is null) throw new NotSupportedException("This property does not support range filtering. ");
            return ValueIndex.FilterRangesObject(set, from, to);
        }
        public virtual IdSet WhereIn(IdSet ids, IEnumerable<object?> values, QueryContext ctx) {
            throw new NotSupportedException("This property does not support filtering by multiple values. ");
        }
        public virtual object TransformFromOuterToInnerValue(object value, INodeData? oldNodeData) {
            return value;
        }
        public virtual bool IsReferenceTypeAndMustCopy() {
            return false;
        }
        public virtual bool IsNodeRelevantForIndex(INodeData node, IIndex index) => true;
        public virtual bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
            throw new NotImplementedException("The property " + CodeName + " of type " + PropertyType + " cannot support value requirements. ");
        }
        public abstract bool AreValuesEqual(object v1, object v2);// => v1.Equals(v2);
    }
}
