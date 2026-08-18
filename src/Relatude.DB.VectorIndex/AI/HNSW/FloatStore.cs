using System.Runtime.InteropServices;

namespace Relatude.DB.AI.HNSW;

/// <summary>
/// The float vectors, one fixed-stride record per ordinal, in their own file — deliberately apart
/// from the routing graph. A walk never reads these: it scores int8 copies out of
/// <see cref="RoutingStore"/>, and only the final candidates are re-scored here, so the exact
/// numbers the caller sees always come from full-precision floats while the latency-critical loop
/// moves a quarter of the bytes. Exact scans read this file too — pure sequential vector data with
/// no edges interleaved.
///
/// <para>When the memory budget allows, the whole file is mirrored into chunked arrays at open and
/// re-scoring and scans never touch the disk; when it does not, only the unflushed tail (vectors
/// added since the last flush, which the file does not have yet) is held, and everything else is
/// read positionally on demand. The mirror can be dropped mid-run when the index outgrows the
/// budget — the tail moves into its own map and nothing else changes.</para>
/// </summary>
internal sealed class FloatStore : IDisposable {
    internal const int FileKind = 1;
    const int ChunkShift = 12; // 4096 ordinals per mirror chunk, matching the routing store
    const int ChunkOrdinals = 1 << ChunkShift;
    const int ChunkMask = ChunkOrdinals - 1;

    readonly FixedStrideFile _file;
    float[][]? _chunks;                       // the mirror; null when the budget said no
    Dictionary<int, float[]>? _tail;          // unflushed vectors when not mirrored (writers hold the index write lock)
    int _appendedFrom = -1;                   // start of the run of new ordinals not yet flushed
    int _nextOrdinal;

    public int Dimensions { get; }
    public string Path => _file.Path;
    public long FileLength => _file.FileLength;
    /// <summary>How many whole records the file holds; records above this exist only in memory.</summary>
    public long RecordCapacity => _file.RecordCapacity;
    public bool Mirrored => _chunks != null;
    /// <summary>Ordinals at or above this exist only in memory — allocated after the last flush.
    /// <see cref="int.MaxValue"/> when everything allocated has been written. Stable while the
    /// caller holds the index lock, which every scan does.</summary>
    public int FirstUnflushedOrdinal => _appendedFrom < 0 ? int.MaxValue : _appendedFrom;
    public long MirrorBytes => _chunks == null ? 0 : (long)countChunks() * ChunkOrdinals * Dimensions * 4;
    int countChunks() {
        var n = 0;
        foreach (var c in _chunks!) {
            if (c != null!) n++;
        }
        return n;
    }
    /// <summary>Bytes pinned by the unflushed tail (zero while mirrored — the mirror holds them).</summary>
    public long TailBytes => _tail == null ? 0 : (long)_tail.Count * (Dimensions * 4 + 56);
    /// <summary>Bytes of vectors not yet written to the file — the tail's when it carries them, the
    /// appended run's otherwise, never both (they are the same vectors).</summary>
    public long UnflushedBytes =>
        _tail != null ? TailBytes : _appendedFrom < 0 ? 0 : (long)(_nextOrdinal - _appendedFrom) * Dimensions * 4;

