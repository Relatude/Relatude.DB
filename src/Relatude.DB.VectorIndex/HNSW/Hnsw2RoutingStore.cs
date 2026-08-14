using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Relatude.DB.VectorIndex.HNSW;

/// <summary>A cheap handle to one routing record's bytes: the array that holds it and the offset it
/// starts at. In resident mode that is a slice of a pinned arena chunk, in cached mode a cache
/// entry's own array — either way the accessors on <see cref="Hnsw2RoutingStore"/> read it in place,
/// nothing is decoded and nothing is copied.</summary>
internal readonly struct RoutingRef(byte[] data, int offset) {
    public readonly byte[] Data = data;
    public readonly int Offset = offset;
}

/// <summary>
/// The routing graph: for every ordinal one fixed-size record holding the node's <b>int8</b> vector,
/// its dequantization scale and its layer-0 neighbour list — everything a graph hop needs, in about
/// a quarter of the bytes of the float vector. The float vectors live in their own file
/// (<see cref="Hnsw2FloatStore"/>) and are only read to re-score final candidates, so the walk — the
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
/// <para><b>Resident mode</b> mirrors the whole file into pinned, chunked arenas: getting a record
/// is two array reads and no residency check, eviction does not exist, and the walk prefetches the
/// next candidates' vectors while scoring the current one — the layout of an in-memory HNSW library,
/// kept crash-safe by the file underneath it. <b>Cached mode</b> (the low-memory configuration)
/// keeps records on disk and reads them through a keyed second-chance cache with eviction hysteresis;
/// because the records are a quarter of the float size, the same cache budget holds four times the
/// nodes it would hold of float records.</para>
///
/// <para><b>Unwritten records.</b> Changed records are marked dirty, which pins them until the next
/// flush. Newly allocated ordinals are always the highest ones, so they need no bookkeeping beyond
/// where the run started; the dirty set holds only rewrites of older records — what an insert does
/// to the neighbour lists of the nodes it attaches to. A flush sends those rewrites to the edge log
/// (one sequential write) and marks the file "behind" on them; the state save writes them into place
/// (<see cref="ConsolidateBehind"/>). In cached mode a behind record that gets evicted parks its
/// edge region in an overlay first — otherwise the eviction would drop the only correct copy.</para>
/// </summary>
internal sealed class Hnsw2RoutingStore : IDisposable {
    internal const int FileKind = 2;
    const long entryOverhead = 72; // entry object, array header, the dictionary slot — roughly
    const int ChunkShift = 12;     // 4096 ordinals per arena chunk
    const int ChunkOrdinals = 1 << ChunkShift;
    const int ChunkMask = ChunkOrdinals - 1;

    readonly FixedStrideFile _file;
    readonly object _lock = new();        // guards residency changes, the dirty marks and the overlay
    readonly HashSet<int> _dirtyEdges = []; // rewritten records, pinned (cached mode) until flushed
    // A record whose neighbour list went to the edge log has a stale edge region in the routing file.
    // The bitmap says which — one bit per vector — and in cached mode the overlay carries the actual
    // region for those of them that have since been evicted, which are the only ones whose correct
    // copy would otherwise be nowhere in memory. In resident mode the arena is never evicted, so the
    // bitmap alone is enough.
    readonly Dictionary<int, int[]> _overlay = [];
    readonly bool _resident;
    readonly ConcurrentDictionary<int, Entry>? _cache; // cached mode: entries only for what is held
    byte[][] _chunks = [];             // resident mode: pinned arena chunks
    ulong[] _behind = [];              // by ordinal: the routing file's edge region is out of date
    int _appendedFrom = -1;            // start of the run of new ordinals not yet flushed
    int _nextOrdinal;                  // one past the highest ordinal allocated, bounding that run
    long _residentBytes;               // cached mode: what the cache holds
    long _maxCacheBytes;               // cached mode: the eviction budget
    long _evictRetryAt;                // cached mode: no sweep until the cache grows past this

