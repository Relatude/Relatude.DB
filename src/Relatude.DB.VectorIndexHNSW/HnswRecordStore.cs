using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Relatude.DB.VectorIndexHNSW;

/// <summary>
/// Where the vectors live: one fixed-size record per ordinal holding a node's vector <i>and</i> its
/// layer-0 neighbour list, so a single positional read gives a search everything it needs to score
/// the node and to keep walking from it. That co-location is the whole point of the layout — a graph
/// search is a chain of dependent random accesses, and paying two reads per hop instead of one would
/// double the latency that dominates it.
///
/// <para>A record is a flat array of 4-byte words:
/// <code>[vector: Dimensions floats][neighbour count][ids: NeighbourCapacity ints][sims: NeighbourCapacity floats]</code>
/// read straight off the disk into one array, with the vector, the neighbour list and the stored
/// edge similarities taken as spans of it. Nothing is decoded and nothing is copied. The order
/// matters twice over: the vector comes first so scoring a record needs no offset arithmetic, and
/// the whole mutable part — count, ids and sims — is one contiguous region at the end, so an insert
/// rewrites a couple of hundred bytes, not the whole multi-kilobyte record. The similarity of each
/// edge is stored beside it because the linker needs exactly those numbers: with them, adding a
/// back-edge to a non-full node costs no vector reads at all, and a full node can reject a
/// challenger against its worst edge before loading anything.</para>
///
/// <para><b>The cache.</b> Records in memory are held as <see cref="RecordEntry"/> objects — the
/// record plus an int8 copy of its vector, quantized once at admission, which is what the walk
/// scores against (see <see cref="VectorMath.DotQ"/>). In the default mode the entries sit in an
/// array indexed by ordinal, so a hit is one array read; eviction is a CLOCK sweep over that array.
/// In low-memory mode the entries sit in a keyed map holding only what is cached, so an empty cache
/// costs nothing per ordinal; eviction is a second-chance sweep over the (small) entry set, with
/// hysteresis so the sweep is paid once per batch of admissions rather than per miss.</para>
///
/// <para><b>Unwritten records.</b> Changed records are marked dirty, which pins them in memory until
/// the next flush. <see cref="DirtyBytes"/> is what the index watches to spill during a bulk load.
/// Newly allocated ordinals are always the highest ones, so they need no bookkeeping beyond where the
/// run started; the set holds only rewrites of older records, which is what an insert does to the
/// neighbour lists of the nodes it attaches to.</para>
/// </summary>
internal sealed class HnswRecordStore : IDisposable {
    internal const int FileKind = 1;
    const long entryOverhead = 96; // entry object, two array headers, the slot — roughly

    readonly FixedStrideFile _file;
    readonly object _lock = new();     // guards residency changes and the dirty marks
    readonly HashSet<int> _dirtySet = []; // rewritten records, pinned until flushed
    // A record whose neighbour list went to the edge log has a stale edge region in the graph file. The
    // bitmap says which — one bit per vector, so tracking it costs nothing at any scale — and the
    // overlay carries the actual region for those of them that have since been evicted, which are the
    // only ones whose correct copy would otherwise be nowhere in memory.
    readonly Dictionary<int, int[]> _overlay = [];
    readonly long _recordBytes;
    readonly bool _lowMemory;
    readonly ConcurrentDictionary<int, RecordEntry>? _cache; // low-memory residency: entries only for what is held
    RecordEntry?[] _resident = [];     // by ordinal: the entry, or null when not in memory; unused in low-memory mode
    ulong[] _behind = [];              // by ordinal: the graph file's edge region is out of date
    int _appendedFrom = -1;            // start of the run of new ordinals not yet flushed
    int _nextOrdinal;                  // one past the highest ordinal allocated, bounding that run
    long _residentBytes;
    long _maxBytes;
    long _evictRetryAt;                // low-memory mode: no sweep until the cache grows past this
    int _hand;                         // the CLOCK sweep position

    /// <summary>A cached record and the scoring form of its vector. The quantized copy is computed
    /// once at admission — vectors are immutable per ordinal, only the edge region mutates — so it
    /// stays valid for the entry's whole residency.</summary>
    internal sealed class RecordEntry {
        public readonly float[] Record;
        public readonly sbyte[] Q;
        public readonly float Rescale;
        public bool Used = true; // the second-chance bit; races with the sweep are benign

