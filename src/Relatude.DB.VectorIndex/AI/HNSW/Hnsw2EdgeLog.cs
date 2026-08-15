using System.Runtime.InteropServices;

namespace Relatude.DB.AI.HNSW;

/// <summary>
/// The edge regions changed since the last state save, as a sequential append-only log.
///
/// <para><b>Why it exists.</b> An HNSW insert is not a write of one record: linking a new node rewrites
/// the neighbour list of every node it attached to, so a few hundred new vectors touch tens of
/// thousands of existing records scattered all over the routing file. Writing them in place at every
/// WAL flush — which is where the data store expects a cheap incremental checkpoint — costs one
/// scattered write each, and a partial write into a page the OS has already evicted has to read that
/// page back first. Appending the same regions to this log instead is one sequential write of a
/// couple of megabytes, and the scattered in-place work moves to the state save, where the index is
/// already allowed to do heavy maintenance.</para>
///
/// <para><b>How it stays correct.</b> The manifest records how many entries are durable, so a torn tail
/// is simply not there as far as the next open is concerned, and no per-entry checksum is needed. While
/// the log holds regions the routing file has not received, the routing store keeps them applied in
/// memory (and, for records it has since evicted, in an overlay) so a reader never sees the stale copy.
/// A state save writes them into the routing file, stamps a manifest claiming zero entries, and only
/// then drops the log — so a crash anywhere in that sequence either replays entries that are already
/// applied (they are idempotent) or ignores a log the manifest no longer claims.</para>
/// </summary>
internal sealed class Hnsw2EdgeLog : IDisposable {
    internal const int FileKind = 5;
    readonly FixedStrideFile _file;
    readonly int _capacity;    // neighbour slots per entry, i.e. the layer-0 degree
    readonly int _regionWords; // [count][ids: capacity][sims: capacity]
    readonly int _words;       // [ordinal] + the region
    readonly List<int> _pending = [];
    int _entries;

    /// <summary>Entries written to the file. The manifest stamps this as the durable count.</summary>
    public int Entries => _entries;
    public string Path => _file.Path;
    public long FileLength => _file.FileLength;
    public bool HasPending => _pending.Count > 0;

    Hnsw2EdgeLog(FixedStrideFile file, int capacity) {
        _file = file;
        _capacity = capacity;
        _regionWords = 1 + 2 * capacity;
        _words = 1 + _regionWords;
    }
    public static Hnsw2EdgeLog Create(string path, long generation, int neighbourCapacity) {
        var words = 2 + 2 * neighbourCapacity;
        return new(FixedStrideFile.Create(path, FileKind, generation, words * 4, [neighbourCapacity], 0), neighbourCapacity);
    }
    public static Hnsw2EdgeLog Open(string path, long generation, int neighbourCapacity, int committedEntries) {
        var words = 2 + 2 * neighbourCapacity;
        var file = FixedStrideFile.Open(path, FileKind, generation, words * 4, [neighbourCapacity], committedEntries);
        return new(file, neighbourCapacity) { _entries = committedEntries };
    }

    /// <summary>Buffers one node's edge region — the count, ids and sims exactly as the record holds
    /// them. Nothing reaches the file until <see cref="FlushPending"/>, which writes the whole batch
    /// in one go.</summary>
    public void Append(int ordinal, ReadOnlySpan<int> region) {
        if (region.Length != _regionWords) throw new ArgumentException("The edge region does not match the log's layout. ");
        _pending.Add(ordinal);
        for (var i = 0; i < region.Length; i++) _pending.Add(region[i]);
    }
    public void FlushPending() {
        if (_pending.Count == 0) return;
        _file.Write(_entries, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_pending)));
        _entries += _pending.Count / _words;
        _pending.Clear();
    }
    public void Fsync() => _file.Fsync();
    /// <summary>The durable entries, oldest first — later entries for the same node supersede earlier
    /// ones, so a replay must apply them in this order. Each yields the raw edge region the routing
    /// store lays over the stale file copy.</summary>
    public IEnumerable<(int ordinal, int[] region)> Replay(int count) {
        if (count <= 0) yield break;
        const int perBatch = 4096;
        var buffer = new int[Math.Min(count, perBatch) * _words];
        for (var first = 0; first < count; first += perBatch) {
            var n = Math.Min(perBatch, count - first);
            _file.Read(first, MemoryMarshal.AsBytes(buffer.AsSpan(0, n * _words)));
            for (var i = 0; i < n; i++) {
                var at = i * _words;
                var ordinal = buffer[at];
                var length = buffer[at + 1];
                if (ordinal < 0 || length < 0 || length > _capacity) throw new InvalidDataException("Invalid edge log entry. ");
                yield return (ordinal, buffer.AsSpan(at + 1, _regionWords).ToArray());
            }
        }
    }
    /// <summary>Forgets the entries without touching the file: their regions have been written into the
    /// routing file, so the next manifest can claim none of them. The bytes are still there — harmlessly,
    /// since nothing past the manifest's count is ever read — until <see cref="TruncateFile"/> reclaims
    /// the space. Keeping those two steps apart is what makes the sequence crash-safe in both
    /// directions.</summary>
    public void Disown() {
        _pending.Clear();
        _entries = 0;
    }
    /// <summary>Reclaims the file's space. Only safe once a manifest claiming no entries is durable.</summary>
    public void TruncateFile() => _file.Truncate();
    public void Dispose() {
        _pending.Clear();
        _file.Dispose();
    }
}
