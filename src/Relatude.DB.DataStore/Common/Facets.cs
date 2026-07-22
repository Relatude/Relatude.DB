using Relatude.DB.Datamodels.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.Common;

public class Facets {

    public Facets(PropertyModel propery, bool? rangeFacet = null, List<FacetValue>? values = null) {
        PropertyId = propery.Id;
        ValueType = propery.PropertyType;
        CodeName = propery.CodeName;
        IsRangeFacet = rangeFacet;

        if (propery is IScalarProperty scalar) {
            if (scalar.FacetRangePowerBase > 0) RangePowerBase = scalar.FacetRangePowerBase;
            if (scalar.FacetRangeCount > 0) RangeCount = scalar.FacetRangeCount;
        }

        _values = values != null ? values : new();

    }
    public bool? IsRangeFacet { get; set; }
    public int RangeCount = 10;
    public double RangePowerBase = 1;//5d;
    public double MaxValue;
    public double MinValue;
    public int MaxValues; // 0 = unlimited; selected values are never trimmed away
    public int MinCount; // values with a lower count are dropped (unless selected); 0 = keep all
    public bool IncludeMissing; // adds a bucket (Value == null) for nodes without a value for the property
    public bool SortByCount; // sort values by descending count (after counting) instead of by value
    public void CopyOptionsFrom(Facets? given) {
        if (given == null) return;
        if (given._displayName != null) _displayName = given._displayName;
        MaxValues = given.MaxValues;
        MinCount = given.MinCount;
        IncludeMissing = given.IncludeMissing;
        SortByCount = given.SortByCount;
        RangeCount = given.RangeCount;
        RangePowerBase = given.RangePowerBase;
    }
    public void ApplyOptions() { // called after counting; must never remove or hide selected values
        if (MinCount > 0) _values.RemoveAll(v => !v.Selected && v.Count < MinCount);
        if (SortByCount) _values.Sort((a, b) => b.Count.CompareTo(a.Count));
        if (MaxValues > 0 && _values.Count > MaxValues) {
            var keep = _values.OrderByDescending(v => v.Selected).ThenByDescending(v => v.Count).Take(MaxValues).ToHashSet();
            _values.RemoveAll(v => !keep.Contains(v));
        }
    }
    List<FacetValue> _values;
    public Guid PropertyId { get; set; }
    string? _displayName = null;
    public string? CodeName { get; set; }
    public string DisplayName {
        get => _displayName ?? CodeName ?? String.Empty;
        set => _displayName = value;
    }
    public PropertyType ValueType { get; set; }
    public List<FacetValue> Values { get => _values; }
    public bool HasValues() => _values.Count > 0;
    public bool HasSelected() {
        foreach (var v in _values) {
            if (v.Selected) return true;
        }
        return false;
    }
    public void AddValue(FacetValue value) {
        _values.Add(value);
    }
    static public void SetSelected(Dictionary<Guid, Facets> facets, Dictionary<Guid, Facets> selected) {
        foreach (var kv in facets) {
            if (selected.TryGetValue(kv.Key, out var s)) {
                kv.Value.SetSelected(s._values);
            } else {
                kv.Value.SetSelected(null);
            }
        }
    }
    public void SetSelected(List<FacetValue>? selected) {
        foreach (var facet in _values) {
            facet.Selected = false;
        }
        if (selected == null) return;
        foreach (var s in selected) {
            var match = _values.FirstOrDefault(f => isSame(ValueType, f, s));
            if (match != null) {
                match.Selected = true;
            } else {
                // a selection outside the default values (typically a custom range) becomes its own
                // selected value, so it is still counted and filtered; it must never replace the defaults
                s.Selected = true;
                _values.Add(s);
            }
        }
    }
    static bool isSame(PropertyType propertyType, FacetValue v1, FacetValue v2) {
        return Equals(normalize(propertyType, v1.Value), normalize(propertyType, v2.Value))
            && Equals(normalize(propertyType, v1.Value2), normalize(propertyType, v2.Value2));
    }
    // selections usually arrive as strings (the typed query API serializes to a query string),
    // so both sides must be coerced to the property's value type before comparing:
    static object? normalize(PropertyType t, object? v) {
        if (v == null) return null;
        try {
            return t switch {
                PropertyType.Boolean => BooleanPropertyModel.ForceValueType(v, out _),
                PropertyType.Integer => IntegerPropertyModel.ForceValueType(v, out _),
                PropertyType.Long => LongPropertyModel.ForceValueType(v, out _),
                PropertyType.Double => DoublePropertyModel.ForceValueType(v, out _),
                PropertyType.Float => FloatPropertyModel.ForceValueType(v, out _),
                PropertyType.Decimal => DecimalPropertyModel.ForceValueType(v, out _),
                PropertyType.DateTime => DateTimePropertyModel.ForceValueType(v, out _),
                PropertyType.DateTimeOffset => DateTimeOffsetPropertyModel.ForceValueType(v, out _),
                PropertyType.TimeSpan => TimeSpanPropertyModel.ForceValueType(v, out _),
                PropertyType.Guid => GuidPropertyModel.ForceValueType(v, out _),
                PropertyType.Reference => ReferencePropertyModel.ForceValueType(v, out _),
                PropertyType.String => StringPropertyModel.ForceValueType(v, out _),
                PropertyType.StringArray => StringPropertyModel.ForceValueType(v, out _), // facet values of a string array are single strings
                _ => v,
            };
        } catch {
            return v; // unparsable input: fall back to comparing the raw value
        }
    }
    override public string ToString() { return DisplayName; }

    public void Sort() {
        if (IsRangeFacet == true) return; // range values keep their given/generated order
        // only sort when every non-null value shares one comparable type: mixed types would
        // give List.Sort an inconsistent comparison (nulls, e.g. the missing-value bucket, sort last)
        var type = _values.FirstOrDefault(v => v.Value != null)?.Value?.GetType();
        if (type == null || !typeof(IComparable).IsAssignableFrom(type)) return;
        if (_values.Any(v => v.Value != null && v.Value.GetType() != type)) return;
        _values.Sort((a, b) => {
            if (a.Value == null) return b.Value == null ? 0 : 1;
            if (b.Value == null) return -1;
            return ((IComparable)a.Value).CompareTo(b.Value);
        });
    }
}
public class FacetValue {
    public FacetValue(object? value) {
        Value = value;
    }
    public FacetValue(object? from, object? to, string? displayName) {
        Value = from;
        Value2 = to;
        _displayName = displayName;
    }
    internal string? _displayName;
    public string DisplayName {
        get => _displayName ?? (Value == null ? "(none)" : this.ToString());
        set => _displayName = value;
    }
    public string? ExplicitDisplayName => _displayName; // null unless a display name was given, unlike DisplayName which falls back to a generated one
    public FacetValue Clone() => new(Value, Value2, _displayName) { FromInclusive = FromInclusive, ToInclusive = ToInclusive, Selected = Selected, Count = Count };
    public object? Value { get; set; } // null marks the missing-value bucket (nodes without a value for the property)
    public object? Value2 { get; set; } // used for ranges

    public bool FromInclusive { get; set; } = true;
    public bool ToInclusive { get; set; } = true;

    public int Count { get; set; }
    public bool Selected { get; set; }
    public override string ToString() {
        //return (FromInclusive ? "[" : "<") + Value + (Value2 == null ? string.Empty : " - " + Value2 + (ToInclusive ? "]" : ">"));
        return Value + (FromInclusive ? " " : " <") + (Value2 == null ? string.Empty : "-" + (ToInclusive ? " " : "> ") + Value2);
    }
}