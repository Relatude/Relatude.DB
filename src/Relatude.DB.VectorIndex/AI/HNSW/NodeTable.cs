using System.Runtime.InteropServices;

namespace Relatude.DB.AI.HNSW;

/// <summary>
/// The graph's topology above layer 0, and the identity of every record: for each ordinal the node
/// id it holds, its top layer and whether it is still live, plus the neighbour lists of the layers
/// above 0. Everything is held in memory and mirrored to two fixed-stride files — the identity
/// because every traversal probes it per neighbour, the upper adjacency because the descent walks
/// it at the start of every search. It costs 12 bytes per vector plus the id map, against the much
/// larger routing records and vector data.
///
/// <para>Upper slots are allocated only for nodes that reach layer 1, so the wide per-layer records
/// are not paid for the 90-something percent of nodes that never leave layer 0 — and a node that
/// does reach layer 1 takes one slot <i>per layer it occupies</i>, consecutively from its base slot,
/// rather than a fixed record wide enough for every possible layer. Layer occupancy falls by a
/// factor <see cref="Connectivity"/> per layer, so nearly every upper node has exactly one layer:
/// fixed records would spend most of the file and the memory mirroring it on zeroed slots.</para>
/// </summary>
internal sealed class NodeTable : IDisposable {
    internal const int NodesFileKind = 3;
    internal const int UpperFileKind = 4;
    const int nodeWords = 3;                 // [nodeId][level and flags][upper slot]
    const int nodeStrideBytes = nodeWords * 4;
    const int liveFlag = 1 << 16;            // packed with the level, which needs 4 bits

    readonly FixedStrideFile _nodesFile;
    readonly FixedStrideFile _upperFile;
    // Changed entries still to be written. New ordinals and new upper slots are always appended, so
    // they need no bookkeeping beyond where the append started — which is what keeps a bulk load from
    // building a set with an entry per vector. The sets hold only rewrites of older entries.
    readonly SortedSet<int> _dirtyNodes = [];
    readonly SortedSet<int> _dirtyUpper = [];
    int _appendedNodesFrom = -1;
    int _appendedUpperFrom = -1;

    int[] _nodeId = [];      // by ordinal
    int[] _levelFlags = [];  // by ordinal
    int[] _upperSlot = [];   // by ordinal; the base of the node's run of slots, -1 below layer 1
    int[] _upper = [];       // by upper slot, mirroring the file layout exactly
    readonly IntMap _ordinalOf = new(); // node id -> ordinal, live nodes only

    public int Connectivity { get; }
    public int MaxLevels { get; }
    /// <summary>Words per upper slot: a count, <see cref="Connectivity"/> ordinals and their stored
    /// similarities for one layer. A node on layers 1..L holds L consecutive slots, layer <c>l</c>
    /// at its base slot + <c>l - 1</c>.</summary>
    public int UpperWords { get; }
    public int NextOrdinal { get; private set; }
    public int NextUpperSlot { get; private set; }
    public int LiveCount { get; private set; }
    public int DeadCount { get; private set; }
    /// <summary>Where a search enters the graph: a live node on <see cref="MaxLevel"/>.</summary>
    public int EntryOrdinal { get; private set; } = -1;
    public int MaxLevel { get; private set; } = -1;
    /// <summary>Bytes of the table's structures: the identity arrays, the id map and the upper
    /// mirror. What the budget arithmetic charges for this table.</summary>
    public long ResidentBytes =>
        (long)_nodeId.Length * 12 + (long)_ordinalOf.Count * 16 + (long)_upper.Length * 4;
    NodeTable(FixedStrideFile nodesFile, FixedStrideFile upperFile, int connectivity, int maxLevels) {
        _nodesFile = nodesFile;
        _upperFile = upperFile;
        Connectivity = connectivity;
        MaxLevels = maxLevels;
        UpperWords = 1 + 2 * connectivity;
    }

