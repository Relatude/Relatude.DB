using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// Merges a configuration section over the settings loaded from relatude.db.json, so any setting can be
/// overridden from appsettings.json, appsettings.{Environment}.json, environment variables, user secrets
/// or any other configuration source. The section has the same shape as relatude.db.json.
/// Objects merge key by key and scalars replace. Array elements are matched on Id when the overlay
/// element gives one, otherwise on position; unmatched overlay elements are appended. Overlays cannot
/// remove elements or set values to null.
/// Overridden keys are restored to the file's own values before settings are written back, so
/// configuration-supplied values (such as secrets) never reach relatude.db.json.
/// </summary>
public sealed class SettingsOverlay {
    public const string DefaultSectionName = "RelatudeDB";

    readonly JsonObject _overlay;
    readonly Action<string> _logInfo;
    readonly Action<string> _logWarning;
    readonly List<Override> _overrides = [];

    SettingsOverlay(JsonObject overlay, Action<string> logInfo, Action<string> logWarning) {
        _overlay = overlay;
        _logInfo = logInfo;
        _logWarning = logWarning;
    }

    /// <summary>Reads the section and validates it against the settings types. Returns null when the
    /// section is absent or holds nothing usable. Unrecognized keys, read-only keys and values that do
    /// not parse are reported through the warning callback and skipped.</summary>
    public static SettingsOverlay? Create(IConfiguration configuration, string sectionName, Action<string> logInfo, Action<string> logWarning) {
        var section = configuration.GetSection(sectionName);
        if (section.Value == null && !section.GetChildren().Any()) return null;
        var overlay = convertObject(section, typeof(RelatudeDBServerSettings), sectionName, logWarning);
        return overlay == null ? null : new SettingsOverlay(overlay, logInfo, logWarning);
    }

    public bool HasOverrides => _overrides.Count > 0;

    /// <summary>Returns the file settings with the section merged in, and records every key that
    /// actually changed so it can be stripped again before saving.</summary>
    public RelatudeDBServerSettings Apply(RelatudeDBServerSettings fileSettings) {
        _overrides.Clear();
        var root = JsonSerializer.SerializeToNode(fileSettings, LocalSettingsLoaderFile.JsonOptions) as JsonObject
            ?? throw new Exception("Could not serialize the server settings.");
        mergeObject(root, _overlay, []);
        if (_overrides.Count == 0) return fileSettings;
        _logInfo("Configuration overrides applied: " + string.Join(", ", _overrides.Select(o => o.Display)));
        foreach (var o in _overrides) {
            if (o.Path[^1].Property is "Id" or "DefaultStoreId") {
                _logWarning("Configuration override \"" + o.Display + "\" changes an identity key. This re-identifies the object instead of reconfiguring it, and can orphan existing data.");
            }
        }
        return root.Deserialize<RelatudeDBServerSettings>(LocalSettingsLoaderFile.JsonOptions)
            ?? throw new Exception("Could not read the merged server settings.");
    }

    /// <summary>Returns a copy of the live settings where every overridden key holds the value the file
    /// had, so configuration-supplied values are not written to disk. Keys the overlay appended are
    /// removed again.</summary>
    public RelatudeDBServerSettings RemoveOverridesBeforeSave(RelatudeDBServerSettings liveSettings) {
        if (_overrides.Count == 0) return liveSettings;
        if (JsonSerializer.SerializeToNode(liveSettings, LocalSettingsLoaderFile.JsonOptions) is not JsonObject root) return liveSettings;
        for (var i = _overrides.Count - 1; i >= 0; i--) restore(root, _overrides[i]);
        return root.Deserialize<RelatudeDBServerSettings>(LocalSettingsLoaderFile.JsonOptions) ?? liveSettings;
    }

    sealed class Step {
        public string? Property { get; init; }
        public Guid? ElementId { get; init; }
        public int ElementIndex { get; init; } = -1;
        public static Step AtProperty(string name) => new() { Property = name };
        public static Step AtElement(Guid? id, int index) => new() { ElementId = id, ElementIndex = index };
    }
    sealed class Override {
        public required Step[] Path { get; init; }
        public required string Display { get; init; }
        public JsonNode? FileValue { get; init; }
        public bool ExistedInFile { get; init; }
    }

