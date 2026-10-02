using System.Reflection;
using System.Text.Json.Serialization;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Datamodels;

public partial class Datamodel {
    /// <summary>
    /// The attribute values the database overrides on top of what the sources say (see
    /// <see cref="DatamodelOverrides"/>). The server reads them from the database's overrides file when
    /// it loads the model, and the store applies them when it opens (<see cref="ApplyOverrides"/>), so
    /// the model the datamodel editor works on - and writes back into the sources - never has them
    /// mixed into its types. Null when nothing is overridden.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatamodelOverrides? Overrides { get; set; }

    /// <summary>
    /// What applying the overrides, and resolving the settings types inherit, had to say: an override of
    /// a type or property that is not there any more, a value that does not parse, two base types that
    /// disagree. None of it stops the model from loading - the override in question is skipped, the
    /// setting falls back to what the type declares - but each is worth a line in the log and in the
    /// editor. Not serialized: it describes one load of the model.
    /// </summary>
    [JsonIgnore]
    public readonly List<string> OverrideNotices = new();
    void notice(string message) {
        if (!OverrideNotices.Contains(message)) OverrideNotices.Add(message);
    }

    /// <summary>Whether <see cref="Overrides"/> has been applied to the types.</summary>
    [JsonIgnore]
    public bool OverridesApplied { get; private set; }

    /// <summary>
    /// Writes <see cref="Overrides"/> into the types as if the sources said so: a node type setting onto
    /// the type, an attribute of a property onto the property where the type declares it, and an
    /// attribute of an inherited property into the type's <see cref="NodeTypeModel.PropertyOverrides"/>,
    /// where the runtime value replaces what the type's code says. Called by the store as it opens,
    /// before the model is initialized; does nothing when there are no overrides or they are applied.
    /// Whatever cannot be applied is skipped and noted in <see cref="OverrideNotices"/>.
    /// </summary>
    public void ApplyOverrides() {
        lock (_lock) {
            if (OverridesApplied || Overrides == null) return;
            if (_hasInitialized) throw new Exception("The datamodel overrides must be applied before the datamodel is initialized. Set Datamodel.Overrides before the store is created. ");
            OverridesApplied = true;
            foreach (var (typeId, typeOverride) in Overrides.NodeTypes) applyTypeOverride(typeId, typeOverride);
        }
    }
    void applyTypeOverride(Guid typeId, NodeTypeOverride typeOverride) {
        if (!NodeTypes.TryGetValue(typeId, out var type)) {
            if (!typeOverride.IsEmpty) notice("The overrides name a node type " + (typeOverride.Name ?? "") + " (" + typeId + ") that is not in the datamodel; they are skipped. ");
            return;
        }
        reportUnknown(typeOverride.Unknown, type.FullName);
        foreach (var (name, _, value) in DatamodelOverrides.SetMembers(typeOverride)) setOverridden(type, name, value, type.FullName);
        if (typeOverride.Properties == null) return;
        var properties = propertiesOf(type);
        foreach (var (propertyId, propertyOverride) in typeOverride.Properties) {
            if (!properties.TryGetValue(propertyId, out var found)) {
                if (!propertyOverride.IsEmpty) notice("The overrides name a property " + (propertyOverride.Name ?? "") + " (" + propertyId + ") that " + type.FullName + " does not have; it is skipped. ");
                continue;
            }
            var (property, declaring) = found;
            var where = type.FullName + "." + property.CodeName;
            if (property.Internal) {
                notice("The property " + where + " is kept by the engine and cannot be overridden; the override is skipped. ");
                continue;
            }
            reportUnknown(propertyOverride.Unknown, where);
            foreach (var (name, scope, value) in DatamodelOverrides.SetMembers(propertyOverride)) {
                if (declaring == type) {
                    // the type that declares the property: the override is what the declaration says
                    setOverridden(property, name, value, where);
                } else if (scope == OverrideScope.Inherited) {
                    var member = property.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (member == null) {
                        notice(name + " does not apply to " + where + ", a " + property.PropertyType + " property; the override is skipped. ");
                        continue;
                    }
                    if (!OverrideValues.TryConvert(value, member.PropertyType, out var converted, out var error)) {
                        notice("The override of " + name + " on " + where + " is skipped: " + error);
                        continue;
                    }
                    type.PropertyOverrides ??= new();
                    if (!type.PropertyOverrides.TryGetValue(propertyId, out var own)) type.PropertyOverrides[propertyId] = own = new PropertyOverride { Name = property.CodeName };
                    typeof(PropertyOverride).GetProperty(name)!.SetValue(own, converted);
                } else {
                    notice(name + " of " + declaring.FullName + "." + property.CodeName + " is one setting for the property wherever it is used, so it can only be overridden on "
                        + declaring.FullName + ", which declares it. The override on " + type.FullName + " is skipped. ");
                }
            }
        }
    }
    void setOverridden(object target, string name, object value, string where) {
        var member = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (member == null || member.SetMethod?.IsPublic != true) {
            notice(name + " does not apply to " + where + (target is PropertyModel p ? ", a " + p.PropertyType + " property" : "") + "; the override is skipped. ");
            return;
        }
        if (!OverrideValues.TryConvert(value, member.PropertyType, out var converted, out var error)) {
            notice("The override of " + name + " on " + where + " is skipped: " + error);
            return;
        }
        member.SetValue(target, converted);
    }
    void reportUnknown(Dictionary<string, System.Text.Json.JsonElement>? unknown, string where) {
        if (unknown == null) return;
        foreach (var key in unknown.Keys) notice("The overrides of " + where + " hold \"" + key + "\", which is not an attribute that can be overridden; it is skipped. ");
    }
    // the properties a type has before the model is initialized, with the type declaring each: its own
    // and those of everything it inherits from, the base node type included
    Dictionary<Guid, (PropertyModel property, NodeTypeModel declaring)> propertiesOf(NodeTypeModel type) {
        var result = new Dictionary<Guid, (PropertyModel, NodeTypeModel)>();
        var seen = new HashSet<Guid>();
        var pending = new Stack<NodeTypeModel>();
        pending.Push(type);
        while (pending.Count > 0) {
            var t = pending.Pop();
            if (!seen.Add(t.Id)) continue;
            foreach (var p in t.Properties.Values) result.TryAdd(p.Id, (p, t));
            foreach (var parentId in t.Parents) if (NodeTypes.TryGetValue(parentId, out var parent)) pending.Push(parent);
            if (t.Id != NodeConstants.BaseNodeTypeId && NodeTypes.TryGetValue(NodeConstants.BaseNodeTypeId, out var baseType)) pending.Push(baseType);
        }
        return result;
    }