    public static NodeTable Create(string nodesPath, string upperPath, long generation, int connectivity, int maxLevels) {
        var upperWords = 1 + 2 * connectivity;
        var nodes = FixedStrideFile.Create(nodesPath, NodesFileKind, generation, nodeStrideBytes, [], 0);
        try {
            var upper = FixedStrideFile.Create(upperPath, UpperFileKind, generation, upperWords * 4, [connectivity, maxLevels], 0);
            return new(nodes, upper, connectivity, maxLevels);
        } catch {
            nodes.Dispose();
            throw;
        }
    }
    /// <summary>Opens the two files, reads the committed part into memory and rebuilds the derived
    /// lookups. Anything past the manifest's counts is uncommitted scratch and ignored.</summary>
    public static NodeTable Open(string nodesPath, string upperPath, long generation, int connectivity, int maxLevels,
        int committedOrdinals, int committedUpperSlots, int expectedEntry, int expectedMaxLevel) {
        var upperWords = 1 + 2 * connectivity;
        var nodes = FixedStrideFile.Open(nodesPath, NodesFileKind, generation, nodeStrideBytes, [], committedOrdinals);
        NodeTable table;
        try {
            var upper = FixedStrideFile.Open(upperPath, UpperFileKind, generation, upperWords * 4, [connectivity, maxLevels], committedUpperSlots);
            table = new(nodes, upper, connectivity, maxLevels);
        } catch {
            nodes.Dispose();
            throw;
        }
        try {
            table.load(committedOrdinals, committedUpperSlots, expectedEntry, expectedMaxLevel);
            return table;
        } catch {
            table.Dispose();
            throw;
        }
    }
    void load(int ordinals, int upperSlots, int expectedEntry, int expectedMaxLevel) {
        _nodeId = new int[Math.Max(1, ordinals)];
        _levelFlags = new int[_nodeId.Length];
        _upperSlot = new int[_nodeId.Length];
        if (ordinals > 0) {
            var raw = new int[(long)ordinals * nodeWords];
            _nodesFile.Read(0, MemoryMarshal.AsBytes(raw.AsSpan()));
            for (var o = 0; o < ordinals; o++) {
                _nodeId[o] = raw[o * nodeWords];
                _levelFlags[o] = raw[o * nodeWords + 1];
                _upperSlot[o] = raw[o * nodeWords + 2];
            }
        }
        var upperInts = (long)upperSlots * UpperWords;
        if (upperInts > int.MaxValue) throw new InvalidDataException("The upper-layer adjacency does not fit in one array. ");
        _upper = new int[Math.Max(UpperWords, (int)upperInts)];
        if (upperSlots > 0) _upperFile.Read(0, MemoryMarshal.AsBytes(_upper.AsSpan(0, upperSlots * UpperWords)));
        NextOrdinal = ordinals;
        NextUpperSlot = upperSlots;
        _ordinalOf.EnsureCapacity(ordinals);
        for (var o = 0; o < ordinals; o++) {
            var level = LevelOf(o);
            if (level < 0 || level >= MaxLevels) throw new InvalidDataException("The node table holds a layer outside the configured range. ");
            var slot = _upperSlot[o];
            if (level > 0 && (slot < 0 || slot + level > upperSlots)) throw new InvalidDataException("The node table points outside the upper-layer file. ");
            if (!IsLive(o)) {
                DeadCount++;
                continue;
            }
            if (_ordinalOf.TryGetValue(_nodeId[o], out _)) throw new InvalidDataException("The node table holds the same node id twice. ");
            _ordinalOf[_nodeId[o]] = o;
            LiveCount++;
        }
        // trust the manifest's entry point only when it is still a live node on the layer it claims
        if (expectedEntry >= 0 && expectedEntry < ordinals && IsLive(expectedEntry) && LevelOf(expectedEntry) == expectedMaxLevel) {
            EntryOrdinal = expectedEntry;
            MaxLevel = expectedMaxLevel;
        } else {
            RecomputeEntry();
        }
    }

    // ---- identity ---------------------------------------------------------------------------------

    public bool TryGetOrdinal(int nodeId, out int ordinal) => _ordinalOf.TryGetValue(nodeId, out ordinal);
    public int NodeIdOf(int ordinal) => _nodeId[ordinal];
    public int LevelOf(int ordinal) => _levelFlags[ordinal] & 0xFFFF;
    public bool IsLive(int ordinal) => ordinal >= 0 && ordinal < NextOrdinal && (_levelFlags[ordinal] & liveFlag) != 0;

