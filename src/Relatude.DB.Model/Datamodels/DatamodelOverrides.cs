using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Datamodels;

/// <summary>
/// How far an overridden attribute reaches. Every attribute that can be overridden is a member of
/// <see cref="NodeTypeOverride"/> or <see cref="PropertyOverride"/> carrying one of these through
/// <see cref="OverrideScopeAttribute"/>; an attribute that is not there cannot be overridden (an id,
/// a name, the type of a property, whether its values are unique...).
/// </summary>
public enum OverrideScope {
    /// <summary>
    /// Set on a type, the value applies to that type and to every type inheriting from it, unless one of
    /// those sets its own. Node type switches like TextIndex are inherited this way, and so are the
    /// property attributes that may differ between the types that have a property, like its default
    /// value: a derived type can give an inherited property a default of its own.
    /// </summary>
    Inherited,
    /// <summary>A node type setting that applies to the type it is set on, and not to the types inheriting from it.</summary>
    ThisType,
    /// <summary>
    /// A setting of the property as a whole: the property has one index and one set of rules, shared by
    /// every type that has it. So it is overridden on the type that declares the property, and the new
    /// value applies wherever the property is used.
    /// </summary>
    WholeProperty,
    /// <summary>
    /// A setting of the property as a whole that any type having the property can ask for: whether it
    /// has a value index, and whether it is a facet. The property has one index and one set of facet
    /// counts, shared by every type that has it, so a type that inherits the property cannot have them
    /// to itself - it asks for the property's, and the property has them when its declaration says so or
    /// any type that has it asks (<see cref="OverrideScopeAttribute.Asks"/> is the value that asks). A
    /// type that says the opposite takes back only its own request (the one in its code, say); it cannot
    /// take the setting away from the declaration or the other types.
    /// </summary>
    AnyType,
}
/// <summary>Marks a member of <see cref="NodeTypeOverride"/> or <see cref="PropertyOverride"/> with how far it reaches.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OverrideScopeAttribute(OverrideScope scope) : Attribute {
    public OverrideScope Scope { get; } = scope;
    /// <summary>
    /// For <see cref="OverrideScope.AnyType"/>: the value with which a type asks for the setting - true
    /// for Indexed, false for NotFacet, which is an opt-out.
    /// </summary>
    public bool Asks { get; set; } = true;
}

/// <summary>
/// Attribute values a database sets on top of what its datamodel sources say, so that a type compiled
/// into an assembly - one of your own, or one you cannot change - can be indexed, constrained or given
/// defaults differently without recompiling anything. The datamodel editor writes them, the server keeps
/// them with the database (see <see cref="Datamodel.Overrides"/>), and the store applies them when it
/// opens, exactly as if the attributes had been written that way in the source.
///
/// Keyed by node type id, then property id. The names next to the ids are only there for people reading
/// the file: an override follows its type and property through a rename, and one whose type or property
/// is gone is reported and skipped, never a reason for the database not to open.
/// </summary>
public sealed class DatamodelOverrides {
    public Dictionary<Guid, NodeTypeOverride> NodeTypes { get; set; } = new();
    /// <summary>True when nothing is overridden.</summary>
    [JsonIgnore]
    public bool IsEmpty => NodeTypes.Values.All(t => t.IsEmpty);
    /// <summary>The override for a node type, created when there is none yet.</summary>
    public NodeTypeOverride ForType(NodeTypeModel type) {
        if (!NodeTypes.TryGetValue(type.Id, out var o)) NodeTypes[type.Id] = o = new NodeTypeOverride();
        o.Name = type.FullName;
        return o;
    }
    /// <summary>The override for a property in the context of a node type, created when there is none yet.</summary>
    public PropertyOverride ForProperty(NodeTypeModel type, PropertyModel property) => ForType(type).ForProperty(property);
    /// <summary>Drops the entries that override nothing - what is left of an override that was reset.</summary>
    public void RemoveEmpty() {
        foreach (var t in NodeTypes.Values) {
            if (t.Properties == null) continue;
            foreach (var key in t.Properties.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) t.Properties.Remove(key);
            if (t.Properties.Count == 0) t.Properties = null;
        }
        foreach (var key in NodeTypes.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) NodeTypes.Remove(key);
    }
    /// <summary>
    /// Brings the names next to the ids up to date with the model, so a file read by a person says what
    /// the types and properties are called now. Entries the model does not know keep the name they had.
    /// </summary>
    public void UpdateNames(Datamodel model) {
        foreach (var (typeId, t) in NodeTypes) {
            if (!model.NodeTypes.TryGetValue(typeId, out var type)) continue;
            t.Name = type.FullName;
            if (t.Properties == null) continue;
            foreach (var (propertyId, p) in t.Properties) {
                var property = model.NodeTypes.Values.SelectMany(n => n.Properties.Values).FirstOrDefault(x => x.Id == propertyId);
                if (property != null) p.Name = property.CodeName;
            }
        }
    }