    // ---- what each type sees: called while the model initializes ----

    // The attributes types set for the properties they inherit (NodeTypeModel.PropertyOverrides): for
    // every type and every property it has, the most specific type that sets one wins - the way C# finds
    // the override of a member - and what nothing overrides is what the declaring type says. Fills the
    // per type lists and lookups the engine reads (TextIndexProperties, DisplayProperties, the default
    // values and boosts), and leaves the shared property objects alone.
    void resolvePropertySettingsPerType() {
        var overriding = NodeTypes.Values.Where(t => t.PropertyOverrides is { Count: > 0 }).ToList();
        foreach (var t in overriding) checkPropertyOverrides(t);
        foreach (var t in NodeTypes.Values) {
            var relevant = overriding.Where(o => t.ThisAndAllInheritedTypes.ContainsKey(o.Id)).ToList();
            foreach (var p in t.AllProperties.Values) {
                var displayName = p.DisplayName;
                var excluded = p.ExcludeFromTextIndex;
                if (relevant.Count > 0) {
                    if (resolve(t, p, relevant, nameof(PropertyOverride.DisplayName), o => o.DisplayName, out var dn)) displayName = (bool)dn!;
                    if (resolve(t, p, relevant, nameof(PropertyOverride.ExcludeFromTextIndex), o => o.ExcludeFromTextIndex, out var ex)) excluded = (bool)ex!;
                    if (resolve(t, p, relevant, nameof(PropertyOverride.IndexBoost), o => o.IndexBoost, out var boost)) t.IndexBoosts[p.Id] = (int)boost!;
                    if (resolve(t, p, relevant, nameof(PropertyOverride.DefaultValue), o => o.DefaultValue, out var value)) t.DefaultValues[p.Id] = value;
                }
                if (displayName) t.DisplayProperties.Add(p);
                if (!excluded) t.TextIndexProperties.Add(p);
            }
        }
    }
    void checkPropertyOverrides(NodeTypeModel t) {
        foreach (var (propertyId, o) in t.PropertyOverrides!) {
            if (!t.AllProperties.TryGetValue(propertyId, out var p)) {
                notice(t.FullName + " overrides a property " + (o.Name ?? "") + " (" + propertyId + ") it does not have; the override is ignored. ");
                continue;
            }
            foreach (var (name, scope, _) in DatamodelOverrides.SetMembers(o)) {
                if (scope != OverrideScope.Inherited) notice(t.FullName + " overrides " + name + " of " + p.CodeName + ", which is one setting for the property wherever it is used. "
                    + "A type can only override the attributes of an inherited property that may differ between types (the default value, the text index and the display name); it is ignored. ");
            }
        }
    }
    bool resolve(NodeTypeModel t, PropertyModel p, List<NodeTypeModel> overriding, string name, Func<PropertyOverride, object?> pick, out object? value) {
        value = null;
        var member = p.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        var candidates = new List<(NodeTypeModel type, object? value)>();
        foreach (var x in overriding) {
            if (!x.PropertyOverrides!.TryGetValue(p.Id, out var o) || !x.AllProperties.ContainsKey(p.Id)) continue;
            var raw = pick(o);
            if (raw == null) continue;
            if (member == null) {
                notice(x.FullName + " overrides " + name + " of " + p.CodeName + ", which a " + p.PropertyType + " property does not have; it is ignored. ");
                continue;
            }
            if (!OverrideValues.TryConvert(raw, member.PropertyType, out var converted, out var error)) {
                notice("The override of " + name + " of " + p.CodeName + " on " + x.FullName + " is ignored: " + error);
                continue;
            }
            candidates.Add((x, converted));
        }
        if (candidates.Count == 0) return false;
        // the most specific: a candidate some other candidate inherits from is overridden by that one
        var mostSpecific = candidates.Where(c => !candidates.Any(o => o.type != c.type && o.type.ThisAndAllInheritedTypes.ContainsKey(c.type.Id))).ToList();
        var distinct = mostSpecific.Select(c => c.value).Distinct().ToList();
        if (distinct.Count == 1) {
            value = distinct[0];
            return true;
        }
        var declaring = NodeTypes.TryGetValue(p.NodeType, out var d) ? d.FullName : "the declaring type";
        notice(t.FullName + " gets different values for " + name + " of " + p.CodeName + " from "
            + string.Join(" and ", mostSpecific.Select(c => c.type.FullName)) + ", so it uses what " + declaring + " declares. Override it on " + t.FullName + " to choose. ");
        return false;
    }

