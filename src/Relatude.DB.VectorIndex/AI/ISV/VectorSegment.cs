using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Relatude.DB.AI.ISV;

/// <summary>
/// One immutable segment file of vectors, grouped into per-cluster blocks so a search only reads
/// the byte ranges of the clusters it probes. Layout:
/// <code>[header][directory][ids region][vector blocks][deleted ids][footer]</code>
/// The node ids, the directory and the deletions stay in memory (O(records), a few bytes each);
/// the packed vectors are read on demand with positional reads, so any number of searches can read
/// concurrently. Big clusters are chunked into several blocks of at most
/// <see cref="MaxRecordsPerBlock"/> records, which caps single-read sizes and gives parallel scans
/// even work items. Opening validates magics, lengths and a metadata hash; any mismatch throws and
/// the caller resets the index, so partially written files never crash the process.
/// </summary>
internal sealed class VectorSegment : IDisposable {
    internal const long HeaderMagic = 0x3147_4553_5644_5452; // "RTDVSEG1"
    internal const long EndMagic = 0x3144_4E45_5644_5452;    // "RTDVEND1"
    internal const int Version = 1;
    internal const int MaxRecordsPerBlock = 4096;
    internal const int HeaderBytes = 44;
    internal const int DirEntryBytes = 16;
    internal const int FooterBytes = 16;

    public sealed class Block {
        public int ClusterId;
        public int Ordinal; // index in file order; part of the cache key
        public int[] Ids = [];
        public long VectorsOffset;
    }

    readonly SafeFileHandle _handle;
    readonly Dictionary<int, (int first, int count)> _clusterRanges; // clusterId -> range in Blocks
    public long Id { get; }
    public int Dimensions { get; }
    public long CentroidGeneration { get; }
    public long FileLength { get; }
    public string Path { get; }
    public Block[] Blocks { get; }
    public int[] DeletedIds { get; }
    public int TotalRecords { get; }

    VectorSegment(SafeFileHandle handle, string path, long id, int dimensions, long centroidGeneration,
        long fileLength, Block[] blocks, int[] deletedIds, int totalRecords) {
        _handle = handle;
        Path = path;
        Id = id;
        Dimensions = dimensions;
        CentroidGeneration = centroidGeneration;
        FileLength = fileLength;
        Blocks = blocks;
        DeletedIds = deletedIds;
        TotalRecords = totalRecords;
        _clusterRanges = [];
        for (var i = 0; i < blocks.Length; i++) {
            if (_clusterRanges.TryGetValue(blocks[i].ClusterId, out var range)) {
                _clusterRanges[blocks[i].ClusterId] = (range.first, range.count + 1);
            } else {
                _clusterRanges.Add(blocks[i].ClusterId, (i, 1));
            }
        }
    }

