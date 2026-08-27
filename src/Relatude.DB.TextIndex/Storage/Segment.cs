using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Relatude.DB.Common;

namespace Relatude.DB.DataStores.Indexes.TextIndexing;

internal readonly record struct TermEntry(string Term, long PostingsOffset, int PostingsLength);

internal sealed class DecodedBlock {
    public string[] Terms = [];
    public long[] PostingsOffset = [];
    public int[] PostingsLength = [];
    public long ByteSize;
}

/// <summary>One term's raw postings read from a segment: adds and tombstones, sorted by node id.</summary>
internal readonly struct SegPostings(int[] addIds, byte[] addHits, int[] delIds) {
    public readonly int[] AddIds = addIds;
    public readonly byte[] AddHits = addHits;
    public readonly int[] DelIds = delIds;
}

/// <summary>
/// An immutable on-disk segment (see <see cref="SegmentWriter"/> for the format). Only the block
/// index — one first-term per dictionary block — is kept in memory; term dictionary blocks and
/// postings are read on demand with positional reads, so any number of searches can read
/// concurrently through one file handle. Decoded blocks are cached in the shared byte-budget cache.
/// </summary>
internal sealed class Segment : IDisposable {
    public const byte CacheKindBlock = 0;
    readonly SafeFileHandle _handle;
    readonly string[] _blockFirstTerms;
    readonly long[] _blockOffsets;
    readonly int[] _blockLengths;
    readonly int[] _blockTermCounts;
    readonly long _postingsStart;
    readonly long _docStart;
    readonly long _dictStart;
    public long Id { get; }
    public string Path { get; }
    public long FileLength { get; }
    public int TermCount { get; }