    FloatStore(FixedStrideFile file, int dimensions, bool mirror) {
        _file = file;
        Dimensions = dimensions;
        if (mirror) _chunks = [];
        else _tail = [];
    }
    public static FloatStore Create(string path, long generation, int dimensions, bool mirror) {
        var file = FixedStrideFile.Create(path, FileKind, generation, dimensions * 4, [dimensions], 0);
        return new(file, dimensions, mirror);
    }
    public static FloatStore Open(string path, long generation, int dimensions, bool mirror,
        int committedRecords, int maxThreads) {
        var file = FixedStrideFile.Open(path, FileKind, generation, dimensions * 4, [dimensions], committedRecords);
        var store = new FloatStore(file, dimensions, mirror);
        try {
            store._nextOrdinal = committedRecords;
            if (mirror && committedRecords > 0) store.mirrorFromFile(committedRecords, maxThreads);
            return store;
        } catch {
            store.Dispose();
            throw;
        }
    }
    void mirrorFromFile(int records, int maxThreads) {
        var chunkCount = (records + ChunkOrdinals - 1) >> ChunkShift;
        for (var c = 0; c < chunkCount; c++) ensureChunk(c << ChunkShift);
        Parallel.For(0, chunkCount, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxThreads) }, c => {
            var first = c << ChunkShift;
            var count = Math.Min(ChunkOrdinals, records - first);
            _file.Read(first, MemoryMarshal.AsBytes(_chunks![c].AsSpan(0, count * Dimensions)));
        });
    }

    // ---- reads -----------------------------------------------------------------------------------

    /// <summary>The vector when it is in memory (the mirror or the unflushed tail), without touching
    /// the disk. The span is valid while the caller holds the index lock.</summary>
    public bool TryPeek(int ordinal, out ReadOnlySpan<float> vector) {
        if (_chunks != null) {
            var chunk = _chunks[ordinal >> ChunkShift];
            vector = chunk.AsSpan((ordinal & ChunkMask) * Dimensions, Dimensions);
            return true;
        }
        if (_tail != null && _tail.TryGetValue(ordinal, out var kept)) {
            vector = kept;
            return true;
        }
        vector = default;
        return false;
    }
    /// <summary>The vector, from memory when it is there and positionally from the file when not.
    /// <paramref name="buffer"/> must hold <see cref="Dimensions"/> floats and receives the vector
    /// only on a file read; the returned span is the vector either way.</summary>
    public ReadOnlySpan<float> Read(int ordinal, Span<float> buffer) {
        if (TryPeek(ordinal, out var vector)) return vector;
        var target = buffer[..Dimensions];
        _file.Read(ordinal, MemoryMarshal.AsBytes(target));
        return target;
    }
    /// <summary>The exact cosine similarity of the query and one stored vector — the number a search
    /// returns, always computed from full-precision floats.</summary>
    public float ExactSim(ReadOnlySpan<float> query, int ordinal, Span<float> buffer) =>
        VectorMath.Dot(query, Read(ordinal, buffer));
    /// <summary>Reads a run of records straight into the caller's buffer. An exact scan uses this:
    /// a few large sequential reads, nothing admitted anywhere.</summary>
    public void ReadRange(int firstOrdinal, int count, Span<float> target) =>
        _file.Read(firstOrdinal, MemoryMarshal.AsBytes(target[..(count * Dimensions)]));

    // ---- writes ----------------------------------------------------------------------------------

    /// <summary>Stores a freshly allocated ordinal's vector, in memory until the next flush.</summary>
    public void Allocate(int ordinal, ReadOnlySpan<float> vector) {
        if (_chunks != null) {
            ensureChunk(ordinal);
            vector.CopyTo(_chunks[ordinal >> ChunkShift].AsSpan((ordinal & ChunkMask) * Dimensions, Dimensions));
        } else {
            _tail![ordinal] = vector.ToArray();
        }
        if (_appendedFrom < 0) _appendedFrom = ordinal;
        if (ordinal >= _nextOrdinal) _nextOrdinal = ordinal + 1;
    }
    /// <summary>Writes the unflushed tail to the file as a few large sequential writes. Vectors are
    /// immutable per ordinal, so appends are all a flush ever has. Does not fsync.</summary>
    public void FlushAppended() {
        if (_appendedFrom < 0) return;
        if (_chunks != null) {
            var first = _appendedFrom;
            while (first < _nextOrdinal) {
                var chunk = _chunks[first >> ChunkShift];
                var inChunk = first & ChunkMask;
                var n = Math.Min(ChunkOrdinals - inChunk, _nextOrdinal - first);
                _file.Write(first, MemoryMarshal.AsBytes(chunk.AsSpan(inChunk * Dimensions, n * Dimensions)));
                first += n;
            }
        } else {
            var maxRun = Math.Max(1, 4 * 1024 * 1024 / (Dimensions * 4));
            var buffer = new float[Math.Min(maxRun, _nextOrdinal - _appendedFrom) * Dimensions];
            for (var first = _appendedFrom; first < _nextOrdinal; first += maxRun) {
                var n = Math.Min(maxRun, _nextOrdinal - first);
                for (var i = 0; i < n; i++) {
                    if (!_tail!.TryGetValue(first + i, out var v)) throw new InvalidOperationException("An unflushed vector is no longer in memory. ");
                    v.CopyTo(buffer.AsSpan(i * Dimensions));
                }
                _file.Write(first, MemoryMarshal.AsBytes(buffer.AsSpan(0, n * Dimensions)));
            }
            _tail!.Clear();
        }
        _appendedFrom = -1;
    }
    /// <summary>Demotes the store to disk reads mid-run because the index outgrew its budget: the
    /// unflushed tail moves into its own map (the file does not have those vectors yet) and the
    /// mirror is released. Caller holds the index write lock.</summary>
    public void DropMirror() {
        if (_chunks == null) return;
        _tail = [];
        for (var ordinal = Math.Max(0, _appendedFrom); _appendedFrom >= 0 && ordinal < _nextOrdinal; ordinal++) {
            var chunk = _chunks[ordinal >> ChunkShift];
            _tail[ordinal] = chunk.AsSpan((ordinal & ChunkMask) * Dimensions, Dimensions).ToArray();
        }
        _chunks = null;
    }
    /// <summary>What a full mirror of <paramref name="records"/> vectors costs — whole chunks, the
    /// granularity the mirror allocates in. The budget arithmetic's number for the mirror.</summary>
    public long MirrorBytesFor(int records) =>
        (long)((records + ChunkOrdinals - 1) >> ChunkShift) * ChunkOrdinals * Dimensions * 4;
    /// <summary>The reverse of <see cref="DropMirror"/>, for a raised budget that affords the mirror
    /// again: the flushed records are read from the file in parallel (the cost of an open, paid on
    /// the explicit call) and the unflushed tail is folded in from its map. Caller holds the index
    /// write lock.</summary>
    public void BuildMirror(int maxThreads) {
        if (_chunks != null) return;
        var tail = _tail;
        _chunks = [];
        if (_nextOrdinal > 0) {
            // everything below the unflushed run is in the file; the tail map carries the rest
            var flushed = _appendedFrom < 0 ? _nextOrdinal : _appendedFrom;
            mirrorFromFile(flushed, maxThreads);
            if (tail != null) {
                foreach (var (ordinal, v) in tail) {
                    ensureChunk(ordinal);
                    v.CopyTo(_chunks[ordinal >> ChunkShift].AsSpan((ordinal & ChunkMask) * Dimensions, Dimensions));
                }
            }
        }
        _tail = null;
    }
    public void Fsync() => _file.Fsync();

    void ensureChunk(int ordinal) {
        var chunk = ordinal >> ChunkShift;
        if (chunk >= _chunks!.Length) {
            var size = Math.Max(4, _chunks.Length * 2);
            while (size <= chunk) size *= 2;
            Array.Resize(ref _chunks, size);
        }
        _chunks[chunk] ??= GC.AllocateUninitializedArray<float>(ChunkOrdinals * Dimensions);
    }
    public void Dispose() {
        _chunks = null;
        _tail = null;
        _file.Dispose();
    }
}
