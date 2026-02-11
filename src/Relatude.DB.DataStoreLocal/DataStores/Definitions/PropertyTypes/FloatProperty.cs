using System.Diagnostics.CodeAnalysis;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes {
    internal class FloatProperty : ValueProperty<float> {
        public FloatProperty(FloatPropertyModel pm, Definition def) : base(pm, def) {
            MinValue = pm.MinValue;
            MaxValue = pm.MaxValue;
            DefaultValue = pm.DefaultValue;
        }
        protected override void WriteValue(float v, IAppendStream stream) => stream.WriteFloat(v);
        protected override float ReadValue(IReadStream stream) => stream.ReadFloat();

        public override PropertyType PropertyType => PropertyType.Float;
        public float DefaultValue;
        public float MinValue = float.MinValue;
        public float MaxValue = float.MaxValue;
        public override object ForceValueType(object value, out bool changed) {
            return FloatPropertyModel.ForceValueType(value, out changed);
        }
        public override void ValidateValue(object value) {
            var v = (float)value;
            if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
            if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
        }
        public override object GetDefaultValue() => DefaultValue;
        public static object GetValue(byte[] bytes) => BitConverter.ToSingle(bytes, 0);
        public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
            var v1 = FloatPropertyModel.ForceValueType(value1, out _);
            var v2 = FloatPropertyModel.ForceValueType(value2, out _);
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
            if (v1 is float f1 && v2 is float f2) return f1 == f2;
            return false;
        }
    }
}
