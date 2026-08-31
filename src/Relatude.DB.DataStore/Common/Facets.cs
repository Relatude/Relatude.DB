using Relatude.DB.Datamodels;
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

        // dates default to uniform calendar buckets: the general 1.8 power curve would give the
        // OLDEST dates the finest buckets (the curve is anchored at the minimum), which is backwards
        // for typical date facets. An explicit FacetRangePowerBase on the property still wins below.
        if (propery.PropertyType is PropertyType.DateTime or PropertyType.DateTimeOffset) RangePowerBase = 1;

        if (propery is IScalarProperty scalar) {
            if (scalar.FacetRangePowerBase > 0) RangePowerBase = scalar.FacetRangePowerBase;
            if (scalar.FacetRangeCount > 0) RangeCount = scalar.FacetRangeCount;
        }

        _values = values != null ? values : new();

    }
    public bool? IsRangeFacet { get; set; }
    public int RangeCount = 10;
    public double RangePowerBase = 1.8d; // finer buckets near the minimum (typical for prices); DateTime properties default to 1 (linear) in the constructor
    public int MaxValues; // 0 = unlimited; selected values are never trimmed away
    public int MinCount; // values with a lower count are dropped (unless selected); 0 = keep all
    public bool IncludeMissing; // adds a bucket (Value == null) for nodes without a value for the property
    public bool SortByCount; // sort values by descending count (after counting) instead of by value
    /// <summary>Copies the facet spec, including cloned values, so the copy can be modified independently.</summary>
    public Facets Clone() {
        var c = (Facets)MemberwiseClone();
        c._values = new List<FacetValue>(_values.Count);
        foreach (var v in _values) c._values.Add(v.Clone());
        return c;
    }
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
        // SortByCount is the caller asking for an order; without it the buckets take their natural
        // one (see Sort). Either way this runs before the trim below, which keeps whatever order it
        // finds - it only decides which buckets survive.
        if (SortByCount) _values.Sort((a, b) => b.Count.CompareTo(a.Count));
        else Sort();
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
                PropertyType.Integer => normalizeInt(v),
                PropertyType.EnumArray => normalizeInt(v), // facet values of an enum array are single ints
                PropertyType.Long => LongPropertyModel.ForceValueType(v, out _),
                PropertyType.Double => DoublePropertyModel.ForceValueType(v, out _),
                PropertyType.Float => FloatPropertyModel.ForceValueType(v, out _),
                PropertyType.Decimal => DecimalPropertyModel.ForceValueType(v, out _),
                PropertyType.DateTime => DateTimePropertyModel.ForceValueType(v, out _),
                PropertyType.DateTimeOffset => DateTimeOffsetPropertyModel.ForceValueType(v, out _),
                PropertyType.TimeSpan => TimeSpanPropertyModel.ForceValueType(v, out _),
                PropertyType.Guid => normalizeGuid(v),
                PropertyType.Reference => normalizeGuid(v),
                PropertyType.String => StringPropertyModel.ForceValueType(v, out _),
                PropertyType.StringArray => StringPropertyModel.ForceValueType(v, out _), // facet values of a string array are single strings
                PropertyType.GuidArray => normalizeGuid(v), // facet values of a guid array are single guids
                PropertyType.References => normalizeGuid(v), // facet values of a references property are single guids
                PropertyType.Relation => v is INodeData nd ? nd.Id : normalizeGuid(v), // relation buckets hold node data, selections arrive as guids
                _ => v,
            };
        } catch {
            return v; // unparsable input: fall back to comparing the raw value
        }
    }
    // Guid.Empty is a legitimate bucket value, so unparsable selection input must stay raw (and
    // never match) instead of collapsing to Guid.Empty the way ForceValueType would:
    static object normalizeGuid(object v) {
        if (v is Guid g) return g;
        if (v is string s) return Guid.TryParse(s, out var parsed) ? parsed : v;
        return GuidPropertyModel.ForceValueType(v, out _);
    }
    // same rationale for int buckets: 0 is a legitimate value and unparsable input must not
    // collapse to it (enum name strings stay raw here and are resolved by the property instead)
    static object normalizeInt(object v) {
        if (v is int i) return i;
        if (v is Enum e) return Convert.ToInt32(e);
        if (v is string s) return int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : v;
        return IntegerPropertyModel.ForceValueType(v, out _);
    }
    override public string ToString() { return DisplayName; }

    /// <summary>
    /// The order the buckets are in when the caller has not asked for one (<see cref="SortByCount"/>
    /// is what asks). Range facets keep the order their buckets were generated in - they are a scale,
    /// and a scale out of order is not a scale.
    ///
    /// Everything else sorts by what the bucket is read as. A bucket that carries a name sorts by the
    /// name, because the value behind it is an enum number or the guid of a referenced node and
    /// nobody reads those; a bucket without one sorts by its value, which keeps numbers and dates in
    /// their own order rather than in the order their formatted text happens to fall in. The
    /// missing-value bucket sorts last either way.
    /// </summary>
    public void Sort() {
        if (IsRangeFacet == true) return; // range values keep their given/generated order
        if (_values.Any(v => v.ExplicitDisplayName != null)) {
            _values.Sort((a, b) => {
                if (a.Value == null) return b.Value == null ? 0 : 1;
                if (b.Value == null) return -1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });
            return;
        }
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