    void record(List<Step> path, JsonNode? fileValue, bool existedInFile) {
        _overrides.Add(new Override {
            Path = [.. path],
            Display = display(path),
            FileValue = fileValue?.DeepClone(),
            ExistedInFile = existedInFile,
        });
    }

    void mergeObject(JsonObject baseObj, JsonObject overlayObj, List<Step> path) {
        foreach (var (key, overlayValue) in overlayObj.ToArray()) {
            path.Add(Step.AtProperty(key));
            var existed = baseObj.TryGetPropertyValue(key, out var baseValue);
            if (overlayValue is JsonObject oo && baseValue is JsonObject bo) mergeObject(bo, oo, path);
            else if (overlayValue is JsonArray oa && baseValue is JsonArray ba) mergeArray(ba, oa, path);
            else if (!JsonNode.DeepEquals(baseValue, overlayValue)) {
                record(path, baseValue, existed);
                baseObj[key] = overlayValue?.DeepClone();
            }
            path.RemoveAt(path.Count - 1);
        }
    }

    void mergeArray(JsonArray baseArr, JsonArray overlayArr, List<Step> path) {
        for (var i = 0; i < overlayArr.Count; i++) {
            var overlayElement = overlayArr[i];
            var id = idOf(overlayElement);
            var index = id == null ? (i < baseArr.Count ? i : -1) : indexOfId(baseArr, id.Value);
            if (index < 0) {
                baseArr.Add(overlayElement?.DeepClone());
                path.Add(Step.AtElement(id, baseArr.Count - 1));
                record(path, null, existedInFile: false);
                path.RemoveAt(path.Count - 1);
                continue;
            }
            var baseElement = baseArr[index];
            path.Add(Step.AtElement(idOf(baseElement), index));
            if (overlayElement is JsonObject oo && baseElement is JsonObject bo) mergeObject(bo, oo, path);
            else if (overlayElement is JsonArray oa && baseElement is JsonArray ba) mergeArray(ba, oa, path);
            else if (!JsonNode.DeepEquals(baseElement, overlayElement)) {
                record(path, baseElement, existedInFile: true);
                baseArr[index] = overlayElement?.DeepClone();
            }
            path.RemoveAt(path.Count - 1);
        }
    }

    void restore(JsonObject root, Override o) {
        JsonNode current = root;
        for (var i = 0; i < o.Path.Length - 1; i++) {
            var next = stepInto(current, o.Path[i]);
            if (next == null) return;
            current = next;
        }
        var last = o.Path[^1];
        if (last.Property != null) {
            if (current is not JsonObject obj) return;
            if (o.ExistedInFile) obj[last.Property] = o.FileValue?.DeepClone();
            else obj.Remove(last.Property);
        } else {
            if (current is not JsonArray arr) return;
            var index = indexOf(arr, last);
            if (index < 0) return;
            if (o.ExistedInFile) arr[index] = o.FileValue?.DeepClone();
            else arr.RemoveAt(index);
        }
    }

    static JsonNode? stepInto(JsonNode current, Step step) {
        if (step.Property != null) return current is JsonObject obj && obj.TryGetPropertyValue(step.Property, out var value) ? value : null;
        if (current is not JsonArray arr) return null;
        var index = indexOf(arr, step);
        return index < 0 ? null : arr[index];
    }
    static int indexOf(JsonArray arr, Step step) {
        if (step.ElementId != null) return indexOfId(arr, step.ElementId.Value);
        return step.ElementIndex >= 0 && step.ElementIndex < arr.Count ? step.ElementIndex : -1;
    }
    static int indexOfId(JsonArray arr, Guid id) {
        for (var i = 0; i < arr.Count; i++) if (idOf(arr[i]) == id) return i;
        return -1;
    }
    static Guid? idOf(JsonNode? element) {
        if (element is not JsonObject obj) return null;
        if (!obj.TryGetPropertyValue("Id", out var value) || value is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id) ? id : null;
    }
    static string display(List<Step> path) {
        var sb = new StringBuilder();
        foreach (var step in path) {
            if (step.Property != null) {
                if (sb.Length > 0) sb.Append('.');
                sb.Append(step.Property);
            } else {
                sb.Append('[').Append(step.ElementIndex).Append(']');
            }
        }
        return sb.ToString();
    }

