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
/// relatude.db.overrides.json (what the admin UI changed) and then the RelatudeDB configuration section
/// (appsettings, environment variables) are merged in, so the result is the effective settings, the
/// same the server would run with.
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
        var fileSettings = ReadSettingsFile(target);
        var overlay = AppConfig.CreateOverlay(target);
        var effective = openOverrides(fileSettings, overlay, target, Output.Detail, Output.Warn)?.Effective ?? fileSettings;
        return overlay == null ? effective : overlay.Apply(effective);
    }

    /// <summary>relatude.db.json alone, with nothing merged over it.</summary>
    public static RelatudeDBServerSettings ReadSettingsFile(Target target) {
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
        return settings;
    }

    /// <summary>
    /// Where relatude.db.overrides.json is, found the way the server finds it: the file --overrides
    /// names, or else with the default database as relatude.db.json and the configuration section
    /// describe it - at the root of the storage that database keeps its files in.
    /// </summary>
    public static SettingsOverridesLocation OverridesLocation(RelatudeDBServerSettings fileSettings, SettingsOverlay? overlay, Target target) {
        if (target.OverridesFile != null) return SettingsOverridesLocation.OnDisk(target.OverridesFile, target.Root);
        return SettingsOverridesLocation.Resolve(overlay?.Preview(fileSettings) ?? fileSettings, target.Root);
    }

    /// <summary>What relatude.db.overrides.json changes compared with relatude.db.json, for the settings
    /// command: where the file is, one entry per setting, and why it could not be read when it could not.</summary>
    public static (string Where, SettingsPatch.Entry[] Entries, string? Problem) Overrides(Target target) {
        var opened = openOverrides(ReadSettingsFile(target), AppConfig.CreateOverlay(target), target, _ => { }, _ => { });
        if (opened == null) return ("the default database's storage", [], "the storage could not be reached");
        return (opened.Value.File.Display, [.. opened.Value.File.Entries], opened.Value.Problem);
    }

    // The overrides file merged over the settings file, as the server does at start. A file that is
    // not valid JSON is an error, as it stops the server; storage that cannot be reached is a warning,
    // and the settings are read without the file, as the server runs without it.
    static (SettingsOverridesFile File, RelatudeDBServerSettings Effective, string? Problem)? openOverrides(RelatudeDBServerSettings fileSettings,
        SettingsOverlay? overlay, Target target, Action<string> info, Action<string> warn) {
        SettingsOverridesLocation? location = null;
        try {
            location = OverridesLocation(fileSettings, overlay, target);
            var file = SettingsOverridesFile.Open(location, fileSettings, info, warn, out var effective);
            return (file, effective, null);
        } catch (SettingsOverridesFileInvalidException err) {
            throw new CliException(err.Message, err);
        } catch (Exception err) {
            var problem = "the overrides file could not be read (" + err.Message + ")";
            warn("Settings changed in the admin UI are left out: " + problem + ".");
            if (location == null) return null;
            return (SettingsOverridesFile.ForUnavailable(location, fileSettings, problem), fileSettings, problem);
        }
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
