using Relatude.DB.Common;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// The data folder below the application's folder used to be called relatude.db, and its scratch folder
/// relatude.db.temp; they are <see cref="Defaults.DataFolderPath"/> and <see cref="Defaults.TempFolderPath"/>
/// now. At start, before the settings are read, the server renames an old data folder and changes the
/// paths in relatude.db.json - and in relatude.db.overrides.json, which is inside the folder - that point
/// into it. The files are changed as text, so their comments stay; only path values naming the folder
/// ("relatude.db", "relatude.db/...") change.
/// </summary>
public static class DataFolderMigration {
    public const string LegacyDataFolderPath = "relatude.db";
    public const string LegacyTempFolderPath = "relatude.db.temp";

    /// <summary>
    /// Renames <paramref name="rootDataFolder"/>/relatude.db to relatude.data, and points the settings files at
    /// it. Nothing is moved when relatude.data is there already: the settings then decide which is used, and
    /// <paramref name="warn"/> says so. A rename that fails while relatude.db.json still names the old folder
    /// is no reason not to start - the old folder is used as the settings say, and the next start tries again;
    /// one that fails while relatude.db.json names the new folder stops the start, which would otherwise open
    /// an empty database there.
    /// </summary>
    public static void Run(string rootDataFolder, string? settingsFile, Action<string> info, Action<string> warn) {
        var old = Path.Combine(rootDataFolder, LegacyDataFolderPath);
        var current = Path.Combine(rootDataFolder, Defaults.DataFolderPath);
        var settingsText = settingsFile != null && File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : null;
        var settingsNameOld = settingsText != null && namesOldFolder(settingsText);
        // the settings that will be read use the new folder: they say so, or there are none and the defaults will
        var settingsNameNew = settingsText == null || names(settingsText, Defaults.DataFolderPath);
        // an old folder the settings name neither way is not the database's: left alone
        if (Directory.Exists(old) && (settingsNameOld || settingsNameNew)) {
            if (Directory.Exists(current)) {
                warn("Both " + LegacyDataFolderPath + "/ and " + Defaults.DataFolderPath + "/ are in " + rootDataFolder + ". Nothing is moved: "
                    + Defaults.SettingsFileName + " decides which is used. " + Defaults.DataFolderPath + "/ is the name the data folder has now; delete the other once nothing in it is missed.");
                return;
            }
            try {
                Directory.Move(old, current);
            } catch (Exception err) {
                var message = "Could not rename the data folder " + LegacyDataFolderPath + "/ to " + Defaults.DataFolderPath + "/ (" + err.Message + ").";
                if (settingsNameOld) {
                    warn(message + " It is used where it is, as " + Defaults.SettingsFileName + " says, and renamed at a later start.");
                    return;
                }
                throw new Exception(message + " " + Defaults.SettingsFileName + " names " + Defaults.DataFolderPath + "/, so the database would open empty there: "
                    + "stop whatever holds files in " + LegacyDataFolderPath + "/, or rename it by hand, and start again.", err);
            }
            info("Renamed the data folder " + LegacyDataFolderPath + "/ to " + Defaults.DataFolderPath + "/.");
            var overridesFile = Path.Combine(current, SettingsOverridesLocation.FolderName, SettingsOverridesFile.FileName);
            if (File.Exists(overridesFile)) rewrite(overridesFile, File.ReadAllText(overridesFile), info, warn);
        }
        if (settingsNameOld) rewrite(settingsFile!, settingsText!, info, warn);
    }

    /// <summary>Deletes the old scratch folder, relatude.db.temp, when it is there: it is emptied at every start anyway.</summary>
    public static void RemoveLegacyTempFolder(string rootDataFolder, Action<string> info) {
        var old = Path.Combine(rootDataFolder, LegacyTempFolderPath);
        if (!Directory.Exists(old)) return;
        try {
            Directory.Delete(old, recursive: true);
            info("Deleted the old scratch folder " + LegacyTempFolderPath + "/; the scratch folder is " + Defaults.TempFolderPath + "/ now.");
        } catch {
            // left for the next start: nothing reads it
        }
    }

    // a path value naming the old folder, or a folder or file in it: "relatude.db", "relatude.db/...",
    // "relatude.db\\..." - and not relatude.db.json, relatude.db2 or the admin UI's /relatude.db
    static readonly string[] oldValues = ["\"" + LegacyDataFolderPath + "\"", "\"" + LegacyDataFolderPath + "/", "\"" + LegacyDataFolderPath + "\\\\"];
    static bool namesOldFolder(string text) => oldValues.Any(v => text.Contains(v, StringComparison.Ordinal));
    static bool names(string text, string folder) => text.Contains("\"" + folder + "\"", StringComparison.Ordinal)
        || text.Contains("\"" + folder + "/", StringComparison.Ordinal) || text.Contains("\"" + folder + "\\\\", StringComparison.Ordinal);
    public static string Replace(string text) => text
        .Replace(oldValues[0], "\"" + Defaults.DataFolderPath + "\"", StringComparison.Ordinal)
        .Replace(oldValues[1], "\"" + Defaults.DataFolderPath + "/", StringComparison.Ordinal)
        .Replace(oldValues[2], "\"" + Defaults.DataFolderPath + "\\\\", StringComparison.Ordinal);

    static void rewrite(string file, string text, Action<string> info, Action<string> warn) {
        if (!namesOldFolder(text)) return;
        try {
            var temp = file + ".tmp";
            File.WriteAllText(temp, Replace(text));
            FileOpenRetry.Replace(temp, file, TimeSpan.FromSeconds(10));
            info("Changed the paths in " + Path.GetFileName(file) + " from " + LegacyDataFolderPath + "/ to " + Defaults.DataFolderPath + "/.");
        } catch (Exception err) {
            warn("Could not change the paths in " + file + " from " + LegacyDataFolderPath + "/ to " + Defaults.DataFolderPath + "/ (" + err.Message + "). Change them by hand.");
        }
    }
}
