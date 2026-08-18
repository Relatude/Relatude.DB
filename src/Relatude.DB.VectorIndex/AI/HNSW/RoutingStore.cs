using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Relatude.DB.AI.HNSW;

/// <summary>A cheap handle to one routing record's bytes: the pinned arena chunk that holds it and
/// the offset it starts at. The accessors on <see cref="RoutingStore"/> read it in place — nothing
/// is decoded and nothing is copied.</summary>
internal readonly struct RoutingRef(byte[] data, int offset) {
    public readonly byte[] Data = data;
    public readonly int Offset = offset;
}

/// <summary>
/// The routing graph: for every ordinal one fixed-size record holding the node's <b>int8</b> vector,
/// its dequantization scale and its layer-0 neighbour list — everything a graph hop needs, in about
/// a quarter of the bytes of the float vector. The float vectors live in their own file
/// (<see cref="FloatStore"/>) and are only read to re-score final candidates, so the walk — the
/// latency-critical chain of dependent accesses — runs entirely over these small records.
///
/// <para>A record is laid out as
/// <code>[q: Dimensions sbytes][pad][rescale: float][count: int][ids: NeighbourCapacity ints][sims: NeighbourCapacity floats]</code>
/// with the vector first, so scoring needs no offset arithmetic, and the whole mutable part — count,
/// ids and stored edge similarities — contiguous at the end, so an insert rewrites a couple of
/// hundred bytes. The similarity of each edge is stored beside it because the linker needs exactly
/// those numbers: adding a back-edge to a non-full node costs no vector reads at all, and a full
/// node rejects a poorer challenger against its stored worst edge before loading anything.</para>
///
/// <para>The whole file is mirrored into pinned, chunked arenas: getting a record is two array reads
/// and no residency check, eviction does not exist, and the walk prefetches the next candidates'
/// vectors while scoring the current one — the layout of an in-memory HNSW library, kept crash-safe
/// by the file underneath it. This residency is deliberate and unconditional: a graph walk is a
/// chain of dependent, deliberately scattered accesses, so a partially resident graph does not
/// degrade — it thrashes (see <see cref="VectorIndexOptions.MaxMemoryBytes"/>).</para>
///
/// <para><b>Unwritten records.</b> Changed records are marked dirty until the next flush. Newly
/// allocated ordinals are always the highest ones, so they need no bookkeeping beyond where the run
/// started; the dirty set holds only rewrites of older records — what an insert does to the
/// neighbour lists of the nodes it attaches to. A flush sends those rewrites to the edge log (one
/// sequential write) and marks the file "behind" on them — one bit per vector, the arena holding
/// the correct copy — and the state save writes them into place
/// (<see cref="ConsolidateBehind"/>).</para>
/// </summary>
internal sealed class RoutingStore : IDisposable {
    internal const int FileKind = 2;
    const int ChunkShift = 12;     // 4096 ordinals per arena chunk
    const int ChunkOrdinals = 1 << ChunkShift;
    const int ChunkMask = ChunkOrdinals - 1;

    readonly FixedStrideFile _file;
    readonly object _lock = new();          // guards the dirty marks and the append bookkeeping
    readonly HashSet<int> _dirtyEdges = []; // rewritten records, unflushed
    byte[][] _chunks = [];             // the pinned arena chunks the whole graph lives in
    ulong[] _behind = [];              // by ordinal: the routing file's edge region is out of date
    int _appendedFrom = -1;            // start of the run of new ordinals not yet flushed
    int _nextOrdinal;                  // one past the highest ordinal allocated, bounding that run

    public int Dimensions { get; }
    /// <summary>Slots for layer-0 neighbours in every record (the graph's layer-0 degree).</summary>
    public int NeighbourCapacity { get; }
    public int StrideBytes { get; }
    /// <summary>Byte offset of the rescale float, right after the padded int8 vector.</summary>
    readonly int _rescaleOffset;
    /// <summary>Byte offset of the mutable edge region: [count][ids][sims].</summary>
    readonly int _edgeOffset;
    readonly int _simsOffset;
    /// <summary>4-byte words of the edge region — the unit the edge log stores.</summary>
    public int EdgeWords { get; }
    public string Path => _file.Path;
    public long FileLength => _file.FileLength;
    public long RecordCapacity => _file.RecordCapacity;
    /// <summary>Bytes the arena holds in memory.</summary>
    public long ResidentBytes => (long)countChunks() * ChunkOrdinals * StrideBytes;
    int countChunks() {
        var n = 0;
        foreach (var c in _chunks) {
            if (c != null!) n++;
        }
        return n;
    }

