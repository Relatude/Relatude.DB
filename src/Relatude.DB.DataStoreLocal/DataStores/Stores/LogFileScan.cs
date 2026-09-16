using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Stores;

/// <summary>What the fixed header of a log file says. <see cref="FirstTimestamp"/> is 0 when the
/// file holds no transactions yet.</summary>
public sealed record LogFileHeader(long FormatVersion, Guid FileId, long Length, long FirstTimestamp) {
    public DateTime? FirstUtc => LogFileScan.AsUtc(FirstTimestamp);
}

/// <summary>Where a log file has to be cut to end at a moment in time, and what falls on each side
/// of the cut. <see cref="KeepEnd"/> is the byte position right after the last kept transaction.</summary>
public sealed record LogFileCut(
    long KeepEnd, long FileSize,
    long TransactionsKept, long TransactionsDropped, long ActionsKept, long ActionsDropped,
    long FirstTimestamp, long LastKeptTimestamp, long LastTimestamp) {
    public long BytesKept => KeepEnd;
    public long BytesDropped => FileSize - KeepEnd;
    public DateTime? FirstUtc => LogFileScan.AsUtc(FirstTimestamp);
    /// <summary>The time of the newest transaction on the kept side - what the database ends at
    /// once it runs on the cut copy.</summary>
    public DateTime? LastKeptUtc => LogFileScan.AsUtc(LastKeptTimestamp);
    public DateTime? LastUtc => LogFileScan.AsUtc(LastTimestamp);
}

/// <summary>
/// Reading a write ahead log from the outside: its header, where its transactions end, and a copy
/// of it that stops at a moment in time.
/// <para>Unlike <see cref="LogReader"/> this walks the file's framing only - transaction and action
/// markers, timestamps and action lengths - and never deserializes an action, so it needs no
/// datamodel and works on any log file, including one that belongs to a different database. What it
/// cannot do is tell a valid action from a corrupt one: a transaction whose framing does not parse
/// ends the walk, on the same reasoning the log format is built on (a torn tail is everything after
/// the last transaction that reads cleanly).</para>
/// <para>The file must not be open for writing: the store holds its log with FileShare.None, so
/// everything here is for a closed database, or for a file that is not one's log at all.</para>
/// </summary>
public static class LogFileScan {

    /// <summary>Bytes before the first transaction: the start marker, the verified format version
    /// and the file id (see <see cref="WALFile"/>).</summary>
    public const long HeaderLength = 48;
    /// <summary>Where the file id sits in the header. Rewriting it is what makes a copy a log file
    /// of its own; see <see cref="Copy"/>.</summary>
    public const long FileIdPosition = 32;
    const int markerLength = 16;
    const int copyChunkSize = 1024 * 1024;

    internal static DateTime? AsUtc(long timestamp) => timestamp > 0 ? new DateTime(timestamp, DateTimeKind.Utc) : null;

    /// <summary>Reads the header of a log file, and the timestamp of its first transaction if it has
    /// one. Throws when the file is not a log file this version can read, which is what makes this
    /// the check to run before a file is put in place as a database.</summary>
    public static LogFileHeader ReadHeader(IIOProvider io, string[] fileKey) {
        if (io.DoesNotExistOrIsEmpty(fileKey)) throw new IOException("The file is empty or does not exist. ");
        using var stream = io.OpenRead(fileKey, 0);
        return readHeader(stream);
    }

    static LogFileHeader readHeader(IReadStream stream) {
        if (stream.Length < HeaderLength) throw new IOException("The file is too short to be a database log file. ");
        if (stream.ReadGuid() != WALFile._logStartMarker) throw new IOException("The file is not a database log file. ");
        long version;
        try {
            version = stream.ReadVerifiedLong();
        } catch {
            throw new IOException("The file is not a database log file: its format version is unreadable. ");
        }
        if (version != WALFile._logVersioNumber && version != WALFile._logVersionNumberV1000)
            throw new IOException("Unsupported database log file version " + version + ". Expected "
                + WALFile._logVersioNumber + " or " + WALFile._logVersionNumberV1000 + ". ");
        var fileId = stream.ReadGuid();
        // the first transaction starts right after the header, so its timestamp sits one marker in
        long first = 0;
        if (stream.Length >= HeaderLength + markerLength + 8 && stream.ReadGuid() == WALFile._transactionStartMarker) first = stream.ReadLong();
        stream.Position = HeaderLength;
        return new LogFileHeader(version, fileId, HeaderLength, first);
    }

    /// <summary>The tick count a moment is measured by here. A local time is converted; a time that
    /// does not say which it is, is taken at its word, since the log is written in UTC.</summary>
    static long ticks(DateTime untilUtc) => (untilUtc.Kind == DateTimeKind.Local ? untilUtc.ToUniversalTime() : untilUtc).Ticks;

