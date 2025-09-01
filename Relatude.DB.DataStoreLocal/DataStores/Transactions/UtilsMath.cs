using System.Text;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Transactions;
internal static class UtilsMath {
	public static object Add(PropertyModel propDef, object oldValue, object value) {
		if (propDef.PropertyType == PropertyType.Integer) {
			return IntegerPropertyModel.ForceValueType(value, out _) + IntegerPropertyModel.ForceValueType(oldValue, out _);
		} else if (propDef.PropertyType == PropertyType.Float) {
			return FloatPropertyModel.ForceValueType(value, out _) + FloatPropertyModel.ForceValueType(oldValue, out _);
		} else if (propDef.PropertyType == PropertyType.Double) {
			return DoublePropertyModel.ForceValueType(value, out _) + DoublePropertyModel.ForceValueType(oldValue, out _);
		} else if (propDef.PropertyType == PropertyType.Decimal) {
			return DecimalPropertyModel.ForceValueType(value, out _) + DecimalPropertyModel.ForceValueType(oldValue, out _);
		} else if (propDef.PropertyType == PropertyType.String) {
			return StringPropertyModel.ForceValueType(oldValue, out _) + StringPropertyModel.ForceValueType(value, out _);
		} else if (propDef.PropertyType == PropertyType.StringArray) {
			var existing = StringArrayPropertyModel.ForceValueType(oldValue, out _);
			var newValues = StringArrayPropertyModel.ForceValueType(value, out _);
			return existing.Concat(newValues).ToArray();
		} else if (propDef.PropertyType == PropertyType.Long) {
			return LongPropertyModel.ForceValueType(value, out _) + LongPropertyModel.ForceValueType(oldValue, out _);
		} else if (propDef.PropertyType == PropertyType.DateTime) {
			// Ensuring the DateTime is not set beyond the max/min value. >Max becomes Max and <Min becomes Min
			var oldDateTime = DateTimePropertyModel.ForceValueType(oldValue, out _);
			var timeSpan = TimeSpanPropertyModel.ForceValueType(value, out _);
			if (timeSpan.Ticks == 0) return oldValue;
			bool isTimeSpanAdding = timeSpan.Ticks > 0;
			if (isTimeSpanAdding) {
				var maxThatCanBeAdded = DateTime.MaxValue - oldDateTime;
				return maxThatCanBeAdded > timeSpan ? oldDateTime.Add(timeSpan) : DateTime.MaxValue;
			} else {
				var maxThatCanBeAdded = DateTime.MinValue - oldDateTime;
				return maxThatCanBeAdded < timeSpan ? oldDateTime.Add(timeSpan) : DateTime.MinValue;
			}
		} else if (propDef.PropertyType == PropertyType.TimeSpan) {
			return TimeSpanPropertyModel.ForceValueType(value, out _) + TimeSpanPropertyModel.ForceValueType(oldValue, out _);
		} else {
			throw new NotImplementedException();
		}
	}
	public static object Multiply(PropertyModel propDef, object oldValue, object value) {
		if (propDef.PropertyType == PropertyType.Integer) {
			return IntegerPropertyModel.ForceValueType(value, out _) * IntegerPropertyModel.ForceValueType(oldValue, out _);
		} else if (propDef.PropertyType == PropertyType.Float) {
			return FloatPropertyModel.ForceValueType(value, out _) * FloatPropertyModel.ForceValueType(oldValue, out _);
		} else if (propDef.PropertyType == PropertyType.Double) {
			return DoublePropertyModel.ForceValueType(value, out _) * DoublePropertyModel.ForceValueType(oldValue, out _);
		} else if (propDef.PropertyType == PropertyType.Decimal) {
			return DecimalPropertyModel.ForceValueType(value, out _) * DecimalPropertyModel.ForceValueType(oldValue, out _);
		} else if (propDef.PropertyType == PropertyType.Long) {
			return LongPropertyModel.ForceValueType(value, out _) * LongPropertyModel.ForceValueType(oldValue, out _);
		} else {
			throw new NotImplementedException();
		}
	}

}
