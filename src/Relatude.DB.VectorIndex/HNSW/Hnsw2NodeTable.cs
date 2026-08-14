using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Relatude.DB.VectorIndex.HNSW;

/// <summary>
/// The graph's topology above layer 0, and the identity of every record: for each ordinal the node
/// id it holds, its top layer and whether it is still live, plus the neighbour lists of the layers
/// above 0. The identity is held in memory and mirrored to a fixed-stride file; the upper-layer
/// adjacency is mirrored in memory too, unless the index runs with the graph on disk (a budget at
/// or below <see cref="HnswVectorIndexOptions.LowMemoryThresholdBytes"/>, or a graph that no longer
/// fits it), where it is read from its file per hop and only the unflushed lists are held (see
/// below).
///
/// <para>The identity is the part of the index that is deliberately <i>not</i> on disk in either
/// mode. It is the structure a search routes through before it touches any data — every traversal
/// probes it per neighbour, so it has to be an array read. It costs 12 bytes per vector plus the id
/// map, against gigabytes of vector data on disk.</para>
///
/// <para>Upper slots are allocated only for nodes that reach layer 1, so the wide per-layer records
/// are not paid for the 90-something percent of nodes that never leave layer 0 — and a node that
/// does reach layer 1 takes one slot <i>per layer it occupies</i>, consecutively from its base slot,
/// rather than a fixed record wide enough for every possible layer. Layer occupancy falls by a
/// factor <see cref="Connectivity"/> per layer, so nearly every upper node has exactly one layer:
/// fixed records would spend most of the file and the memory mirroring it on zeroed slots.</para>
///
/// <para><b>On-disk mode.</b> The in-memory mirror of the upper file is dropped: a read fetches
/// one slot (~4 · (1 + 2 · <see cref="Connectivity"/>) bytes) positionally into a caller buffer, and
/// changed or new lists sit in a pending map until the next flush writes them into place. The
/// pending map is the only thing checked before the file, so a reader always sees the newest list;
/// every allocated slot gets a pending entry at birth, which is the invariant that lets the flush
/// watermark say "every slot below me is on disk". The descent reads a handful of slots per search,
/// so the cost is one small positional read per upper hop.</para>
/// </summary>
internal sealed class Hnsw2NodeTable : IDisposable {
    internal const int NodesFileKind = 3;
    internal const int UpperFileKind = 4;
    const int nodeWords = 3;                 // [nodeId][level and flags][upper slot]
    const int nodeStrideBytes = nodeWords * 4;
    const int liveFlag = 1 << 16;            // packed with the level, which needs 4 bits

    readonly FixedStrideFile _nodesFile;
    readonly FixedStrideFile _upperFile;
    readonly bool _upperOnDisk;
    // Changed entries still to be written. New ordinals and new upper slots are always appended, so
    // they need no bookkeeping beyond where the append started — which is what keeps a bulk load from
    // building a set with an entry per vector. The sets hold only rewrites of older entries.
    readonly SortedSet<int> _dirtyNodes = [];
    readonly SortedSet<int> _dirtyUpper = [];
    int _appendedNodesFrom = -1;
    int _appendedUpperFrom = -1;
    // On-disk mode: the upper slots not yet written to the file, as raw slot words — rewrites and
    // new slots alike — checked before the file on every read. Bounded by the memtable flush
    // threshold. Concurrent because a batch build's workers replace entries in parallel; a stored
    // array is never mutated, only swapped, so a reader sees a whole old or whole new slot.
    readonly ConcurrentDictionary<int, int[]>? _pendingUpper;
    int[] _emptySlot = []; // the pending entry every new slot starts with; immutable, shared
    int _flushedUpperSlots; // on-disk mode: every slot below this has been written to the file

    int[] _nodeId = [];      // by ordinal
    int[] _levelFlags = [];  // by ordinal
    int[] _upperSlot = [];   // by ordinal; the base of the node's run of slots, -1 below layer 1
    int[] _upper = [];       // by upper slot, mirroring the file layout exactly; unused in on-disk mode
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
    /// <summary>Bytes of the always-resident structures: the identity arrays, the id map and the
    /// upper mirror when it is held. What the budget arithmetic charges for this table.</summary>
    public long ResidentBytes =>
        (long)_nodeId.Length * 12 + (long)_ordinalOf.Count * 16
        + (_upperOnDisk ? _pendingUpper!.Count * (long)(UpperWords * 4 + 48) : (long)_upper.Length * 4);
    Hnsw2NodeTable(FixedStrideFile nodesFile, FixedStrideFile upperFile, int connectivity, int maxLevels, bool upperOnDisk) {
        _nodesFile = nodesFile;
        _upperFile = upperFile;
        Connectivity = connectivity;
        MaxLevels = maxLevels;
        UpperWords = 1 + 2 * connectivity;
        _upperOnDisk = upperOnDisk;
        if (upperOnDisk) {
            _pendingUpper = new();
            _emptySlot = new int[UpperWords];
        }
    }