    /// <summary>A cached routing record. The Used bit is the second chance; races with the sweep are
    /// benign.</summary>
    sealed class Entry(byte[] data) {
        public readonly byte[] Data = data;
        public bool Used = true;
    }

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
    public bool Resident => _resident;
    /// <summary>Bytes held in memory: the arena in resident mode, the cache in cached mode.</summary>
    public long ResidentBytes => _resident ? (long)countChunks() * ChunkOrdinals * StrideBytes : Interlocked.Read(ref _residentBytes);
    int countChunks() {
        var n = 0;
        foreach (var c in _chunks) {
            if (c != null!) n++;
        }
        return n;
    }
    /// <summary>Cached mode's eviction budget; ignored (the arena holds everything) in resident mode.</summary>
    public long MaxCacheBytes {
        get { lock (_lock) return _maxCacheBytes; }
        set {
            lock (_lock) {
                _maxCacheBytes = value;
                _evictRetryAt = 0;
                if (!_resident) evict();
            }
        }
    }

    Hnsw2RoutingStore(FixedStrideFile file, int dimensions, int neighbourCapacity, bool resident, long maxCacheBytes) {
        _file = file;
        Dimensions = dimensions;
        NeighbourCapacity = neighbourCapacity;
        var qPad = (4 - (dimensions & 3)) & 3;
        _rescaleOffset = dimensions + qPad;
        _edgeOffset = _rescaleOffset + 4;
        _simsOffset = _edgeOffset + 4 + 4 * neighbourCapacity;
        EdgeWords = 1 + 2 * neighbourCapacity;
        StrideBytes = _edgeOffset + 4 * EdgeWords;
        _resident = resident;
        _maxCacheBytes = maxCacheBytes;
        if (!resident) _cache = new();
    }

    static int strideOf(int dimensions, int neighbourCapacity) =>
        dimensions + ((4 - (dimensions & 3)) & 3) + 4 + 4 * (1 + 2 * neighbourCapacity);