        public RecordEntry(float[] record, int dimensions) {
            Record = record;
            Q = new sbyte[dimensions];
            VectorMath.Quantize(record.AsSpan(0, dimensions), Q, out Rescale);
        }
    }

    public int Dimensions { get; }
    /// <summary>Slots for layer-0 neighbours in every record (the graph's layer-0 degree).</summary>
    public int NeighbourCapacity { get; }
    /// <summary>4-byte words per record: the vector, the count, the ids and the sims.</summary>
    public int Words { get; }
    /// <summary>Words of the mutable tail: the count, the ids and the sims.</summary>
    public int EdgeRegionWords { get; }
    public int StrideBytes => Words * 4;
    public string Path => _file.Path;
    public long FileLength => _file.FileLength;
    /// <summary>How many whole records the file holds. A record above this is one that only exists in
    /// memory, so a sequential scan has to take it from there instead.</summary>
    public long RecordCapacity => _file.RecordCapacity;
    public long MaxCacheBytes {
        get { lock (_lock) return _maxBytes; }
        set {
            lock (_lock) {
                _maxBytes = value;
                _evictRetryAt = 0;
                evict();
            }
        }
    }

    HnswRecordStore(FixedStrideFile file, int dimensions, int neighbourCapacity, long maxCacheBytes, bool lowMemory) {
        _file = file;
        Dimensions = dimensions;
        NeighbourCapacity = neighbourCapacity;
        EdgeRegionWords = 1 + 2 * neighbourCapacity;
        Words = dimensions + EdgeRegionWords;
        _recordBytes = (long)Words * 4 + dimensions + entryOverhead; // the record, its int8 copy, the bookkeeping
        _maxBytes = maxCacheBytes;
        _lowMemory = lowMemory;
        if (lowMemory) _cache = new();
    }

    public static HnswRecordStore Create(string path, long generation, int dimensions, int neighbourCapacity, long maxCacheBytes, bool lowMemory) {
        var words = dimensions + 1 + 2 * neighbourCapacity;
        var file = FixedStrideFile.Create(path, FileKind, generation, words * 4, [dimensions, neighbourCapacity], 0);
        return new(file, dimensions, neighbourCapacity, maxCacheBytes, lowMemory);
    }
    public static HnswRecordStore Open(string path, long generation, int dimensions, int neighbourCapacity, long maxCacheBytes, bool lowMemory, int committedRecords) {
        var words = dimensions + 1 + 2 * neighbourCapacity;
        var file = FixedStrideFile.Open(path, FileKind, generation, words * 4, [dimensions, neighbourCapacity], committedRecords);
        var store = new HnswRecordStore(file, dimensions, neighbourCapacity, maxCacheBytes, lowMemory);
        store.ensureCapacity(committedRecords - 1);
        store._nextOrdinal = committedRecords;
        return store;
    }

    // ---- record accessors -------------------------------------------------------------------------

    /// <summary>The node's vector: the head of the record. Read-only — records are only mutated
    /// through <see cref="SetNeighbours"/>, and only in their edge region.</summary>
    public ReadOnlySpan<float> Vector(float[] record) => record.AsSpan(0, Dimensions);
    /// <summary>The node's layer-0 neighbour ordinals. The stored count is clamped to the capacity,
    /// so a count torn by a crash yields a short list instead of reading past the record.</summary>
    public ReadOnlySpan<int> Neighbours(float[] record) {
        var words = MemoryMarshal.Cast<float, int>(record.AsSpan());
        var count = Math.Clamp(words[Dimensions], 0, NeighbourCapacity);
        return words.Slice(Dimensions + 1, count);
    }
    /// <summary>The stored similarity of each layer-0 edge, aligned with <see cref="Neighbours"/>.</summary>
    public ReadOnlySpan<float> NeighbourSims(float[] record) {
        var words = MemoryMarshal.Cast<float, int>(record.AsSpan());
        var count = Math.Clamp(words[Dimensions], 0, NeighbourCapacity);
        return record.AsSpan(Dimensions + 1 + NeighbourCapacity, count);
    }
    /// <summary>Rewrites a record's edge region: the ids and, beside them, their similarities to the
    /// record's own vector — the numbers the linker consults before it loads anything.</summary>
    public void SetNeighbours(int ordinal, ReadOnlySpan<int> ids, ReadOnlySpan<float> sims) {
        if (ids.Length > NeighbourCapacity) throw new ArgumentException("More neighbours than the record has slots for. ");
        var record = GetForWrite(ordinal).Record;
        var words = MemoryMarshal.Cast<float, int>(record.AsSpan());
        words[Dimensions] = ids.Length;
        ids.CopyTo(words.Slice(Dimensions + 1, ids.Length));
        sims.CopyTo(record.AsSpan(Dimensions + 1 + NeighbourCapacity, sims.Length));
    }
    /// <summary>Where in a record the region <see cref="SetNeighbours"/> can change starts.</summary>
    int edgeRegionOffset => Dimensions * 4;
    ReadOnlySpan<int> edgeRegion(float[] record) =>
        MemoryMarshal.Cast<float, int>(record.AsSpan(Dimensions, EdgeRegionWords));

