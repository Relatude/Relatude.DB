using System.Buffers.Binary;
using Relatude.DB.Common;

namespace Relatude.DB.AI.HNSW;

/// <summary>
/// An array of fixed-size records on disk, addressed by index. All the graph's files are one of
/// these: the float vectors, the routing records, the node table, the upper-layer adjacency and the
/// edge log.
///
/// <para>These files are updated in place — a graph insert rewrites the neighbour lists of existing
/// nodes, so there is nothing immutable to swap. What makes that safe is that the index only ever
/// <i>appends</i> record indexes: the manifest's record count is the commit boundary, and anything
/// beyond it is uncommitted scratch the next open ignores. A record below the boundary can be
/// rewritten (a neighbour list changing), and a write torn by a crash then leaves a mix of old and
/// new neighbour ids — all of them valid ids either way, so the worst case is a node with slightly
/// worse edges, never a lost or corrupted vector.</para>
///
/// <para>The header carries a magic, the format version, the file's kind and generation and the
/// parameters the records were laid out with, so a file from another index, another generation or
/// another configuration is rejected instead of being read as garbage.</para>
/// </summary>
internal sealed class FixedStrideFile : IDisposable {
    internal const int HeaderBytes = 64;
    const long Magic = 0x3246_5453_5648_5452; // "RTHVSTF2"
    const int Version = 1;
    const int ParameterCount = 4;

    readonly FileStream _fs;
    public string Path { get; }
    public int Kind { get; }
    public long Generation { get; }
    public int StrideBytes { get; }
    /// <summary>How many whole records the file currently has room for. Records past the manifest's
    /// count are uncommitted, so this is a lower bound check, never the live record count.</summary>
    public long RecordCapacity => Math.Max(0, (_fs.Length - HeaderBytes) / StrideBytes);
    public long FileLength => _fs.Length;

    FixedStrideFile(FileStream fs, string path, int kind, long generation, int strideBytes) {
        _fs = fs;
        Path = path;
        Kind = kind;
        Generation = generation;
        StrideBytes = strideBytes;
    }

    public static FixedStrideFile Create(string path, int kind, long generation, int strideBytes,
        ReadOnlySpan<int> parameters, long preallocateRecords) {
        if (strideBytes <= 0) throw new ArgumentException("The record stride must be positive. ");
        var fs = open(path, FileMode.Create, HeaderBytes + Math.Max(0, preallocateRecords) * strideBytes);
        try {
            var header = buildHeader(kind, generation, strideBytes, parameters);
            RandomAccess.Write(fs.SafeFileHandle, header, 0);
            return new FixedStrideFile(fs, path, kind, generation, strideBytes);
        } catch {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Opens an existing file and verifies it is the one expected. Throws
    /// <see cref="InvalidDataException"/> on any mismatch, which the index turns into a reset.</summary>
    public static FixedStrideFile Open(string path, int kind, long generation, int strideBytes,
        ReadOnlySpan<int> parameters, long minRecords) {
        var fs = open(path, FileMode.Open, 0);
        try {
            if (fs.Length < HeaderBytes) throw invalid("file too short");
            Span<byte> header = stackalloc byte[HeaderBytes];
            readExactly(fs, header, 0);
            var expected = buildHeader(kind, generation, strideBytes, parameters);
            if (BinaryPrimitives.ReadInt64LittleEndian(header) != Magic) throw invalid("bad magic");
            if (BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != Version) throw invalid("unsupported version");
            if (!header.SequenceEqual(expected)) throw invalid("header does not match the manifest (kind, generation, stride or layout parameters)");
            var file = new FixedStrideFile(fs, path, kind, generation, strideBytes);
            if (file.RecordCapacity < minRecords) throw invalid($"file holds {file.RecordCapacity} records, the manifest claims {minRecords}");
            return file;
        } catch {
            fs.Dispose();
            throw;
        }
    }

    static FileStream open(string path, FileMode mode, long preallocationBytes) {
        // Retried because FileShare.Read makes this the single writer: on a host that recycles with the
        // processes overlapping, the previous one may still hold it for a moment. See FileOpenRetry.
        return FileOpenRetry.Open(path, () => openOnce(path, mode, preallocationBytes));
    }
    static FileStream openOnce(string path, FileMode mode, long preallocationBytes) {
        // BufferSize 0 turns off FileStream's own buffering: every read and write below goes through
        // RandomAccess on the handle, and the stream is kept only for its Flush(true) fsync.
        return new FileStream(path, new FileStreamOptions {
            Mode = mode,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            Options = FileOptions.RandomAccess,
            BufferSize = 0,
            PreallocationSize = mode == FileMode.Create ? Math.Max(0, preallocationBytes) : 0,
        });
    }

    static byte[] buildHeader(int kind, long generation, int strideBytes, ReadOnlySpan<int> parameters) {
        if (parameters.Length > ParameterCount) throw new ArgumentException("Too many layout parameters. ");
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt64LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), kind);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), generation);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), strideBytes);
        for (var i = 0; i < parameters.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28 + i * 4), parameters[i]);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(HeaderBytes - 8), Hash.Fnv1a(header.AsSpan(0, HeaderBytes - 8)));
        return header;
    }
    static InvalidDataException invalid(string reason) => new("Invalid graph file: " + reason + ". ");

    /// <summary>Reads whole records starting at <paramref name="firstRecord"/>; the buffer length
    /// decides how many. Positional and thread-safe: concurrent readers never share a stream position.</summary>
    public void Read(long firstRecord, Span<byte> buffer) => readExactly(_fs, buffer, offsetOf(firstRecord));
    /// <summary>Writes whole records starting at <paramref name="firstRecord"/>, extending the file
    /// when they are past the end.</summary>
    public void Write(long firstRecord, ReadOnlySpan<byte> buffer) => RandomAccess.Write(_fs.SafeFileHandle, buffer, offsetOf(firstRecord));
    /// <summary>Writes part of one record. Used to persist a change that only touched a known region
    /// of it, rather than rewriting bytes that did not change.</summary>
    public void WriteWithin(long record, int offsetInRecord, ReadOnlySpan<byte> buffer) {
        if (offsetInRecord < 0 || offsetInRecord + buffer.Length > StrideBytes) throw new ArgumentOutOfRangeException(nameof(offsetInRecord));
        RandomAccess.Write(_fs.SafeFileHandle, buffer, offsetOf(record) + offsetInRecord);
    }
    long offsetOf(long record) => HeaderBytes + record * StrideBytes;

    public void Fsync() => _fs.Flush(flushToDisk: true);
    /// <summary>Drops every record, keeping the header. Only meaningful for a file whose records are
    /// all superseded — the edge log after its entries have been applied to the graph.</summary>
    public void Truncate() => _fs.SetLength(HeaderBytes);

    static void readExactly(FileStream fs, Span<byte> buffer, long offset) {
        var total = 0;
        while (total < buffer.Length) {
            var n = RandomAccess.Read(fs.SafeFileHandle, buffer[total..], offset + total);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of graph file. ");
            total += n;
        }
    }
    public void Dispose() => _fs.Dispose();
}

internal static class Hash {
    public static ulong Fnv1a(ReadOnlySpan<byte> bytes) {
        var hash = 14695981039346656037UL;
        foreach (var b in bytes) {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
