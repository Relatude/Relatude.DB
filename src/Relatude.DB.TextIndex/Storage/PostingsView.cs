namespace Relatude.DB.DataStores.Indexes.TextIndexing;

/// <summary>The disk-resolved postings of one word (merged across all segments, newest wins,
/// tombstones applied), sorted by node id. Cached per word until the segment list changes.</summary>
internal sealed class DiskPostings(int[] ids, byte[] hits) {
    public static readonly DiskPostings Empty = new([], []);
    public readonly int[] Ids = ids;
    public readonly byte[] Hits = hits;
    public long ByteSize => Ids.Length * 5 + 48;
}

/// <summary>
/// One word's live postings: the cached disk-resolved postings with the memtable's ops for that
/// word applied as an overlay at read time. Keeping the overlay virtual means writes never
/// invalidate the postings cache — only a segment flush or merge does.
/// </summary>
internal sealed class PostingsView {
    public static readonly PostingsView Empty = new(DiskPostings.Empty, null);
    readonly DiskPostings _disk;
    readonly Dictionary<int, short>? _overlay; // memtable ops: hits, or MemTable.Tombstone
    public int Count { get; }
    public PostingsView(DiskPostings disk, Dictionary<int, short>? overlay) {
        _disk = disk;
        _overlay = overlay;
        var count = disk.Ids.Length;
        if (overlay != null) {
            foreach (var kv in overlay) {
                var onDisk = Array.BinarySearch(disk.Ids, kv.Key) >= 0;
                if (onDisk) count--; // the overlay op replaces (or tombstones) the disk entry
                if (kv.Value >= 0) count++;
            }
        }
        Count = count;
    }
    public IEnumerable<(int nodeId, byte hits)> Enumerate() {
        if (_overlay == null) {
            for (var i = 0; i < _disk.Ids.Length; i++) yield return (_disk.Ids[i], _disk.Hits[i]);
        } else {
            for (var i = 0; i < _disk.Ids.Length; i++) {
                if (!_overlay.ContainsKey(_disk.Ids[i])) yield return (_disk.Ids[i], _disk.Hits[i]);
            }
            foreach (var kv in _overlay) {
                if (kv.Value >= 0) yield return (kv.Key, (byte)kv.Value);
            }
        }
    }
}
