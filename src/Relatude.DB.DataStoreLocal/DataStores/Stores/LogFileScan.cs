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
/// One slice of a log file's timeline: a stretch of time of fixed width, and what the log holds in
/// it. Times are unix milliseconds rather than tick counts, because that is what the admin UI draws
/// the picture on and a tick count is past what a javascript number holds exactly.
/// <para><see cref="StartPosition"/> and <see cref="EndPosition"/> are transaction boundaries, so a
/// second scan can be handed the ends of the stretch being looked at and walk only that much of the
/// file rather than all of it again.</para>
/// </summary>
public sealed record LogFileSlice(long FromMs, long ToMs, long FirstMs, long LastMs,
    long Transactions, long Actions, long Bytes, long StartPosition, long EndPosition);

/// <summary>A log file's transactions over time; see <see cref="LogFileScan.Timeline"/>. Slices
/// holding nothing are left out, so a quiet week costs nothing to carry: a slice says where it sits
/// (<see cref="LogFileSlice.FromMs"/>), it is not found by its place in the list.</summary>
public sealed record LogFileTimeline(long FileSize, long ScanStart, long ScanEnd, long SliceMs,
    long Transactions, long Actions, long FirstTimestamp, long LastTimestamp, List<LogFileSlice> Slices) {
    public DateTime? FirstUtc => LogFileScan.AsUtc(FirstTimestamp);
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
        walk(stream, fileSize, t => {
            if (first == 0) first = t.Timestamp;
            last = t.Timestamp;
            if (!pastTarget && t.Timestamp <= untilTimestamp) {
                kept++;
                actionsKept += t.Actions;
                lastKept = t.Timestamp;
                keepEnd = t.End;
            } else {
                pastTarget = true;
                dropped++;
                actionsDropped += t.Actions;
            }
            return true;
        });
        return new LogFileCut(keepEnd, fileSize, kept, dropped, actionsKept, actionsDropped, first, lastKept, last);
    }

    /// <summary>One transaction as the walk met it: when it was written, how many actions it holds,
    /// and the bytes it occupies.</summary>
    readonly record struct walkedTransaction(long Timestamp, int Actions, long Start, long End);

    /// <summary>
    /// Walks the framing of a log file from where the stream stands to <paramref name="endPosition"/>,
    /// handing over every transaction that reads cleanly. The walk stops at the end, at a transaction
    /// the callback says no more after, or at the first one that does not parse - a torn tail is
    /// everything after the last transaction that reads cleanly, which is the rule the log format is
    /// built on.
    /// </summary>
    static void walk(IReadStream stream, long endPosition, Func<walkedTransaction, bool> onTransaction) {
        while (stream.Position + markerLength <= endPosition) {
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
                    // the action's own checksum follows its bytes; both must be inside the walk
                    if (length < 0 || stream.Position + length + 4 > endPosition) throw new IOException("Action length outside the file. ");
                    stream.Skip(length);
                    stream.Skip(4); // the checksum: the bytes are copied as they are, so it is not re-verified here
                }
                if (stream.ReadGuid() != WALFile._transactionEndMarker) throw new IOException("Transaction end marker missing. ");
            } catch {
                break; // a tail that does not parse is a partially written transaction: the file ends here
            }
            var end = stream.Position;
            if (!onTransaction(new walkedTransaction(timestamp, actions, start, end))) break;
            if (end <= start) break; // a transaction that consumed nothing would loop forever
        }
    }

    /// <summary>The narrowest slice a timeline is measured in. A log written in one burst lands
    /// inside a single millisecond whatever is done, and every slice edge being a whole millisecond
    /// is what lets the admin UI do its own arithmetic on them.</summary>
    const long minSliceTicks = TimeSpan.TicksPerMillisecond;
    const int progressEveryTransactions = 4096;

    /// <summary>
    /// Every transaction in a log file, gathered into slices of equal width so the whole file can be
    /// drawn as one picture: how much was written when, from the first transaction to the last.
    /// <para>The width is not given but found: slices start one millisecond wide and are merged in
    /// pairs whenever the file turns out to reach further than <paramref name="maxSlices"/> of them,
    /// so one pass over the file produces a picture of the whole of it at the finest width that
    /// fits. That is why this cannot be answered from the header - the walk is the answer.</para>
    /// <para><paramref name="startPosition"/> and <paramref name="endPosition"/> narrow the walk to
    /// part of the file (0 for the whole of it). They must be transaction boundaries, which is what
    /// the positions on a previous scan's slices are: handing back the ends of a stretch that was
    /// drawn is how the same stretch is looked at more closely without reading the file again.</para>
    /// </summary>
    public static LogFileTimeline Timeline(IIOProvider io, string[] fileKey, long startPosition, long endPosition,
        int maxSlices, Action<long, long>? progress = null, CancellationToken cancellation = default) {
        maxSlices = Math.Clamp(maxSlices, 8, 16384);
        if ((maxSlices & 1) != 0) maxSlices++; // merging works in pairs, so an odd last slice would fall off
        using var stream = io.OpenRead(fileKey, 0);
        var header = readHeader(stream);
        var fileSize = stream.Length;
        var from = startPosition <= header.Length ? header.Length : Math.Min(startPosition, fileSize);
        var to = endPosition <= 0 ? fileSize : Math.Min(endPosition, fileSize);
        if (to < from) to = from;
        stream.Position = from;

        var transactions = new long[maxSlices];
        var actions = new long[maxSlices];
        var bytes = new long[maxSlices];
        var firstTicks = new long[maxSlices];
        var lastTicks = new long[maxSlices];
        var startPos = new long[maxSlices];
        var endPos = new long[maxSlices];
        var used = 0; // slices 0..used-1 have been written to; a slice is empty when its count is 0

        // Halves the resolution: each pair of slices becomes one, which frees the upper half of the
        // arrays for the time the file turned out to reach into. A zero is "nothing here" throughout
        // - no transaction carries a zero timestamp and none begins at byte zero, the header is there.
        void mergePairs() {
            var half = maxSlices / 2;
            for (var i = 0; i < half; i++) {
                var a = i * 2;
                var b = a + 1;
                transactions[i] = transactions[a] + transactions[b];
                actions[i] = actions[a] + actions[b];
                bytes[i] = bytes[a] + bytes[b];
                firstTicks[i] = firstTicks[a] != 0 ? firstTicks[a] : firstTicks[b];
                lastTicks[i] = lastTicks[b] != 0 ? lastTicks[b] : lastTicks[a];
                startPos[i] = startPos[a] != 0 ? startPos[a] : startPos[b];
                endPos[i] = endPos[b] != 0 ? endPos[b] : endPos[a];
            }
            foreach (var array in new[] { transactions, actions, bytes, firstTicks, lastTicks, startPos, endPos })
                Array.Clear(array, half, maxSlices - half);
            used = (used + 1) / 2;
        }

        var width = minSliceTicks;
        long origin = 0, total = 0, totalActions = 0, first = 0, last = 0;
        var started = false;
        var sinceProgress = 0;
        walk(stream, to, t => {
            if (!started) {
                started = true;
                first = t.Timestamp;
                origin = alignDownToMs(t.Timestamp); // so every slice edge lands on a whole millisecond
            }
            last = t.Timestamp;
            total++;
            totalActions += t.Actions;
            // a timestamp older than the first one is a clock that went backwards: it belongs to the
            // start of the picture rather than to a slice before it, which there is no room for
            var index = t.Timestamp <= origin ? 0 : (t.Timestamp - origin) / width;
            while (index >= maxSlices) {
                mergePairs();
                width *= 2;
                index = (t.Timestamp - origin) / width;
            }
            var i = (int)index;
            if (i >= used) used = i + 1;
            transactions[i]++;
            actions[i] += t.Actions;
            bytes[i] += t.End - t.Start;
            if (firstTicks[i] == 0) firstTicks[i] = t.Timestamp;
            lastTicks[i] = t.Timestamp;
            if (startPos[i] == 0) startPos[i] = t.Start;
            endPos[i] = t.End;
            if (++sinceProgress >= progressEveryTransactions) {
                sinceProgress = 0;
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke(t.End - from, to - from);
            }
            return true;
        });

        var slices = new List<LogFileSlice>();
        for (var i = 0; i < used; i++) {
            if (transactions[i] == 0) continue;
            var sliceStart = origin + i * width;
            slices.Add(new LogFileSlice(ToUnixMs(sliceStart), ToUnixMs(sliceStart + width),
                ToUnixMs(firstTicks[i]), ToUnixMsRoundUp(lastTicks[i]),
                transactions[i], actions[i], bytes[i], startPos[i], endPos[i]));
        }
        return new LogFileTimeline(fileSize, from, stream.Position, width / TimeSpan.TicksPerMillisecond,
            total, totalActions, first, last, slices);
    }

    static long alignDownToMs(long ticks) => ticks - (ticks - DateTime.UnixEpoch.Ticks) % TimeSpan.TicksPerMillisecond;
    /// <summary>A tick count as unix milliseconds, the unit the admin UI measures the picture in.</summary>
    public static long ToUnixMs(long ticks) => (ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
    /// <summary>The same, rounded up: a moment named in whole milliseconds includes the transaction
    /// it was taken from, which one rounded down would cut away.</summary>
    public static long ToUnixMsRoundUp(long ticks) {
        var rest = (ticks - DateTime.UnixEpoch.Ticks) % TimeSpan.TicksPerMillisecond;
        return ToUnixMs(ticks) + (rest == 0 ? 0 : 1);
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
