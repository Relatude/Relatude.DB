namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// Where relatude.db.json is kept: in relatude.settings/ below the root data folder - the content root
/// of the application, unless <see cref="ServerOptions.DefaultDataFolderPath"/> names another
/// (<see cref="Defaults.SettingsFilePath"/>). Older versions kept the file in the root data folder
/// itself, and for a while in relatude.settings/db/; those are the <see cref="LegacyFilePaths"/>, where
/// a file is still found and from where the server moves it at start (<see cref="Find"/>).
/// <para>Relative paths in the file - the folder of a storage provider on local disk, of a datamodel
/// source, of an overrides file - stay relative to the root data folder, not to the folder the file is
/// now in, so moving the file changes nothing it says.</para>
/// </summary>
public static class SettingsFileLocation {
    // the folder the file was kept in for a while, below relatude.settings
    const string interimFolderName = "db";

    /// <summary>The settings file below <paramref name="root"/>: relatude.settings/relatude.db.json.</summary>
    public static string FilePath(string root) => Path.Combine(FolderPath(root), Defaults.SettingsFileName);
    /// <summary>The folder the settings file is kept in below <paramref name="root"/>: relatude.settings.</summary>
    public static string FolderPath(string root) => Path.Combine(root, Defaults.SettingsFolderPath);
    /// <summary>
    /// Where older versions kept the settings file, the most recent first: relatude.settings/db/, and
    /// <paramref name="root"/> itself.
    /// </summary>
    public static string[] LegacyFilePaths(string root) => [
        Path.Combine(FolderPath(root), interimFolderName, Defaults.SettingsFileName),
        Path.Combine(root, Defaults.SettingsFileName),
    ];
    /// <summary>Whether <paramref name="path"/> is one of the places older versions kept the settings file of <paramref name="root"/> in.</summary>
    public static bool IsLegacyPlace(string path, string root)
        => LegacyFilePaths(root).Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The root data folder a settings file belongs to: one folder up for one kept in relatude.settings,
    /// two for one in relatude.settings/db, otherwise the folder it is in - a file in the oldest place, or
    /// one named outright somewhere else.
    /// </summary>
    public static string RootOf(string settingsFile) {
        var folder = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(settingsFile))!);
        if (isSettingsFolder(folder)) return folder.Parent!.FullName;
        if (string.Equals(folder.Name, interimFolderName, StringComparison.OrdinalIgnoreCase) && folder.Parent is { } parent && isSettingsFolder(parent)) {
            return parent.Parent!.FullName;
        }
        return folder.FullName;

        static bool isSettingsFolder(DirectoryInfo dir) => dir.Parent != null && string.Equals(dir.Name, Defaults.SettingsFolderPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="root"/> has a settings file, in its place or an older one.</summary>
    public static bool HasSettingsFile(string root) => File.Exists(FilePath(root)) || LegacyFilePaths(root).Any(File.Exists);

    /// <summary>
    /// The settings file below <paramref name="root"/>, without moving anything: the one in
    /// relatude.settings, else one in an older place, else - for a file still to be written - the one in
    /// relatude.settings. What the command line tool reads; only the server moves the file.
    /// </summary>
    public static string Existing(string root) {
        var file = FilePath(root);
        if (File.Exists(file)) return file;
        return LegacyFilePaths(root).FirstOrDefault(File.Exists) ?? file;
    }

    /// <summary>
    /// The settings file the server reads below <paramref name="root"/>, moving the one an older version
    /// kept elsewhere into relatude.settings when there is none there yet: the folder is created and the
    /// file moved across as it is, comments and all, so nothing is left in the old place to be edited by
    /// mistake. A move that fails is no reason not to start - the file is then read and written where it
    /// is, and the next start tries again. A file left in an older place beside the one in
    /// relatude.settings is not read, and <paramref name="warn"/> says so. With no file anywhere, the one
    /// in relatude.settings is returned, for the loader to write.
    /// </summary>
    public static string Find(string root, Action<string> info, Action<string> warn) {
        var file = FilePath(root);
        if (!File.Exists(file) && LegacyFilePaths(root).FirstOrDefault(File.Exists) is { } legacy) {
            try {
                Directory.CreateDirectory(FolderPath(root));
                File.Move(legacy, file);
                info("Moved " + Display(legacy, root) + " into " + Defaults.SettingsFolderPath + ": the settings are read from " + Display(file, root) + " from now on.");
                removeIfEmpty(Path.GetDirectoryName(legacy)!, root);
            } catch (Exception err) {
                // a second process starting at the same moment - an overlapped recycle - may have moved it first
                if (!File.Exists(file) || File.Exists(legacy)) {
                    warn("Could not move " + Display(legacy, root) + " into " + Defaults.SettingsFolderPath + " (" + err.Message
                        + "). It is read and written where it is, and the next start tries again.");
                    return legacy;
                }
            }
        }
        if (File.Exists(file)) {
            foreach (var old in LegacyFilePaths(root).Where(File.Exists)) {
                warn("There is a " + Display(old, root) + " in an older place as well as " + Display(file, root) + ", and only the second is read."
                    + " Delete the old one, once anything it says that should still apply is in the new one.");
            }
        }
        return file;
    }

    // the relatude.settings/db folder a file was moved out of, when nothing else was kept there
    static void removeIfEmpty(string folder, string root) {
        if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return;
        try {
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        } catch {
            // an empty folder left behind is harmless
        }
    }

    /// <summary>A path for people: relative to <paramref name="root"/>, with forward slashes, when it is below it.</summary>
    public static string Display(string path, string root) {
        if (string.IsNullOrEmpty(root)) return path;
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative.Replace('\\', '/');
    }
}
