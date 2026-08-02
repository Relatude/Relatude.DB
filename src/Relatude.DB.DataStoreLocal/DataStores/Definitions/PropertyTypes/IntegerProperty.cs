using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;
internal class IntegerProperty : ValueProperty<int>, IPropertyContainsValue {
    readonly bool _isEnum;
    readonly Dictionary<int, string> _nameByValue = new();
    readonly Dictionary<string, int> _valueByName = new();
    public IntegerProperty(IntegerPropertyModel pm, Definition def) : base(pm, def) {
        MinValue = pm.MinValue;
        MaxValue = pm.MaxValue;
        DefaultValue = pm.DefaultValue;
        _isEnum = pm.IsEnum;
        if (pm.LegalValues != null && pm.LegalValueNames != null) {
            for (var i = 0; i < pm.LegalValues.Length && i < pm.LegalValueNames.Length; i++) {
                _nameByValue[pm.LegalValues[i]] = pm.LegalValueNames[i];
                _valueByName[pm.LegalValueNames[i]] = pm.LegalValues[i];
            }
        }
    }
    protected override void WriteValue(int v, IAppendStream stream) => stream.WriteInt(v);
    protected override int ReadValue(IReadStream stream) => stream.ReadInt();
    public override PropertyType PropertyType => PropertyType.Integer;
    public readonly int DefaultValue;
    public readonly int MinValue = int.MinValue;
    public readonly int MaxValue = int.MaxValue;
    protected override bool AutoRangeBuckets => !_isEnum; // enums facet as one bucket per value, like enum arrays
    // facet selections may arrive as ints, boxed enums, numeric strings or enum NAME strings (same as enum arrays):
    bool tryResolve(object value, out int result) {
        if (value is int i) { result = i; return true; }
        if (value is Enum e) { result = Convert.ToInt32(e); return true; }
        if (value is string s) {
            if (int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out result)) return true;
            if (_valueByName.TryGetValue(s, out result)) return true;
        }
        result = 0;
        return false;
    }
    protected override int coerce(object v) => tryResolve(v, out var i) ? i : base.coerce(v);
    public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
        var facets = base.GetDefaultFacets(given, ctx);
        if (_nameByValue.Count > 0) {
            foreach (var v in facets.Values) { // enum buckets show the enum name, not the int (range buckets keep their generated names)
                if (v.ExplicitDisplayName != null || v.Value == null || v.Value2 != null) continue;
                if (tryResolve(v.Value, out var value) && _nameByValue.TryGetValue(value, out var name)) v.DisplayName = name;
            }
        }
        return facets;
    }
    public override void ValidateValue(object value, INodeData node) {
        var v = (int)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public static object GetValue(byte[] bytes) => BitConverter.ToInt32(bytes, 0);
    public override bool SatisfyValueRequirement(object? value1, object? value2, ValueRequirement requirement) {
        var v1 = IntegerPropertyModel.ForceValueType(value1, out _);
        var v2 = IntegerPropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            ValueRequirement.Less => v1 < v2,
            ValueRequirement.LessOrEqual => v1 <= v2,
            ValueRequirement.Greater => v1 > v2,
            ValueRequirement.GreaterOrEqual => v1 >= v2,
            _ => throw new NotImplementedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is int i1 && v2 is int i2) return i1 == i2;
        return false;
    }
}
