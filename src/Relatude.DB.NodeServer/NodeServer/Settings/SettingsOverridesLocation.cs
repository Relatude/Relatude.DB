using Relatude.DB.IO;
using System.Text.Json;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// Where relatude.db.overrides.json is kept: with the default database, in an overrides/ folder at the
/// root of the storage provider that database keeps its files in - so on local disk beside its data/,
/// state/ and logs/ folders, in a blob container when the database is in one - unless
/// <see cref="ServerOptions.SettingsOverridesFilePath"/> names a file on disk instead.
///
/// <para>The default database is found in relatude.db.json with the RelatudeDB configuration section
/// over it, never with the overrides file itself: the file cannot decide where it is to be found. So a
/// change made in the admin UI to the default database, its storage provider or that provider's folder
/// moves the database's files at its next open but leaves the overrides file where it is, and the next
/// start finds it there again. Only when such a change is moved into relatude.db.json does the file
/// follow (see <see cref="RelatudeDBServer.MoveOverridesIntoSettingsFile"/>). The settings callbacks
/// in code are not consulted either: they run after the files are read.</para>
///
/// <para>When there is no database to follow - no databases, or one without a primary storage
/// provider - the file is kept on disk at <see cref="SettingsOverridesFile.FallbackRelativePath"/>
/// below the root data folder.</para>
/// </summary>
public sealed class SettingsOverridesLocation {
    SettingsOverridesLocation(IIOProvider? io, string? diskPath, string[] key, Guid? ioId, IOSettings? ioSettings, string display, string? note) {
        Io = io;
        DiskPath = diskPath;
        Key = key;
        IoId = ioId;
        IoSettings = ioSettings;
        Display = display;
        Note = note;
    }

    /// <summary>The folder the file is kept in, below the storage root.</summary>
    public const string FolderName = FileKeyUtility.OverridesFolderName;
    /// <summary>The key of the file in a storage provider: in the overrides folder.</summary>
    public static readonly string[] FileKey = [FolderName, SettingsOverridesFile.FileName];

    /// <summary>The key of the file in <see cref="Io"/>, when it is read through the provider.</summary>
    public string[] Key { get; }

    /// <summary>The provider holding the file, when it is not read as a plain disk file.</summary>
    public IIOProvider? Io { get; }
    /// <summary>The file on disk, when there is one: a named file, or a provider on local disk.</summary>
    public string? DiskPath { get; }
    /// <summary>The storage provider of the default database the file is kept with, if it is.</summary>
    public Guid? IoId { get; }
    /// <summary>The provider's settings as the location was found from, to tell whether it has moved.</summary>
    public IOSettings? IoSettings { get; }
    /// <summary>Where the file is, for people: relative to the root data folder when it is below it.</summary>
    public string Display { get; }
    /// <summary>Why the location is not the obvious one, when it is not; for the startup log.</summary>
    public string? Note { get; }

    /// <summary>A file on disk, relative to the root data folder or absolute.</summary>
    public static SettingsOverridesLocation OnDisk(string path, string rootDataFolder, string? note = null) {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(rootDataFolder, path));
        return new(null, full, [Path.GetFileName(full)], null, null, displayOf(full, rootDataFolder), note);
    }

    /// <summary>Where the file is kept when there is no database to keep it with: see
    /// <see cref="SettingsOverridesFile.FallbackRelativePath"/>.</summary>
    public static SettingsOverridesLocation Fallback(string rootDataFolder, string? note = null) {
        var location = OnDisk(SettingsOverridesFile.FallbackRelativePath, rootDataFolder, note);
        return new(null, location.DiskPath, FileKey, null, null, location.Display, note);
    }

    /// <summary>
    /// The location that follows the default database of <paramref name="settings"/> - relatude.db.json
    /// with configuration over it. Creating the storage provider can fail (a folder outside the
    /// application root, an unreachable blob service); the exception is the caller's to handle.
    /// </summary>
    public static SettingsOverridesLocation Resolve(RelatudeDBServerSettings settings, string rootDataFolder,
        Func<IOSettings, IIOProvider>? createProvider = null) {
        var containers = settings.ContainerSettings ?? [];
        var container = containers.FirstOrDefault(c => c.Id == settings.DefaultStoreId);
        string? note = null;
        if (container == null && containers.Length > 0) {
            container = containers[0];
            note = "DefaultStoreId names no database, so the overrides file is kept with the first one, \"" + (container.Name ?? container.Id.ToString()) + "\".";
        }
        if (container == null) {
            return Fallback(rootDataFolder, "There is no database to keep the overrides file with.");
        }
        var ioSettings = container.IoDatabase is Guid ioId ? (container.IOSettings ?? []).FirstOrDefault(s => s.Id == ioId) : null;
        if (ioSettings == null) {
            return Fallback(rootDataFolder,
                "The default database \"" + (container.Name ?? container.Id.ToString()) + "\" has no primary storage provider to keep the overrides file in.");
        }
        var io = (createProvider ?? (s => IOSettings.Create(s, rootDataFolder)))(ioSettings);
        var copy = copyOf(ioSettings);
        // a provider on local disk: the file is read and written as a plain file, which can be replaced
        // atomically - a provider only offers delete-then-write
        if (io is IOProviderDisk disk) {
            var full = Path.GetFullPath(Path.Combine(disk.BaseFolder, FolderName, SettingsOverridesFile.FileName));
            return new(io, full, FileKey, ioSettings.Id, copy, displayOf(full, rootDataFolder), note);
        }
        var where = ioSettings.IOType switch {
            IOTypes.AzureBlobStorage => "Azure blob container \"" + ioSettings.BlobContainerName + "\"",
            IOTypes.Memory => "memory storage, lost when the server stops",
            _ => ioSettings.IOType.ToString(),
        };
        var name = string.IsNullOrEmpty(ioSettings.Name) ? "" : "\"" + ioSettings.Name + "\", ";
        return new(io, null, FileKey, ioSettings.Id, copy, FileKey.AsKeyString() + " in " + name + where, note);
    }

    /// <summary>Whether both name the same file: the same path on disk, or the same provider settings.</summary>
    public bool SameAs(SettingsOverridesLocation other) {
        if (DiskPath != null || other.DiskPath != null) return string.Equals(DiskPath, other.DiskPath, StringComparison.OrdinalIgnoreCase);
        return IoId == other.IoId && Key.IsSameKey(other.Key) && sameSettings(IoSettings, other.IoSettings);
    }

    /// <summary>Whether a provider built from <paramref name="settings"/> is the one this location uses,
    /// so the server can share the instance instead of building a second one.</summary>
    public bool IsProviderFor(IOSettings settings) => Io != null && IoId == settings.Id && sameSettings(IoSettings, settings);

    static bool sameSettings(IOSettings? a, IOSettings? b)
        => a != null && b != null && JsonSerializer.Serialize(a, LocalSettingsLoaderFile.JsonOptions) == JsonSerializer.Serialize(b, LocalSettingsLoaderFile.JsonOptions);

    static IOSettings copyOf(IOSettings settings)
        => JsonSerializer.Deserialize<IOSettings>(JsonSerializer.Serialize(settings, LocalSettingsLoaderFile.JsonOptions), LocalSettingsLoaderFile.JsonOptions)!;

    static string displayOf(string fullPath, string rootDataFolder) {
        if (string.IsNullOrEmpty(rootDataFolder)) return fullPath;
        var relative = Path.GetRelativePath(rootDataFolder, fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? fullPath : relative.Replace('\\', '/');
    }
}