    /// <summary>The members that can be overridden, with their scope: <paramref name="overrideType"/> is NodeTypeOverride or PropertyOverride.</summary>
    public static IReadOnlyDictionary<string, OverrideScope> Scopes(Type overrideType) => overrideType == typeof(NodeTypeOverride) ? nodeTypeScopes.Value : propertyScopes.Value;
    static readonly Lazy<Dictionary<string, OverrideScope>> nodeTypeScopes = new(() => scopesOf(typeof(NodeTypeOverride)));
    static readonly Lazy<Dictionary<string, OverrideScope>> propertyScopes = new(() => scopesOf(typeof(PropertyOverride)));
    static Dictionary<string, OverrideScope> scopesOf(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => (p.Name, attribute: p.GetCustomAttribute<OverrideScopeAttribute>()))
        .Where(x => x.attribute != null).ToDictionary(x => x.Name, x => x.attribute!.Scope, StringComparer.Ordinal);
    /// <summary>
    /// The <see cref="OverrideScope.AnyType"/> members of <see cref="PropertyOverride"/>, with the value
    /// each asks with (<see cref="OverrideScopeAttribute.Asks"/>), in declaration order.
    /// </summary>
    public static IReadOnlyList<(string name, bool asks)> Requests => requests.Value;
    static readonly Lazy<List<(string name, bool asks)>> requests = new(() => typeof(PropertyOverride).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => (p.Name, attribute: p.GetCustomAttribute<OverrideScopeAttribute>()))
        .Where(x => x.attribute?.Scope == OverrideScope.AnyType).Select(x => (x.Name, x.attribute!.Asks)).ToList());
    /// <summary>The overridden members of an override, with the value each is given.</summary>
    internal static IEnumerable<(string name, OverrideScope scope, object value)> SetMembers(object o) {
        foreach (var (name, scope) in Scopes(o.GetType())) {
            var value = o.GetType().GetProperty(name)!.GetValue(o);
            if (value != null) yield return (name, scope, value);
        }
    }
}

