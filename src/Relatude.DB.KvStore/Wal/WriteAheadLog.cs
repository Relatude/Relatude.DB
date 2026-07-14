using System.Buffers.Binary;
using KvStore.Internal;
using KvStore.Paging;

namespace KvStore.Wal;

/// <summary>
/// An append-only redo log that makes multi-page updates atomic and crash-safe with a
/// <b>single fsync per commit</b>.
///
/// On commit, the image of every staged page is appended as one transaction record
/// terminated by a checksummed commit marker, and the log is fsync'd — that fsync is the
/// commit point. The pages are <i>not</i> applied to the main database file yet; the log
/// accumulates committed records until the owning <see cref="Pager"/> checkpoints, applying
/// every logged page to the main file in bulk, fsyncing it, and then truncating the log.
///
/// Recovery scans the records in order and replays each complete, checksum-valid one; it
/// stops at the first torn or corrupt record (everything after the tear was never
/// acknowledged as committed). Replay is idempotent — image entries hold exact page images,
/// patch entries set absolute byte values (not deltas), and later records simply overwrite
/// earlier ones, so replaying the whole log onto an already-checkpointed main file lands on
/// the same final state — which is also why <see cref="Checkpoint"/> needs no fsync of its
/// own: if the truncation is lost in a crash, recovery just replays changes the main file
/// already contains.
///
/// <para><b>Used-extent framing:</b> a slotted page is meaningful only in its head (header +
/// slot array) and tail (cell area); the gap between them is never read. The caller may pass
/// an extent provider so each record carries just those two ranges instead of the full
/// <see cref="Pager.PageSize"/> image — recovery reconstructs the page with a zeroed gap,
/// which is logically identical. Without a provider, full images are written.</para>
///
/// <para><b>Patch entries:</b> scattered small updates (in-place value overwrites) change a few
/// dozen bytes of a page whose previous version is already durable — logging a full image per
/// dirty page turns kilobytes of WAL (and checkpoint) I/O per changed byte into the dominant
/// write cost. When the caller supplies the page's previous durable image (<c>prevOf</c>), the
/// page is byte-diffed against it and, when compact, logged as patch segments instead — marked
/// by the impossible head length <see cref="PatchSentinel"/>, so old logs parse unchanged. A
/// page identical to its durable image is skipped entirely; a commit whose every page is
/// unchanged writes (and fsyncs) nothing, because the durable state already matches. Recovery
/// applies patches over the page's current content — the main file plus earlier replayed
/// records, which is exactly the state the diff was taken against.</para>
///
/// Record layout (repeated end to end):
///   [u32 pageCount]
///   pageCount x ( [u32 pageId] [u16 headLen] [u16 tailStart] [headLen bytes] [PageSize-tailStart bytes]
///               | [u32 pageId] [u16 0xFFFF] [u16 segCount] segCount x ([u16 offset] [u16 len] [len bytes]) )
///   [u32 COMMIT_MARKER] [u32 crc32c-of-everything-above]
/// </summary>
internal sealed class WriteAheadLog : IDisposable
{
    // Bumped (..42 -> ..43) when the record framing changed to used extents + CRC-32C, so a
    // pre-upgrade record left by a crash is rejected as torn instead of being misparsed. The
    // patch-entry extension keeps the marker: its sentinel head length (0xFFFF > PageSize) makes
    // old readers reject a new log as torn, and old logs contain no sentinel, so the new reader
    // parses them exactly as before.
    private const uint CommitMarker = 0xC0FFEE43u;

    private const int PageDescSize = 8; // u32 pageId + u16 headLen|sentinel + u16 tailStart|segCount

    /// <summary>Head-length value marking a patch entry; impossible for an image entry (head
    /// length never exceeds <see cref="Pager.PageSize"/>).</summary>
    private const int PatchSentinel = 0xFFFF;

    private const int PatchDescSize = 4;      // u16 offset + u16 length per segment
    private const int MaxPatchSegments = 16;  // a diff more fragmented than this logs a full image
    private const int PatchMergeGap = 32;     // unchanged runs shorter than this fold into a segment

