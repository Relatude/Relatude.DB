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
/// <code>[vector: Dimensions floats][neighbour count][neighbour ordinals: NeighbourCapacity ints]</code>
/// read straight off the disk into one array, with the vector and the neighbour list taken as spans of
/// it. Nothing is decoded and nothing is copied. The order matters twice over: the vector comes first
/// so scoring a record needs no offset arithmetic, and the count sits with the neighbour list so the
/// only part an insert ever rewrites is one contiguous region at the end — a hundred-odd bytes, not the
/// whole multi-kilobyte record. That is what keeps the write amplification of the back-edge updates
/// (see <see cref="HnswGraph"/>) from dominating every durable checkpoint.</para>
///
/// <para><b>The cache.</b> Records in memory are held in an array indexed by ordinal, so a hit is one
/// array read and nothing else — no hash lookup, no lock, no list to reorder. That matters more here
/// than it would for a scanning index: a graph walk does a few thousand of these lookups per query
/// and each one is followed by a single dot product, so bookkeeping per lookup competes directly with
/// the arithmetic it exists to save. Eviction is a CLOCK sweep — a per-record "used recently" bit that
/// a hit sets and the sweep clears — which approximates LRU while keeping the read path free of any
/// write the other threads would have to see. The array costs 8 bytes per vector, in the same class as
/// the node table.</para>
///
/// <para><b>Unwritten records.</b> Changed records are marked dirty, which pins them in memory until
/// the next flush. <see cref="DirtyBytes"/> is what the index watches to spill during a bulk load.
/// Newly allocated ordinals are always the highest ones, so they need no bookkeeping beyond where the
/// run started; the set holds only rewrites of older records, which is what an insert does to the
/// neighbour lists of the nodes it attaches to.</para>
/// </summary>
internal sealed class HnswRecordStore : IDisposable {
    internal const int FileKind = 1;
    const long entryOverhead = 24; // the resident slot and its used bit, roughly

    readonly FixedStrideFile _file;
    readonly object _lock = new();     // guards residency changes and the dirty marks
    readonly HashSet<int> _dirtySet = []; // rewritten records, pinned until flushed
    // A record whose neighbour list went to the edge log has a stale edge region in the graph file. The
    // bitmap says which — one bit per vector, so tracking it costs nothing at any scale — and the
    // overlay carries the actual list for those of them that have since been evicted, which are the only
    // ones whose correct copy would otherwise be nowhere in memory.
    readonly Dictionary<int, int[]> _overlay = [];
    readonly long _recordBytes;
    float[]?[] _resident = [];         // by ordinal: the record, or null when not in memory
    byte[] _used = [];                 // by ordinal: the CLOCK bit a hit sets
    ulong[] _behind = [];              // by ordinal: the graph file's edge region is out of date
    int _appendedFrom = -1;            // start of the run of new ordinals not yet flushed
    int _nextOrdinal;                  // one past the highest ordinal allocated, bounding that run
    long _residentBytes;
    long _maxBytes;
    int _hand;                         // the CLOCK sweep position

