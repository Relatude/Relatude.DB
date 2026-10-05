using Relatude.DB.IO;

namespace Relatude.DB.Logging;
/// <summary>
/// Where the definitions of logs are kept: one {key}.json per log
/// (<see cref="FileKeyUtility.Logger_DefinitionFileName"/>) in one folder of a storage provider.
///
/// The server keeps the definitions of a database's custom logs with the application's other
/// settings, in relatude.settings/logs/{database short name}/ below its root data folder, so they are
/// deployed and kept in source control with the application. <see cref="InLogFolder"/> keeps them in
/// the log folder of the logs' own storage instead, beside the folders of their data.
/// </summary>
public sealed class LogDefinitionFolder {
    /// <param name="io">The provider the folder is in.</param>
    /// <param name="folder">The folder below the provider's root, empty for the root itself.</param>
    /// <param name="display">Where the folder is, for people: relatude.settings/logs/db, say.</param>
    public LogDefinitionFolder(IIOProvider io, string[] folder, string display) {
        IO = io;
        Folder = folder;
        Display = display;
    }
    public IIOProvider IO { get; }
    public string[] Folder { get; }
    /// <summary>Where the folder is, for people.</summary>
    public string Display { get; }

    /// <summary>The definitions in the log folder of <paramref name="io"/>: log/{key}.json.</summary>
    public static LogDefinitionFolder InLogFolder(IIOProvider io) => new(io, [FileKeyUtility.LogFolderName], FileKeyUtility.LogFolderName);

    /// <summary>The file a log's definition is kept in.</summary>
    public string[] FileKey(string logKey) => [.. Folder, FileKeyUtility.Logger_DefinitionFileName(logKey)];
    /// <summary>The file a log's definition is kept in, for people.</summary>
    public string DisplayOf(string logKey) => DisplayOf(FileKey(logKey));
    /// <summary>A file in this folder, for people.</summary>
    public string DisplayOf(string[] fileKey) {
        var name = fileKey[Folder.Length..].AsKeyString();
        return Display.Length == 0 ? name : Display + "/" + name;
    }
    /// <summary>Every definition file in the folder, by name.</summary>
    public string[][] FileKeys() => [.. IO.Search([.. Folder, "*.json"]).Where(k => FileKeyUtility.Logger_IsDefinitionFileName(k.FileName()))];
    /// <summary>Whether this is the log folder of <paramref name="io"/>, where older versions kept definitions too.</summary>
    public bool IsLogFolderOf(IIOProvider io) => ReferenceEquals(io, IO) && Folder.IsSameKey([FileKeyUtility.LogFolderName]);
}