    public static Hnsw2NodeTable Create(string nodesPath, string upperPath, long generation, int connectivity, int maxLevels, bool upperOnDisk) {
        var upperWords = 1 + 2 * connectivity;
        var nodes = FixedStrideFile.Create(nodesPath, NodesFileKind, generation, nodeStrideBytes, [], 0);
        try {
            var upper = FixedStrideFile.Create(upperPath, UpperFileKind, generation, upperWords * 4, [connectivity, maxLevels], 0);
            return new(nodes, upper, connectivity, maxLevels, upperOnDisk);
        } catch {
            nodes.Dispose();
            throw;
        }
    }
    /// <summary>Opens the two files and reads the committed part into memory (the upper adjacency
    /// stays on disk in on-disk mode), then rebuilds the derived lookups. Anything past the
    /// manifest's counts is uncommitted scratch and ignored.</summary>
    public static Hnsw2NodeTable Open(string nodesPath, string upperPath, long generation, int connectivity, int maxLevels,
        bool upperOnDisk, int committedOrdinals, int committedUpperSlots, int expectedEntry, int expectedMaxLevel) {
        var upperWords = 1 + 2 * connectivity;
        var nodes = FixedStrideFile.Open(nodesPath, NodesFileKind, generation, nodeStrideBytes, [], committedOrdinals);
        Hnsw2NodeTable table;
        try {
            var upper = FixedStrideFile.Open(upperPath, UpperFileKind, generation, upperWords * 4, [connectivity, maxLevels], committedUpperSlots);
            table = new(nodes, upper, connectivity, maxLevels, upperOnDisk);
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
        if (_upperOnDisk) {
            _flushedUpperSlots = upperSlots; // the committed slots are on disk; reads go there
        } else {
            var upperInts = (long)upperSlots * UpperWords;
            if (upperInts > int.MaxValue) throw new InvalidDataException("The upper-layer adjacency does not fit in one array. ");
            _upper = new int[Math.Max(UpperWords, (int)upperInts)];
            if (upperSlots > 0) _upperFile.Read(0, MemoryMarshal.AsBytes(_upper.AsSpan(0, upperSlots * UpperWords)));
        }
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
            if (_upperOnDisk) {
                // every new slot gets a pending (empty) list — the invariant that lets the flush
                // watermark advance to NextUpperSlot: every slot below it has been written by a flush
                for (var u = slot; u < slot + level; u++) _pendingUpper![u] = _emptySlot;
            } else {
                ensureUpperCapacity(NextUpperSlot - 1);
                _upper.AsSpan(slot * UpperWords, level * UpperWords).Clear();
                if (_appendedUpperFrom < 0) _appendedUpperFrom = slot;
            }
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

    /// <summary>The node's neighbours on one layer. The level is bounded by the node's own top layer,
    /// not just the configured range: slots are allocated per occupied layer, so a level beyond the
    /// node's own (a stale neighbour id can point at any node) would read another node's slot.
    /// <para><paramref name="buffer"/> must hold <see cref="UpperWords"/> ints; it is used only in
    /// on-disk mode, where the slot may be read from the file into it — the returned span is
    /// only valid until the buffer's next use.</para></summary>
    public ReadOnlySpan<int> UpperNeighbours(int ordinal, int level, Span<int> buffer) {
        var raw = slotWords(ordinal, level, buffer);
        if (raw.IsEmpty) return [];
        return raw.Slice(1, Math.Clamp(raw[0], 0, Connectivity));
    }
    /// <summary>The node's raw slot on one layer — [count][ids][sims] — copied into
    /// <paramref name="buffer"/> (which must hold <see cref="UpperWords"/> ints), returning the
    /// count. What the linker reads before deciding whether it needs any vectors at all: ids sit at
    /// [1..], sims at [1 + <see cref="Connectivity"/>..]. Returns 0 with the buffer's count word
    /// cleared when the node does not occupy the layer.</summary>
    public int UpperEdges(int ordinal, int level, Span<int> buffer) {
        var raw = slotWords(ordinal, level, buffer);
        if (raw.IsEmpty) {
            buffer[0] = 0;
            return 0;
        }
        if (raw != buffer[..UpperWords]) raw.CopyTo(buffer); // pending or mirror: bring it local
        return Math.Clamp(buffer[0], 0, Connectivity);
    }
    /// <summary>One layer's raw slot: [count][ids][sims], from the pending map, the file or the
    /// mirror, whichever holds the newest copy. Empty when the node does not occupy the layer.</summary>
    ReadOnlySpan<int> slotWords(int ordinal, int level, Span<int> buffer) {
        var slot = _upperSlot[ordinal];
        if (slot < 0 || level < 1 || level > LevelOf(ordinal)) return [];
        var unit = slot + level - 1;
        if (_upperOnDisk) {
            if (_pendingUpper!.TryGetValue(unit, out var pending)) return pending;
            if (unit >= _flushedUpperSlots) return []; // allocated this session, nothing linked yet
            _upperFile.Read(unit, MemoryMarshal.AsBytes(buffer[..UpperWords]));
            return buffer[..UpperWords];
        }
        return _upper.AsSpan(unit * UpperWords, UpperWords);
    }
    public void SetUpperNeighbours(int ordinal, int level, ReadOnlySpan<int> ids, ReadOnlySpan<float> sims) {
        var slot = _upperSlot[ordinal];
        if (slot < 0 || level < 1 || level > LevelOf(ordinal)) throw new InvalidOperationException("The node does not occupy that layer. ");
        if (ids.Length > Connectivity) throw new ArgumentException("More neighbours than the layer has slots for. ");
        var unit = slot + level - 1;
        if (_upperOnDisk) {
            var raw = new int[UpperWords]; // a fresh array per write: pending entries are never mutated
            raw[0] = ids.Length;
            ids.CopyTo(raw.AsSpan(1));
            sims.CopyTo(MemoryMarshal.Cast<int, float>(raw.AsSpan(1 + Connectivity, sims.Length)));
            _pendingUpper![unit] = raw;
            return;
        }
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
            var upper = _upperOnDisk
                ? _pendingUpper!.Count
                : (long)_dirtyUpper.Count + (_appendedUpperFrom < 0 ? 0 : NextUpperSlot - _appendedUpperFrom);
            return nodes * nodeStrideBytes + upper * UpperWords * 4;
        }
    }

    /// <summary>Writes the changed entries of both files, coalescing runs of consecutive indexes.
    /// Does not fsync; <see cref="Fsync"/> is the durable point.</summary>
    public void FlushDirty() {
        flushSet(_nodesFile, _dirtyNodes, nodeWords, copyNodeRecords);
        if (_appendedNodesFrom >= 0) writeRange(_nodesFile, _appendedNodesFrom, NextOrdinal - _appendedNodesFrom, nodeWords, copyNodeRecords);
        _appendedNodesFrom = -1;
        if (_upperOnDisk) {
            flushPendingUpper();
            return;
        }
        flushSet(_upperFile, _dirtyUpper, UpperWords, copyUpperRecords);
        if (_appendedUpperFrom >= 0) writeRange(_upperFile, _appendedUpperFrom, NextUpperSlot - _appendedUpperFrom, UpperWords, copyUpperRecords);
        _appendedUpperFrom = -1;
    }
    /// <summary>On-disk mode's flush: every pending slot written into place, runs of consecutive
    /// slots coalesced into one write. Afterwards every slot below <see cref="NextUpperSlot"/> is on
    /// the file (see the invariant in <see cref="Allocate"/>), so the watermark advances and reads
    /// stop consulting the map.</summary>
    void flushPendingUpper() {
        if (!_pendingUpper!.IsEmpty) {
            var units = _pendingUpper.Keys.ToArray();
            Array.Sort(units);
            var maxRun = Math.Max(1, 1024 * 1024 / (UpperWords * 4));
            var buffer = Array.Empty<int>();
            var i = 0;
            while (i < units.Length) {
                var run = 1;
                while (run < maxRun && i + run < units.Length && units[i + run] == units[i + run - 1] + 1) run++;
                if (buffer.Length < run * UpperWords) buffer = new int[run * UpperWords];
                var span = buffer.AsSpan(0, run * UpperWords);
                for (var r = 0; r < run; r++) _pendingUpper[units[i + r]].CopyTo(span.Slice(r * UpperWords, UpperWords));
                _upperFile.Write(units[i], MemoryMarshal.AsBytes(span));
                i += run;
            }
            _pendingUpper.Clear();
        }
        _flushedUpperSlots = Math.Max(_flushedUpperSlots, NextUpperSlot);
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