    // ---- reads ------------------------------------------------------------------------------------

    /// <summary>The record for an ordinal, from memory when it is there and from the file when it is
    /// not. The hit path is a single array read (a hash probe in low-memory mode); only a miss takes
    /// the lock.</summary>
    public RecordEntry Get(int ordinal) {
        if (_lowMemory) {
            if (_cache!.TryGetValue(ordinal, out var hit)) {
                hit.Used = true;
                return hit;
            }
            var loaded = readEntry(ordinal);
            lock (_lock) {
                if (_cache.TryGetValue(ordinal, out var raced)) return raced; // loaded first; identical
                _cache[ordinal] = loaded;
                _residentBytes += _recordBytes;
                evict();
            }
            return loaded;
        }
        var resident = _resident;
        if ((uint)ordinal < (uint)resident.Length) {
            var hit = resident[ordinal];
            if (hit != null) {
                hit.Used = true;
                return hit;
            }
        }
        var entry = readEntry(ordinal);
        lock (_lock) {
            if ((uint)ordinal >= (uint)_resident.Length) return entry; // grown away under us; still valid data
            var raced = _resident[ordinal];
            if (raced != null) return raced; // another thread loaded it first; the copies are identical
            _resident[ordinal] = entry;
            _residentBytes += _recordBytes;
            evict();
        }
        return entry;
    }
    RecordEntry readEntry(int ordinal) { // reads outside the lock; positional
        var record = new float[Words];
        _file.Read(ordinal, MemoryMarshal.AsBytes(record.AsSpan()));
        lock (_lock) applyOverlay(ordinal, record);
        return new(record, Dimensions);
    }
    /// <summary>True when <see cref="Get"/> would not touch the disk. Used to decide whether a batch
    /// of neighbours is worth loading in parallel.</summary>
    public bool IsResident(int ordinal) {
        if (_lowMemory) return _cache!.ContainsKey(ordinal);
        var resident = _resident;
        return (uint)ordinal < (uint)resident.Length && resident[ordinal] != null;
    }
    /// <summary>The record if it happens to be in memory, without claiming it was used. An exact scan
    /// reads every record exactly once, so counting those touches as recency would evict the working
    /// set of the graph walks in favour of records nothing will ask for again.</summary>
    public bool TryPeek(int ordinal, out float[] record) {
        var entry = peekEntry(ordinal);
        if (entry != null) {
            record = entry.Record;
            return true;
        }
        record = [];
        return false;
    }
    RecordEntry? peekEntry(int ordinal) {
        if (_lowMemory) return _cache!.TryGetValue(ordinal, out var e) ? e : null;
        var resident = _resident;
        return (uint)ordinal < (uint)resident.Length ? resident[ordinal] : null;
    }
    /// <summary>Ordinals at or above this exist only in memory — allocated after the last flush, so
    /// the graph file holds nothing for them yet. <see cref="int.MaxValue"/> when everything
    /// allocated has been written. Stable while the caller holds the index lock, which every scan
    /// does.</summary>
    public int FirstUnflushedOrdinal => _appendedFrom < 0 ? int.MaxValue : _appendedFrom;
    /// <summary>Loads a batch of records concurrently, so the disk latency of one graph hop's
    /// neighbours is paid once rather than once per neighbour. The caller passes only ordinals it
    /// found non-resident; the re-check inside the fan-out just skips one a racing search loaded
    /// meanwhile. Called with the index's read or write lock held, which is what makes the residency
    /// table the only shared state being touched.</summary>
    public void Prefetch(List<int> ordinals) {
        if (ordinals.Count < 4 || Environment.ProcessorCount <= 2) return; // not worth the fan-out
        Parallel.ForEach(ordinals, ordinal => {
            if (!IsResident(ordinal)) Get(ordinal);
        });
    }
    /// <summary>Reads a run of records straight into the caller's buffer, bypassing the residency
    /// table. An exact scan uses this: it wants a few large sequential reads, and admitting every
    /// record it touches would evict exactly the records the graph walks need.</summary>
    public void ReadRange(int firstOrdinal, int count, Span<float> target) =>
        _file.Read(firstOrdinal, MemoryMarshal.AsBytes(target[..(count * Words)]));