    /// <summary>Walks the whole file and reports where it would be cut to end at
    /// <paramref name="untilUtc"/>, and how much falls on each side.</summary>
    public static LogFileCut Until(IIOProvider io, string[] fileKey, DateTime untilUtc) => Until(io, fileKey, ticks(untilUtc));

    public static LogFileCut Until(IIOProvider io, string[] fileKey, long untilTimestamp) {
        using var stream = io.OpenRead(fileKey, 0);
        return until(stream, untilTimestamp);
    }

    static LogFileCut until(IReadStream stream, long untilTimestamp) {
        var header = readHeader(stream);
        var fileSize = stream.Length;
        var keepEnd = header.Length;
        long kept = 0, dropped = 0, actionsKept = 0, actionsDropped = 0;
        long first = 0, lastKept = 0, last = 0;
        // Timestamps only ever move forward in the log, so the kept side is a prefix: from the first
        // transaction that is too new, everything goes. Without that rule a copy could keep a
        // transaction whose effect depends on one it left out.
        var pastTarget = false;
        while (stream.Position + markerLength <= fileSize) {
            var start = stream.Position;
            long timestamp;
            int actions;
            try {
                if (stream.ReadGuid() != WALFile._transactionStartMarker) break;
                timestamp = stream.ReadLong();
                actions = stream.ReadVerifiedInt();
                if (actions < 0) throw new IOException("Negative action count. ");
                for (var i = 0; i < actions; i++) {
                    if (stream.ReadGuid() != WALFile._actionMarker) throw new IOException("Action marker missing. ");
                    var length = stream.ReadVerifiedInt();
                    // the action's own checksum follows its bytes; both must be inside the file
                    if (length < 0 || stream.Position + length + 4 > fileSize) throw new IOException("Action length outside the file. ");
                    stream.Skip(length);
                    stream.Skip(4); // the checksum: the bytes are copied as they are, so it is not re-verified here
                }
                if (stream.ReadGuid() != WALFile._transactionEndMarker) throw new IOException("Transaction end marker missing. ");
            } catch {
                break; // a tail that does not parse is a partially written transaction: the file ends here
            }
            if (first == 0) first = timestamp;
            last = timestamp;
            if (!pastTarget && timestamp <= untilTimestamp) {
                kept++;
                actionsKept += actions;
                lastKept = timestamp;
                keepEnd = stream.Position;
            } else {
                pastTarget = true;
                dropped++;
                actionsDropped += actions;
            }
            if (stream.Position <= start) break; // a transaction that consumed nothing would loop forever
        }
        return new LogFileCut(keepEnd, fileSize, kept, dropped, actionsKept, actionsDropped, first, lastKept, last);
    }

    /// <summary>
    /// Copies the log up to <paramref name="untilUtc"/> into another file, leaving the source
    /// untouched. The copy gets a file id of its own, so everything derived from the source log -
    /// the index engines above all - sees a different log and rebuilds from it rather than carrying
    /// over transactions the copy does not have.
    /// </summary>
    public static LogFileCut CopyUntil(IIOProvider sourceIo, string[] sourceKey, IIOProvider destIo, string[] destKey,
        DateTime untilUtc, Action<long, long>? progress = null) {
        var cut = Until(sourceIo, sourceKey, ticks(untilUtc));
        Copy(sourceIo, sourceKey, destIo, destKey, cut.KeepEnd, Guid.NewGuid(), progress);
        return cut;
    }

    /// <summary>
    /// Copies the first <paramref name="length"/> bytes of a log file onto another key, optionally
    /// stamping a new file id into the copy's header (see <see cref="CopyUntil"/> for why).
    /// </summary>
    public static void Copy(IIOProvider sourceIo, string[] sourceKey, IIOProvider destIo, string[] destKey,
        long length, Guid? newFileId, Action<long, long>? progress = null) {
        if (length < HeaderLength) throw new IOException("A log file cannot be shorter than its header. ");
        destIo.DeleteFileIfItExists(destKey);
        try {
            using (var read = sourceIo.OpenRead(sourceKey, 0))
            using (var write = destIo.OpenAppend(destKey)) {
                var copied = 0L;
                while (copied < length) {
                    var bytes = read.Read((int)Math.Min(copyChunkSize, length - copied));
                    if (bytes.Length == 0) throw new IOException("The source log file ended after " + copied + " of " + length + " bytes. ");
                    // the header is in the first chunk by construction: the smallest chunk is far larger
                    if (copied == 0 && newFileId.HasValue) newFileId.Value.ToByteArray().CopyTo(bytes, FileIdPosition);
                    write.Append(bytes);
                    copied += bytes.Length;
                    progress?.Invoke(copied, length);
                }
                write.Flush(true);
            }
        } catch {
            destIo.DeleteFileIfItExists(destKey); // half a log file must never be left where a whole one is expected
            throw;
        }
    }
}