    public static VectorSegment Open(string path, long expectedId, int expectedDimensions, long expectedCentroidGeneration) {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try {
            var len = RandomAccess.GetLength(handle);
            if (len < HeaderBytes + FooterBytes) throw invalid("file too short");
            Span<byte> h = stackalloc byte[HeaderBytes];
            readExactly(handle, h, 0);
            if (BinaryPrimitives.ReadInt64LittleEndian(h) != HeaderMagic) throw invalid("bad header magic");
            if (BinaryPrimitives.ReadInt32LittleEndian(h[8..]) != Version) throw invalid("unsupported version");
            var segmentId = BinaryPrimitives.ReadInt64LittleEndian(h[12..]);
            var dims = BinaryPrimitives.ReadInt32LittleEndian(h[20..]);
            var gen = BinaryPrimitives.ReadInt64LittleEndian(h[24..]);
            var blockCount = BinaryPrimitives.ReadInt32LittleEndian(h[32..]);
            var delCount = BinaryPrimitives.ReadInt32LittleEndian(h[36..]);
            var totalRecords = BinaryPrimitives.ReadInt32LittleEndian(h[40..]);
            if (segmentId != expectedId) throw invalid("segment id mismatch");
            if (dims != expectedDimensions) throw invalid("dimensions mismatch");
            if (gen != expectedCentroidGeneration) throw invalid("centroid generation mismatch");
            if (dims <= 0 || blockCount < 0 || delCount < 0 || totalRecords < 0) throw invalid("bad header counts");
            // bound every count by the actual file length before allocating anything based on them:
            var dirOffset = (long)HeaderBytes;
            var idsOffset = dirOffset + (long)blockCount * DirEntryBytes;
            var vectorOffset = idsOffset + (long)totalRecords * 4;
            var minLen = vectorOffset + (long)totalRecords * dims * 4 + (long)delCount * 4 + FooterBytes;
            if (minLen != len) throw invalid("file length mismatch");
            var dirBytes = new byte[blockCount * DirEntryBytes];
            readExactly(handle, dirBytes, dirOffset);
            var blocks = new Block[blockCount];
            var runningOffset = vectorOffset;
            var recordSum = 0L;
            var prevCluster = int.MinValue;
            for (var i = 0; i < blockCount; i++) {
                var e = dirBytes.AsSpan(i * DirEntryBytes);
                var clusterId = BinaryPrimitives.ReadInt32LittleEndian(e);
                var count = BinaryPrimitives.ReadInt32LittleEndian(e[4..]);
                var offset = BinaryPrimitives.ReadInt64LittleEndian(e[8..]);
                if (count <= 0 || count > MaxRecordsPerBlock) throw invalid("bad block count");
                if (clusterId < prevCluster) throw invalid("directory not sorted");
                if (offset != runningOffset) throw invalid("bad block offset");
                prevCluster = clusterId;
                runningOffset += (long)count * dims * 4;
                recordSum += count;
                blocks[i] = new Block { ClusterId = clusterId, Ordinal = i, Ids = new int[count], VectorsOffset = offset };
            }
            if (recordSum != totalRecords) throw invalid("record count mismatch");
            Span<byte> f = stackalloc byte[FooterBytes];
            readExactly(handle, f, len - FooterBytes);
            if (BinaryPrimitives.ReadInt64LittleEndian(f[8..]) != EndMagic) throw invalid("bad end magic");
            var metaHash = hashMeta(h, dirBytes);
            if (BinaryPrimitives.ReadUInt64LittleEndian(f) != metaHash) throw invalid("metadata hash mismatch");
            // one sequential read for all node ids, then slice them out per block:
            var allIds = new int[totalRecords];
            readExactly(handle, MemoryMarshal.AsBytes(allIds.AsSpan()), idsOffset);
            var idPos = 0;
            foreach (var b in blocks) {
                Array.Copy(allIds, idPos, b.Ids, 0, b.Ids.Length);
                idPos += b.Ids.Length;
            }
            var dels = new int[delCount];
            if (delCount > 0) readExactly(handle, MemoryMarshal.AsBytes(dels.AsSpan()), runningOffset);
            return new VectorSegment(handle, path, segmentId, dims, gen, len, blocks, dels, totalRecords);
        } catch {
            handle.Dispose();
            throw;
        }
    }
    static InvalidDataException invalid(string reason) => new("Invalid vector segment file: " + reason + ". ");
    internal static ulong hashMeta(ReadOnlySpan<byte> header, ReadOnlySpan<byte> directory) {
        // fnv1a over header then directory, matching the manifest's hash primitive
        var hash = 14695981039346656037UL;
        foreach (var b in header) { hash ^= b; hash *= 1099511628211UL; }
        foreach (var b in directory) { hash ^= b; hash *= 1099511628211UL; }
        return hash;
    }
    public bool TryGetClusterRange(int clusterId, out int first, out int count) {
        if (_clusterRanges.TryGetValue(clusterId, out var range)) {
            first = range.first;
            count = range.count;
            return true;
        }
        first = 0;
        count = 0;
        return false;
    }
    /// <summary>Reads a block's packed vectors (Ids.Length * Dimensions floats) in one positional read.</summary>
    public float[] ReadVectors(Block block) {
        var data = new float[block.Ids.Length * Dimensions];
        readExactly(_handle, MemoryMarshal.AsBytes(data.AsSpan()), block.VectorsOffset);
        return data;
    }
    /// <summary>Reads one record's vector; used for sparse sampling without loading whole blocks.</summary>
    public void ReadVector(Block block, int recordIndex, float[] target) {
        readExactly(_handle, MemoryMarshal.AsBytes(target.AsSpan(0, Dimensions)), block.VectorsOffset + (long)recordIndex * Dimensions * 4);
    }
    static void readExactly(SafeFileHandle handle, Span<byte> buffer, long offset) {
        var total = 0;
        while (total < buffer.Length) {
            var n = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of vector segment file. ");
            total += n;
        }
    }
    public void Dispose() => _handle.Dispose();
}
