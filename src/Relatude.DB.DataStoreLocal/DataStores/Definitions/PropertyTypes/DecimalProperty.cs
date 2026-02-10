using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes
{
    internal class DecimalProperty : ValueProperty<decimal> {
        public DecimalProperty(DecimalPropertyModel pm, Definition def) : base(pm, def) {
            MinValue = pm.MinValue;
            MaxValue = pm.MaxValue;
            DefaultValue = pm.DefaultValue;
        }
        protected override void WriteValue(decimal v, IAppendStream stream) => stream.WriteDecimal(v);
        protected override decimal ReadValue(IReadStream stream) => stream.ReadDecimal();

        public override PropertyType PropertyType => PropertyType.Decimal;
        public decimal DefaultValue;
        public override IRangeIndex? ValueIndex => Index;
        public decimal MinValue = decimal.MinValue;
        public decimal MaxValue = decimal.MaxValue;
        public IValueIndex<decimal>? Index;
        public override bool TryReorder(IdSet unsorted, bool descending, [MaybeNullWhen(false)] out IdSet sorted) {
            if (Index != null) {
                sorted = Index.ReOrder(unsorted, descending);
                return true;
            }
            return base.TryReorder(unsorted, descending, out sorted);
        }
        public override object ForceValueType(object value, out bool changed) {
            return DecimalPropertyModel.ForceValueType(value, out changed);
        }
        public override void ValidateValue(object value) {
            var v = (decimal)value;
            if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
            if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
        }
        public override object GetDefaultValue() => DefaultValue;

        public static object GetValue(byte[] bytes) {
            return new decimal(
                BitConverter.ToInt32(bytes, 0),
                BitConverter.ToInt32(bytes, 4),
                BitConverter.ToInt32(bytes, 8),
                bytes[15] == 128,
                bytes[14]);
        }
        public override bool CanBeFacet() => Indexed;
        public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
            throw new NotSupportedException();
        }
        public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
            throw new NotSupportedException();
        }
        public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) {
            throw new NotSupportedException();
        }
        public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
            var v1 = DecimalPropertyModel.ForceValueType(value1, out _);
            var v2 = DecimalPropertyModel.ForceValueType(value2, out _);
            return requirement switch {
                ValueRequirement.Equal => v1 == v2,
                ValueRequirement.NotEqual => v1 != v2,
                ValueRequirement.Greater => v1 > v2,
                ValueRequirement.GreaterOrEqual => v1 >= v2,
                ValueRequirement.Less => v1 < v2,
                ValueRequirement.LessOrEqual => v1 <= v2,
                _ => throw new NotSupportedException(),
            };
        }
        public override bool AreValuesEqual(object v1, object v2) {
            if (v1 is decimal d1 && v2 is decimal d2) return d1 == d2;
            return false;
        }
    }
}