    // ---- writes ------------------------------------------------------------------------------------

    public long DirtyBytes {
        get {
            var count = (long)_dirtySet.Count + (_appendedFrom < 0 ? 0 : _nextOrdinal - _appendedFrom);
            return count * _recordBytes;
        }
    }
    /// <summary>True while a record's only correct copy is the one in memory, which is what keeps the
    /// evictor from dropping it.</summary>
    public bool IsDirty(int ordinal) => (_appendedFrom >= 0 && ordinal >= _appendedFrom) || _dirtySet.Contains(ordinal);
    /// <summary>A record for a freshly allocated ordinal carrying the given vector, resident and
    /// dirty from the start — an allocated-but-unflushed ordinal is never read from the file.</summary>
    public RecordEntry Allocate(int ordinal, ReadOnlySpan<float> vector) {
        ensureCapacity(ordinal);
        var record = new float[Words];
        vector.CopyTo(record.AsSpan(0, Dimensions));
        var entry = new RecordEntry(record, Dimensions);
        lock (_lock) {
            admit(ordinal, entry);
            _residentBytes += _recordBytes;
            if (_appendedFrom < 0) _appendedFrom = ordinal;
            if (ordinal >= _nextOrdinal) _nextOrdinal = ordinal + 1;
        }
        return entry;
    }
    /// <summary>The record's entry with the record pinned dirty until the next flush, loading it if
    /// needed. Only the edge region may be mutated through it.</summary>
    public RecordEntry GetForWrite(int ordinal) {
        lock (_lock) {
            var entry = residentOrNull(ordinal, markUsed: true);
            if (entry == null) {
                var record = new float[Words];
                _file.Read(ordinal, MemoryMarshal.AsBytes(record.AsSpan()));
                applyOverlay(ordinal, record);
                ensureCapacity(ordinal);
                entry = new(record, Dimensions);
                admit(ordinal, entry);
                _residentBytes += _recordBytes;
            }
            if (_appendedFrom < 0 || ordinal < _appendedFrom) _dirtySet.Add(ordinal);
            if (ordinal >= _nextOrdinal) _nextOrdinal = ordinal + 1;
            return entry;
        }
    }
    /// <summary>The entry when it is in memory, else null. Mutating callers hold <see cref="_lock"/>;
    /// flush-path callers run under the index's write lock, which excludes every mutator.</summary>
    RecordEntry? residentOrNull(int ordinal, bool markUsed) {
        var entry = peekEntry(ordinal);
        if (entry != null && markUsed) entry.Used = true;
        return entry;
    }
    void admit(int ordinal, RecordEntry entry) { // must hold _lock; capacity already ensured
        if (_lowMemory) _cache![ordinal] = entry;
        else _resident[ordinal] = entry;
    }
    /// <summary>
    /// Writes every change out. New records are always the highest ordinals, so they go to the graph
    /// file as a few large sequential writes however many there are. Rewrites are scattered and only
    /// ever touched the edge region: with an <paramref name="edgeLog"/> they are appended to it
    /// instead — one sequential write for the batch, which is what makes a WAL-flush checkpoint cheap —
    /// and without one they go straight into the graph file, which is what a state save does to make
    /// the log droppable. Runs of consecutive ordinals are written whole either way, where one bigger
    /// write beats several small ones.
    /// <para>Does not fsync; <see cref="Fsync"/> is the durable point. Flushed records stay in memory,
    /// now unpinned and evictable.</para>
    /// </summary>
    public void FlushDirty(HnswEdgeLog? edgeLog) {
        if (_dirtySet.Count > 0) {
            var rewritten = new int[_dirtySet.Count];
            _dirtySet.CopyTo(rewritten);
            Array.Sort(rewritten);
            foreach (var ordinal in rewritten) {
                var record = recordToFlush(ordinal);
                if (edgeLog != null) {
                    edgeLog.Append(ordinal, edgeRegion(record));
                    setBehind(ordinal); // the graph file's edge region for this record is now stale
                } else {
                    _file.WriteWithin(ordinal, edgeRegionOffset,
                        MemoryMarshal.AsBytes(record.AsSpan(Dimensions, EdgeRegionWords)));
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
            _dirtySet.Clear();
            _appendedFrom = -1;
            _evictRetryAt = 0; // the unpinned records make a sweep worth trying again
            evict();
        }
    }
    /// <summary>Writes every edge region the edge log is carrying into the graph file, so the log can
    /// be dropped. This is the scattered in-place work the log exists to keep off the WAL-flush path.
    /// A record still in memory is written from there; one that has been evicted since, from the
    /// overlay that its eviction put its region into.</summary>
    public void ConsolidateBehind() {
        for (var ordinal = 0; ordinal < _nextOrdinal; ordinal++) {
            if (!isBehind(ordinal)) continue;
            var entry = residentOrNull(ordinal, markUsed: false);
            ReadOnlySpan<int> region = entry != null ? edgeRegion(entry.Record)
                : _overlay.TryGetValue(ordinal, out var kept) ? kept
                : throw new InvalidOperationException("A record the graph file is behind on is neither in memory nor in the overlay. ");
            _file.WriteWithin(ordinal, edgeRegionOffset, MemoryMarshal.AsBytes(region));
        }
        lock (_lock) {
            _overlay.Clear();
            Array.Clear(_behind);
        }
    }
    /// <summary>Restores what the durable edge log says at open: those records' file copies are stale, and
    /// none of them is in memory yet, so every entry goes into the overlay. Entries come in write order,
    /// so a later region for the same node simply replaces an earlier one.</summary>
    public void LoadOverlay(IEnumerable<(int ordinal, int[] region)> entries) {
        foreach (var (ordinal, region) in entries) {
            if (ordinal >= _nextOrdinal) continue; // past the commit boundary: uncommitted scratch
            ensureCapacity(ordinal);
            _overlay[ordinal] = region;
            setBehind(ordinal);
        }
    }
    void applyOverlay(int ordinal, float[] record) { // must hold _lock
        if (_overlay.Count == 0 || !_overlay.TryGetValue(ordinal, out var region)) return;
        region.CopyTo(MemoryMarshal.Cast<float, int>(record.AsSpan(Dimensions, EdgeRegionWords)));
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
    float[] _flushBuffer = [];
    void writeFullRun(int firstOrdinal, int count) {
        if (count == 1) {
            _file.Write(firstOrdinal, MemoryMarshal.AsBytes(recordToFlush(firstOrdinal).AsSpan()));
            return;
        }
        if (_flushBuffer.Length < count * Words) _flushBuffer = new float[count * Words];
        for (var i = 0; i < count; i++) recordToFlush(firstOrdinal + i).CopyTo(_flushBuffer, i * Words);
        _file.Write(firstOrdinal, MemoryMarshal.AsBytes(_flushBuffer.AsSpan(0, count * Words)));
    }
    float[] recordToFlush(int ordinal) =>
        (residentOrNull(ordinal, markUsed: false)
            ?? throw new InvalidOperationException("A record marked dirty is no longer in memory. ")).Record;
    public void Fsync() => _file.Fsync();
    public void ClearCache() {
        lock (_lock) {
            if (_lowMemory) {
                foreach (var kv in _cache!) {
                    if (IsDirty(kv.Key)) continue;
                    dropEntry(kv.Key, kv.Value.Record);
                }
                return;
            }
            for (var ordinal = 0; ordinal < _resident.Length; ordinal++) {
                if (IsDirty(ordinal)) continue;
                drop(ordinal);
            }
        }
    }

    void ensureCapacity(int ordinal) { // callers hold either the index write lock or _lock
        if (_lowMemory) { // residency is per-entry; only the behind bitmap tracks every ordinal
            var words = (ordinal >> 6) + 1;
            if (words <= _behind.Length) return;
            var size = Math.Max(16, _behind.Length * 2);
            while (size < words) size *= 2;
            Array.Resize(ref _behind, size);
            return;
        }
        if (ordinal < _resident.Length) return;
        var size2 = Math.Max(1024, _resident.Length * 2);
        while (size2 <= ordinal) size2 *= 2;
        var resident = new RecordEntry?[size2];
        var behind = new ulong[(size2 + 63) / 64];
        Array.Copy(_resident, resident, _resident.Length);
        Array.Copy(_behind, behind, _behind.Length);
        _behind = behind;
        _resident = resident; // published last: a racing reader sees either table, both consistent
    }
    /// <summary>CLOCK: sweep forward giving every record one second chance — a record whose used bit
    /// is set keeps its place and loses the bit, one without it is dropped. Dirty records are never
    /// dropped, so a cache budget smaller than the unflushed set is exceeded rather than enforced;
    /// what bounds that is the memtable flush threshold. Low-memory mode makes the same bargain by
    /// sweeping the (small) entry set instead of an array over every ordinal.</summary>
    void evict() { // must hold _lock
        if (_residentBytes <= _maxBytes) return;
        if (_lowMemory) {
            // Evict a batch, down to 90% of the budget, not just below it: a sweep costs a walk over
            // the whole entry set, so it has to buy headroom for the next thousand admissions rather
            // than one — evicting to the budget exactly would put that walk on every cache miss.
            // And when a sweep CANNOT reach the floor — the pinned dirty records plus the entries a
            // walk is actively using can exceed it — back off until the cache has grown by another
            // slice of the budget, or the failed sweep itself lands on every admission and the index
            // stops making progress. The budget is a target the unevictable set may exceed; what
            // bounds that set is the memtable flush threshold.
            if (_residentBytes < _evictRetryAt) return;
            var floor = _maxBytes - _maxBytes / 10;
            for (var pass = 0; pass < 2 && _residentBytes > floor; pass++) {
                foreach (var kv in _cache!) {
                    if (_residentBytes <= floor) break;
                    if (kv.Value.Used) {
                        kv.Value.Used = false;
                        continue;
                    }
                    if (IsDirty(kv.Key)) continue;
                    dropEntry(kv.Key, kv.Value.Record);
                }
            }
            _evictRetryAt = _residentBytes > floor ? _residentBytes + _maxBytes / 20 : 0;
            return;
        }
        var n = _resident.Length;
        if (n == 0) return;
        var limit = n * 2L;
        for (var scanned = 0L; scanned < limit && _residentBytes > _maxBytes; scanned++) {
            var ordinal = _hand;
            _hand = _hand + 1 >= n ? 0 : _hand + 1;
            var entry = _resident[ordinal];
            if (entry == null) continue;
            if (entry.Used) {
                entry.Used = false;
                continue;
            }
            if (IsDirty(ordinal)) continue;
            drop(ordinal);
        }
    }
    /// <summary>Low-memory mode's <see cref="drop"/>: same overlay rule, keyed removal.</summary>
    void dropEntry(int ordinal, float[] record) { // must hold _lock
        if (isBehind(ordinal)) _overlay[ordinal] = edgeRegion(record).ToArray();
        if (_cache!.TryRemove(ordinal, out _)) _residentBytes -= _recordBytes;
    }
    /// <summary>Removes a clean record from memory. When the graph file is behind on its edges, the
    /// region is kept in the overlay first — otherwise this would be the moment the only correct copy
    /// of it disappeared.</summary>
    void drop(int ordinal) { // must hold _lock
        var entry = _resident[ordinal];
        if (entry == null) return;
        if (isBehind(ordinal)) _overlay[ordinal] = edgeRegion(entry.Record).ToArray();
        _resident[ordinal] = null;
        _residentBytes -= _recordBytes;
    }
    public void Dispose() {
        lock (_lock) {
            _resident = [];
            _behind = [];
            _cache?.Clear();
            _dirtySet.Clear();
            _overlay.Clear();
            _residentBytes = 0;
        }
        _file.Dispose();
    }
}
