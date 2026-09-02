using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>The kind of input the admin UI renders for a setting, derived from its CLR type.</summary>
public enum SettingEditor {
    Text,
    Number,
    Integer,
    Toggle,
    /// <summary>A fixed list of choices: an enum, or a <see cref="SettingDefinition.Picker"/>.</summary>
    Choice,
}

/// <summary>
/// Reads and writes settings by property path ("LocalSettings.NodeCacheSizeGb"), and reports what a
/// property is: its editor, its choices, whether it may be left empty, and the value it holds when
/// nothing is configured. Everything the admin UI knows about a setting beyond its text comes from
/// here, so the settings classes stay the single definition of shape and defaults.
///
/// A path segment may address one element of a collection by its Id - "IOSettings[8f1c...].Path" -
/// which is how the list editors reach the fields of one storage provider or file store. Elements
/// are addressed by Id and never by position, so a path keeps meaning when the array is reordered,
/// and it is the same form <see cref="SettingsOverlay.IsOverridden"/> expects.
/// </summary>
public static class SettingsAccessor {

    static readonly JsonSerializerOptions _valueOptions = createValueOptions();
    static readonly NullabilityInfoContext _nullability = new();
    static readonly Dictionary<Type, object?> _defaultInstances = [];

    static JsonSerializerOptions createValueOptions() {
        var options = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString, // number inputs post strings
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new BoolFromStringConverter());
        return options;
    }

    /// <summary>Reads a toggle from "true"/"false" as well as from a JSON boolean, so a value that has
    /// travelled as text - what <see cref="SettingListDefinition.NewItem"/> holds - lands the same way
    /// a number written as a string does.</summary>
    sealed class BoolFromStringConverter : JsonConverter<bool> {
        public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) {
            if (reader.TokenType != JsonTokenType.String) return reader.GetBoolean();
            var text = reader.GetString();
            if (bool.TryParse(text, out var value)) return value;
            throw new JsonException("\"" + text + "\" is not true or false.");
        }
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    /// <summary>What a settings property is, without any of the text around it.</summary>
    public sealed class PropertyDescription {
        public required PropertyInfo Property { get; init; }
        public required Type ValueType { get; init; } // the property type, unwrapped from Nullable<>
        public required SettingEditor Editor { get; init; }
        /// <summary>True when the value may be cleared: a nullable value type or a nullable reference.</summary>
        public required bool Optional { get; init; }
        public string[]? EnumNames { get; init; }
        /// <summary>The value the property holds on a freshly constructed settings object.</summary>
        public JsonNode? DefaultValue { get; init; }
    }

    /// <summary>One step of a path: a property, optionally followed by the Id of the collection
    /// element to step into.</summary>
    readonly record struct PathStep(string Property, Guid? ElementId);

    static PathStep[] parse(string path) {
        var segments = path.Split('.');
        var steps = new PathStep[segments.Length];
        for (var i = 0; i < segments.Length; i++) {
            var segment = segments[i];
            var open = segment.IndexOf('[');
            if (open < 0) {
                steps[i] = new PathStep(segment, null);
                continue;
            }
            if (!segment.EndsWith(']') || !Guid.TryParse(segment[(open + 1)..^1], out var id)) {
                throw new Exception("Setting \"" + path + "\" addresses a collection element by something other than its id.");
            }
            steps[i] = new PathStep(segment[..open], id);
        }
        return steps;
    }

    /// <summary>The type a step lands on: the property's own type, or the collection's element type
    /// when the step goes on into one element.</summary>
    static Type typeAfter(PropertyInfo property, PathStep step) {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (step.ElementId == null) return type;
        if (type.IsArray) return type.GetElementType()!;
        if (type.IsGenericType) return type.GetGenericArguments()[0];
        throw new Exception("\"" + property.Name + "\" is not a collection.");
    }

    /// <summary>The element of a collection carrying this Id. Throws rather than returning null: a
    /// path into an element that has been removed is a stale request, not an empty value.</summary>
    public static object ElementOf(object? collection, Guid id, string path) {
        if (collection is System.Collections.IEnumerable items) {
            foreach (var item in items) {
                if (item == null) continue;
                var idProperty = item.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                if (idProperty?.GetValue(item) is Guid found && found == id) return item;
            }
        }
        throw new Exception("Setting \"" + path + "\" points at an element that no longer exists.");
    }

    /// <summary>Resolves a path against a settings type. Throws when it names no such property, which
    /// is what keeps <see cref="SettingsCatalog"/> honest as the settings classes change.</summary>
    public static PropertyDescription Describe(Type rootType, string path) {
        var property = resolveProperty(rootType, path, out var ownerType);
        var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return new PropertyDescription {
            Property = property,
            ValueType = valueType,
            Editor = editorFor(valueType),
            Optional = isOptional(property),
            EnumNames = valueType.IsEnum ? Enum.GetNames(valueType) : null,
            DefaultValue = toJson(readDefault(ownerType, property)),
        };
    }

    /// <summary>The current value, as JSON, or null when anything on the way to it is unset.</summary>
    public static JsonNode? Read(object root, string path) {
        object? current = root;
        foreach (var step in parse(path)) {
            if (current == null) return null;
            current = propertyOf(current.GetType(), step.Property, path).GetValue(current);
            if (step.ElementId != null && current != null) current = ElementOf(current, step.ElementId.Value, path);
        }
        return toJson(current);
    }

    /// <summary>
    /// Writes a value, creating the objects on the way to it when the path runs through one that is
    /// still null (a database with no AI settings yet, say). Returns true when the value actually
    /// changed, so a save can report what it did.
    /// </summary>
    public static bool Write(object root, string path, JsonElement value) {
        var steps = parse(path);
        var current = root;
        for (var i = 0; i < steps.Length - 1; i++) {
            var property = propertyOf(current.GetType(), steps[i].Property, path);
            var next = property.GetValue(current);
            if (steps[i].ElementId != null) {
                // elements are added through the list commands, never conjured by a write
                current = ElementOf(next, steps[i].ElementId!.Value, path);
                continue;
            }
            if (next == null) {
                next = Activator.CreateInstance(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType)
                    ?? throw new Exception("Setting \"" + path + "\" cannot be created.");
                property.SetValue(current, next);
            }
            current = next;
        }
        if (steps[^1].ElementId != null) throw new Exception("Setting \"" + path + "\" names a collection element, not a value.");
        var target = propertyOf(current.GetType(), steps[^1].Property, path);
        if (target.SetMethod?.IsPublic != true) throw new Exception("Setting \"" + path + "\" is read only.");
        var converted = Convert(value, target, path);
        var before = target.GetValue(current);
        if (Equals(before, converted)) return false;
        target.SetValue(current, converted);
        return true;
    }

    /// <summary>Turns a value posted by the browser into the property's own type. An empty string
    /// clears an optional setting rather than storing an empty one.</summary>
    public static object? Convert(JsonElement value, PropertyInfo property, string path) {
        var type = property.PropertyType;
        var valueType = Nullable.GetUnderlyingType(type) ?? type;
        var blank = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || (value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString()));
        if (blank) {
            if (valueType == typeof(string)) return isOptional(property) ? null : string.Empty;
            if (isOptional(property)) return null;
            return valueType == typeof(Guid) ? Guid.Empty : Activator.CreateInstance(valueType);
        }
        try {
            return JsonSerializer.Deserialize(value.GetRawText(), type, _valueOptions);
        } catch (Exception error) {
            throw new Exception("Setting \"" + path + "\" does not accept this value: " + error.Message);
        }
    }

    /// <summary>Every public read-write property of a settings type, by path, including the ones on
    /// nested settings objects. Used to find settings the catalog does not cover.</summary>
    public static IEnumerable<string> AllPaths(Type rootType, int maxDepth = 2) {
        return walk(rootType, "", maxDepth, []);
        static IEnumerable<string> walk(Type type, string prefix, int depth, HashSet<Type> seen) {
            if (depth < 0 || !seen.Add(type)) yield break;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (property.GetMethod?.IsPublic != true) continue;
                var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                var isLeaf = valueType.IsEnum || valueType.IsPrimitive || valueType == typeof(string)
                    || valueType == typeof(Guid) || valueType == typeof(decimal) || valueType == typeof(DateTime)
                    || valueType == typeof(DateTimeOffset) || valueType == typeof(TimeSpan);
                if (isLeaf) {
                    if (property.SetMethod?.IsPublic == true) yield return prefix + property.Name;
                    continue;
                }
                // arrays and lists are collections of things, not settings: the UI edits them elsewhere
                if (valueType.IsArray || (valueType.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(valueType))) continue;
                if (!valueType.IsClass) continue;
                // a computed object (the engine a default id resolves to) is a view of other settings, not a
                // container of its own: nothing below it can be written, so nothing below it is a setting
                if (property.SetMethod?.IsPublic != true) continue;
                foreach (var nested in walk(valueType, prefix + property.Name + ".", depth - 1, seen)) yield return nested;
            }
            seen.Remove(type);
        }
    }

    // walks the path down to the property it names, and hands back the type that declares it -
    // which is the type a default value has to be read off
    static PropertyInfo resolveProperty(Type rootType, string path, out Type ownerType) {
        var steps = parse(path);
        ownerType = rootType;
        for (var i = 0; i < steps.Length - 1; i++) {
            ownerType = typeAfter(propertyOf(ownerType, steps[i].Property, path), steps[i]);
        }
        return propertyOf(ownerType, steps[^1].Property, path);
    }

    static PropertyInfo propertyOf(Type type, string name, string path) {
        var found = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (found == null) throw new Exception("Setting \"" + path + "\" does not exist on " + type.Name + ".");
        return found;
    }

    // the value a property holds on a settings object that has just been constructed: what you get
    // when nothing is configured, which is what "default" means to whoever reads the admin UI
    static object? readDefault(Type ownerType, PropertyInfo property) {
        object? instance;
        lock (_defaultInstances) {
            if (!_defaultInstances.TryGetValue(ownerType, out instance)) {
                try { instance = Activator.CreateInstance(ownerType); } catch { instance = null; }
                _defaultInstances[ownerType] = instance;
            }
        }
        if (instance == null) return null;
        try { return property.GetValue(instance); } catch { return null; }
    }

    static bool isOptional(PropertyInfo property) {
        if (Nullable.GetUnderlyingType(property.PropertyType) != null) return true;
        if (property.PropertyType.IsValueType) return false;
        lock (_nullability) return _nullability.Create(property).WriteState != NullabilityState.NotNull;
    }

    static SettingEditor editorFor(Type valueType) {
        if (valueType == typeof(bool)) return SettingEditor.Toggle;
        if (valueType.IsEnum) return SettingEditor.Choice;
        if (valueType == typeof(double) || valueType == typeof(float) || valueType == typeof(decimal)) return SettingEditor.Number;
        if (valueType == typeof(int) || valueType == typeof(long) || valueType == typeof(short) || valueType == typeof(byte)
            || valueType == typeof(uint) || valueType == typeof(ulong) || valueType == typeof(ushort) || valueType == typeof(sbyte)) {
            return SettingEditor.Integer;
        }
        return SettingEditor.Text; // strings and Guids, the latter usually behind a picker
    }

    static JsonNode? toJson(object? value) {
        if (value == null) return null;
        if (value is Enum) return JsonValue.Create(value.ToString());
        return JsonSerializer.SerializeToNode(value, _valueOptions);
    }
}
