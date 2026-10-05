using System.Text.Json;
using System.Text.Json.Nodes;
using Relatude.DB.Datamodels;

namespace Relatude.DB.NodeServer;

/// <summary>
/// The two files a database's datamodel overrides are kept in, as JSON, and how they combine. The
/// shared file (relatude.settings/[short name]/datamodel.overrides.json) is part of the application:
/// it goes into source control and is deployed to every installation. The installation's file
/// (overrides/datamodel.overrides.json on the database's storage) holds what the data model editor
/// changed on this installation, and is merged over the shared one - the way relatude.db.overrides.json
/// is merged over relatude.db.json.
///
/// The installation's file is a patch: an attribute it sets replaces the shared value, and one it sets
/// to null takes the shared value away, so the attribute follows the source again on this
/// installation. Null is free for that: an override is never null (null means "not overridden"). An
/// attribute's value is taken whole - a default value that is an array is one value - and the names
/// beside the ids are only for people.
/// </summary>
public static class DatamodelOverridesLayers {
    const string nodeTypesKey = "NodeTypes";
    const string propertiesKey = "Properties";
    const string nameKey = "Name";
    static readonly JsonDocumentOptions readOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    // the members a type's and a property's entry can have, so a file written by hand in another case
    // still lines up with the one it is merged with
    static readonly Dictionary<string, string> typeMembers = members(typeof(NodeTypeOverride));
    static readonly Dictionary<string, string> propertyMembers = members(typeof(PropertyOverride));
    static Dictionary<string, string> members(Type type) {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [nameKey] = nameKey };
        if (type == typeof(NodeTypeOverride)) names[propertiesKey] = propertiesKey;
        foreach (var name in DatamodelOverrides.Scopes(type).Keys) names[name] = name;
        return names;
    }

    /// <summary>
    /// One overridden attribute in a file: of a node type (PropertyId null), or of a property seen from the
    /// type. A null Value takes the shared value away; only the installation's file has those.
    /// </summary>
    public readonly record struct Entry(Guid TypeId, Guid? PropertyId, string Attribute, JsonNode? Value, string? TypeName, string? PropertyName);
    /// <summary>Names one attribute of a type, or of a property seen from it.</summary>
    public readonly record struct AttributePath(Guid TypeId, Guid? PropertyId, string Attribute);

    /// <summary>
    /// A file's content, keyed and spelled the same way whatever wrote it: ids in one format, attributes
    /// in the model's case. Null for an empty file. Nulls are kept where they take a shared value away
    /// (keepResets, the installation's file) and dropped where they mean nothing.
    /// </summary>
    public static JsonObject? Parse(string? json, bool keepResets) {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var node = JsonNode.Parse(json, null, readOptions);
        if (node is not JsonObject root) throw new JsonException("The file does not hold a JSON object.");
        return normalize(root, keepResets);
    }
    /// <summary>The overrides the model carries (<see cref="Datamodel.Overrides"/>) as a file's content.</summary>
    public static JsonObject FromOverrides(DatamodelOverrides? overrides) {
        if (overrides == null) return empty();
        var copy = JsonSerializer.Deserialize<DatamodelOverrides>(JsonSerializer.Serialize(overrides, DatamodelJson.Options), DatamodelJson.Options)!;
        copy.RemoveEmpty();
        return normalize(JsonSerializer.SerializeToNode(copy, DatamodelJson.Options)!.AsObject(), keepResets: false);
    }
    /// <summary>A file's content as the overrides the model carries, or null when it overrides nothing. Resets are dropped.</summary>
    public static DatamodelOverrides? ToOverrides(JsonObject? layer) {
        if (layer == null) return null;
        var o = JsonSerializer.Deserialize<DatamodelOverrides>(withoutResets(layer).ToJsonString(), DatamodelJson.Options) ?? new();
        o.RemoveEmpty();
        return o.IsEmpty ? null : o;
    }
    public static string Serialize(JsonObject layer) => layer.ToJsonString(DatamodelJson.Options);

    /// <summary>What is in force: the shared overrides with the installation's on top.</summary>
    public static JsonObject Merge(JsonObject? shared, JsonObject? installation) {
        var result = shared == null ? empty() : (JsonObject)shared.DeepClone();
        foreach (var e in Entries(installation)) {
            if (e.Value == null) remove(result, new(e.TypeId, e.PropertyId, e.Attribute));
            else set(result, e);
        }
        return result;
    }

    /// <summary>
    /// The installation's file that makes the shared overrides into the effective ones: what differs from
    /// the shared file, and null for what the shared file sets and the effective overrides do not. An
    /// attribute that says the same as the shared file is left out - it comes from there.
    /// </summary>
    public static JsonObject Diff(JsonObject? shared, JsonObject? effective) {
        var patch = empty();
        var effectiveEntries = Entries(effective);
        var known = effectiveEntries.Select(e => new AttributePath(e.TypeId, e.PropertyId, e.Attribute)).ToHashSet();
        foreach (var e in effectiveEntries) {
            var sharedValue = Get(shared, new(e.TypeId, e.PropertyId, e.Attribute), out var inShared);
            if (!inShared || !JsonNode.DeepEquals(sharedValue, e.Value)) set(patch, e);
        }
        foreach (var s in Entries(shared)) {
            if (s.Value != null && !known.Contains(new(s.TypeId, s.PropertyId, s.Attribute))) set(patch, s with { Value = null });
        }
        return patch;
    }

    /// <summary>Every attribute a file sets, and every shared value it takes away, in the file's order.</summary>
    public static List<Entry> Entries(JsonObject? layer) {
        var list = new List<Entry>();
        if (layer?[nodeTypesKey] is not JsonObject types) return list;
        foreach (var (typeKey, typeNode) in types) {
            if (typeNode is not JsonObject type || !Guid.TryParse(typeKey, out var typeId)) continue;
            var typeName = nameOf(type);
            foreach (var (member, value) in type) {
                if (member is nameKey or propertiesKey) continue;
                list.Add(new(typeId, null, member, value, typeName, null));
            }
            if (type[propertiesKey] is not JsonObject properties) continue;
            foreach (var (propertyKey, propertyNode) in properties) {
                if (propertyNode is not JsonObject property || !Guid.TryParse(propertyKey, out var propertyId)) continue;
                var propertyName = nameOf(property);
                foreach (var (member, value) in property) {
                    if (member == nameKey) continue;
                    list.Add(new(typeId, propertyId, member, value, typeName, propertyName));
                }
            }
        }
        return list;
    }
    /// <summary>Whether the file sets nothing and takes nothing away.</summary>
    public static bool IsEmpty(JsonObject? layer) => Entries(layer).Count == 0;

    /// <summary>The value a file has for an attribute; found says whether it has one at all (a null value is a reset).</summary>
    public static JsonNode? Get(JsonObject? layer, AttributePath path, out bool found) {
        found = false;
        var owner = container(layer, path, create: false, null, null);
        if (owner == null || !owner.TryGetPropertyValue(path.Attribute, out var value)) return null;
        found = true;
        return value;
    }

    /// <summary>
    /// Moves attributes from the installation's file into the shared one: a value is written there (over
    /// whatever the shared file said), a reset takes the shared value away, and either way the entry leaves
    /// the installation's file. What is in force stays the same. Paths the installation's file does not
    /// have are passed over; the count says how many were moved.
    /// </summary>
    public static (JsonObject Shared, JsonObject Installation, int Moved) Move(JsonObject? shared, JsonObject? installation, IEnumerable<AttributePath> paths) {
        var newShared = shared == null ? empty() : (JsonObject)shared.DeepClone();
        var newInstallation = installation == null ? empty() : (JsonObject)installation.DeepClone();
        var entries = Entries(installation).ToDictionary(e => new AttributePath(e.TypeId, e.PropertyId, e.Attribute));
        var moved = 0;
        foreach (var path in paths.Distinct()) {
            if (!entries.TryGetValue(path, out var e)) continue;
            if (e.Value == null) remove(newShared, path);
            else set(newShared, e);
            remove(newInstallation, path);
            moved++;
        }
        return (newShared, newInstallation, moved);
    }

    /// <summary>Brings the names beside the ids up to date with the model; entries it does not know keep theirs.</summary>
    public static void UpdateNames(JsonObject layer, Datamodel model) {
        if (layer[nodeTypesKey] is not JsonObject types) return;
        foreach (var (typeKey, typeNode) in types) {
            if (typeNode is not JsonObject type || !Guid.TryParse(typeKey, out var typeId)) continue;
            if (model.NodeTypes.TryGetValue(typeId, out var t)) type[nameKey] = t.FullName;
            if (type[propertiesKey] is not JsonObject properties) continue;
            foreach (var (propertyKey, propertyNode) in properties) {
                if (propertyNode is not JsonObject property || !Guid.TryParse(propertyKey, out var propertyId)) continue;
                var p = model.NodeTypes.Values.SelectMany(n => n.Properties.Values).FirstOrDefault(x => x.Id == propertyId);
                if (p != null) property[nameKey] = p.CodeName;
            }
        }
    }

    // ---- inside ----

    static JsonObject empty() => new() { [nodeTypesKey] = new JsonObject() };
    static string? nameOf(JsonObject o) => o[nameKey] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static JsonObject normalize(JsonObject root, bool keepResets) {
        var result = empty();
        var types = (JsonObject)result[nodeTypesKey]!;
        var source = root.FirstOrDefault(kv => string.Equals(kv.Key, nodeTypesKey, StringComparison.OrdinalIgnoreCase)).Value;
        if (source == null) return result;
        if (source is not JsonObject sourceTypes) throw new JsonException(nodeTypesKey + " is not an object keyed by node type id.");
        foreach (var (typeKey, typeNode) in sourceTypes) {
            if (!Guid.TryParse(typeKey, out var typeId)) throw new JsonException("\"" + typeKey + "\" under " + nodeTypesKey + " is not a node type id.");
            if (typeNode is not JsonObject type) continue;
            var t = entry(types, typeId);
            foreach (var (member, value) in type) {
                var name = typeMembers.TryGetValue(member, out var known) ? known : member;
                if (name == propertiesKey) {
                    if (value is not JsonObject properties) continue;
                    foreach (var (propertyKey, propertyNode) in properties) {
                        if (!Guid.TryParse(propertyKey, out var propertyId)) throw new JsonException("\"" + propertyKey + "\" under " + propertiesKey + " of " + typeKey + " is not a property id.");
                        if (propertyNode is not JsonObject property) continue;
                        var p = entry((JsonObject)(t[propertiesKey] ??= new JsonObject()), propertyId);
                        foreach (var (propertyMember, propertyValue) in property) {
                            var pname = propertyMembers.TryGetValue(propertyMember, out var pknown) ? pknown : propertyMember;
                            if (propertyValue == null && (!keepResets || pname == nameKey)) continue;
                            p[pname] = propertyValue?.DeepClone();
                        }
                    }
                    continue;
                }
                if (value == null && (!keepResets || name == nameKey)) continue;
                t[name] = value?.DeepClone();
            }
        }
        dropEmpty(result);
        return result;
    }
    static JsonObject entry(JsonObject parent, Guid id) {
        var key = id.ToString();
        if (parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    // the object holding the attribute: the type's entry, or the property's inside it
    static JsonObject? container(JsonObject? layer, AttributePath path, bool create, string? typeName, string? propertyName) {
        if (layer == null) return null;
        if (layer[nodeTypesKey] is not JsonObject types) {
            if (!create) return null;
            layer[nodeTypesKey] = types = new JsonObject();
        }
        var typeKey = path.TypeId.ToString();
        if (types[typeKey] is not JsonObject type) {
            if (!create) return null;
            types[typeKey] = type = new JsonObject();
            if (typeName != null) type[nameKey] = typeName; // first, for the person reading the file
        }
        if (path.PropertyId == null) return type;
        if (type[propertiesKey] is not JsonObject properties) {
            if (!create) return null;
            type[propertiesKey] = properties = new JsonObject();
        }
        var propertyKey = path.PropertyId.Value.ToString();
        if (properties[propertyKey] is not JsonObject property) {
            if (!create) return null;
            properties[propertyKey] = property = new JsonObject();
            if (propertyName != null) property[nameKey] = propertyName;
        }
        return property;
    }
    static void set(JsonObject layer, Entry e) {
        var owner = container(layer, new(e.TypeId, e.PropertyId, e.Attribute), create: true, e.TypeName, e.PropertyName)!;
        owner[e.Attribute] = e.Value?.DeepClone();
    }
    static void remove(JsonObject layer, AttributePath path) {
        var owner = container(layer, path, create: false, null, null);
        if (owner == null) return;
        owner.Remove(path.Attribute);
        dropEmpty(layer);
    }
    // entries left with nothing but a name go, and so do the types left with nothing
    static void dropEmpty(JsonObject layer) {
        if (layer[nodeTypesKey] is not JsonObject types) return;
        foreach (var typeKey in types.Select(kv => kv.Key).ToList()) {
            if (types[typeKey] is not JsonObject type) {
                types.Remove(typeKey);
                continue;
            }
            if (type[propertiesKey] is JsonObject properties) {
                foreach (var propertyKey in properties.Select(kv => kv.Key).ToList()) {
                    if (properties[propertyKey] is not JsonObject property || property.All(kv => kv.Key == nameKey)) properties.Remove(propertyKey);
                }
                if (properties.Count == 0) type.Remove(propertiesKey);
            } else if (type.ContainsKey(propertiesKey)) {
                type.Remove(propertiesKey);
            }
            if (type.All(kv => kv.Key == nameKey)) types.Remove(typeKey);
        }
    }
    static JsonObject withoutResets(JsonObject layer) {
        var copy = (JsonObject)layer.DeepClone();
        foreach (var e in Entries(layer).Where(e => e.Value == null)) remove(copy, new(e.TypeId, e.PropertyId, e.Attribute));
        return copy;
    }
}
