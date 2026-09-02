using Relatude.DB.DataStores;
using Relatude.DB.NodeServer.Settings;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer;

public interface ISettingsLoader {
    Task<RelatudeDBServerSettings> ReadAsync();
    Task WriteAsync(RelatudeDBServerSettings settings);
}
public class LocalSettingsLoaderFile(string filePath) : ISettingsLoader {
    public static JsonSerializerOptions? PrettyJsonOptions = null;
    public static JsonSerializerOptions JsonOptions => getOptions();
    static JsonSerializerOptions getOptions() {
        if (PrettyJsonOptions == null) {
            PrettyJsonOptions = new JsonSerializerOptions {
                PropertyNamingPolicy = null,
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                //DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            PrettyJsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        }
        return PrettyJsonOptions;
    }
    public async Task<RelatudeDBServerSettings> ReadAsync() {
        var path = Path.Combine(filePath);
        var json = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        if (json == "") {
            var settings = RelatudeDBServerSettings.CreateDefault();
            await WriteAsync(settings);
            return settings;
        }
        // compatibility "hack":
        if (json.Contains("\"PersistedQueueStoreEngine\": \"BuiltIn\",")) {
            json = json.Replace("\"PersistedQueueStoreEngine\": \"BuiltIn\",", "\"PersistedQueueStoreEngine\": \"Native\",");
        }
        var migrated = false;
        if (json.Contains("PersistedValueIndexEngine") || json.Contains("PersistedTextIndexEngine") || json.Contains("IndexCacheSizeInMb")
            || json.Contains("UsePersistedValueIndexesByDefault") || json.Contains("UsePersistedTextIndexesByDefault") || json.Contains("\"IndexType\"")) {
            var root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
            if (root != null && MigrateLegacyIndexEngineSettings(root)) {
                json = root.ToJsonString(getOptions());
                migrated = true;
            }
        }
        var result = JsonSerializer.Deserialize<RelatudeDBServerSettings>(json, getOptions()) ?? new RelatudeDBServerSettings() {
            Id = Guid.NewGuid(),
            ContainerSettings = [],
            Name = "Relatude.DB Server"
        };
        if (migrated) await WriteAsync(result); // the file now says what the running settings say; comments in it do not survive
        return result;
    }
    public Task WriteAsync(RelatudeDBServerSettings settings) {
        var json = JsonSerializer.Serialize(settings, getOptions());
        var path = Path.Combine(filePath);
        return File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Translates the index engine settings from before the engine lists existed, in place on the
    /// parsed file. Each database then had one engine per kind chosen by an enum
    /// (<c>PersistedValueIndexEngine</c>, <c>PersistedTextIndexEngine</c>, and <c>IndexType</c> on the
    /// AI settings) plus a flag saying whether indexes were persisted by default. A persisted, non-memory
    /// choice becomes one entry in the matching engine list with the default pointing at it, the vector
    /// entry taking over <c>IndexCacheSizeInMb</c> as its memory budget; anything else means the memory
    /// index, which is what the absence of a default already says. The old keys are removed either way,
    /// and a database that already carries the new keys only loses the old ones. Returns whether the
    /// file changed. Public so the migration can be tested on its own.
    /// </summary>
    public static bool MigrateLegacyIndexEngineSettings(JsonObject root) {
        if (root["ContainerSettings"] is not JsonArray containers) return false;
        var changed = false;
        foreach (var node in containers) {
            if (node is not JsonObject container) continue;
            var local = container["LocalSettings"] as JsonObject;
            if (local != null) {
                changed |= migrateKind(local, "UsePersistedValueIndexesByDefault", "PersistedValueIndexEngine", "ValueIndexes", "DefaultValueIndex", null);
                changed |= migrateKind(local, "UsePersistedTextIndexesByDefault", "PersistedTextIndexEngine", "TextIndexes", "DefaultTextIndex", null);
            }
            if (container["AISettings"] is JsonObject ai && (ai.ContainsKey("IndexType") || ai.ContainsKey("IndexCacheSizeInMb"))) {
                var type = takeString(ai, "IndexType");
                var cacheMb = takeNumber(ai, "IndexCacheSizeInMb");
                if (type != null && !string.Equals(type, "Memory", StringComparison.OrdinalIgnoreCase)) {
                    if (local == null) container["LocalSettings"] = local = new JsonObject();
                    // the engines' own defaults before the budget became a setting
                    var budget = cacheMb ?? (string.Equals(type, IndexEngineTypes.HNSW, StringComparison.OrdinalIgnoreCase) ? 100 : 256);
                    addEngine(local, "VectorIndexes", "DefaultVectorIndex", type, (int)Math.Round(budget));
                }
                changed = true;
            }
        }
        return changed;

        static bool migrateKind(JsonObject local, string useKey, string engineKey, string listKey, string defaultKey, object? _) {
            if (!local.ContainsKey(useKey) && !local.ContainsKey(engineKey)) return false;
            var persisted = takeBool(local, useKey) ?? true; // the defaults the removed properties had
            var engine = takeString(local, engineKey) ?? IndexEngineTypes.Native;
            if (local.ContainsKey(listKey) || local.ContainsKey(defaultKey)) return true; // already on the new keys: the old ones are simply dropped
            if (persisted && !string.Equals(engine, "Memory", StringComparison.OrdinalIgnoreCase)) {
                addEngine(local, listKey, defaultKey, engine, new IndexEngineSettings().MaxMemoryUsageInMb);
            }
            return true;
        }
        static void addEngine(JsonObject local, string listKey, string defaultKey, string typeName, int memoryMb) {
            var id = Guid.NewGuid();
            local[listKey] = new JsonArray(new JsonObject {
                ["Id"] = id,
                ["TypeName"] = typeName,
                ["MaxMemoryUsageInMb"] = memoryMb,
            });
            local[defaultKey] = id;
        }
        static string? takeString(JsonObject obj, string key) {
            var value = obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            obj.Remove(key);
            return value;
        }
        static bool? takeBool(JsonObject obj, string key) {
            var value = obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null;
            obj.Remove(key);
            return value;
        }
        static double? takeNumber(JsonObject obj, string key) {
            var value = obj[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : (double?)null;
            obj.Remove(key);
            return value;
        }
    }
}
