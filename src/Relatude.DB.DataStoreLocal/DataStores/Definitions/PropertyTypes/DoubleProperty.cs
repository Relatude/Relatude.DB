using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes {
    internal class DoubleProperty : ValueProperty<double> {
        public DoubleProperty(DoublePropertyModel pm, Definition def) : base(pm, def) {
            MinValue = pm.MinValue;
            MaxValue = pm.MaxValue;
            DefaultValue = pm.DefaultValue;
        }
        protected override void WriteValue(double v, IAppendStream stream) => stream.WriteDouble(v);
        protected override double ReadValue(IReadStream stream) => stream.ReadDouble();
        public override PropertyType PropertyType => PropertyType.Double;
        public double DefaultValue;
        public double MinValue = double.MinValue;
        public double MaxValue = double.MaxValue;
        public override void ValidateValue(object value) {
            var v = (double)value;
            if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
            if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
        }
        public override object GetDefaultValue() => DefaultValue;
        public static object GetValue(byte[] bytes) => BitConverter.ToDouble(bytes, 0);
        public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
            var v1 = DoublePropertyModel.ForceValueType(value1, out _);
            var v2 = DoublePropertyModel.ForceValueType(value2, out _);
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
            if (v1 is double d1 && v2 is double d2) return d1 == d2;
            return false;
        }
    }
}
