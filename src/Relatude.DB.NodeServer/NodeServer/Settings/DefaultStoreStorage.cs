using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// RelatudeDB:DefaultStore: where the default database keeps its data, given without any id from
/// relatude.db.json, for a host that runs an application it did not build. Every storage provider the
/// default database uses is pointed at one blob container (leased, as two processes meet there during a
/// restart), and the persisted indexes at a folder. It is expanded into ordinary overrides at each
/// <see cref="SettingsOverlay.Apply(JsonObject)"/>, so they are never saved and show as configured.
/// </summary>
public sealed class DefaultStoreStorage {
    public const string ConfigurationKey = "DefaultStore";

    public string? BlobConnectionString { get; init; }
    public string? BlobContainerName { get; init; }
    /// <summary>The default database's <see cref="SettingsLocal.PersistedValueIndexFolderPath"/>.</summary>
    public string? IndexFolderPath { get; init; }

    bool hasBlob => !string.IsNullOrWhiteSpace(BlobConnectionString) && !string.IsNullOrWhiteSpace(BlobContainerName);

    internal static DefaultStoreStorage? Read(IConfigurationSection section, Action<string> warn) {
        string? connection = null, container = null, indexFolder = null;
        foreach (var child in section.GetChildren()) {
            var path = section.Path + ":" + child.Key;
            if (child.GetChildren().Any()) {
                warn("Configuration key \"" + path + "\" holds a section but the setting is a single value - ignored.");
                continue;
            }
            if (child.Key.Equals(nameof(BlobConnectionString), StringComparison.OrdinalIgnoreCase)) connection = child.Value;
            else if (child.Key.Equals(nameof(BlobContainerName), StringComparison.OrdinalIgnoreCase)) container = child.Value;
            else if (child.Key.Equals(nameof(IndexFolderPath), StringComparison.OrdinalIgnoreCase)) indexFolder = child.Value;
            else warn("Configuration key \"" + path + "\" does not match any setting - ignored.");
        }
        if (string.IsNullOrWhiteSpace(connection) != string.IsNullOrWhiteSpace(container)) {
            warn("Configuration keys \"" + section.Path + ":" + nameof(BlobConnectionString) + "\" and \"" + nameof(BlobContainerName)
                + "\" are needed together - the default database's storage is left as it is.");
            connection = container = null;
        }
        var result = new DefaultStoreStorage { BlobConnectionString = connection, BlobContainerName = container, IndexFolderPath = indexFolder };
        return result.hasBlob || !string.IsNullOrWhiteSpace(indexFolder) ? result : null;
    }

    /// <summary>The overrides this stands for, against settings in the form they serialize to; null when
    /// there is no default database to apply them to.</summary>
    internal JsonObject? FragmentFor(JsonObject settings, Action<string> warn) {
        var containers = settings["ContainerSettings"] as JsonArray;
        var defaultId = guidOf(settings["DefaultStoreId"]);
        var store = containers?.OfType<JsonObject>().FirstOrDefault(c => guidOf(c["Id"]) == defaultId);
        if (containers == null || defaultId == null || store == null) {
            warn("Configuration section \"" + SettingsOverlay.DefaultSectionName + ":" + ConfigurationKey + "\" is set, but there is no default database to apply it to - ignored.");
            return null;
        }
        var fragments = new Dictionary<Guid, JsonObject> { [defaultId.Value] = new JsonObject { ["Id"] = defaultId.Value.ToString() } };
        JsonObject fragmentOf(Guid containerId) {
            if (!fragments.TryGetValue(containerId, out var fragment)) fragments[containerId] = fragment = new JsonObject { ["Id"] = containerId.ToString() };
            return fragment;
        }
        if (hasBlob) {
            var ioIds = new List<Guid>();
            var database = guidOf(store["IoDatabase"]);
            if (database == null) {
                // stable, so the same override is made at every start
                database = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(ConfigurationKey + ":" + defaultId)));
                fragmentOf(defaultId.Value)["IoDatabase"] = database.Value.ToString();
            }
            ioIds.Add(database.Value);
            foreach (var key in new[] { "IoIndexes", "IoBackup", "IoLog" }) if (guidOf(store[key]) is Guid id) ioIds.Add(id);
            if (store["FileStoreSettings"] is JsonArray fileStores)
                foreach (var fileStore in fileStores.OfType<JsonObject>()) if (guidOf(fileStore["IoProviderId"]) is Guid id) ioIds.Add(id);
            foreach (var ioId in ioIds.Distinct()) {
                var owner = containers.OfType<JsonObject>().FirstOrDefault(c => c["IOSettings"] is JsonArray ios && ios.OfType<JsonObject>().Any(io => guidOf(io["Id"]) == ioId));
                var fragment = fragmentOf(owner == null ? defaultId.Value : guidOf(owner["Id"])!.Value);
                if (fragment["IOSettings"] is not JsonArray list) fragment["IOSettings"] = list = [];
                var io = new JsonObject {
                    ["Id"] = ioId.ToString(),
                    ["IOType"] = nameof(IOTypes.AzureBlobStorage),
                    ["BlobConnectionString"] = BlobConnectionString,
                    ["BlobContainerName"] = BlobContainerName,
                    ["LockBlob"] = true,
                };
                if (owner == null) io["Name"] = "Blob container";
                list.Add(io);
            }
            // the secondary log writes the primary's file names: in the same container it would write the same blobs
            if (guidOf(store["IoDatabaseSecondary"]) != null) fragmentOf(defaultId.Value)["IoDatabaseSecondary"] = null;
        }
        if (!string.IsNullOrWhiteSpace(IndexFolderPath))
            fragmentOf(defaultId.Value)["LocalSettings"] = new JsonObject { ["PersistedValueIndexFolderPath"] = IndexFolderPath };
        return new JsonObject { ["ContainerSettings"] = new JsonArray([.. fragments.Values]) };
    }

    static Guid? guidOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : null;
}