    static JsonObject? convertObject(IConfigurationSection section, Type type, string configPath, Action<string> warn) {
        var result = new JsonObject();
        var dictionaryValueType = getDictionaryValueType(type);
        foreach (var child in section.GetChildren()) {
            var childPath = configPath + ":" + child.Key;
            if (dictionaryValueType != null) {
                var value = convert(child, dictionaryValueType, childPath, warn);
                if (value != null) result[child.Key] = value;
                continue;
            }
            var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => string.Equals(p.Name, child.Key, StringComparison.OrdinalIgnoreCase));
            if (property == null) {
                warn("Configuration key \"" + childPath + "\" does not match any setting - ignored.");
                continue;
            }
            if (property.SetMethod?.IsPublic != true) {
                warn("Configuration key \"" + childPath + "\" matches a read-only setting - ignored.");
                continue;
            }
            var converted = convert(child, property.PropertyType, childPath, warn);
            if (converted != null) result[property.Name] = converted;
        }
        return result.Count == 0 ? null : result;
    }

    static JsonNode? convert(IConfigurationSection section, Type type, string configPath, Action<string> warn) {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (section.GetChildren().Any()) {
            if (type.IsArray) return convertArray(section, type.GetElementType()!, configPath, warn);
            if (type.IsClass && type != typeof(string)) return convertObject(section, type, configPath, warn);
            warn("Configuration key \"" + configPath + "\" holds a section but the setting is a single " + type.Name + " value - ignored.");
            return null;
        }
        if (section.Value == null) return null;
        return convertValue(section.Value, type, configPath, warn);
    }

    static JsonArray? convertArray(IConfigurationSection section, Type elementType, string configPath, Action<string> warn) {
        var result = new JsonArray();
        foreach (var child in section.GetChildren().OrderBy(c => int.TryParse(c.Key, out var i) ? i : int.MaxValue)) {
            if (!int.TryParse(child.Key, out _)) {
                warn("Configuration key \"" + configPath + ":" + child.Key + "\" is not an array index - ignored.");
                continue;
            }
            var value = convert(child, elementType, configPath + ":" + child.Key, warn);
            if (value != null) result.Add(value);
        }
        return result.Count == 0 ? null : result;
    }

    static JsonNode? convertValue(string raw, Type type, string configPath, Action<string> warn) {
        JsonNode? fail() {
            warn("Configuration key \"" + configPath + "\" does not hold a valid " + type.Name + " - ignored.");
            return null;
        }
        if (type == typeof(string)) return JsonValue.Create(raw);
        if (type == typeof(bool)) return bool.TryParse(raw, out var b) ? JsonValue.Create(b) : fail();
        if (type.IsEnum) return Enum.TryParse(type, raw, true, out var e) ? JsonValue.Create(e!.ToString()) : fail();
        if (type == typeof(Guid)) return Guid.TryParse(raw, out var g) ? JsonValue.Create(g.ToString()) : fail();
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(ushort) || type == typeof(uint))
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l) : fail();
        if (type == typeof(ulong))
            return ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul) ? JsonValue.Create(ul) : fail();
        if (type == typeof(double) || type == typeof(float))
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : fail();
        if (type == typeof(decimal))
            return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? JsonValue.Create(m) : fail();
        if (type == typeof(DateTime))
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? JsonValue.Create(dt) : fail();
        if (type == typeof(DateTimeOffset))
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto) ? JsonValue.Create(dto) : fail();
        if (type == typeof(TimeSpan))
            return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var ts) ? JsonValue.Create(ts) : fail();
        return JsonValue.Create(raw);
    }

    static Type? getDictionaryValueType(Type type) {
        if (!type.IsGenericType) return null;
        var definition = type.GetGenericTypeDefinition();
        if (definition != typeof(Dictionary<,>) && definition != typeof(IDictionary<,>)) return null;
        var arguments = type.GetGenericArguments();
        return arguments[0] == typeof(string) ? arguments[1] : null;
    }
}
