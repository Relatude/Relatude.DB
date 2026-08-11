using System.Runtime.InteropServices;

namespace Relatude.DB.VectorIndexHNSW;

/// <summary>
/// The graph's topology above layer 0, and the identity of every record: for each ordinal the node
/// id it holds, its top layer and whether it is still live, plus the neighbour lists of the layers
/// above 0. All of it is held in memory and mirrored to two fixed-stride files, updated in place for
/// the ordinals that changed.
///
/// <para>This is the part of the index that is deliberately <i>not</i> on disk. It is what an IVF
/// index's centroids are: the structure a search routes through before it touches any data. Keeping
/// it resident makes the descent from the entry point free, and it stays small — 12 bytes per vector
/// for the identity, and upper-layer edges only for the roughly one node in
/// <see cref="Connectivity"/> that has any, so about 40 MB per million vectors against gigabytes of
/// vector data on disk.</para>
///
/// <para>Upper slots are allocated only for nodes that reach layer 1, so the wide per-layer records
/// are not paid for the 90-something percent of nodes that never leave layer 0.</para>
/// </summary>
internal sealed class HnswNodeTable : IDisposable {
    internal const int NodesFileKind = 2;
    internal const int UpperFileKind = 3;
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
    int[] _upperSlot = [];   // by ordinal; -1 for a node that never leaves layer 0
    int[] _upper = [];       // by upper slot, mirroring the file layout exactly
    readonly Dictionary<int, int> _ordinalOf = []; // node id -> ordinal, live nodes only
    readonly HashSet<int>[] _levelMembers;         // live ordinals per layer, index 1..MaxLevels-1

    public int Connectivity { get; }
    public int MaxLevels { get; }
    /// <summary>Words per upper-slot record: a count and <see cref="Connectivity"/> ordinals for
    /// each layer above 0.</summary>
    public int UpperWords { get; }
    public int NextOrdinal { get; private set; }
    public int NextUpperSlot { get; private set; }
    public int LiveCount { get; private set; }
    public int DeadCount { get; private set; }
    /// <summary>Where a search enters the graph: a live node on <see cref="MaxLevel"/>.</summary>
    public int EntryOrdinal { get; private set; } = -1;
    public int MaxLevel { get; private set; } = -1;
    HnswNodeTable(FixedStrideFile nodesFile, FixedStrideFile upperFile, int connectivity, int maxLevels) {
        _nodesFile = nodesFile;
        _upperFile = upperFile;
        Connectivity = connectivity;
        MaxLevels = maxLevels;
        UpperWords = (maxLevels - 1) * (1 + connectivity);
        _levelMembers = new HashSet<int>[maxLevels];
        for (var l = 1; l < maxLevels; l++) _levelMembers[l] = [];
    }