    RoutingStore(FixedStrideFile file, int dimensions, int neighbourCapacity) {
        _file = file;
        Dimensions = dimensions;
        NeighbourCapacity = neighbourCapacity;
        var qPad = (4 - (dimensions & 3)) & 3;
        _rescaleOffset = dimensions + qPad;
        _edgeOffset = _rescaleOffset + 4;
        _simsOffset = _edgeOffset + 4 + 4 * neighbourCapacity;
        EdgeWords = 1 + 2 * neighbourCapacity;
        StrideBytes = _edgeOffset + 4 * EdgeWords;
    }

    static int strideOf(int dimensions, int neighbourCapacity) =>
        dimensions + ((4 - (dimensions & 3)) & 3) + 4 + 4 * (1 + 2 * neighbourCapacity);

    public static RoutingStore Create(string path, long generation, int dimensions, int neighbourCapacity) {
        var file = FixedStrideFile.Create(path, FileKind, generation, strideOf(dimensions, neighbourCapacity), [dimensions, neighbourCapacity], 0);
        return new(file, dimensions, neighbourCapacity);
    }
    /// <summary>Opens the committed records and mirrors the whole file into the arena on every core,
    /// which is a straight sequential read — no decoding, the disk layout is the memory layout.</summary>
    public static RoutingStore Open(string path, long generation, int dimensions, int neighbourCapacity,
        int committedRecords, int maxThreads) {
        var file = FixedStrideFile.Open(path, FileKind, generation, strideOf(dimensions, neighbourCapacity), [dimensions, neighbourCapacity], committedRecords);
        var store = new RoutingStore(file, dimensions, neighbourCapacity);
        try {
            store.ensureCapacity(committedRecords - 1);
            store._nextOrdinal = committedRecords;
            if (committedRecords > 0) store.mirrorFromFile(committedRecords, maxThreads);
            return store;
        } catch {
            store.Dispose();
            throw;
        }
    }
    void mirrorFromFile(int records, int maxThreads) {
        var chunkCount = (records + ChunkOrdinals - 1) >> ChunkShift;
        for (var c = 0; c < chunkCount; c++) ensureChunk(c << ChunkShift);
        var stride = StrideBytes;
        Parallel.For(0, chunkCount, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxThreads) }, c => {
            var first = c << ChunkShift;
            var count = Math.Min(ChunkOrdinals, records - first);
            _file.Read(first, _chunks[c].AsSpan(0, count * stride));
        });
    }

    // ---- record accessors ----------------------------------------------------------------------

    /// <summary>The record for an ordinal: two array reads, no locks — the hot path of every walk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RoutingRef Get(int ordinal) => new(_chunks[ordinal >> ChunkShift], (ordinal & ChunkMask) * StrideBytes);
    /// <summary>Hints the record's int8 vector into the CPU cache. The walk calls this for a hop's
    /// whole neighbour list while scoring, which hides most of the RAM latency of the next hops.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrefetchQ(int ordinal) {
        var chunk = _chunks[ordinal >> ChunkShift];
        var offset = (ordinal & ChunkMask) * StrideBytes;
        VectorMath.Prefetch(chunk, offset);
        if (Dimensions > 64) VectorMath.Prefetch(chunk, offset + 64);
    }

    // The accessors take the ref by value (16 bytes): the spans they return always point into the
    // ref's heap array, and passing by value is what lets the compiler's ref-safety analysis see that.

    /// <summary>The node's int8 vector — what the walk scores against.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<sbyte> Q(RoutingRef r) =>
        MemoryMarshal.Cast<byte, sbyte>(r.Data.AsSpan(r.Offset, Dimensions));
    /// <summary>Takes an int8 lane product back to float space (together with the other side's scale).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Rescale(RoutingRef r) =>
        Unsafe.ReadUnaligned<float>(ref r.Data[r.Offset + _rescaleOffset]);
    /// <summary>The node's layer-0 neighbour ordinals. The stored count is clamped to the capacity,
    /// so a count torn by a crash yields a short list instead of reading past the record.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> NeighbourIds(RoutingRef r) {
        var count = Math.Clamp(Unsafe.ReadUnaligned<int>(ref r.Data[r.Offset + _edgeOffset]), 0, NeighbourCapacity);
        return MemoryMarshal.Cast<byte, int>(r.Data.AsSpan(r.Offset + _edgeOffset + 4, count * 4));
    }
    /// <summary>The stored similarity of each layer-0 edge, aligned with <see cref="NeighbourIds"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<float> NeighbourSims(RoutingRef r) {
        var count = Math.Clamp(Unsafe.ReadUnaligned<int>(ref r.Data[r.Offset + _edgeOffset]), 0, NeighbourCapacity);
        return MemoryMarshal.Cast<byte, float>(r.Data.AsSpan(r.Offset + _simsOffset, count * 4));
    }

    // ---- writes ----------------------------------------------------------------------------------

    public long DirtyBytes {
        get {
            var count = (long)_dirtyEdges.Count + (_appendedFrom < 0 ? 0 : _nextOrdinal - _appendedFrom);
            return count * StrideBytes;
        }
    }
    /// <summary>True while a record's only correct copy is the one in memory.</summary>
    public bool IsDirty(int ordinal) => (_appendedFrom >= 0 && ordinal >= _appendedFrom) || _dirtyEdges.Contains(ordinal);
    /// <summary>A record for a freshly allocated ordinal: the vector quantized straight into place in
    /// the arena, an empty neighbour list, dirty from the start — an allocated-but-unflushed ordinal
    /// is never read from the file. Returns the record so the caller can link from it.</summary>
    public RoutingRef Allocate(int ordinal, ReadOnlySpan<float> vector) {
        ensureCapacity(ordinal);
        ensureChunk(ordinal);
        var r = new RoutingRef(_chunks[ordinal >> ChunkShift], (ordinal & ChunkMask) * StrideBytes);
        VectorMath.Quantize(vector, MemoryMarshal.Cast<byte, sbyte>(r.Data.AsSpan(r.Offset, Dimensions)), out var rescale);
        var qPadStart = r.Offset + Dimensions;
        r.Data.AsSpan(qPadStart, _rescaleOffset - Dimensions).Clear(); // deterministic padding bytes
        Unsafe.WriteUnaligned(ref r.Data[r.Offset + _rescaleOffset], rescale);
        r.Data.AsSpan(r.Offset + _edgeOffset, 4 * EdgeWords).Clear(); // no neighbours yet
        lock (_lock) {
            if (_appendedFrom < 0) _appendedFrom = ordinal;
            if (ordinal >= _nextOrdinal) _nextOrdinal = ordinal + 1;
        }
        return r;
    }
    /// <summary>Rewrites a record's edge region: the ids and, beside them, their similarities to the
    /// record's own vector — the numbers the linker consults before it loads anything. The caller
    /// holds the graph's stripe lock for this ordinal, so two writers never interleave on one record.</summary>
    public void SetNeighbours(int ordinal, ReadOnlySpan<int> ids, ReadOnlySpan<float> sims) {
        if (ids.Length > NeighbourCapacity) throw new ArgumentException("More neighbours than the record has slots for. ");
        var data = _chunks[ordinal >> ChunkShift];
        var offset = (ordinal & ChunkMask) * StrideBytes;
        if (_appendedFrom < 0 || ordinal < _appendedFrom) {
            lock (_lock) _dirtyEdges.Add(ordinal);
        }
        Unsafe.WriteUnaligned(ref data[offset + _edgeOffset], ids.Length);
        ids.CopyTo(MemoryMarshal.Cast<byte, int>(data.AsSpan(offset + _edgeOffset + 4, ids.Length * 4)));
        sims.CopyTo(MemoryMarshal.Cast<byte, float>(data.AsSpan(offset + _simsOffset, sims.Length * 4)));
    }

    /// <summary>
    /// Writes every change out. New records are always the highest ordinals, so they go to the
    /// routing file as a few large sequential writes however many there are. Rewrites are scattered
    /// and only ever touched the edge region: with an <paramref name="edgeLog"/> they are appended to
    /// it instead — one sequential write for the batch, which is what makes a WAL-flush checkpoint
    /// cheap — and without one they go straight into the routing file, which is what a state save
    /// does to make the log droppable.
    /// <para>Does not fsync; <see cref="Fsync"/> is the durable point.</para>
    /// </summary>
    public void FlushDirty(EdgeLog? edgeLog) {
        if (_dirtyEdges.Count > 0) {
            var rewritten = new int[_dirtyEdges.Count];
            _dirtyEdges.CopyTo(rewritten);
            Array.Sort(rewritten);
            foreach (var ordinal in rewritten) {
                var r = Get(ordinal);
                var region = MemoryMarshal.Cast<byte, int>(r.Data.AsSpan(r.Offset + _edgeOffset, 4 * EdgeWords));
                if (edgeLog != null) {
                    edgeLog.Append(ordinal, region);
                    setBehind(ordinal); // the routing file's edge region for this record is now stale
                } else {
                    _file.WriteWithin(ordinal, _edgeOffset, MemoryMarshal.AsBytes(region));
                }
            }
            edgeLog?.FlushPending();
        }
        if (_appendedFrom >= 0) {
            var maxRun = Math.Max(1, 4 * 1024 * 1024 / StrideBytes);
            for (var first = _appendedFrom; first < _nextOrdinal; first += maxRun) {
                writeFullRun(first, Math.Min(maxRun, _nextOrdinal - first));
            }
        }
        lock (_lock) {
            _dirtyEdges.Clear();
            _appendedFrom = -1;
        }
    }
    /// <summary>Writes every edge region the edge log is carrying into the routing file (straight
    /// from the arena, which always holds the newest copy), so the log can be dropped. This is the
    /// scattered in-place work the log exists to keep off the WAL-flush path.</summary>
    public void ConsolidateBehind() {
        for (var ordinal = 0; ordinal < _nextOrdinal; ordinal++) {
            if (!isBehind(ordinal)) continue;
            var r = Get(ordinal);
            var region = MemoryMarshal.Cast<byte, int>(r.Data.AsSpan(r.Offset + _edgeOffset, 4 * EdgeWords));
            _file.WriteWithin(ordinal, _edgeOffset, MemoryMarshal.AsBytes(region));
        }
        lock (_lock) {
            Array.Clear(_behind);
        }
    }
    /// <summary>Restores what the durable edge log says at open: those records' file copies are
    /// stale, so the regions are written straight onto the mirrored arena. Entries come in write
    /// order, so a later region for the same node simply replaces an earlier one.</summary>
    public void LoadOverlay(IEnumerable<(int ordinal, int[] region)> entries) {
        foreach (var (ordinal, region) in entries) {
            if (ordinal >= _nextOrdinal) continue; // past the commit boundary: uncommitted scratch
            ensureCapacity(ordinal);
            var r = Get(ordinal);
            region.CopyTo(MemoryMarshal.Cast<byte, int>(r.Data.AsSpan(r.Offset + _edgeOffset, 4 * EdgeWords)));
            setBehind(ordinal);
        }
    }
    bool isBehind(int ordinal) {
        var word = ordinal >> 6;
        return (uint)word < (uint)_behind.Length && (_behind[word] & (1UL << (ordinal & 63))) != 0;
    }
    void setBehind(int ordinal) {
        var word = ordinal >> 6;
        if ((uint)word >= (uint)_behind.Length) return; // capacity always covers allocated ordinals
        _behind[word] |= 1UL << (ordinal & 63);
    }
    void writeFullRun(int firstOrdinal, int count) {
        // the arena is the file layout: whole chunks (or the run inside one) write out directly
        var first = firstOrdinal;
        var end = firstOrdinal + count;
        while (first < end) {
            var chunk = _chunks[first >> ChunkShift];
            var inChunk = first & ChunkMask;
            var n = Math.Min(ChunkOrdinals - inChunk, end - first);
            _file.Write(first, chunk.AsSpan(inChunk * StrideBytes, n * StrideBytes));
            first += n;
        }
    }
    public void Fsync() => _file.Fsync();

    void ensureCapacity(int ordinal) { // callers hold either the index write lock or _lock
        var words = (ordinal >> 6) + 1;
        if (words <= _behind.Length) return;
        var size = Math.Max(16, _behind.Length * 2);
        while (size < words) size *= 2;
        Array.Resize(ref _behind, size);
    }
    void ensureChunk(int ordinal) {
        var chunk = ordinal >> ChunkShift;
        if (chunk >= _chunks.Length) {
            var size = Math.Max(4, _chunks.Length * 2);
            while (size <= chunk) size *= 2;
            Array.Resize(ref _chunks, size);
        }
        // pinned so the walk can take raw addresses for prefetching; uninitialized because every
        // record is fully written at allocation before anything can reach its ordinal
        _chunks[chunk] ??= GC.AllocateUninitializedArray<byte>(ChunkOrdinals * StrideBytes, pinned: true);
    }
    public void Dispose() {
        lock (_lock) {
            _chunks = [];
            _behind = [];
            _dirtyEdges.Clear();
        }
        _file.Dispose();
    }
}
