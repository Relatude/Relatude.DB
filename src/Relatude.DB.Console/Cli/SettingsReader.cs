using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.Cli;

/// <summary>
/// Reads relatude.db.json without opening anything and without writing to it: the server's own loader
/// creates a default file when none is there, which is not what a read only command should do.
/// The RelatudeDB configuration section (appsettings, environment variables) is merged in, so the
/// result is the effective settings, the same the server would run with.
/// </summary>
public static class SettingsReader {
    static JsonSerializerOptions options() {
        var o = new JsonSerializerOptions {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
    public static RelatudeDBServerSettings Read(Target target) {
        if (!target.SettingsExists) throw new CliException("No settings file at " + target.SettingsPath);
        var json = File.ReadAllText(target.SettingsPath);
        if (json.Trim().Length == 0) throw new CliException("The settings file is empty: " + target.SettingsPath);
        json = json.Replace("\"PersistedQueueStoreEngine\": \"BuiltIn\",", "\"PersistedQueueStoreEngine\": \"Native\","); // as the server does
        RelatudeDBServerSettings settings;
        try {
            settings = JsonSerializer.Deserialize<RelatudeDBServerSettings>(json, options())
                ?? throw new CliException("The settings file could not be read: " + target.SettingsPath);
        } catch (JsonException err) {
            throw new CliException("The settings file is not valid JSON: " + target.SettingsPath + Environment.NewLine + err.Message, err);
        }
        return AppConfig.ApplyOverlay(settings, target);
    }
    public static string Serialize(RelatudeDBServerSettings settings) => JsonSerializer.Serialize(settings, options());

    /// <summary>The container the command works on: --store by name or id, otherwise the default one.</summary>
    public static Guid SelectContainerId(RelatudeDBServerSettings settings, string? nameOrId) {
        var containers = settings.ContainerSettings ?? [];
        if (containers.Length == 0) throw new CliException("The settings file has no ContainerSettings.");
        if (nameOrId != null) {
            if (Guid.TryParse(nameOrId, out var id)) {
                if (containers.Any(c => c.Id == id)) return id;
                throw new CliException("No database with id " + id + ". Available: " + describe(containers));
            }
            var matches = containers.Where(c => string.Equals(c.Name, nameOrId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0].Id;
            if (matches.Length > 1) throw new CliException("More than one database is named \"" + nameOrId + "\", use --store with its id.");
            throw new CliException("No database named \"" + nameOrId + "\". Available: " + describe(containers));
        }
        if (containers.Any(c => c.Id == settings.DefaultStoreId)) return settings.DefaultStoreId;
        if (containers.Length == 1) return containers[0].Id;
        throw new CliException("DefaultStoreId does not name a database, pick one with --store. Available: " + describe(containers));
    }
    public static NodeStoreContainerSettings SelectContainer(RelatudeDBServerSettings settings, string? nameOrId) {
        var id = SelectContainerId(settings, nameOrId);
        return settings.ContainerSettings!.First(c => c.Id == id);
    }
    static string describe(NodeStoreContainerSettings[] containers)
        => string.Join(", ", containers.Select(c => "\"" + c.Name + "\" (" + c.Id + ")"));

    /// <summary>
    /// Builds the datamodel from the container's DatamodelSources, the same way the server does, so the
    /// model can be inspected without opening the database.
    /// </summary>
    public static Datamodel BuildDatamodelFromSettings(CommandArgs args, Target target) {
        var settings = Read(target);
        var container = SelectContainer(settings, target.Store);
        target.RegisterAssemblyProbing();
        var dm = new Datamodel();
        foreach (var source in container.DatamodelSources ?? []) {
            try {
                addSource(dm, source, container, target);
            } catch (Exception err) when (err is not CliException) {
                throw new CliException("Datamodel source \"" + (source.Name ?? source.Id.ToString()) + "\" ("
                    + source.Type + " " + source.Reference + ") could not be loaded: " + err.Message
                    + Environment.NewLine + "Build the application, or name its output folder with --bin."
                    + Environment.NewLine + target.Describe(), err);
            }
        }
        ModelSource.AddTo(dm, args, target);
        if (dm.NodeTypes.Count <= 1 && dm.Relations.Count == 0) {
            throw new CliException("The datamodel sources in " + target.SettingsPath + " produced no model types.");
        }
        return dm;
    }
    static void addSource(Datamodel dm, DatamodelSource source, NodeStoreContainerSettings container, Target target) {
        DatamodelSourceLoader.Load(dm, source, target.Root, id => {
            var io = (container.IOSettings ?? []).FirstOrDefault(s => s.Id == id);
            return io == null ? null : IOSettings.Create(io, target.Root);
        });
    }
}