    // The node type switches a type does not set itself it takes from its base types: the most specific
    // base that sets one wins, and a type whose base types disagree takes the database default. Resolved
    // from what every type declares before anything is filled in, so the order the types are visited in
    // does not matter.
    Dictionary<Guid, bool?> inheritedSwitches(Func<NodeTypeModel, bool?> get, string name) {
        var result = new Dictionary<Guid, bool?>();
        foreach (var t in NodeTypes.Values) {
            var own = get(t);
            if (own.HasValue) {
                result[t.Id] = own;
                continue;
            }
            var candidates = t.ThisAndAllInheritedTypes.Values.Where(x => x != t && get(x).HasValue).ToList();
            var mostSpecific = candidates.Where(c => !candidates.Any(o => o != c && o.ThisAndAllInheritedTypes.ContainsKey(c.Id))).ToList();
            var values = mostSpecific.Select(get).Distinct().ToList();
            if (values.Count == 1) {
                result[t.Id] = values[0];
            } else {
                result[t.Id] = null;
                if (values.Count > 1) notice(t.FullName + " inherits different values for " + name + " from " + string.Join(" and ", mostSpecific.Select(c => c.FullName + " (" + get(c) + ")"))
                    + ", so it uses the database's default. Set " + name + " on " + t.FullName + " to choose. ");
            }
        }
        return result;
    }
}