    public static HnswNodeTable Create(string nodesPath, string upperPath, long generation, int connectivity, int maxLevels) {
        var upperWords = (maxLevels - 1) * (1 + connectivity);
        var nodes = FixedStrideFile.Create(nodesPath, NodesFileKind, generation, nodeStrideBytes, [], 0);
        try {
            var upper = FixedStrideFile.Create(upperPath, UpperFileKind, generation, upperWords * 4, [connectivity, maxLevels], 0);
            return new(nodes, upper, connectivity, maxLevels);
        } catch {
            nodes.Dispose();
            throw;
        }
    }
    /// <summary>Opens the two files and reads the committed part of both into memory, then rebuilds
    /// the derived lookups. Anything past the manifest's counts is uncommitted scratch and ignored.</summary>
    public static HnswNodeTable Open(string nodesPath, string upperPath, long generation, int connectivity, int maxLevels,
        int committedOrdinals, int committedUpperSlots, int expectedEntry, int expectedMaxLevel) {
        var upperWords = (maxLevels - 1) * (1 + connectivity);
        var nodes = FixedStrideFile.Open(nodesPath, NodesFileKind, generation, nodeStrideBytes, [], committedOrdinals);
        HnswNodeTable table;
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
        var upperInts = (long)upperSlots * UpperWords;
        if (upperInts > int.MaxValue) throw new InvalidDataException("The upper-layer adjacency does not fit in one array. ");
        _nodeId = new int[Math.Max(1, ordinals)];
        _levelFlags = new int[_nodeId.Length];
        _upperSlot = new int[_nodeId.Length];
        _upper = new int[Math.Max(UpperWords, (int)upperInts)];
        if (ordinals > 0) {
            var raw = new int[(long)ordinals * nodeWords];
            _nodesFile.Read(0, MemoryMarshal.AsBytes(raw.AsSpan()));
            for (var o = 0; o < ordinals; o++) {
                _nodeId[o] = raw[o * nodeWords];
                _levelFlags[o] = raw[o * nodeWords + 1];
                _upperSlot[o] = raw[o * nodeWords + 2];
            }
        }
        if (upperSlots > 0) _upperFile.Read(0, MemoryMarshal.AsBytes(_upper.AsSpan(0, upperSlots * UpperWords)));
        NextOrdinal = ordinals;
        NextUpperSlot = upperSlots;
        for (var o = 0; o < ordinals; o++) {
            if (!IsLive(o)) {
                DeadCount++;
                continue;
            }
            var level = LevelOf(o);
            if (level < 0 || level >= MaxLevels) throw new InvalidDataException("The node table holds a layer outside the configured range. ");
            if (!_ordinalOf.TryAdd(_nodeId[o], o)) throw new InvalidDataException("The node table holds the same node id twice. ");
            LiveCount++;
            for (var l = 1; l <= level; l++) _levelMembers[l].Add(o);
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
        if (level > 0) {
            var slot = NextUpperSlot++;
            ensureUpperCapacity(slot);
            _upper.AsSpan(slot * UpperWords, UpperWords).Clear();
            _upperSlot[ordinal] = slot;
            if (_appendedUpperFrom < 0) _appendedUpperFrom = slot;
            for (var l = 1; l <= level; l++) _levelMembers[l].Add(ordinal);
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
        var level = LevelOf(ordinal);
        _levelFlags[ordinal] = level; // clears the live flag, keeps the layer for the file record
        _ordinalOf.Remove(_nodeId[ordinal]);
        for (var l = 1; l <= level; l++) _levelMembers[l].Remove(ordinal);
        LiveCount--;
        DeadCount++;
        markNodeDirty(ordinal);
        if (EntryOrdinal == ordinal) RecomputeEntry();
    }
    /// <summary>Picks a new entry point: any live node on the highest occupied layer.</summary>
    public void RecomputeEntry() {
        for (var l = MaxLevels - 1; l >= 1; l--) {
            foreach (var ordinal in _levelMembers[l]) {
                EntryOrdinal = ordinal;
                MaxLevel = l;
                return;
            }
        }
        foreach (var ordinal in _ordinalOf.Values) { // everything left is on layer 0
            EntryOrdinal = ordinal;
            MaxLevel = 0;
            return;
        }
        EntryOrdinal = -1;
        MaxLevel = -1;
    }

    // ---- upper-layer adjacency ---------------------------------------------------------------------

    public ReadOnlySpan<int> UpperNeighbours(int ordinal, int level) {
        var slot = _upperSlot[ordinal];
        if (slot < 0 || level < 1 || level >= MaxLevels) return [];
        var offset = slot * UpperWords + (level - 1) * (1 + Connectivity);
        var count = Math.Clamp(_upper[offset], 0, Connectivity);
        return _upper.AsSpan(offset + 1, count);
    }
    public void SetUpperNeighbours(int ordinal, int level, ReadOnlySpan<int> neighbours) {
        var slot = _upperSlot[ordinal];
        if (slot < 0) throw new InvalidOperationException("The node has no layers above 0. ");
        if (neighbours.Length > Connectivity) throw new ArgumentException("More neighbours than the layer has slots for. ");
        var offset = slot * UpperWords + (level - 1) * (1 + Connectivity);
        _upper[offset] = neighbours.Length;
        neighbours.CopyTo(_upper.AsSpan(offset + 1, neighbours.Length));
        if (_appendedUpperFrom < 0 || slot < _appendedUpperFrom) _dirtyUpper.Add(slot);
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
        flushSet(_upperFile, _dirtyUpper, UpperWords, copyUpperRecords);
        if (_appendedNodesFrom >= 0) writeRange(_nodesFile, _appendedNodesFrom, NextOrdinal - _appendedNodesFrom, nodeWords, copyNodeRecords);
        if (_appendedUpperFrom >= 0) writeRange(_upperFile, _appendedUpperFrom, NextUpperSlot - _appendedUpperFrom, UpperWords, copyUpperRecords);
        _appendedNodesFrom = -1;
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
    void ensureUpperCapacity(int slot) {
        var needed = (long)(slot + 1) * UpperWords;
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