    Segment(long id, string path, SafeFileHandle handle, long fileLength, long postingsStart, long docStart, long dictStart,
        string[] firstTerms, long[] blockOffsets, int[] blockLengths, int[] blockTermCounts, int termCount) {
        Id = id;
        Path = path;
        _handle = handle;
        FileLength = fileLength;
        _postingsStart = postingsStart;
        _docStart = docStart;
        _dictStart = dictStart;
        _blockFirstTerms = firstTerms;
        _blockOffsets = blockOffsets;
        _blockLengths = blockLengths;
        _blockTermCounts = blockTermCounts;
        TermCount = termCount;
    }
    public static Segment Open(string path, long expectedId) {
        // a segment being written by the previous process during a host handover is held briefly
        var handle = FileOpenRetry.Open(path, () => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        try {
            var length = RandomAccess.GetLength(handle);
            if (length < 20 + SegmentWriter.FooterLength) throw new InvalidDataException("Segment file too short. ");
            var header = readAt(handle, 0, 20);
            var hPos = 0;
            if (BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(hPos)) != SegmentWriter.HeaderMagic) throw new InvalidDataException("Bad segment header. ");
            hPos += 8;
            if (BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(hPos)) != SegmentWriter.Version) throw new InvalidDataException("Unknown segment version. ");
            hPos += 4;
            if (BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(hPos)) != expectedId) throw new InvalidDataException("Segment id mismatch. ");
            var footer = readAt(handle, length - SegmentWriter.FooterLength, SegmentWriter.FooterLength);
            var f = 0;
            var postingsStart = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(f)); f += 8;
            var docStart = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(f)); f += 8;
            var dictStart = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(f)); f += 8;
            var blockIndexStart = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(f)); f += 8;
            var termCount = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(f)); f += 4;
            var blockCount = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(f)); f += 4;
            if (BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(f)) != SegmentWriter.FooterMagic) throw new InvalidDataException("Bad segment footer. ");
            var indexBytes = readAt(handle, blockIndexStart, (int)(length - SegmentWriter.FooterLength - blockIndexStart));
            var firstTerms = new string[blockCount];
            var blockOffsets = new long[blockCount];
            var blockLengths = new int[blockCount];
            var blockTermCounts = new int[blockCount];
            var pos = 0;
            var offset = dictStart;
            for (var b = 0; b < blockCount; b++) {
                var termLen = VarInt.ReadInt(indexBytes, ref pos);
                firstTerms[b] = Encoding.UTF8.GetString(indexBytes, pos, termLen);
                pos += termLen;
                blockTermCounts[b] = VarInt.ReadInt(indexBytes, ref pos);
                blockLengths[b] = VarInt.ReadInt(indexBytes, ref pos);
                blockOffsets[b] = offset;
                offset += blockLengths[b];
            }
            return new Segment(expectedId, path, handle, length, postingsStart, docStart, dictStart,
                firstTerms, blockOffsets, blockLengths, blockTermCounts, termCount);
        } catch {
            handle.Dispose();
            throw;
        }
    }
    static byte[] readAt(SafeFileHandle handle, long offset, int length) {
        var buffer = new byte[length];
        var read = 0;
        while (read < length) {
            var n = RandomAccess.Read(handle, buffer.AsSpan(read), offset + read);
            if (n == 0) throw new EndOfStreamException("Unexpected end of segment file. ");
            read += n;
        }
        return buffer;
    }
    byte[] readAt(long offset, int length) => readAt(_handle, offset, length);

    DecodedBlock getBlock(int blockIndex, TextIndexCache? cache, int owner) {
        if (cache != null) {
            var key = new CacheKey(owner, CacheKindBlock, Id, blockIndex, null);
            if (cache.TryGet(key, out var cached)) return (DecodedBlock)cached;
            var block = decodeBlock(blockIndex);
            cache.Set(key, block, block.ByteSize);
            return block;
        }
        return decodeBlock(blockIndex);
    }
    DecodedBlock decodeBlock(int blockIndex) {
        var bytes = readAt(_blockOffsets[blockIndex], _blockLengths[blockIndex]);
        var count = _blockTermCounts[blockIndex];
        var terms = new string[count];
        var offsets = new long[count];
        var lengths = new int[count];
        var pos = 0;
        var prev = "";
        long size = 64;
        for (var i = 0; i < count; i++) {
            var shared = VarInt.ReadInt(bytes, ref pos);
            var suffixLen = VarInt.ReadInt(bytes, ref pos);
            var suffix = Encoding.UTF8.GetString(bytes, pos, suffixLen);
            pos += suffixLen;
            var term = shared == 0 ? suffix : string.Concat(prev.AsSpan(0, shared), suffix);
            terms[i] = term;
            if (i == 0) offsets[i] = _postingsStart + VarInt.ReadLong(bytes, ref pos);
            else offsets[i] = offsets[i - 1] + lengths[i - 1];
            lengths[i] = VarInt.ReadInt(bytes, ref pos);
            prev = term;
            size += term.Length * 2 + 40;
        }
        return new DecodedBlock { Terms = terms, PostingsOffset = offsets, PostingsLength = lengths, ByteSize = size };
    }

    /// <summary>Index of the last block whose first term is &lt;= <paramref name="word"/>, or -1.</summary>
    int findBlock(string word) {
        int lo = 0, hi = _blockFirstTerms.Length - 1, result = -1;
        while (lo <= hi) {
            var mid = (lo + hi) / 2;
            if (string.CompareOrdinal(_blockFirstTerms[mid], word) <= 0) {
                result = mid;
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }
        return result;
    }
    public bool TryGetTerm(string word, TextIndexCache? cache, int owner, out TermEntry entry) {
        var b = findBlock(word);
        if (b < 0) {
            entry = default;
            return false;
        }
        var block = getBlock(b, cache, owner);
        var i = Array.BinarySearch(block.Terms, word, StringComparer.Ordinal);
        if (i < 0) {
            entry = default;
            return false;
        }
        entry = new TermEntry(word, block.PostingsOffset[i], block.PostingsLength[i]);
        return true;
    }
    /// <summary>All terms &gt;= <paramref name="lowerBound"/> in ordinal order.</summary>
    public IEnumerable<TermEntry> Scan(string lowerBound, TextIndexCache? cache, int owner) {
        if (_blockFirstTerms.Length == 0) yield break;
        var startBlock = lowerBound.Length == 0 ? 0 : Math.Max(0, findBlock(lowerBound));
        for (var b = startBlock; b < _blockFirstTerms.Length; b++) {
            var block = getBlock(b, cache, owner);
            var start = 0;
            if (b == startBlock && lowerBound.Length > 0) {
                start = Array.BinarySearch(block.Terms, lowerBound, StringComparer.Ordinal);
                if (start < 0) start = ~start;
            }
            for (var i = start; i < block.Terms.Length; i++) {
                yield return new TermEntry(block.Terms[i], block.PostingsOffset[i], block.PostingsLength[i]);
            }
        }
    }
    public SegPostings ReadPostings(TermEntry entry) {
        var bytes = readAt(entry.PostingsOffset, entry.PostingsLength);
        var pos = 0;
        var addCount = VarInt.ReadInt(bytes, ref pos);
        var addIds = new int[addCount];
        var addHits = new byte[addCount];
        var prev = 0;
        for (var i = 0; i < addCount; i++) {
            prev += VarInt.ReadInt(bytes, ref pos);
            addIds[i] = prev;
            addHits[i] = bytes[pos++];
        }
        var delCount = VarInt.ReadInt(bytes, ref pos);
        var delIds = new int[delCount];
        prev = 0;
        for (var i = 0; i < delCount; i++) {
            prev += VarInt.ReadInt(bytes, ref pos);
            delIds[i] = prev;
        }
        return new SegPostings(addIds, addHits, delIds);
    }
    /// <summary>The segment's doc-length ops, sorted by node id (-1 = document removed).</summary>
    public List<(int id, int wordCountOrRemove)> ReadDocOps() {
        var end = _dictStart;
        var bytes = readAt(_docStart, (int)(end - _docStart));
        var pos = 0;
        var count = VarInt.ReadInt(bytes, ref pos);
        var list = new List<(int, int)>(count);
        var prev = 0;
        for (var i = 0; i < count; i++) {
            prev += VarInt.ReadInt(bytes, ref pos);
            var kind = bytes[pos++];
            list.Add((prev, kind == 1 ? VarInt.ReadInt(bytes, ref pos) : -1));
        }
        return list;
    }
    public void Dispose() => _handle.Dispose();
}
