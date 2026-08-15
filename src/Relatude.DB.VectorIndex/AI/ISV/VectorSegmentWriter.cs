using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Relatude.DB.AI.ISV;

/// <summary>
/// Writes one segment file. Per-cluster record counts must be known up front (they determine the
/// file layout); records are then appended in any cluster interleaving — sequential per cluster —
/// which lets a rebuild stream its sources once and scatter records to their new clusters. Appends
/// go through per-cluster staging buffers so the positional writes stay reasonably large. Nothing
/// references the file until it is finished, fsynced and the manifest is re-pointed, so a crash
/// mid-write just leaves a stray file for the next open to delete.
/// </summary>
internal sealed class VectorSegmentWriter : IDisposable {
    sealed class ClusterStream {
        public int ClusterId;
        public int Total;
        public int Appended;
        public int FirstBlockOrdinal;
        public long FirstRecordGlobal;
        public float[] Staging = [];
        public int StagingCount;
        public int StagingStartRecord; // record index within the cluster of the first staged record
    }
    readonly SafeFileHandle _handle;
    readonly string _path;
    readonly long _segmentId;
    readonly int _dims;
    readonly long _centroidGeneration;
    readonly int[] _deletedIds;
    readonly ClusterStream[] _clusters;
    readonly Dictionary<int, int> _clusterLookup; // clusterId -> index into _clusters
    readonly (int clusterId, int count, long vectorsOffset)[] _blocks;
    readonly int[] _allIds;
    readonly int _stagingRecords;
    readonly long _idsOffset;
    readonly long _delsOffset;
    readonly long _fileLength;
    bool _handleTransferred;
    public VectorSegmentWriter(string path, long segmentId, int dims, long centroidGeneration,
        IReadOnlyList<(int clusterId, int count)> clusterCounts, int[] deletedIds) {
        if (dims <= 0) throw new ArgumentException("Dimensions must be set before a segment can be written. ");
        _path = path;
        _segmentId = segmentId;
        _dims = dims;
        _centroidGeneration = centroidGeneration;
        _deletedIds = deletedIds;
        // layout: clusters sorted ascending, big clusters chunked into blocks of at most MaxRecordsPerBlock
        var sorted = clusterCounts.OrderBy(c => c.clusterId).ToArray();
        var totalRecords = 0L;
        var blockCount = 0;
        foreach (var (clusterId, count) in sorted) {
            if (count <= 0) throw new ArgumentException("Cluster counts must be positive. ");
            totalRecords += count;
            blockCount += (count + VectorSegment.MaxRecordsPerBlock - 1) / VectorSegment.MaxRecordsPerBlock;
        }
        if (totalRecords > int.MaxValue) throw new ArgumentException("Too many records for one segment. ");
        _allIds = new int[totalRecords];
        _blocks = new (int, int, long)[blockCount];
        _clusters = new ClusterStream[sorted.Length];
        _clusterLookup = new(sorted.Length);
        _idsOffset = VectorSegment.HeaderBytes + (long)blockCount * VectorSegment.DirEntryBytes;
        var vectorOffset = _idsOffset + totalRecords * 4;
        var blockOrdinal = 0;
        var recordGlobal = 0L;
        for (var i = 0; i < sorted.Length; i++) {
            var (clusterId, count) = sorted[i];
            _clusters[i] = new ClusterStream {
                ClusterId = clusterId,
                Total = count,
                FirstBlockOrdinal = blockOrdinal,
                FirstRecordGlobal = recordGlobal,
            };
            _clusterLookup.Add(clusterId, i);
            recordGlobal += count;
            var left = count;
            while (left > 0) {
                var inBlock = Math.Min(left, VectorSegment.MaxRecordsPerBlock);
                _blocks[blockOrdinal++] = (clusterId, inBlock, vectorOffset);
                vectorOffset += (long)inBlock * dims * 4;
                left -= inBlock;
            }
        }
        _delsOffset = vectorOffset;
        _fileLength = _delsOffset + (long)deletedIds.Length * 4 + VectorSegment.FooterBytes;
        // staging: aim at large writes without letting many clusters hold too much memory in total
        var recordBytes = (long)dims * 4;
        var perClusterBytes = Math.Clamp(256L * 1024 * 1024 / Math.Max(1, _clusters.Length), 64L * 1024, 4L * 1024 * 1024);
        _stagingRecords = (int)Math.Clamp(perClusterBytes / recordBytes, 1, VectorSegment.MaxRecordsPerBlock);
        _handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.None, _fileLength);
    }
    public void Append(int clusterId, int nodeId, ReadOnlySpan<float> vector) {
        var c = _clusters[_clusterLookup[clusterId]];
        if (c.Appended >= c.Total) throw new InvalidOperationException("More records appended than declared for the cluster. ");
        if (c.Staging.Length == 0) c.Staging = new float[_stagingRecords * _dims];
        var record = c.Appended++;
        _allIds[c.FirstRecordGlobal + record] = nodeId;
        vector.CopyTo(c.Staging.AsSpan(c.StagingCount * _dims, _dims));
        c.StagingCount++;
        var next = record + 1;
        // flush when the staging buffer is full, at every chunk boundary (so a flushed run never
        // spans two blocks) and after the cluster's last record:
        if (c.StagingCount == _stagingRecords || next % VectorSegment.MaxRecordsPerBlock == 0 || next == c.Total) flushStaging(c);
    }
    void flushStaging(ClusterStream c) {
        if (c.StagingCount == 0) return;
        RandomAccess.Write(_handle, MemoryMarshal.AsBytes(c.Staging.AsSpan(0, c.StagingCount * _dims)), byteOffsetOfRecord(c, c.StagingStartRecord));
        c.StagingStartRecord += c.StagingCount;
        c.StagingCount = 0;
    }
    long byteOffsetOfRecord(ClusterStream c, int record) {
        var chunk = record / VectorSegment.MaxRecordsPerBlock;
        var local = record % VectorSegment.MaxRecordsPerBlock;
        var block = _blocks[c.FirstBlockOrdinal + chunk];
        return block.vectorsOffset + (long)local * _dims * 4;
    }
    /// <summary>Writes the metadata, fsyncs the file and opens it as an immutable segment.</summary>
    public VectorSegment Finish() {
        foreach (var c in _clusters) {
            if (c.Appended != c.Total) throw new InvalidOperationException("Fewer records appended than declared for cluster " + c.ClusterId + ". ");
            flushStaging(c);
        }
        if (_allIds.Length > 0) RandomAccess.Write(_handle, MemoryMarshal.AsBytes(_allIds.AsSpan()), _idsOffset);
        if (_deletedIds.Length > 0) RandomAccess.Write(_handle, MemoryMarshal.AsBytes(_deletedIds.AsSpan()), _delsOffset);
        var header = new byte[VectorSegment.HeaderBytes];
        BinaryPrimitives.WriteInt64LittleEndian(header, VectorSegment.HeaderMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), VectorSegment.Version);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12), _segmentId);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), _dims);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), _centroidGeneration);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), _blocks.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36), _deletedIds.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), _allIds.Length);
        var directory = new byte[_blocks.Length * VectorSegment.DirEntryBytes];
        for (var i = 0; i < _blocks.Length; i++) {
            var e = directory.AsSpan(i * VectorSegment.DirEntryBytes);
            BinaryPrimitives.WriteInt32LittleEndian(e, _blocks[i].clusterId);
            BinaryPrimitives.WriteInt32LittleEndian(e[4..], _blocks[i].count);
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], _blocks[i].vectorsOffset);
        }
        RandomAccess.Write(_handle, header, 0);
        RandomAccess.Write(_handle, directory, VectorSegment.HeaderBytes);
        var footer = new byte[VectorSegment.FooterBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(footer, VectorSegment.hashMeta(header, directory));
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(8), VectorSegment.EndMagic);
        RandomAccess.Write(_handle, footer, _fileLength - VectorSegment.FooterBytes);
        _handleTransferred = true; // the FileStream below takes ownership of the handle and closes it
        using (var fs = new FileStream(_handle, FileAccess.ReadWrite)) fs.Flush(true);
        return VectorSegment.Open(_path, _segmentId, _dims, _centroidGeneration);
    }
    public void Dispose() {
        // on the failure path the partial file is left behind as a stray; the next open deletes it
        if (!_handleTransferred) _handle.Dispose();
    }
}