    /// <summary>Claims the next ordinal for a new node. Ordinals are never reused: a reused slot
    /// would sit below the manifest's commit boundary, so a crash between the record write and the
    /// next manifest could leave it looking like committed data for the wrong node.</summary>
    public int Allocate(int nodeId, int level) {
        if (level < 0 || level >= MaxLevels) throw new ArgumentOutOfRangeException(nameof(level));
        var ordinal = NextOrdinal++;
        ensureOrdinalCapacity(ordinal);
        _nodeId[ordinal] = nodeId;
        _levelFlags[ordinal] = level | liveFlag;
        _upperSlot[ordinal] = -1;
        if (_appendedNodesFrom < 0) _appendedNodesFrom = ordinal;
        if (level > 0) { // one slot per occupied layer, consecutively from the base
            var slot = NextUpperSlot;
            NextUpperSlot += level;
            _upperSlot[ordinal] = slot;
            ensureUpperCapacity(NextUpperSlot - 1);
            _upper.AsSpan(slot * UpperWords, level * UpperWords).Clear();
            if (_appendedUpperFrom < 0) _appendedUpperFrom = slot;
        }
        _ordinalOf[nodeId] = ordinal;
        LiveCount++;
        if (EntryOrdinal < 0) { // the first node is the entry point by default
            EntryOrdinal = ordinal;
            MaxLevel = level;
        }
        return ordinal;
    }
    /// <summary>Makes a node the entry point because it reached a new top layer. Called only after it
    /// has been linked, so a search never enters the graph through a node with no edges.</summary>
    public void PromoteEntry(int ordinal) {
        var level = LevelOf(ordinal);
        if (level <= MaxLevel) return;
        MaxLevel = level;
        EntryOrdinal = ordinal;
    }
    /// <summary>Marks a node dead. Its record and its in-edges stay until a compaction rewrites the
    /// index; a traversal skips dead ordinals, so the graph stays correct in the meantime.</summary>
    public void Kill(int ordinal) {
        if (!IsLive(ordinal)) return;
        _levelFlags[ordinal] = LevelOf(ordinal); // clears the live flag, keeps the layer for the file record
        _ordinalOf.Remove(_nodeId[ordinal]);
        LiveCount--;
        DeadCount++;
        markNodeDirty(ordinal);
        if (EntryOrdinal == ordinal) RecomputeEntry();
    }
    /// <summary>Picks a new entry point: any live node on the highest occupied layer. One pass over
    /// the flags array — it only runs when the entry point itself dies, which one node per index can
    /// do, so a scan beats carrying per-layer membership sets on every insert and delete.</summary>
    public void RecomputeEntry() {
        var best = -1;
        var bestLevel = -1;
        for (var o = 0; o < NextOrdinal; o++) {
            var flags = _levelFlags[o];
            if ((flags & liveFlag) == 0) continue;
            var level = flags & 0xFFFF;
            if (level > bestLevel) {
                bestLevel = level;
                best = o;
            }
        }
        EntryOrdinal = best;
        MaxLevel = bestLevel;
    }

    // ---- upper-layer adjacency ---------------------------------------------------------------------

    /// <summary>The node's neighbours on one layer, straight out of the mirror. The level is bounded
    /// by the node's own top layer, not just the configured range: slots are allocated per occupied
    /// layer, so a level beyond the node's own (a stale neighbour id can point at any node) would
    /// read another node's slot.</summary>
    public ReadOnlySpan<int> UpperNeighbours(int ordinal, int level) {
        var raw = slotWords(ordinal, level);
        if (raw.IsEmpty) return [];
        return raw.Slice(1, Math.Clamp(raw[0], 0, Connectivity));
    }
    /// <summary>The node's raw slot on one layer — [count][ids][sims] — copied into
    /// <paramref name="buffer"/> (which must hold <see cref="UpperWords"/> ints), returning the
    /// count. What the linker reads before deciding whether it needs any vectors at all: ids sit at
    /// [1..], sims at [1 + <see cref="Connectivity"/>..]. The copy is deliberate — the linker holds
    /// the list while rewriting the same slot. Returns 0 with the buffer's count word cleared when
    /// the node does not occupy the layer.</summary>
    public int UpperEdges(int ordinal, int level, Span<int> buffer) {
        var raw = slotWords(ordinal, level);
        if (raw.IsEmpty) {
            buffer[0] = 0;
            return 0;
        }
        raw.CopyTo(buffer);
        return Math.Clamp(buffer[0], 0, Connectivity);
    }
    /// <summary>One layer's raw slot: [count][ids][sims]. Empty when the node does not occupy the layer.</summary>
    ReadOnlySpan<int> slotWords(int ordinal, int level) {
        var slot = _upperSlot[ordinal];
        if (slot < 0 || level < 1 || level > LevelOf(ordinal)) return [];
        var unit = slot + level - 1;
        return _upper.AsSpan(unit * UpperWords, UpperWords);
    }
    public void SetUpperNeighbours(int ordinal, int level, ReadOnlySpan<int> ids, ReadOnlySpan<float> sims) {
        var slot = _upperSlot[ordinal];
        if (slot < 0 || level < 1 || level > LevelOf(ordinal)) throw new InvalidOperationException("The node does not occupy that layer. ");
        if (ids.Length > Connectivity) throw new ArgumentException("More neighbours than the layer has slots for. ");
        var unit = slot + level - 1;
        var offset = unit * UpperWords;
        _upper[offset] = ids.Length;
        ids.CopyTo(_upper.AsSpan(offset + 1, ids.Length));
        sims.CopyTo(MemoryMarshal.Cast<int, float>(_upper.AsSpan(offset + 1 + Connectivity, sims.Length)));
        lock (_dirtyUpper) { // a batch build's workers write different slots concurrently
            if (_appendedUpperFrom < 0 || unit < _appendedUpperFrom) _dirtyUpper.Add(unit);
        }
    }
    void markNodeDirty(int ordinal) {
        if (_appendedNodesFrom < 0 || ordinal < _appendedNodesFrom) _dirtyNodes.Add(ordinal);
    }

