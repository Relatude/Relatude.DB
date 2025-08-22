using WAF.Datamodels.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WAF.Common;

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
        IsRangeFacet = value.Value2 != null;
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
        List<FacetValue> notFound = new();
        if (selected == null) return;
        foreach (var s in selected) {
            bool found = false;
            foreach (var facet in _values) {
                if (isSame(this.ValueType, facet, s)) {
                    facet.Selected = true;
                    found = true;
                    break;
                }
            }
            if (!found) notFound.Add(s);
        }
        if (notFound.Count > 0) {
            _values = notFound;
        }
    }
    static bool isSame(PropertyType propertyType, FacetValue v1, FacetValue v2) {
        // should be improved later on using propertyType
        if (v1.Value == null && v2.Value == null) return true;
        if (v1.Value == null && v2.Value != null) return false;
        if (v1.Value != null && v2.Value == null) return false;
        if (v1.Value?.ToString()?.ToLower() == v2.Value?.ToString()?.ToLower()) {
            if (v1.Value2 == null && v2.Value2 == null) return true;
            if (v1.Value2 == null && v2.Value2 != null) return false;
            if (v1.Value2 != null && v2.Value2 == null) return false;
            if (v1.Value2?.ToString()?.ToLower() == v2.Value2?.ToString()?.ToLower()) return true;
        }
        return false;
    }
    override public string ToString() { return DisplayName; }

    public void Sort() {
        if (IsRangeFacet == false) {
            _values.Sort((a, b) => {
                if (a.Value is IComparable c1 && b.Value is IComparable c2) {
                    return c1.CompareTo(c2);
                }
                return 0;
            });
        }
    }
}
public class FacetValue {
    public FacetValue(object value) {
        Value = value;
    }
    public FacetValue(object from, object? to, string? displayName) {
        Value = from;
        Value2 = to;
        _displayName = displayName;
    }
    internal string? _displayName;
    public string DisplayName {
        get => _displayName == null ? this.ToString() : _displayName;
        set => _displayName = value;
    }
    public object Value { get; set; }
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