/// <summary>
/// The attributes of one node type that are overridden, and the overrides of the properties seen from
/// that type. A null member is not overridden.
/// </summary>
public sealed class NodeTypeOverride {
    /// <summary>The full name of the type when the override was written; for people reading the file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    [OverrideScope(OverrideScope.Inherited), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TextIndex { get; set; }
    [OverrideScope(OverrideScope.Inherited), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SemanticIndex { get; set; }
    [OverrideScope(OverrideScope.Inherited), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InstantTextIndexing { get; set; }
    [OverrideScope(OverrideScope.ThisType), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Hidden { get; set; }
    [OverrideScope(OverrideScope.ThisType), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DefaultReadAccess { get; set; }
    [OverrideScope(OverrideScope.ThisType), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DefaultEditViewAccess { get; set; }
    /// <summary>
    /// Overrides of properties, by property id: the type's own properties, and the ones it inherits. An
    /// inherited property only takes the attributes that may differ between types
    /// (<see cref="OverrideScope.Inherited"/>); the others belong to the type that declares it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<Guid, PropertyOverride>? Properties { get; set; }
    /// <summary>Members the file holds that are not overridable attributes - a typo, or an attribute that cannot be overridden. Reported, kept when the file is written again.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
    [JsonIgnore]
    public bool IsEmpty => !DatamodelOverrides.SetMembers(this).Any() && (Properties == null || Properties.Values.All(p => p.IsEmpty)) && (Unknown == null || Unknown.Count == 0);
    /// <summary>The override of a property, created when there is none yet.</summary>
    public PropertyOverride ForProperty(PropertyModel property) {
        Properties ??= new();
        if (!Properties.TryGetValue(property.Id, out var o)) Properties[property.Id] = o = new PropertyOverride();
        o.Name = property.CodeName;
        return o;
    }
}

/// <summary>
/// Overridden attributes of a property. The names are the property model's own (and the property
/// attributes', except that the text index boost is <see cref="IndexBoost"/> here, as in the model).
/// Values are of the property's type; in a JSON file a decimal, date or duration may also be given as
/// a string in the invariant culture, the way the property attributes take them. A null member is not
/// overridden.
///
/// Used in two places: in <see cref="NodeTypeOverride.Properties"/>, the database's runtime overrides,
/// and in <see cref="NodeTypeModel.PropertyOverrides"/>, where a type overrides attributes of a property
/// it inherits in its own definition ([PropertyOverride] in code). There only the
/// <see cref="OverrideScope.Inherited"/> attributes apply, and <see cref="Indexed"/> and
/// <see cref="NotFacet"/> (<see cref="OverrideScope.AnyType"/>), with which the type asks for the
/// property's index and for it to be a facet.
/// </summary>
public sealed class PropertyOverride {
    /// <summary>The name of the property when the override was written; for people reading the file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    // may differ between the types that have the property:
    /// <summary>The value a node of this type reads while it has none stored, and starts with when created.</summary>
    [OverrideScope(OverrideScope.Inherited), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? DefaultValue { get; set; }
    [OverrideScope(OverrideScope.Inherited), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ExcludeFromTextIndex { get; set; }
    [OverrideScope(OverrideScope.Inherited), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IndexBoost { get; set; }
    [OverrideScope(OverrideScope.Inherited), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DisplayName { get; set; }

    // the property as a whole, but any type that has it can ask for them:
    /// <summary>
    /// Whether the property has a value index. On the type that declares the property it is what the
    /// declaration says; on a type that inherits it, true asks for the property's index - there is one,
    /// shared by every type that has the property - and false takes back the type's own request.
    /// </summary>
    [OverrideScope(OverrideScope.AnyType, Asks = true), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Indexed { get; set; }
    /// <summary>
    /// Whether the property is kept out of the facets. On the type that declares the property it is what
    /// the declaration says; on a type that inherits it, false asks for the property to be a facet - its
    /// facet counts are one, shared by every type that has the property - and true takes back the type's
    /// own request. A facet needs the value index, so a property without one is no facet either way.
    /// </summary>
    [OverrideScope(OverrideScope.AnyType, Asks = false), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NotFacet { get; set; }

    // the property as a whole (one index, one set of rules):
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IndexStorageType? IndexType { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IndexedByWords { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IndexStorageType? TextIndexType { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLength { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegularExpression { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? MinValue { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? MaxValue { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FacetRangePowerBase { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FacetRangeCount { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Facet { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TextIndexRelatedDisplayName { get; set; }
    [OverrideScope(OverrideScope.WholeProperty), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TextIndexRelatedContent { get; set; }

    /// <summary>Members the file holds that are not overridable attributes. Reported, kept when the file is written again.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
    [JsonIgnore]
    public bool IsEmpty => !DatamodelOverrides.SetMembers(this).Any() && (Unknown == null || Unknown.Count == 0);
}

/// <summary>
/// Turns an override value into the type of the model member it overrides. The value comes from a JSON
/// file (a <see cref="JsonElement"/>), from code, or from an attribute, where decimals, dates, durations
/// and guids are strings because C# attributes cannot hold them.
/// </summary>
public static class OverrideValues {
    public static bool TryConvert(object? raw, Type target, out object? value, out string? error) {
        value = null;
        error = null;
        var type = Nullable.GetUnderlyingType(target) ?? target;
        try {
            if (raw is JsonElement element) {
                if (element.ValueKind == JsonValueKind.Null) return accept(target, out error);
                if (type == typeof(string)) {
                    value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
                    return true;
                }
                if (element.ValueKind == JsonValueKind.String) return TryConvert(element.GetString(), target, out value, out error);
                value = element.Deserialize(type, DatamodelJson.Options);
                return true;
            }
            if (raw == null) return accept(target, out error);
            if (type.IsInstanceOfType(raw)) {
                value = raw;
                return true;
            }
            if (raw is string s) return parse(s, type, out value, out error);
            if (type.IsEnum) {
                value = Enum.ToObject(type, raw);
                return true;
            }
            if (raw is Enum e && type == typeof(int)) {
                value = Convert.ToInt32(e, CultureInfo.InvariantCulture);
                return true;
            }
            if (raw is IConvertible && (type.IsPrimitive || type == typeof(decimal))) {
                value = Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
                return true;
            }
        } catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or JsonException or ArgumentException) {
            error = describe(raw) + " is not a valid " + typeName(type) + ": " + ex.Message;
            return false;
        }
        error = describe(raw) + " is not a valid " + typeName(type) + ".";
        return false;
    }
    static bool accept(Type target, out string? error) {
        if (!target.IsValueType || Nullable.GetUnderlyingType(target) != null) {
            error = null;
            return true;
        }
        error = "null is not a valid " + typeName(target) + ".";
        return false;
    }
    static bool parse(string s, Type type, out object? value, out string? error) {
        var ok = false;
        value = null;
        var inv = CultureInfo.InvariantCulture;
        if (type == typeof(bool)) { ok = bool.TryParse(s, out var v); value = v; }
        else if (type == typeof(int)) { ok = int.TryParse(s, NumberStyles.Integer, inv, out var v); value = v; }
        else if (type == typeof(long)) { ok = long.TryParse(s, NumberStyles.Integer, inv, out var v); value = v; }
        else if (type == typeof(double)) { ok = double.TryParse(s, NumberStyles.Float, inv, out var v); value = v; }
        else if (type == typeof(float)) { ok = float.TryParse(s, NumberStyles.Float, inv, out var v); value = v; }
        else if (type == typeof(decimal)) { ok = decimal.TryParse(s, NumberStyles.Float, inv, out var v); value = v; }
        else if (type == typeof(Guid)) { ok = Guid.TryParse(s, out var v); value = v; }
        else if (type == typeof(TimeSpan)) { ok = TimeSpan.TryParse(s, inv, out var v); value = v; }
        else if (type == typeof(DateTime)) { ok = DateTime.TryParse(s, inv, DateTimeStyles.RoundtripKind, out var v); value = v; }
        else if (type == typeof(DateTimeOffset)) { ok = DateTimeOffset.TryParse(s, inv, DateTimeStyles.RoundtripKind, out var v); value = v; }
        else if (type.IsEnum) { ok = Enum.TryParse(type, s, ignoreCase: true, out var v); value = v; }
        error = ok ? null : "\"" + s + "\" is not a valid " + typeName(type) + ".";
        if (!ok) value = null;
        return ok;
    }
    static string describe(object? raw) => raw switch {
        null => "null",
        string s => "\"" + s + "\"",
        JsonElement e => e.GetRawText(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => raw.ToString() ?? "",
    };
    static string typeName(Type type) => type == typeof(int) ? "integer" : type == typeof(long) ? "long integer" : type == typeof(bool) ? "true/false value"
        : type == typeof(double) || type == typeof(float) || type == typeof(decimal) ? "number" : type.Name;
}
