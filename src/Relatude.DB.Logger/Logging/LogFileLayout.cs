using Relatude.DB.IO;

namespace Relatude.DB.Logging;
/// <summary>
/// Every log keeps its files - entries, text copies, statistics and their backup - in a folder of its
/// own below the log folder, named after its key (<see cref="FileKeyUtility.Logger_FolderKey"/>). Older
/// versions called the log folder "log" rather than "logs", and before that kept all of a log's files
/// directly in it. <see cref="MoveIntoLogFolders"/> moves what they left, before anything reads it.
/// </summary>
public static class LogFileLayout {
    /// <summary>
    /// Moves what older versions left into the folders the logs read today: everything in the old log
    /// folder (log/) into the log folder (logs/), then the files kept directly in the log folder into the
    /// folders of their logs. Run it before any log is created on <paramref name="io"/>: a log reads its
    /// files when it is created. Returns the number of files moved.
    /// </summary>
    public static int MoveIntoLogFolders(IIOProvider io, Action<string>? log = null) => MoveOutOfLegacyLogFolder(io, log) + MoveFlatFilesIntoLogFolders(io, log);

    /// <summary>
    /// Moves everything an older version kept in the old log folder (log/) into the log folder (logs/),
    /// where they keep the place they had below it. The whole folder is renamed where the provider can
    /// and the new one is not there yet; otherwise every file is moved on its own, by the rules of
    /// <see cref="MoveFlatFilesIntoLogFolders"/>. Returns the number of files moved.
    /// </summary>
    public static int MoveOutOfLegacyLogFolder(IIOProvider io, Action<string>? log = null) {
        string[] legacy = [FileKeyUtility.LegacyLogFolderName];
        string[] current = [FileKeyUtility.LogFolderName];
        var files = filesBelow(io, legacy);
        if (files.Count == 0) return 0;
        if (io.CanRenameFolder && !filesBelow(io, current).Any()) {
            try {
                io.RenameFolder(legacy, current);
                log?.Invoke($"Renamed the log folder {legacy.AsKeyString()}/ to {current.AsKeyString()}/ ({files.Count} {(files.Count == 1 ? "file" : "files")}).");
                return files.Count;
            } catch {
                // an empty logs/ already there, or a file in use: one file at a time below
            }
        }
        var moved = 0;
        foreach (var key in files) {
            if (move(io, key, [.. current, .. key[legacy.Length..]], log)) moved++;
        }
        if (moved > 0) log?.Invoke($"Moved {moved} log {(moved == 1 ? "file" : "files")} from {legacy.AsKeyString()}/ to {current.AsKeyString()}/.");
        if (io.SupportsEmptyFolders && filesBelow(io, legacy).Count == 0) {
            try { io.DeleteFolderIfItExists(legacy); } catch { } // an empty folder left behind is harmless
        }
        return moved;
    }

    /// <summary>
    /// Moves the files an older version kept directly in the log folder into the folders of their logs,
    /// renamed where the provider can rename and copied, checked and deleted where it cannot (blob
    /// storage). Every file found is moved, whether a log of that key is defined or not, so nothing is
    /// left behind where no log reads any more. A file whose place is already taken by one of the same
    /// size is what an interrupted move leaves: the old one is deleted. One of another size is left
    /// where it is and said so - a log's history is never overwritten and never stops a start.
    /// Returns the number of files moved.
    /// </summary>
    public static int MoveFlatFilesIntoLogFolders(IIOProvider io, Action<string>? log = null) {
        var legacy = FileKeyUtility.Logger_GetLegacyFlatFileKeys(io);
        if (legacy.Length == 0) return 0;
        var movedByLog = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in legacy) {
            var logKey = FileKeyUtility.Logger_KeyOfFileName(key.FileName());
            if (logKey == null) continue;
            string[] target = [.. FileKeyUtility.Logger_FolderKey(logKey), key.FileName()];
            if (!move(io, key, target, log)) continue;
            var folder = FileKeyUtility.Logger_FolderKey(logKey).AsKeyString();
            movedByLog[folder] = movedByLog.GetValueOrDefault(folder) + 1;
        }
        foreach (var (folder, count) in movedByLog) {
            log?.Invoke($"Moved {count} log {(count == 1 ? "file" : "files")} into {folder}/, the folder of the log.");
        }
        return movedByLog.Values.Sum();
    }

    // One file to its new place; true when it was moved. A place taken by a file of the same size is
    // what an interrupted move leaves, and the old file is deleted; another size is left alone.
    static bool move(IIOProvider io, string[] from, string[] to, Action<string>? log) {
        try {
            if (io.Exists(to)) {
                if (io.GetFileSizeOrZeroIfUnknown(to) == io.GetFileSizeOrZeroIfUnknown(from)) {
                    io.DeleteFileIfItExists(from); // copied before, but not yet deleted
                } else {
                    log?.Invoke($"Log file {from.AsKeyString()} left where it is: {to.AsKeyString()} already exists with another size.");
                }
                return false;
            }
            if (io.CanRenameFile) {
                io.RenameFile(from, to);
            } else {
                io.CopyFile(from, to);
                if (io.GetFileSizeOrZeroIfUnknown(to) != io.GetFileSizeOrZeroIfUnknown(from))
                    throw new Exception("the copy does not have the size of the original");
                io.DeleteFileIfItExists(from);
            }
            return true;
        } catch (Exception error) {
            log?.Invoke($"Could not move log file {from.AsKeyString()} to {to.AsKeyString()}: {error.Message}");
            return false;
        }
    }

    // The files below a folder, at any depth. Memory and blob storage list every file they have; a disk
    // provider lists only the folders it knows, which the old log folder no longer is, so that one is
    // read from the disk itself.
    static List<string[]> filesBelow(IIOProvider io, string[] folder) {
        var listed = io.GetFiles().Select(f => f.KeyOf())
            .Where(k => k.Length > folder.Length && k[..folder.Length].IsSameKey(folder))
            .ToList();
        if (listed.Count > 0) return listed;
        if (!io.TryGetLocalFolderPath(folder, out var local) || !Directory.Exists(local)) return [];
        return [.. Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories)
            .Select(file => (string[])[.. folder, .. Path.GetRelativePath(local, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)])];
    }
}