    public static Hnsw2RoutingStore Create(string path, long generation, int dimensions, int neighbourCapacity,
        bool resident, long maxCacheBytes) {
        var file = FixedStrideFile.Create(path, FileKind, generation, strideOf(dimensions, neighbourCapacity), [dimensions, neighbourCapacity], 0);
        return new(file, dimensions, neighbourCapacity, resident, maxCacheBytes);
    }
    /// <summary>Opens the committed records; in resident mode the whole file is mirrored into the
    /// arena on every core, which is a straight sequential read — no decoding, the disk layout is
    /// the memory layout.</summary>
    public static Hnsw2RoutingStore Open(string path, long generation, int dimensions, int neighbourCapacity,
        bool resident, long maxCacheBytes, int committedRecords, int maxThreads) {
        var file = FixedStrideFile.Open(path, FileKind, generation, strideOf(dimensions, neighbourCapacity), [dimensions, neighbourCapacity], committedRecords);
        var store = new Hnsw2RoutingStore(file, dimensions, neighbourCapacity, resident, maxCacheBytes);
        try {
            store.ensureCapacity(committedRecords - 1);
            store._nextOrdinal = committedRecords;
            if (resident && committedRecords > 0) store.mirrorFromFile(committedRecords, maxThreads);
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

    /// <summary>The record for an ordinal. Resident mode: two array reads, no locks, no residency
    /// check — the hot path of every walk. Cached mode: a dictionary probe, and a positional file
    /// read on a miss.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RoutingRef Get(int ordinal) {
        if (_resident) return new(_chunks[ordinal >> ChunkShift], (ordinal & ChunkMask) * StrideBytes);
        if (_cache!.TryGetValue(ordinal, out var hit)) {
            hit.Used = true;
            return new(hit.Data, 0);
        }
        return getSlow(ordinal);
    }
    RoutingRef getSlow(int ordinal) {
        var loaded = readEntry(ordinal);
        lock (_lock) {
            if (_cache!.TryGetValue(ordinal, out var raced)) return new(raced.Data, 0); // loaded first; identical
            _cache[ordinal] = loaded;
            _residentBytes += StrideBytes + entryOverhead;
            evict();
        }
        return new(loaded.Data, 0);
    }
    Entry readEntry(int ordinal) { // reads outside the lock; positional
        var data = new byte[StrideBytes];
        _file.Read(ordinal, data);
        lock (_lock) applyOverlay(ordinal, data);
        return new(data);
    }
    /// <summary>True when <see cref="Get"/> would not touch the disk. Always true in resident mode.</summary>
    public bool IsResident(int ordinal) => _resident || _cache!.ContainsKey(ordinal);
    /// <summary>Hints the record's int8 vector into the CPU cache; resident mode only, where the
    /// address is known without any lookup. The walk calls this for a hop's whole neighbour list
    /// while scoring, which hides most of the RAM latency of the next hops.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrefetchQ(int ordinal) {
        var chunk = _chunks[ordinal >> ChunkShift];
        var offset = (ordinal & ChunkMask) * StrideBytes;
        VectorMath.Prefetch(chunk, offset);
        if (Dimensions > 64) VectorMath.Prefetch(chunk, offset + 64);
    }
    /// <summary>Loads a batch of records concurrently (cached mode), so the disk latency of one graph
    /// hop's neighbours is paid once rather than once per neighbour. The caller passes only ordinals
    /// it found non-resident; the re-check inside the fan-out just skips one a racing search loaded
    /// meanwhile.</summary>
    public void Prefetch(List<int> ordinals, int maxThreads) {
        if (_resident || ordinals.Count < 4 || maxThreads <= 2 || Environment.ProcessorCount <= 2) return;
        Parallel.ForEach(ordinals, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, ordinal => {
            if (!IsResident(ordinal)) Get(ordinal);
        });
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
            return count * (StrideBytes + entryOverhead);
        }
    }
    /// <summary>True while a record's only correct copy is the one in memory, which is what keeps the
    /// cached mode's evictor from dropping it.</summary>
    public bool IsDirty(int ordinal) => (_appendedFrom >= 0 && ordinal >= _appendedFrom) || _dirtyEdges.Contains(ordinal);
    /// <summary>A record for a freshly allocated ordinal: the vector quantized straight into place,
    /// an empty neighbour list, resident and dirty from the start — an allocated-but-unflushed
    /// ordinal is never read from the file. Returns the record so the caller can link from it.</summary>
    public RoutingRef Allocate(int ordinal, ReadOnlySpan<float> vector) {
        ensureCapacity(ordinal);
        RoutingRef r;
        if (_resident) {
            ensureChunk(ordinal);
            r = new(_chunks[ordinal >> ChunkShift], (ordinal & ChunkMask) * StrideBytes);
        } else {
            r = new(new byte[StrideBytes], 0);
        }
        VectorMath.Quantize(vector, MemoryMarshal.Cast<byte, sbyte>(r.Data.AsSpan(r.Offset, Dimensions)), out var rescale);
        var qPadStart = r.Offset + Dimensions;
        r.Data.AsSpan(qPadStart, _rescaleOffset - Dimensions).Clear(); // deterministic padding bytes
        Unsafe.WriteUnaligned(ref r.Data[r.Offset + _rescaleOffset], rescale);
        r.Data.AsSpan(r.Offset + _edgeOffset, 4 * EdgeWords).Clear(); // no neighbours yet
        lock (_lock) {
            if (!_resident) {
                _cache![ordinal] = new(r.Data);
                _residentBytes += StrideBytes + entryOverhead;
            }
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
        byte[] data;
        int offset;
        if (_resident) {
            data = _chunks[ordinal >> ChunkShift];
            offset = (ordinal & ChunkMask) * StrideBytes;
            if (_appendedFrom < 0 || ordinal < _appendedFrom) {
                lock (_lock) _dirtyEdges.Add(ordinal);
            }
        } else {
            var entry = getForWrite(ordinal);
            data = entry.Data;
            offset = 0;
        }
        Unsafe.WriteUnaligned(ref data[offset + _edgeOffset], ids.Length);
        ids.CopyTo(MemoryMarshal.Cast<byte, int>(data.AsSpan(offset + _edgeOffset + 4, ids.Length * 4)));
        sims.CopyTo(MemoryMarshal.Cast<byte, float>(data.AsSpan(offset + _simsOffset, sims.Length * 4)));
    }
    /// <summary>Cached mode: the record's entry with the record pinned dirty until the next flush,
    /// loading it if needed.</summary>
    Entry getForWrite(int ordinal) {
        lock (_lock) {
            Entry? entry = _cache!.TryGetValue(ordinal, out var hit) ? hit : null;
            if (entry == null) {
                var data = new byte[StrideBytes];
                _file.Read(ordinal, data);
                applyOverlay(ordinal, data);
                ensureCapacity(ordinal);
                entry = new(data);
                _cache[ordinal] = entry;
                _residentBytes += StrideBytes + entryOverhead;
            }
            entry.Used = true;
            if (_appendedFrom < 0 || ordinal < _appendedFrom) _dirtyEdges.Add(ordinal);
            if (ordinal >= _nextOrdinal) _nextOrdinal = ordinal + 1;
            return entry;
        }
    }

    /// <summary>
    /// Writes every change out. New records are always the highest ordinals, so they go to the
    /// routing file as a few large sequential writes however many there are. Rewrites are scattered
    /// and only ever touched the edge region: with an <paramref name="edgeLog"/> they are appended to
    /// it instead — one sequential write for the batch, which is what makes a WAL-flush checkpoint
    /// cheap — and without one they go straight into the routing file, which is what a state save
    /// does to make the log droppable.
    /// <para>Does not fsync; <see cref="Fsync"/> is the durable point. Flushed records stay in
    /// memory, in cached mode now unpinned and evictable.</para>
    /// </summary>
    public void FlushDirty(Hnsw2EdgeLog? edgeLog) {
        if (_dirtyEdges.Count > 0) {
            var rewritten = new int[_dirtyEdges.Count];
            _dirtyEdges.CopyTo(rewritten);
            Array.Sort(rewritten);
            foreach (var ordinal in rewritten) {
                var r = residentRef(ordinal);
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
            _evictRetryAt = 0; // the unpinned records make a sweep worth trying again
            if (!_resident) evict();
        }
    }
    /// <summary>Writes every edge region the edge log is carrying into the routing file, so the log
    /// can be dropped. This is the scattered in-place work the log exists to keep off the WAL-flush
    /// path. In resident mode the region always comes from the arena; in cached mode from the entry
    /// when it is still held, else from the overlay its eviction parked the region in.</summary>
    public void ConsolidateBehind() {
        for (var ordinal = 0; ordinal < _nextOrdinal; ordinal++) {
            if (!isBehind(ordinal)) continue;
            ReadOnlySpan<int> region;
            if (_resident) {
                var r = residentRef(ordinal);
                region = MemoryMarshal.Cast<byte, int>(r.Data.AsSpan(r.Offset + _edgeOffset, 4 * EdgeWords));
            } else if (_cache!.TryGetValue(ordinal, out var entry)) {
                region = MemoryMarshal.Cast<byte, int>(entry.Data.AsSpan(_edgeOffset, 4 * EdgeWords));
            } else if (_overlay.TryGetValue(ordinal, out var kept)) {
                region = kept;
            } else {
                throw new InvalidOperationException("A record the routing file is behind on is neither in memory nor in the overlay. ");
            }
            _file.WriteWithin(ordinal, _edgeOffset, MemoryMarshal.AsBytes(region));
        }
        lock (_lock) {
            _overlay.Clear();
            Array.Clear(_behind);
        }
    }
    /// <summary>Restores what the durable edge log says at open: those records' file copies are stale.
    /// In resident mode the regions are written straight onto the mirrored arena; in cached mode they
    /// go into the overlay, to be stamped onto any record read from the file. Entries come in write
    /// order, so a later region for the same node simply replaces an earlier one.</summary>
    public void LoadOverlay(IEnumerable<(int ordinal, int[] region)> entries) {
        foreach (var (ordinal, region) in entries) {
            if (ordinal >= _nextOrdinal) continue; // past the commit boundary: uncommitted scratch
            ensureCapacity(ordinal);
            if (_resident) {
                var r = residentRef(ordinal);
                region.CopyTo(MemoryMarshal.Cast<byte, int>(r.Data.AsSpan(r.Offset + _edgeOffset, 4 * EdgeWords)));
            } else {
                _overlay[ordinal] = region;
            }
            setBehind(ordinal);
        }
    }
    void applyOverlay(int ordinal, byte[] data) { // must hold _lock
        if (_overlay.Count == 0 || !_overlay.TryGetValue(ordinal, out var region)) return;
        region.CopyTo(MemoryMarshal.Cast<byte, int>(data.AsSpan(_edgeOffset, 4 * EdgeWords)));
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
    RoutingRef residentRef(int ordinal) {
        if (_resident) return new(_chunks[ordinal >> ChunkShift], (ordinal & ChunkMask) * StrideBytes);
        if (_cache!.TryGetValue(ordinal, out var entry)) return new(entry.Data, 0);
        throw new InvalidOperationException("A record marked dirty is no longer in memory. ");
    }
    byte[] _flushBuffer = [];
    void writeFullRun(int firstOrdinal, int count) {
        if (_resident) {
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
            return;
        }
        if (count == 1) {
            _file.Write(firstOrdinal, residentRef(firstOrdinal).Data);
            return;
        }
        if (_flushBuffer.Length < count * StrideBytes) _flushBuffer = new byte[count * StrideBytes];
        for (var i = 0; i < count; i++) residentRef(firstOrdinal + i).Data.CopyTo(_flushBuffer.AsSpan(i * StrideBytes));
        _file.Write(firstOrdinal, _flushBuffer.AsSpan(0, count * StrideBytes));
    }
    public void Fsync() => _file.Fsync();
    /// <summary>Cached mode: drops every clean record. Resident mode keeps the arena — it is the
    /// index, not a cache over it.</summary>
    public void ClearCache() {
        if (_resident) return;
        lock (_lock) {
            foreach (var kv in _cache!) {
                if (IsDirty(kv.Key)) continue;
                dropEntry(kv.Key, kv.Value.Data);
            }
        }
    }

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
    /// <summary>Cached mode's eviction: a second-chance sweep over the (small) entry set, down to 90%
    /// of the budget rather than just below it — a sweep costs a walk over the whole entry set, so it
    /// has to buy headroom for the next thousand admissions rather than one. When a sweep cannot
    /// reach the floor (the pinned dirty records plus the entries walks are actively using can exceed
    /// it), back off until the cache has grown by another slice of the budget, or the failed sweep
    /// itself lands on every admission and the index stops making progress. The budget is a target
    /// the unevictable set may exceed; what bounds that set is the memtable flush threshold.</summary>
    void evict() { // must hold _lock; cached mode only
        if (_residentBytes <= _maxCacheBytes) return;
        if (_residentBytes < _evictRetryAt) return;
        var floor = _maxCacheBytes - _maxCacheBytes / 10;
        for (var pass = 0; pass < 2 && _residentBytes > floor; pass++) {
            foreach (var kv in _cache!) {
                if (_residentBytes <= floor) break;
                if (kv.Value.Used) {
                    kv.Value.Used = false;
                    continue;
                }
                if (IsDirty(kv.Key)) continue;
                dropEntry(kv.Key, kv.Value.Data);
            }
        }
        _evictRetryAt = _residentBytes > floor ? _residentBytes + _maxCacheBytes / 20 : 0;
    }
    /// <summary>Removes a clean record from the cache. When the routing file is behind on its edges,
    /// the region is kept in the overlay first — otherwise this would be the moment the only correct
    /// copy of it disappeared.</summary>
    void dropEntry(int ordinal, byte[] data) { // must hold _lock
        if (isBehind(ordinal)) _overlay[ordinal] = MemoryMarshal.Cast<byte, int>(data.AsSpan(_edgeOffset, 4 * EdgeWords)).ToArray();
        if (_cache!.TryRemove(ordinal, out _)) _residentBytes -= StrideBytes + entryOverhead;
    }
    public void Dispose() {
        lock (_lock) {
            _chunks = [];
            _behind = [];
            _cache?.Clear();
            _dirtyEdges.Clear();
            _overlay.Clear();
            _residentBytes = 0;
        }
        _file.Dispose();
    }
}
