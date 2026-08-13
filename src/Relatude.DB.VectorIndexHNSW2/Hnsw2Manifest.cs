using System.Buffers.Binary;

namespace Relatude.DB.VectorIndexHNSW2;

/// <summary>
/// The index's durable root: which generation of the graph files is live, how many records of each
/// of them are committed, where a search enters the graph, the WAL file the data belongs to and the
/// timestamp of the last durable position (<see cref="Hnsw2VectorIndex.PersistedTimestamp"/>).
/// Written via a temp file and an atomic replace, and only after the files it describes are fsynced —
/// a crash mid-write leaves the previous manifest in place, never a half-written one. A missing,
/// corrupt or foreign-WAL manifest resets the index to empty so the WAL replay rebuilds it.
///
/// <para><see cref="NextOrdinal"/> and <see cref="NextUpperSlot"/> are the commit boundary of the
/// in-place graph files: records at or beyond them were allocated after this manifest was written,
/// so an open ignores them and the replay re-adds those vectors. That is what makes updating the
/// files in place safe without a second copy of the data.</para>
/// </summary>
internal sealed class Hnsw2Manifest {
    const long Magic = 0x324D_5648_4244_5452; // "RTDBHVM2", changes with breaking format changes
    const int Version = 1;
    const int totalBytes = 8 + 4 + 16 + 8 + 4 + 8 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 8;

    public Guid WalFileId;
    public long Timestamp;
    public int Dimensions;
    public long Generation;      // which set of vectors_/routing_/nodes_/upper_ files is live
    public int NextOrdinal;      // committed record count of the vector, routing and node files
    public int NextUpperSlot;    // committed record count of the upper-layer file
    public int LiveCount;
    public int DeadCount;
    public int EntryOrdinal = -1;
    public int MaxLevel = -1;
    public int Connectivity;      // the layout parameters the files were written with; a
    public int ConnectivityLevel0;// configuration change invalidates them rather than corrupting them
    public int MaxLevels;
    /// <summary>Durable entries of the edge log — neighbour lists the routing file has not received
    /// yet. Anything the log holds beyond this was not durable when the manifest was written, so it
    /// is not replayed. See <see cref="Hnsw2EdgeLog"/>.</summary>
    public int EdgeLogEntries;

    public static Hnsw2Manifest? TryRead(string path) {
        try {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != totalBytes) return null;
            if (BinaryPrimitives.ReadInt64LittleEndian(bytes) != Magic) return null;
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != Version) return null;
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8)) != Hash.Fnv1a(bytes.AsSpan(0, bytes.Length - 8))) return null;
            var pos = 12;
            var m = new Hnsw2Manifest { WalFileId = new Guid(bytes.AsSpan(pos, 16)) };
            pos += 16;
            m.Timestamp = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos)); pos += 8;
            m.Dimensions = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.Generation = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos)); pos += 8;
            m.NextOrdinal = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.NextUpperSlot = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.LiveCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.DeadCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.EntryOrdinal = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.MaxLevel = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.Connectivity = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.ConnectivityLevel0 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.MaxLevels = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos)); pos += 4;
            m.EdgeLogEntries = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos));
            if (m.NextOrdinal < 0 || m.NextUpperSlot < 0 || m.LiveCount < 0 || m.DeadCount < 0 || m.EdgeLogEntries < 0) return null;
            if (m.Dimensions < 0 || m.Generation < 0) return null;
            return m;
        } catch {
            return null; // unreadable manifests count as missing; the caller resets and rebuilds
        }
    }

    public void Write(string path) {
        var bytes = new byte[totalBytes];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
        var pos = 12;
        WalFileId.TryWriteBytes(bytes.AsSpan(pos, 16));
        pos += 16;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(pos), Timestamp); pos += 8;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), Dimensions); pos += 4;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(pos), Generation); pos += 8;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), NextOrdinal); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), NextUpperSlot); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), LiveCount); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), DeadCount); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), EntryOrdinal); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), MaxLevel); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), Connectivity); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), ConnectivityLevel0); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), MaxLevels); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(pos), EdgeLogEntries); pos += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(pos), Hash.Fnv1a(bytes.AsSpan(0, pos)));
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
            fs.Write(bytes);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>The file names of one generation of graph files inside an index folder.</summary>
internal sealed class Hnsw2Paths(string folder) {
    public string Folder => folder;
    public string Manifest => Path.Combine(folder, "manifest.bin");
    /// <summary>The float vectors, one fixed-stride record per ordinal. Read for exact re-scoring
    /// and exact scans; never needed to walk the graph.</summary>
    public string Vectors(long generation) => Path.Combine(folder, "vectors_" + generation.ToString("d8") + ".bin");
    /// <summary>The routing records: a node's int8 vector, its scale and its layer-0 neighbour
    /// list — everything one graph hop needs, in about a quarter of the float record's bytes.</summary>
    public string Routing(long generation) => Path.Combine(folder, "routing_" + generation.ToString("d8") + ".bin");
    public string Nodes(long generation) => Path.Combine(folder, "nodes_" + generation.ToString("d8") + ".bin");
    public string Upper(long generation) => Path.Combine(folder, "upper_" + generation.ToString("d8") + ".bin");
    public string Edges(long generation) => Path.Combine(folder, "edges_" + generation.ToString("d8") + ".log");
    public string[] Generation(long generation) => [Vectors(generation), Routing(generation), Nodes(generation), Upper(generation), Edges(generation)];
    /// <summary>Every data file in the folder, whichever generation it belongs to.</summary>
    public IEnumerable<string> AllDataFiles() =>
        Directory.GetFiles(folder, "vectors_*.bin")
            .Concat(Directory.GetFiles(folder, "routing_*.bin"))
            .Concat(Directory.GetFiles(folder, "nodes_*.bin"))
            .Concat(Directory.GetFiles(folder, "upper_*.bin"))
            .Concat(Directory.GetFiles(folder, "edges_*.log"));
}