    public int Dimensions { get; }
    /// <summary>Slots for layer-0 neighbours in every record (the graph's layer-0 degree).</summary>
    public int NeighbourCapacity { get; }
    /// <summary>4-byte words per record: the count, the vector and the neighbour slots.</summary>
    public int Words { get; }
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
                evict();
            }
        }
    }

    HnswRecordStore(FixedStrideFile file, int dimensions, int neighbourCapacity, long maxCacheBytes) {
        _file = file;
        Dimensions = dimensions;
        NeighbourCapacity = neighbourCapacity;
        Words = 1 + dimensions + neighbourCapacity;
        _recordBytes = (long)Words * 4 + entryOverhead;
        _maxBytes = maxCacheBytes;
    }

    public static HnswRecordStore Create(string path, long generation, int dimensions, int neighbourCapacity, long maxCacheBytes) {
        var words = 1 + dimensions + neighbourCapacity;
        var file = FixedStrideFile.Create(path, FileKind, generation, words * 4, [dimensions, neighbourCapacity], 0);
        return new(file, dimensions, neighbourCapacity, maxCacheBytes);
    }
    public static HnswRecordStore Open(string path, long generation, int dimensions, int neighbourCapacity, long maxCacheBytes, int committedRecords) {
        var words = 1 + dimensions + neighbourCapacity;
        var file = FixedStrideFile.Open(path, FileKind, generation, words * 4, [dimensions, neighbourCapacity], committedRecords);
        var store = new HnswRecordStore(file, dimensions, neighbourCapacity, maxCacheBytes);
        store.ensureCapacity(committedRecords - 1);
        store._nextOrdinal = committedRecords;
        return store;
    }

    // ---- record accessors -------------------------------------------------------------------------

    /// <summary>The node's vector: the head of the record, so a pinned copy and a record can be
    /// scored through the same span. Read-only — records are only mutated through
    /// <see cref="SetNeighbours"/>, which the caller reaches under the index's write lock.</summary>
    public ReadOnlySpan<float> Vector(float[] record) => record.AsSpan(0, Dimensions);
    /// <summary>The node's layer-0 neighbour ordinals. The stored count is clamped to the capacity,
    /// so a count torn by a crash yields a short list instead of reading past the record.</summary>
    public ReadOnlySpan<int> Neighbours(float[] record) {
        var words = MemoryMarshal.Cast<float, int>(record.AsSpan());
        var count = Math.Clamp(words[Dimensions], 0, NeighbourCapacity);
        return words.Slice(Dimensions + 1, count);
    }
    public void SetNeighbours(int ordinal, ReadOnlySpan<int> neighbours) {
        if (neighbours.Length > NeighbourCapacity) throw new ArgumentException("More neighbours than the record has slots for. ");
        var words = MemoryMarshal.Cast<float, int>(GetForWrite(ordinal).AsSpan());
        words[Dimensions] = neighbours.Length;
        neighbours.CopyTo(words.Slice(Dimensions + 1, neighbours.Length));
    }
    /// <summary>Where in a record the region <see cref="SetNeighbours"/> can change starts.</summary>
    int edgeRegionOffset => Dimensions * 4;

    // ---- reads ------------------------------------------------------------------------------------

    /// <summary>The record for an ordinal, from memory when it is there and from the file when it is
    /// not. The hit path is a single array read; only a miss takes the lock.</summary>
    public float[] Get(int ordinal) {
        var resident = _resident;
        if ((uint)ordinal < (uint)resident.Length) {
            var hit = resident[ordinal];
            if (hit != null) {
                _used[ordinal] = 1;
                return hit;
            }
        }
        var record = new float[Words];
        _file.Read(ordinal, MemoryMarshal.AsBytes(record.AsSpan())); // outside the lock; positional
        lock (_lock) {
            applyOverlay(ordinal, record);
            if ((uint)ordinal >= (uint)_resident.Length) return record; // grown away under us; still valid data
            var raced = _resident[ordinal];
            if (raced != null) return raced; // another thread loaded it first; the copies are identical
            _resident[ordinal] = record;
            _used[ordinal] = 1;
            _residentBytes += _recordBytes;
            evict();
        }
        return record;
    }
    /// <summary>True when <see cref="Get"/> would not touch the disk. Used to decide whether a batch
    /// of neighbours is worth loading in parallel.</summary>
    public bool IsResident(int ordinal) {
        var resident = _resident;
        return (uint)ordinal < (uint)resident.Length && resident[ordinal] != null;
    }
    /// <summary>The record if it happens to be in memory, without claiming it was used. An exact scan
    /// reads every record exactly once, so counting those touches as recency would evict the working
    /// set of the graph walks in favour of records nothing will ask for again.</summary>
    public bool TryPeek(int ordinal, out float[] record) {
        var resident = _resident;
        if ((uint)ordinal < (uint)resident.Length) {
            var hit = resident[ordinal];
            if (hit != null) {
                record = hit;
                return true;
            }
        }
        record = [];
        return false;
    }
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
    /// <summary>A zeroed record for a freshly allocated ordinal, resident and dirty from the start —
    /// an allocated-but-unflushed ordinal is never read from the file.</summary>
    public float[] Allocate(int ordinal) {
        ensureCapacity(ordinal);
        var record = new float[Words];
        lock (_lock) {
            _resident[ordinal] = record;
            _used[ordinal] = 1;
            _residentBytes += _recordBytes;
            if (_appendedFrom < 0) _appendedFrom = ordinal;
            if (ordinal >= _nextOrdinal) _nextOrdinal = ordinal + 1;
        }
        return record;
    }
    /// <summary>The record as a mutable array, loading it if needed and pinning it until the next
    /// flush. Loading here happens under the lock, which is free of contention: writes only run under
    /// the index's write lock, so no search is in flight.</summary>
    public float[] GetForWrite(int ordinal) {
        lock (_lock) {
            var record = (uint)ordinal < (uint)_resident.Length ? _resident[ordinal] : null;
            if (record == null) {
                record = new float[Words];
                _file.Read(ordinal, MemoryMarshal.AsBytes(record.AsSpan()));
                applyOverlay(ordinal, record);
                ensureCapacity(ordinal);
                _resident[ordinal] = record;
                _residentBytes += _recordBytes;
            }
            _used[ordinal] = 1;
            if (_appendedFrom < 0 || ordinal < _appendedFrom) _dirtySet.Add(ordinal);
            if (ordinal >= _nextOrdinal) _nextOrdinal = ordinal + 1;
            return record;
        }
    }
    /// <summary>
    /// Writes every change out. New records are always the highest ordinals, so they go to the graph
    /// file as a few large sequential writes however many there are. Rewrites are scattered and only
    /// ever touched the neighbour region: with an <paramref name="edgeLog"/> they are appended to it
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
                    edgeLog.Append(ordinal, Neighbours(record));
                    setBehind(ordinal); // the graph file's edge region for this record is now stale
                } else {
                    _file.WriteWithin(ordinal, edgeRegionOffset,
                        MemoryMarshal.AsBytes(record.AsSpan(Dimensions, 1 + NeighbourCapacity)));
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
            evict(); // the records just unpinned may put the cache back over its budget
        }
    }
    /// <summary>Writes every neighbour list the edge log is carrying into the graph file, so the log can
    /// be dropped. This is the scattered in-place work the log exists to keep off the WAL-flush path.
    /// A record still in memory is written from there; one that has been evicted since, from the
    /// overlay that its eviction put its list into.</summary>
    public void ConsolidateBehind() {
        var buffer = new int[1 + NeighbourCapacity];
        for (var ordinal = 0; ordinal < _nextOrdinal; ordinal++) {
            if (!isBehind(ordinal)) continue;
            var record = (uint)ordinal < (uint)_resident.Length ? _resident[ordinal] : null;
            ReadOnlySpan<int> edges = record != null ? Neighbours(record)
                : _overlay.TryGetValue(ordinal, out var kept) ? kept
                : throw new InvalidOperationException("A record the graph file is behind on is neither in memory nor in the overlay. ");
            buffer[0] = edges.Length;
            edges.CopyTo(buffer.AsSpan(1));
            _file.WriteWithin(ordinal, edgeRegionOffset, MemoryMarshal.AsBytes(buffer.AsSpan(0, 1 + edges.Length)));
        }
        lock (_lock) {
            _overlay.Clear();
            Array.Clear(_behind);
        }
    }
    /// <summary>Restores what the durable edge log says at open: those records' file copies are stale, and
    /// none of them is in memory yet, so every entry goes into the overlay. Entries come in write order,
    /// so a later list for the same node simply replaces an earlier one.</summary>
    public void LoadOverlay(IEnumerable<(int ordinal, int[] neighbours)> entries) {
        foreach (var (ordinal, neighbours) in entries) {
            if (ordinal >= _nextOrdinal) continue; // past the commit boundary: uncommitted scratch
            ensureCapacity(ordinal);
            _overlay[ordinal] = neighbours;
            setBehind(ordinal);
        }
    }
    void applyOverlay(int ordinal, float[] record) { // must hold _lock
        if (_overlay.Count == 0 || !_overlay.TryGetValue(ordinal, out var edges)) return;
        var words = MemoryMarshal.Cast<float, int>(record.AsSpan());
        words[Dimensions] = edges.Length;
        edges.CopyTo(words.Slice(Dimensions + 1, edges.Length));
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
        _resident[ordinal] ?? throw new InvalidOperationException("A record marked dirty is no longer in memory. ");
    public void Fsync() => _file.Fsync();
    public void ClearCache() {
        lock (_lock) {
            for (var ordinal = 0; ordinal < _resident.Length; ordinal++) {
                if (IsDirty(ordinal)) continue;
                drop(ordinal);
            }
        }
    }

    void ensureCapacity(int ordinal) { // callers hold either the index write lock or _lock
        if (ordinal < _resident.Length) return;
        var size = Math.Max(1024, _resident.Length * 2);
        while (size <= ordinal) size *= 2;
        var resident = new float[]?[size];
        var used = new byte[size];
        var behind = new ulong[(size + 63) / 64];
        Array.Copy(_resident, resident, _resident.Length);
        Array.Copy(_used, used, _used.Length);
        Array.Copy(_behind, behind, _behind.Length);
        _used = used;
        _behind = behind;
        _resident = resident; // published last: a racing reader sees either table, both consistent
    }
    /// <summary>CLOCK: sweep forward giving every record one second chance — a record whose used bit
    /// is set keeps its place and loses the bit, one without it is dropped. Dirty records are never
    /// dropped, so a cache budget smaller than the unflushed set is exceeded rather than enforced;
    /// what bounds that is the memtable flush threshold.</summary>
    void evict() { // must hold _lock
        if (_residentBytes <= _maxBytes) return;
        var n = _resident.Length;
        if (n == 0) return;
        var limit = n * 2L;
        for (var scanned = 0L; scanned < limit && _residentBytes > _maxBytes; scanned++) {
            var ordinal = _hand;
            _hand = _hand + 1 >= n ? 0 : _hand + 1;
            if (_resident[ordinal] == null) continue;
            if (_used[ordinal] != 0) {
                _used[ordinal] = 0;
                continue;
            }
            if (IsDirty(ordinal)) continue;
            drop(ordinal);
        }
    }
    /// <summary>Removes a clean record from memory. When the graph file is behind on its edges, the list
    /// is kept in the overlay first — otherwise this would be the moment the only correct copy of it
    /// disappeared.</summary>
    void drop(int ordinal) { // must hold _lock
        var record = _resident[ordinal];
        if (record == null) return;
        if (isBehind(ordinal)) _overlay[ordinal] = Neighbours(record).ToArray();
        _resident[ordinal] = null;
        _used[ordinal] = 0;
        _residentBytes -= _recordBytes;
    }
    public void Dispose() {
        lock (_lock) {
            _resident = [];
            _used = [];
            _behind = [];
            _dirtySet.Clear();
            _overlay.Clear();
            _residentBytes = 0;
        }
        _file.Dispose();
    }
}
