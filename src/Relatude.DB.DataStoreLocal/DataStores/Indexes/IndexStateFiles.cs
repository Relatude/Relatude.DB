using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;
/// <summary>
/// Reads and writes the state files of the in-memory indexes (state/index.[id].[number].bin).
/// A save writes a NEW numbered file and deletes the older files only after the new one is
/// completely written (numbered names rather than write-and-rename, as some IO providers cannot
/// rename files), so a shutdown mid-write leaves the previous state file in place. The body is
/// followed by one or more stamps of [timestamp][wal file id][completion marker]; another stamp is
/// appended on a log rewrite hot swap, so the completion marker is always the last 16 bytes of the
/// file. A numbered file that does not end with the marker was interrupted mid-write and is
/// deleted at the next store open (see DataStoreLocal.deleteIncompleteAndLegacyStateFiles), which
/// then falls back to the previous complete file. The unnumbered legacy file (index.[id].bin,
/// written by older versions without markers) is not read; it is deleted at open too, and the
/// index rebuilds from the log.
/// </summary>
internal static class IndexStateFiles {
    public static void Save(IIOProvider io, string uniqueKey, long logTimestamp, Guid walFileId, Action<IAppendStream> writeBody) {
        var oldFileKeys = FileKeyUtility.Index_GetAllFileKeys(io, uniqueKey);
        var newFileKey = FileKeyUtility.Index_NextFileKey(uniqueKey, oldFileKeys);
        io.DeleteFileIfItExists(newFileKey); // safety, the state must never be appended to an existing file
        using (var stream = io.OpenAppend(newFileKey)) {
            writeBody(stream);
            appendStamp(stream, logTimestamp, walFileId, withCompletionMarker: true);
        }
        foreach (var oldFileKey in oldFileKeys) io.DeleteFileIfItExists(oldFileKey);
    }
    /// <summary>Reads the newest state file of the index, body through <paramref name="readBody"/>.
    /// False when the index has no state file (nothing read).</summary>
    public static bool TryRead(IIOProvider io, string uniqueKey, Guid walFileId, Action<IReadStream> readBody, out long persistedTimestamp) {
        persistedTimestamp = 0;
        var fileKey = FileKeyUtility.Index_GetNewestFileKey(io, uniqueKey);
        if (fileKey == null || io.DoesNotExistsOrIsEmpty(fileKey)) return false;
        using var stream = io.OpenRead(fileKey, 0);
        readBody(stream);
        var walId = Guid.Empty;
        while (stream.More()) {
            persistedTimestamp = stream.ReadVerifiedLong();
            walId = stream.ReadGuid();
            if (stream.ReadGuid() != FileKeyUtility.StateFileCompletionMarker)
                throw new Exception("Index state file is missing its completion marker. ");
        }
        if (walId != walFileId) throw new Exception("WAL file ID mismatch when reading index state. ");
        return true;
    }
    /// <summary>Appends a new stamp to the newest state file (used on log rewrite hot swaps, when
    /// the persisted body already equals the in-memory state). False when the index has no state
    /// file to stamp; the caller must save the full state instead.</summary>
    public static bool TryAppendNewTimestamp(IIOProvider io, string uniqueKey, long newTimestamp, Guid walFileId) {
        var fileKey = FileKeyUtility.Index_GetNewestFileKey(io, uniqueKey);
        if (fileKey == null || io.DoesNotExistsOrIsEmpty(fileKey)) return false;
        using var stream = io.OpenAppend(fileKey);
        appendStamp(stream, newTimestamp, walFileId, withCompletionMarker: true);
        return true;
    }
    static void appendStamp(IAppendStream stream, long timestamp, Guid walFileId, bool withCompletionMarker) {
        stream.WriteVerifiedLong(timestamp);
        stream.WriteGuid(walFileId);
        if (withCompletionMarker) stream.WriteGuid(FileKeyUtility.StateFileCompletionMarker);
    }
}
