using Relatude.DB.Common;
using System.Buffers.Binary;

namespace Relatude.DB.AI.ISV;

/// <summary>
/// The index's durable root: which segment files are live, the centroid generation they are
/// partitioned by, the WAL file the data belongs to, and the timestamp of the last durable position
/// (<see cref="IVSVectorIndex.PersistedTimestamp"/>). Written via a temp file and an atomic
/// replace, and only after the files it references are fsynced — a crash mid-write leaves the
/// previous manifest in place, never a half-written one. A missing, corrupt or foreign-WAL manifest
/// resets the index to empty so the WAL replay rebuilds it.
/// </summary>
internal sealed class VectorIndexManifest {
    const long Magic = 0x3158_5644_4244_5452; // "RTDBDVX1", changes with breaking format changes
    const int Version = 1;
    const int fixedBytes = 8 + 4 + 16 + 8 + 4 + 8 + 8 + 4 + 4 + 8; // everything except the segment ids
    public Guid WalFileId;
    public long Timestamp;
    public int Dimensions;
    public long NextSegmentId = 1;
    public long CentroidGeneration; // 0 = not clustered yet, single-cluster segments and exact search
    public int TrainedAtCount;      // live vector count when the centroids were trained
    public long[] SegmentIds = [];
    public static VectorIndexManifest? TryRead(string path) {
        try {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < fixedBytes) return null;
            if (BinaryPrimitives.ReadInt64LittleEndian(bytes) != Magic) return null;
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != Version) return null;
            var hash = fnv1a(bytes.AsSpan(0, bytes.Length - 8));
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8)) != hash) return null;
            var pos = 12;
            var m = new VectorIndexManifest {
                WalFileId = new Guid(bytes.AsSpan(pos, 16))
            };
            pos += 16;
            m.Timestamp = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos)); pos += 8;
            m.Dimensions = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.NextSegmentId = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos)); pos += 8;
            m.CentroidGeneration = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos)); pos += 8;
            m.TrainedAtCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            if (count < 0 || bytes.Length - 8 - pos != count * 8) return null;
            m.SegmentIds = new long[count];
            for (var i = 0; i < count; i++) {
                m.SegmentIds[i] = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos));
                pos += 8;
            }
            return m;
        } catch {
            return null; // unreadable manifests count as missing; the caller resets and rebuilds
        }
    }
    public void Write(string path) {
        var bytes = new byte[fixedBytes + SegmentIds.Length * 8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
        var pos = 12;
        WalFileId.TryWriteBytes(bytes.AsSpan(pos, 16));
        pos += 16;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(pos), Timestamp); pos += 8;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), Dimensions); pos += 4;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(pos), NextSegmentId); pos += 8;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(pos), CentroidGeneration); pos += 8;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), TrainedAtCount); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), SegmentIds.Length); pos += 4;
        foreach (var id in SegmentIds) {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(pos), id);
            pos += 8;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(pos), fnv1a(bytes.AsSpan(0, pos)));
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
            fs.Write(bytes);
            fs.Flush(true);
        }
        FileOpenRetry.Replace(tmp, path);
    }
    internal static ulong fnv1a(ReadOnlySpan<byte> bytes) {
        var hash = 14695981039346656037UL;
        foreach (var b in bytes) {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