    // ---- persistence -------------------------------------------------------------------------------

    public long DirtyBytes {
        get {
            var nodes = (long)_dirtyNodes.Count + (_appendedNodesFrom < 0 ? 0 : NextOrdinal - _appendedNodesFrom);
            var upper = (long)_dirtyUpper.Count + (_appendedUpperFrom < 0 ? 0 : NextUpperSlot - _appendedUpperFrom);
            return nodes * nodeStrideBytes + upper * UpperWords * 4;
        }
    }

    /// <summary>Writes the changed entries of both files, coalescing runs of consecutive indexes.
    /// Does not fsync; <see cref="Fsync"/> is the durable point.</summary>
    public void FlushDirty() {
        flushSet(_nodesFile, _dirtyNodes, nodeWords, copyNodeRecords);
        if (_appendedNodesFrom >= 0) writeRange(_nodesFile, _appendedNodesFrom, NextOrdinal - _appendedNodesFrom, nodeWords, copyNodeRecords);
        _appendedNodesFrom = -1;
        flushSet(_upperFile, _dirtyUpper, UpperWords, copyUpperRecords);
        if (_appendedUpperFrom >= 0) writeRange(_upperFile, _appendedUpperFrom, NextUpperSlot - _appendedUpperFrom, UpperWords, copyUpperRecords);
        _appendedUpperFrom = -1;
    }

    void copyNodeRecords(int first, int count, Span<int> target) {
        for (var i = 0; i < count; i++) {
            target[i * nodeWords] = _nodeId[first + i];
            target[i * nodeWords + 1] = _levelFlags[first + i];
            target[i * nodeWords + 2] = _upperSlot[first + i];
        }
    }
    void copyUpperRecords(int first, int count, Span<int> target) => _upper.AsSpan(first * UpperWords, count * UpperWords).CopyTo(target);

    delegate void Copy(int first, int count, Span<int> target);
    static void flushSet(FixedStrideFile file, SortedSet<int> dirty, int words, Copy copy) {
        if (dirty.Count == 0) return;
        var indexes = new int[dirty.Count];
        dirty.CopyTo(indexes);
        var maxRun = Math.Max(1, 1024 * 1024 / (words * 4));
        var buffer = Array.Empty<int>();
        var i = 0;
        while (i < indexes.Length) {
            var run = 1;
            while (run < maxRun && i + run < indexes.Length && indexes[i + run] == indexes[i + run - 1] + 1) run++;
            if (buffer.Length < run * words) buffer = new int[run * words];
            copy(indexes[i], run, buffer.AsSpan(0, run * words));
            file.Write(indexes[i], MemoryMarshal.AsBytes(buffer.AsSpan(0, run * words)));
            i += run;
        }
        dirty.Clear();
    }
    static void writeRange(FixedStrideFile file, int first, int count, int words, Copy copy) {
        if (count <= 0) return;
        var chunk = Math.Max(1, 1024 * 1024 / (words * 4));
        var buffer = new int[Math.Min(count, chunk) * words];
        for (var i = 0; i < count; i += chunk) {
            var n = Math.Min(chunk, count - i);
            copy(first + i, n, buffer.AsSpan(0, n * words));
            file.Write(first + i, MemoryMarshal.AsBytes(buffer.AsSpan(0, n * words)));
        }
    }
    public void Fsync() {
        _nodesFile.Fsync();
        _upperFile.Fsync();
    }
    public string[] Paths => [_nodesFile.Path, _upperFile.Path];
    public long FileLengths => _nodesFile.FileLength + _upperFile.FileLength;

    void ensureOrdinalCapacity(int ordinal) {
        if (ordinal < _nodeId.Length) return;
        var size = Math.Max(1024, _nodeId.Length * 2);
        while (size <= ordinal) size *= 2;
        Array.Resize(ref _nodeId, size);
        Array.Resize(ref _levelFlags, size);
        Array.Resize(ref _upperSlot, size);
    }
    void ensureUpperCapacity(int lastSlot) {
        var needed = (long)(lastSlot + 1) * UpperWords;
        if (needed <= _upper.Length) return;
        var size = Math.Max(64L * UpperWords, _upper.Length * 2L);
        while (size < needed) size *= 2;
        if (size > int.MaxValue) throw new InvalidOperationException("The upper-layer adjacency has outgrown one array. ");
        Array.Resize(ref _upper, (int)size);
    }
    public void Dispose() {
        _nodesFile.Dispose();
        _upperFile.Dispose();
    }
}
