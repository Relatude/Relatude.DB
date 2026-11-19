using System.Buffers.Binary;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.History;

/// <summary>A log file the history reader reads: the one in use through the log's own streams, any other by opening it for each read.</summary>
internal interface IHistoryLogFile {
    /// <summary>The length now; -1 when the file is not there.</summary>
    long Length();
    /// <summary>Reads <paramref name="count"/> bytes from <paramref name="position"/> into the start of <paramref name="buffer"/>; false when the file does not reach that far.</summary>
    bool Read(long position, int count, byte[] buffer);
}

/// <summary>The log file in use, or the secondary log while the log has it open: read through the log's own stream
/// under a read lease held for one read, so a swap, a revert or a close never waits for more than one chunk.</summary>
internal sealed class ActiveLogFile(Func<WALFile> wal, bool secondary) : IHistoryLogFile {
    public long Length() => wal().LengthForHistory(secondary);
    public bool Read(long position, int count, byte[] buffer) => wal().ReadForHistory(secondary, position, count, buffer);
}

/// <summary>A log file no one appends to: opened for one read and closed after it, so deleting it never waits for the history.</summary>
internal sealed class ClosedLogFile(IIOProvider io, string[] key) : IHistoryLogFile {
    public long Length() => io.Exists(key) ? io.GetFileSizeOrZeroIfUnknown(key) : -1;
    public bool Read(long position, int count, byte[] buffer) {
        if (position < 0 || position + count > Length()) return false;
        using var stream = io.OpenRead(key, position);
        var read = 0;
        while (read < count) {
            var n = stream.ReadInto(buffer.AsSpan(read, count - read));
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>
/// A window over a log file: the bytes asked for are read in chunks of at most <see cref="Chunk"/> (more only for an
/// action bigger than that), and spans are handed out of the window. Counts what it read for the build's pace.
/// </summary>
internal sealed class LogWindow(IHistoryLogFile file, long length) {
    public const int Chunk = 1024 * 1024;
    byte[] _buffer = new byte[64 * 1024];
    long _start = -1;
    int _count;
    public long Length { get; private set; } = length;
    public long BytesRead { get; private set; }
    /// <summary>The bytes [position, position + count): false when the file does not hold them all (yet).</summary>
    public bool TryGet(long position, int count, out ReadOnlySpan<byte> span) {
        span = default;
        if (count < 0) return false;
        if (position + count > Length) {
            Length = Math.Max(Length, file.Length()); // the file in use grows while it is read
            if (position + count > Length) return false;
        }
        if (_start < 0 || position < _start || position + count > _start + _count) {
            var n = (int)Math.Min(Math.Max(count, Chunk), Length - position);
            if (n < count) return false;
            if (_buffer.Length < n) _buffer = new byte[Math.Max(n, Math.Min(Chunk, _buffer.Length * 2))];
            if (!file.Read(position, n, _buffer)) return false;
            BytesRead += n;
            _start = position;
            _count = n;
        }
        span = _buffer.AsSpan((int)(position - _start), count);
        return true;
    }
}

/// <summary>A node action of a transaction: where the node's bytes are in the file, and its head.</summary>
internal readonly record struct LoggedNodeAction(PrimitiveOperation Operation, long Position, int Length, NodeHead Head);
/// <summary>A relation action of a transaction: ids as the log names them (internal, reused), and where it is.</summary>
internal readonly record struct LoggedRelationAction(PrimitiveOperation Operation, Guid Relation, int Source, int Target, long Position);
/// <summary>One transaction of the log as the history reader reads it.</summary>
internal sealed class LoggedTransaction {
    public long Start;
    public long End;
    public long Timestamp;
    public Guid Writer;
    public TransactionLogFlags Flags;
    public readonly List<object> Actions = []; // LoggedNodeAction and LoggedRelationAction in log order
    public bool OnlyAdds = true;
    public int NodeAdds;
    public int RelationAdds;
    public int Reorders;
    public HashSet<Guid>? RelationIds;
}

internal enum ParseResult { Read, Incomplete, Damaged }

/// <summary>Reads the framing of the log, as <see cref="LogReader"/> does, into <see cref="LoggedTransaction"/>s, decoding no value.</summary>
internal static class LogTransactions {
    static readonly byte[] startMarker = WALFile._transactionStartMarker.ToByteArray();
    static Guid guid(ReadOnlySpan<byte> s) => new(s[..16]);
    static int verifiedInt(ReadOnlySpan<byte> s) {
        var v1 = BinaryPrimitives.ReadInt32LittleEndian(s);
        var v2 = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        if (v1 != -v2) throw new InvalidDataException("Verification failed. ");
        return v1;
    }
    static uint checksum(ReadOnlySpan<byte> bytes) {
        uint sum = 0;
        foreach (var b in bytes) sum += b;
        return sum;
    }
    /// <summary>
    /// Reads the transaction starting at <paramref name="position"/>. Incomplete when the file ends before it does (it is
    /// still being written, or a torn tail): read again next time from the same place. Damaged when the bytes are not a
    /// transaction; <paramref name="next"/> is then where the next start marker is, or the end.
    /// </summary>
    public static ParseResult Read(LogWindow window, long position, long formatVersion, HashSet<Guid> ignoredProperties,
        out LoggedTransaction? transaction, out long next) {
        transaction = null;
        next = position;
        try {
            if (!window.TryGet(position, 16, out var s)) return ParseResult.Incomplete;
            var marker = guid(s);
            if (marker == WALFile._completionMarker) { // a stamped copy put in place: its transactions go on after the stamp
                if (!window.TryGet(position, (int)LogFileScan.CompletionStampLength, out _)) return ParseResult.Incomplete;
                next = position + LogFileScan.CompletionStampLength;
                return ParseResult.Damaged; // nothing to read here, go on
            }
            if (marker != WALFile._transactionStartMarker) return skipToNextStart(window, position + 1, out next);
            var p = position + 16;
            var writerSize = WALFile.HasTransactionWriter(formatVersion) ? WALFile.TransactionWriterHeaderSize : 0;
            if (!window.TryGet(p, 16 + writerSize, out s)) return ParseResult.Incomplete;
            var t = new LoggedTransaction { Start = position, Timestamp = BinaryPrimitives.ReadInt64LittleEndian(s) };
            var count = verifiedInt(s[8..]);
            if (count < 0) throw new InvalidDataException("Negative action count. ");
            if (writerSize > 0) {
                t.Writer = guid(s[16..]);
                t.Flags = (TransactionLogFlags)s[32];
            }
            p += 16 + writerSize;
            var chained = WALFile.HasVersionChain(formatVersion);
            for (var i = 0; i < count; i++) {
                if (!window.TryGet(p, 24, out s)) return ParseResult.Incomplete;
                if (guid(s) != WALFile._actionMarker) throw new InvalidDataException("Action marker missing. ");
                var length = verifiedInt(s[16..]);
                if (length < 0) throw new InvalidDataException("Negative action length. ");
                var dataAt = p + 24;
                if (!window.TryGet(dataAt, length + 4, out s)) return ParseResult.Incomplete;
                var data = s[..length];
                if (BinaryPrimitives.ReadUInt32LittleEndian(s[length..]) != checksum(data)) throw new InvalidDataException("Action checksum mismatch. ");
                action(t, data, dataAt, chained, ignoredProperties);
                p = dataAt + length + 4;
            }
            if (!window.TryGet(p, 16, out s)) return ParseResult.Incomplete;
            if (guid(s) != WALFile._transactionEndMarker) throw new InvalidDataException("Transaction end marker missing. ");
            t.End = p + 16;
            transaction = t;
            next = t.End;
            return ParseResult.Read;
        } catch (InvalidDataException) {
            return skipToNextStart(window, position + 1, out next);
        } catch (NotSupportedException) {
            return skipToNextStart(window, position + 1, out next);
        }
    }
    static void action(LoggedTransaction t, ReadOnlySpan<byte> data, long dataAt, bool chained, HashSet<Guid> ignored) {
        var target = (PrimitiveActionTarget)data[0];
        if (target == PrimitiveActionTarget.Node) {
            var op = (PrimitiveOperation)data[1];
            var at = 2 + (chained ? WALFile.VersionHeaderSize : 0);
            var node = data[at..];
            var head = HistoryDecoder.Decode(node, ignored);
            t.Actions.Add(new LoggedNodeAction(op, dataAt + at, node.Length, head));
            if (op == PrimitiveOperation.Add) t.NodeAdds++; else t.OnlyAdds = false;
        } else if (target == PrimitiveActionTarget.Relation) {
            var op = (PrimitiveOperation)data[1];
            var relation = new Guid(data.Slice(2, 16));
            var source = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[18..]);
            var targetId = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[22..]);
            t.Actions.Add(new LoggedRelationAction(op, relation, source, targetId, dataAt));
            if (op == PrimitiveOperation.Add) {
                t.RelationAdds++;
                (t.RelationIds ??= []).Add(relation);
            } else t.OnlyAdds = false;
        } else if (target == PrimitiveActionTarget.RelationOrder) {
            t.Reorders++; // a list's order put back or changed: no link made or taken away
        }
    }
    // a damaged stretch is passed over to the next transaction start marker, as the log reader does
    static ParseResult skipToNextStart(LogWindow window, long from, out long next) {
        var p = from;
        while (true) {
            var n = (int)Math.Min(LogWindow.Chunk, window.Length - p);
            if (n < 16) {
                next = Math.Max(from, window.Length - 15);
                return ParseResult.Damaged;
            }
            if (!window.TryGet(p, n, out var s)) {
                next = p;
                return ParseResult.Incomplete;
            }
            var i = s.IndexOf(startMarker);
            if (i >= 0) {
                next = p + i;
                return ParseResult.Damaged;
            }
            p += n - 15; // a marker may straddle two chunks
        }
    }
    /// <summary>The timestamp of the transaction starting at <paramref name="position"/>, or 0 when there is none there.</summary>
    public static long TimestampAt(IHistoryLogFile file, long position) {
        var buffer = new byte[24];
        if (!file.Read(position, 24, buffer)) return 0;
        if (new Guid(buffer.AsSpan(0, 16)) != WALFile._transactionStartMarker) return 0;
        return BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16));
    }
    /// <summary>The header of a log file: its format version and id. Null when it is not one.</summary>
    public static (long version, Guid fileId)? Header(IHistoryLogFile file) {
        var buffer = new byte[48];
        if (!file.Read(0, 48, buffer)) return null;
        if (new Guid(buffer.AsSpan(0, 16)) != WALFile._logStartMarker) return null;
        var v1 = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16));
        var v2 = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24));
        if (v1 != -v2 || !WALFile.IsKnownFormat(v1)) return null;
        return (v1, new Guid(buffer.AsSpan(32, 16)));
    }
}
