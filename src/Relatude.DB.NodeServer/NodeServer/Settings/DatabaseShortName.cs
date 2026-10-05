using System.Text.RegularExpressions;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// The short name of a database (<see cref="NodeStoreContainerSettingsBase.ShortName"/>): a short,
/// file-system-safe name the server uses for the folder it keeps the database's settings files in,
/// below relatude.settings - relatude.settings/{short name}/logs/ holds the definitions of its custom
/// logs, and relatude.settings/{short name}/datamodel.overrides.json the datamodel overrides every
/// installation shares. A database without one keeps them directly in relatude.settings
/// (relatude.settings/logs/, relatude.settings/datamodel.overrides.json), which is all an installation
/// with one database needs. Short names are unique within an
/// installation, and only one database can be without one.
/// </summary>
public static class DatabaseShortName {
    // letters, digits, '-' and '_' - a folder name on every file system, and in a URL or a blob name too
    static readonly Regex _pattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);
    static readonly HashSet<string> _reservedByWindows = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    // the folders relatude.settings has for the database without a short name: a short name naming one
    // of them would put that database's folder inside the other database's
    static readonly HashSet<string> _reservedBySettings = new(StringComparer.OrdinalIgnoreCase) { LogsFolderName };
    /// <summary>The folder below a database's settings folder holding the definitions of its custom logs.</summary>
    public const string LogsFolderName = "logs";
    /// <summary>The file in a database's settings folder holding the datamodel overrides every installation shares.</summary>
    public const string DatamodelOverridesFileName = "datamodel.overrides.json";

    /// <summary>Why a short name cannot be used, or null when it can. Empty can: it means none.</summary>
    public static string? Problem(string? shortName) {
        if (string.IsNullOrWhiteSpace(shortName)) return null;
        var name = shortName.Trim();
        if (!_pattern.IsMatch(name)) return "A short name is letters, digits, '-' and '_', starts with a letter or a digit, and is at most 64 characters long: it names a folder.";
        if (_reservedByWindows.Contains(name)) return $"\"{name}\" is a name Windows keeps for a device, so it cannot name a folder.";
        if (_reservedBySettings.Contains(name)) return $"\"{name}\" is the name of a folder " + Defaults.SettingsFolderPath + " already has.";
        return null;
    }

    /// <summary>The short name in force: the one set, trimmed, or null for none - also when the one set
    /// cannot name a folder (the server says so at start, see <see cref="Problem"/>).</summary>
    public static string? Of(NodeStoreContainerSettingsBase settings) {
        var name = settings.ShortName?.Trim();
        return string.IsNullOrEmpty(name) || Problem(name) != null ? null : name;
    }

    /// <summary>Whether two short names name the same folder: compared without case, as Windows and macOS
    /// do, and none is the same as none.</summary>
    public static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>The short name for people: the name, or "none".</summary>
    public static string Describe(string? shortName) => shortName == null ? "none" : "\"" + shortName + "\"";

    /// <summary>The folder of a database's settings files, relative to the root data folder:
    /// relatude.settings/{short name}, or relatude.settings itself for none.</summary>
    public static string SettingsFolder(string? shortName) => shortName == null ? Defaults.SettingsFolderPath : Defaults.SettingsFolderPath + "/" + shortName;

    /// <summary>The folder of a database's log definitions, relative to the root data folder:
    /// relatude.settings/{short name}/logs, or relatude.settings/logs for none.</summary>
    public static string LogDefinitionsFolder(string? shortName) => SettingsFolder(shortName) + "/" + LogsFolderName;

    /// <summary>The file of a database's shared datamodel overrides, relative to the root data folder:
    /// relatude.settings/{short name}/datamodel.overrides.json, or relatude.settings/datamodel.overrides.json for none.</summary>
    public static string DatamodelOverridesFile(string? shortName) => SettingsFolder(shortName) + "/" + DatamodelOverridesFileName;
}