    /// <summary>Record buffers larger than this are not retained between flushes.</summary>
    private const int RetainedBufferBytes = 4 * 1024 * 1024;

    private readonly string _path;
    private FileStream _file;
    private byte[] _record = [];

    // Cached file length, updated by the single writer (append/truncate) and read lock-free.
    // FileStream is not safe for a concurrent Length query while the flush leader appends, and
    // disk-usage queries may come from any thread.
    private long _length;

    // Per-flush scratch for the encoding decisions (reused so the flush path stays allocation-free).
    // headLen == PatchSentinel marks a patch entry, whose segments live in _segs[segStart..+segCount].
    private readonly List<(uint id, byte[] data, int headLen, int tailStart, int segStart, int segCount)> _entries = new();
    private readonly List<(ushort offset, ushort length)> _segs = new();

    public WriteAheadLog(string path)
    {
        _path = path;
        // bufferSize 1 = unbuffered: each record is written as one large span, so FileStream's
        // own buffer would only add a copy.
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        _length = _file.Length;
    }

    /// <summary>Current log size in bytes — the pager's checkpoint trigger, also safe to read
    /// from any thread (disk-usage reporting) while the flush leader appends.</summary>
    public long Length => Volatile.Read(ref _length);

    /// <summary>Appends one committed transaction's page changes and fsyncs. Returns only when
    /// the record is durable; this is the commit point. <paramref name="extentOf"/> (optional)
    /// reports the used head length and tail start of a page so the gap can be skipped.
    /// <paramref name="prevOf"/> (optional) supplies a page's previous <b>durable</b> image —
    /// exactly the bytes recovery would see for that page before this record — enabling compact
    /// patch entries; pages identical to their previous image are skipped, and a commit with no
    /// effective change writes nothing at all.</summary>
    public void WriteTransaction(
        IReadOnlyDictionary<uint, byte[]> dirtyPages,
        Func<uint, byte[], (int headLen, int tailStart)>? extentOf = null,
        Func<uint, byte[]?>? prevOf = null)
    {
        // Pass 1: pick each page's encoding (patch vs extent image) and size the record exactly.
        _entries.Clear();
        _segs.Clear();
        int recordLen = 4 + 8; // count + trailer
        foreach (var (id, data) in dirtyPages)
        {
            var (headLen, tailStart) = Extent(id, data, extentOf);
            int extentBytes = headLen + (Pager.PageSize - tailStart);

            if (prevOf?.Invoke(id) is { } prev)
            {
                int segStart = _segs.Count;
                if (TryDiff(prev, data, _segs, out int patchBytes))
                {
                    int segCount = _segs.Count - segStart;
                    if (segCount == 0) continue; // identical to its durable image — nothing to log
                    int patchEntryBytes = segCount * PatchDescSize + patchBytes;
                    if (patchEntryBytes < extentBytes)
                    {
                        _entries.Add((id, data, PatchSentinel, 0, segStart, segCount));
                        recordLen += PageDescSize + patchEntryBytes;
                        continue;
                    }
                }
                _segs.RemoveRange(segStart, _segs.Count - segStart); // diff not worth it
            }
            _entries.Add((id, data, headLen, tailStart, 0, 0));
            recordLen += PageDescSize + extentBytes;
        }
        if (_entries.Count == 0) return; // durable state already matches — nothing to make durable

        if (_record.Length < recordLen)
            _record = new byte[Math.Max(recordLen, Math.Min(_record.Length * 2, RetainedBufferBytes))];
        var rec = _record;

        int pos = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos), (uint)_entries.Count);
        pos += 4;
        foreach (var (id, data, headLen, tailStart, segStart, segCount) in _entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos), id);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos + 4), (ushort)headLen);
            pos += PageDescSize;
            if (headLen == PatchSentinel)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos - 2), (ushort)segCount);
                for (int s = segStart; s < segStart + segCount; s++)
                {
                    var (offset, length) = _segs[s];
                    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos), offset);
                    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos + 2), length);
                    data.AsSpan(offset, length).CopyTo(rec.AsSpan(pos + PatchDescSize));
                    pos += PatchDescSize + length;
                }
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos - 2), (ushort)tailStart);
                data.AsSpan(0, headLen).CopyTo(rec.AsSpan(pos));
                pos += headLen;
                data.AsSpan(tailStart).CopyTo(rec.AsSpan(pos));
                pos += Pager.PageSize - tailStart;
            }
        }

        uint crc = Crc32.Compute(rec.AsSpan(0, pos));
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos), CommitMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos + 4), crc);
        pos += 8;

        _file.Seek(0, SeekOrigin.End);
        _file.Write(rec, 0, pos);
        _file.Flush(true); // durability barrier: the record (and the file length) is on the device
        Volatile.Write(ref _length, _file.Length);

        if (_record.Length > RetainedBufferBytes) _record = [];
    }

    private static (int headLen, int tailStart) Extent(
        uint id, byte[] data, Func<uint, byte[], (int, int)>? extentOf)
    {
        if (extentOf is null) return (Pager.PageSize, Pager.PageSize);
        var (headLen, tailStart) = extentOf(id, data);
        // A nonsensical extent (overlap or out of range) falls back to the full image.
        if (headLen < 0 || tailStart < headLen || tailStart > Pager.PageSize)
            return (Pager.PageSize, Pager.PageSize);
        return (headLen, tailStart);
    }

    /// <summary>
    /// Byte-diffs a page against its previous durable image into change segments (unchanged runs
    /// shorter than <see cref="PatchMergeGap"/> fold into their neighbours). Returns false when
    /// the diff fragments past <see cref="MaxPatchSegments"/> — the caller then logs an image.
    /// An identical page yields zero segments.
    /// </summary>
    private static bool TryDiff(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur,
        List<(ushort offset, ushort length)> segs, out int patchBytes)
    {
        patchBytes = 0;
        int first = segs.Count;
        int i = prev.CommonPrefixLength(cur); // SIMD scan to the first difference
        while (i < Pager.PageSize)
        {
            int start = i;
            while (i < Pager.PageSize && prev[i] != cur[i]) i++; // changed run (short, byte-wise)

            var (lastOff, lastLen) = segs.Count > first ? segs[^1] : ((ushort)0, (ushort)0);
            if (segs.Count > first && start - (lastOff + lastLen) < PatchMergeGap)
            {
                segs[^1] = (lastOff, (ushort)(i - lastOff)); // fold across the small unchanged gap
            }
            else
            {
                if (segs.Count - first == MaxPatchSegments) return false;
                segs.Add(((ushort)start, (ushort)(i - start)));
            }

            if (i >= Pager.PageSize) break;
            i += prev[i..].CommonPrefixLength(cur[i..]); // skip the unchanged run
        }

        for (int s = first; s < segs.Count; s++) patchBytes += segs[s].length;
        return true;
    }

    /// <summary>
    /// Discards the log after its pages are durably applied to the main file. The caller must
    /// fsync the main file <i>first</i>. No fsync here: a lost truncation only makes recovery
    /// replay pages the main file already holds, which is idempotent.
    /// </summary>
    public void Checkpoint()
    {
        _file.SetLength(0);
        Volatile.Write(ref _length, 0);
    }

    /// <summary>
    /// Replays every complete committed transaction, in commit order, via <paramref name="apply"/>.
    /// A patch entry needs its page's prior content to apply onto: the latest image already
    /// replayed for that page, or — for the first touch — <paramref name="readPage"/>, which must
    /// return the page's current <b>main-file</b> content as a fresh mutable array (that is the
    /// state every diff was taken against). Stops at the first torn or checksum-mismatched record
    /// (nothing at or past a tear was ever acknowledged). Returns the number of transactions
    /// replayed. Does <b>not</b> clear the log — the caller checkpoints after making the replayed
    /// pages durable in the main file.
    /// </summary>
    public int Recover(Func<uint, byte[]> readPage, Action<uint, byte[]> apply)
    {
        if (_file.Length < 12) return 0; // too small to hold a valid record

        var all = new byte[_file.Length];
        _file.Seek(0, SeekOrigin.Begin);
        int read = 0;
        while (read < all.Length)
        {
            int n = _file.Read(all, read, all.Length - read);
            if (n == 0) break;
            read += n;
        }

        // Latest replayed image per page, so chained patches apply onto this recovery's own
        // results rather than re-reading the (not yet rewritten) main file.
        var live = new Dictionary<uint, byte[]>();

        int replayed = 0;
        int pos = 0;
        while (TryParseRecord(all, pos, out var entries, out int recordLen))
        {
            foreach (var (id, image, patches) in entries)
            {
                byte[] page;
                if (image is not null)
                {
                    page = image;
                }
                else
                {
                    if (!live.TryGetValue(id, out page!)) page = readPage(id);
                    foreach (var (offset, bytes) in patches!)
                        bytes.CopyTo(page.AsSpan(offset));
                }
                live[id] = page;
                apply(id, page);
            }
            replayed++;
            pos += recordLen;
        }
        return replayed;
    }

    private static bool TryParseRecord(
        byte[] all, int start,
        out List<(uint id, byte[]? image, List<(int offset, byte[] bytes)>? patches)> entries,
        out int recordLen)
    {
        entries = new List<(uint, byte[]?, List<(int, byte[])>?)>();
        recordLen = 0;
        if (all.Length - start < 12) return false;

        uint pageCount = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(start));
        if (pageCount > 1_000_000) return false;

        // One walk both bounds-checks the variable-length entries and materialises them (images
        // get a zeroed gap; patches keep their segments for the caller to apply in order). The
        // record only counts once the commit marker and CRC at the end validate; any failure
        // means a torn record, and the entries built so far are simply discarded — recovery is
        // cold, so throwaway work on the (only ever final) torn record costs nothing.
        int pos = start + 4;
        for (uint i = 0; i < pageCount; i++)
        {
            if (all.Length - pos < PageDescSize) return false;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(pos));
            int headLen = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(pos + 4));
            if (headLen == PatchSentinel)
            {
                int segCount = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(pos + 6));
                pos += PageDescSize;
                var patches = new List<(int, byte[])>(segCount);
                for (int s = 0; s < segCount; s++)
                {
                    if (all.Length - pos < PatchDescSize) return false;
                    int offset = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(pos));
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(pos + 2));
                    if (length == 0 || offset + length > Pager.PageSize) return false;
                    long next = (long)pos + PatchDescSize + length;
                    if (next > all.Length) return false;
                    patches.Add((offset, all.AsSpan(pos + PatchDescSize, length).ToArray()));
                    pos = (int)next;
                }
                entries.Add((id, null, patches));
            }
            else
            {
                int tailStart = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(pos + 6));
                if (headLen > Pager.PageSize || tailStart < headLen || tailStart > Pager.PageSize) return false;
                long next = (long)pos + PageDescSize + headLen + (Pager.PageSize - tailStart);
                if (next > all.Length) return false;

                var data = new byte[Pager.PageSize];
                all.AsSpan(pos + PageDescSize, headLen).CopyTo(data);
                all.AsSpan(pos + PageDescSize + headLen, Pager.PageSize - tailStart).CopyTo(data.AsSpan(tailStart));
                entries.Add((id, data, null));
                pos = (int)next;
            }
        }

        if (all.Length - pos < 8) return false;
        uint marker = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(pos));
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(pos + 4));
        if (marker != CommitMarker) return false;
        if (Crc32.Compute(all.AsSpan(start, pos - start)) != crc) return false;

        recordLen = pos + 8 - start;
        return true;
    }

    public void Dispose() => _file.Dispose();
}
