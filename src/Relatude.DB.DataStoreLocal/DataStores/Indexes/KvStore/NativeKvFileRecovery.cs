using Relatude.DB.Common;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes.KvStore;

/// <summary>
/// Opens a native KV file whose content is derived from the log. A file that cannot be read, or
/// one an index flagged with <see cref="MarkCorrupt"/>, is set aside and replaced by an empty one,
/// which the store then fills by replaying the log - instead of failing every open of the database.
/// </summary>
internal static class NativeKvFileRecovery {
    public static BPlusTreeStorageEngine Open(string? filePath, BPlusTreeEngineOptions options, string engineName, out string? notice) {
        notice = null;
        if (filePath == null) return new BPlusTreeStorageEngine(null, options);
        var flagPath = flagPathFor(filePath);
        string cause;
        if (File.Exists(flagPath)) {
            cause = "an index found it inconsistent: " + readFlag(flagPath);
        } else {
            BPlusTreeStorageEngine? engine = null;
            try {
                engine = new BPlusTreeStorageEngine(filePath, options);
                engine.VerifyRoots();
                return engine;
            } catch (Exception e) {
                engine?.Dispose(); // whatever failed: an engine left open holds the file for the next open in this process
                if (!IsCorruption(e)) throw;
                cause = "it could not be read: " + e.GetType().Name + ": " + e.Message;
            }
        }
        var copyPath = Path.Combine(Path.GetDirectoryName(filePath)!, FileKeyUtility.IndexEngine_CorruptCopyFileKey(Path.GetFileName(filePath)));
        if (File.Exists(filePath)) FileOpenRetry.Replace(filePath, copyPath);
        if (File.Exists(flagPath)) File.Delete(flagPath);
        notice = engineName + " discarded its file because " + cause + " The file is kept as " + Path.GetFileName(copyPath)
            + "; the engine starts empty and is rebuilt from the log.";
        return new BPlusTreeStorageEngine(filePath, options);
    }

    public static void MarkCorrupt(string? filePath, string reason) {
        if (filePath == null) return;
        try {
            File.WriteAllText(flagPathFor(filePath), DateTime.UtcNow.ToString("o") + " " + reason);
        } catch { } // best effort: the error that reported the corruption is thrown anyway
    }

    public static void ClearCorruptFlag(string? filePath) {
        if (filePath == null) return;
        try {
            var flagPath = flagPathFor(filePath);
            if (File.Exists(flagPath)) File.Delete(flagPath);
        } catch { }
    }

    static string flagPathFor(string filePath) => Path.Combine(Path.GetDirectoryName(filePath)!, FileKeyUtility.IndexEngine_RebuildFlagFileKey(Path.GetFileName(filePath)));

    static string readFlag(string flagPath) {
        try { return File.ReadAllText(flagPath).Trim(); } catch { return "(the reason could not be read)"; }
    }

    // a lock or an unreachable disk is not corruption: a rebuild would only hide it (see FileOpenRetry)
    internal static bool IsCorruption(Exception e) => e is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException;
}
