using System.Buffers.Binary;
using System.Collections.Concurrent;
using KvStore.BTree;
using KvStore.Wal;
using Microsoft.Win32.SafeHandles;

namespace KvStore.Paging;

/// <summary>
/// Owns the database file as an array of fixed-size pages and provides a write-back
/// buffer pool on top of it.
///
/// Page 0 is the <b>meta page</b>: it records the catalog tree's root, the head of the free-page
/// list, how many pages the file currently holds, and the last committed transaction timestamp.
/// Pages handed out by
/// <see cref="AllocatePage"/> are reused from the free list when possible, otherwise the
/// file is extended.
///
/// <para><b>Five page layers</b>, checked in this order on read so a caller always sees the
/// freshest version: <c>_dirty</c> (the in-flight transaction's writes), <c>_staged</c>
/// (transactions that succeeded but have not been flushed yet — async/manual modes),
/// <c>_flushing</c> (the snapshot a flush in progress is writing to the log), <c>_walPages</c>
/// (pages durable in the WAL but not yet applied to the main file), and <c>_clean</c> (a bounded
/// cache of main-file-durable pages with CLOCK eviction). Reads fall through to the file and
/// populate <c>_clean</c>.</para>
///
/// <para><b>Read-path concurrency:</b> <see cref="GetReadable"/> takes no lock. <c>_dirty</c> and
/// <c>_staged</c> are only mutated under the engine's exclusive write lock, so concurrent readers
/// see them read-only; <c>_flushing</c>/<c>_walPages</c> are concurrent dictionaries mutated only
/// by the single flush leader; <c>_clean</c> is a concurrent dictionary whose CLOCK bookkeeping
/// is an atomic touch bit on hits — only misses and checkpoint promotion take <c>_cacheLock</c>.
/// File I/O is positional (<see cref="RandomAccess"/>), so there is no shared stream position.</para>
///
/// <para>Durability is delegated to the <see cref="WriteAheadLog"/>: <see cref="PrepareFlush"/>
/// (under the write lock) snapshots the staged set, and <see cref="CommitFlush"/> makes it
/// durable with one log append + fsync and parks it in <c>_walPages</c>;
/// <see cref="Checkpoint"/> later applies those pages to the main file in bulk.</para>
/// </summary>
internal sealed class Pager : IDisposable
{
    public const int PageSize = 4096;
    public const uint NullPage = uint.MaxValue;
    public const uint MetaPageId = 0;

    /// <summary>Smallest clean-cache size we allow, regardless of the configured byte budget,
    /// so a single root-to-leaf descent never thrashes itself out of cache.</summary>
    public const int MinCachePages = 16;

    private const ulong Magic = 0x_4B56_5354_4F52_4532; // "KVSTORE2" (v2: catalog + timestamp)

    // Meta page field offsets.
    private const int MetaMagicOffset = 0;        // ulong
    private const int MetaPageSizeOffset = 8;     // uint
    private const int MetaCatalogRootOffset = 12; // uint
    private const int MetaFreeListOffset = 16;    // uint
    private const int MetaPageCountOffset = 20;   // uint
    private const int MetaTimestampOffset = 24;   // long
    private const int MetaUsedBytes = MetaTimestampOffset + 8; // everything after is zero

    private readonly SafeFileHandle _file;
    private readonly WriteAheadLog _wal;

    // Bounded cache of main-file-durable pages. Hits are lock-free (concurrent dictionary
    // lookup + atomic touch bit); inserts and CLOCK eviction serialise on _cacheLock. The ring
    // holds every cached entry; the hand sweeps it second-chance style.
    private readonly int _maxCachePages;
    private readonly ConcurrentDictionary<uint, CacheEntry> _clean = new();
    private readonly List<CacheEntry> _clock = new();
    private int _clockHand;
    private readonly object _cacheLock = new();

    // Pages written by the current transaction (mutated in place across the txn), and pages from
    // earlier transactions that committed in memory but are not yet flushed to disk (async mode).
    // Both are only ever mutated under the engine's exclusive write lock.
    private readonly Dictionary<uint, byte[]> _dirty = new();
    private readonly Dictionary<uint, byte[]> _staged = new();

    // The snapshot an in-progress flush is appending to the log (moved out of _staged under the
    // write lock, written + fsync'd outside it), and pages that are durable in the WAL but not
    // yet applied to the main file. Concurrent dictionaries because the flush leader publishes
    // to them while readers run. _walPages entries must never be evicted — until checkpointed,
    // the main file holds stale images.
    private readonly ConcurrentDictionary<uint, byte[]> _flushing = new();
    private readonly ConcurrentDictionary<uint, byte[]> _walPages = new();

    // Read-path gates for the two concurrent layers: 0 means the layer is empty and readers may
    // skip its dictionary probe. A gate is raised (volatile) BEFORE the first page enters the
    // layer and lowered only AFTER every page has been republished to the next layer down, so a
    // reader that sees 0 is guaranteed to find every page further down the chain.
    private int _flushingGate;
    private int _walGate;

    /// <summary>Checkpoint (apply WAL pages to the main file and truncate the log) once the log
    /// grows past this size, bounding both WAL disk usage and recovery time.</summary>
    internal const long CheckpointWalBytes = 4L * 1024 * 1024;

    // Meta fields as of the start of the current transaction, restored on rollback.
    private (uint catalogRoot, uint free, uint pages, long timestamp)? _metaSnapshot;

    // Recycled 4 KB page buffers. The copy into _dirty is the engine's hottest allocation (every
    // first touch of a page per transaction); update-heavy workloads replace the same staged pages
    // over and over, so the replaced arrays feed later transactions' copies instead of the GC.
    // Only arrays whose sole owner was _dirty (on rollback) or _staged (replaced on stage) are
    // recycled — never anything published to _flushing/_walPages/_clean, which readers may hold.
    // Rent/recycle only run under the engine's write lock, so the plain Stack is safe.
    private readonly Stack<byte[]> _pagePool = new();
    private const int MaxPooledPages = 4096; // 16 MB ceiling; beyond that, let the GC have them

    private byte[] RentPageBuffer()
        => _pagePool.TryPop(out var buf) ? buf : GC.AllocateUninitializedArray<byte>(PageSize);

    private void RecyclePageBuffer(byte[] buf)
    {
        if (_pagePool.Count < MaxPooledPages) _pagePool.Push(buf);
    }

    /// <summary>Root of the catalog tree (table name -> table record); the one tree root the
    /// meta page still tracks directly. Named tables' roots live in catalog records.</summary>
    public TreeRoot CatalogRoot { get; } = new();

    public uint FreeListHead { get; set; }
    public uint PageCount { get; set; }

    /// <summary>The timestamp of the last committed engine transaction, persisted in the meta
    /// page (and therefore crash-safe alongside the data it stamped).</summary>
    public long CommitTimestamp { get; set; }

    /// <summary>Bytes the database currently occupies on disk: the main file plus the
    /// write-ahead-log sidecar. Read from the open handles (the files are exclusively locked),
    /// safe from any thread, and a point-in-time snapshot — the WAL portion shrinks to zero at
    /// every checkpoint.</summary>
    public long TotalDiskBytes => RandomAccess.GetLength(_file) + _wal.Length;

    // Exposed for tests: cache occupancy, unflushed-page count, and WAL-durable-page count.
    internal int CleanCacheCount => _clean.Count;
    internal int StagedPageCount => _staged.Count;
    internal int WalPageCount => _walPages.Count;

    private sealed class CacheEntry(uint id, byte[] data)
    {
        public readonly uint Id = id;
        public byte[] Data = data;   // accessed with Volatile: replaced in place on re-cache
        public int Touched;          // CLOCK reference bit; set lock-free on every hit
    }

    private Pager(SafeFileHandle file, WriteAheadLog wal, int maxCachePages)
    {
        _file = file;
        _wal = wal;
        _maxCachePages = maxCachePages;
    }

    public static Pager Open(string path, long cacheSizeBytes)
    {
        var walPath = path + "-wal";
        bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

        int maxPages = (int)Math.Min(int.MaxValue, Math.Max(MinCachePages, cacheSizeBytes / PageSize));

        var file = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var wal = new WriteAheadLog(walPath);
        var pager = new Pager(file, wal, maxPages);

        // Recover any committed-but-unapplied transactions before reading meta, so the
        // file is consistent regardless of when the last crash happened. The replayed pages
        // must be durable in the main file *before* the log is truncated, or a crash between
        // the two would lose them.
        if (wal.Recover(pager.ReadFromFile, (pageId, data) => pager.WriteThroughToFile(pageId, data)) > 0)
            RandomAccess.FlushToDisk(file);
        wal.Checkpoint();

        if (isNew && RandomAccess.GetLength(file) == 0)
        {
            pager.InitializeNewFile();
        }
        else
        {
            pager.LoadMeta();
        }
        return pager;
    }

    private void InitializeNewFile()
    {
        // Page 0 = meta; every tree (catalog included) materialises its root lazily on first
        // insert, so a fresh file is a single page.
        PageCount = 1;
        FreeListHead = NullPage;
        CatalogRoot.PageId = NullPage;
        CommitTimestamp = 0;

        var meta = new byte[PageSize];
        FlushMetaToBuffer(meta);

        // Write directly; the empty database is its own consistent state.
        WriteThroughToFile(MetaPageId, meta);
        RandomAccess.FlushToDisk(_file);
        CleanPut(MetaPageId, meta);
    }

    private void LoadMeta()
    {
        var meta = ReadFromFile(MetaPageId);
        ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(meta.AsSpan(MetaMagicOffset));
        if (magic != Magic)
            throw new InvalidDataException("Not a KvStore database file (bad magic number).");
        uint pageSize = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(MetaPageSizeOffset));
        if (pageSize != PageSize)
            throw new InvalidDataException($"Database uses page size {pageSize}, expected {PageSize}.");

        CatalogRoot.PageId = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(MetaCatalogRootOffset));
        FreeListHead = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(MetaFreeListOffset));
        PageCount = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(MetaPageCountOffset));
        CommitTimestamp = BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(MetaTimestampOffset));
        CleanPut(MetaPageId, meta);
    }

    private void FlushMetaToBuffer(byte[] meta)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(meta.AsSpan(MetaMagicOffset), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(MetaPageSizeOffset), PageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(MetaCatalogRootOffset), CatalogRoot.PageId);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(MetaFreeListOffset), FreeListHead);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(MetaPageCountOffset), PageCount);
        BinaryPrimitives.WriteInt64LittleEndian(meta.AsSpan(MetaTimestampOffset), CommitTimestamp);
    }

    /// <summary>Returns a read-only view of a page. Never mutate the returned array.</summary>
    public byte[] GetReadable(uint pageId)
    {
        // Lock-free: see the class remarks. Count guards keep the read-only hot path from probing
        // plain dictionaries that are empty. The concurrent dictionaries are probed directly: their
        // TryGetValue is a lock-free bucket read, whereas IsEmpty locks every bucket when empty.
        if (_dirty.Count > 0 && _dirty.TryGetValue(pageId, out var d)) return d;
        if (_staged.Count > 0 && _staged.TryGetValue(pageId, out var s)) return s;
        if (Volatile.Read(ref _flushingGate) != 0 && _flushing.TryGetValue(pageId, out var f)) return f;
        if (Volatile.Read(ref _walGate) != 0 && _walPages.TryGetValue(pageId, out var w)) return w;
        if (_clean.TryGetValue(pageId, out var entry))
        {
            Volatile.Write(ref entry.Touched, 1);
            return Volatile.Read(ref entry.Data);
        }
        var data = ReadFromFile(pageId);
        CleanPut(pageId, data);
        return data;
    }

    /// <summary>
    /// Returns a writable copy of a page, registering it in the dirty set so it is
    /// persisted on the next flush. Copies out of the lower layers so neither the staged set
    /// nor the clean cache is ever mutated in place.
    /// </summary>
    public byte[] GetWritable(uint pageId)
    {
        if (_dirty.TryGetValue(pageId, out var d)) return d;
        var src = GetReadable(pageId);
        var copy = RentPageBuffer(); // fully overwritten by the copy before it escapes
        Array.Copy(src, copy, PageSize);
        _dirty[pageId] = copy;
        return copy;
    }

    /// <summary>Allocates a fresh page, reusing a freed page when available.</summary>
    public uint AllocatePage()
    {
        uint id;
        if (FreeListHead != NullPage)
        {
            id = FreeListHead;
            var page = GetReadable(id);
            FreeListHead = BinaryPrimitives.ReadUInt32LittleEndian(page); // next free pointer
        }
        else
        {
            id = PageCount;
            PageCount++;
        }
        // Hand back a zeroed, writable page.
        var buf = RentPageBuffer();
        Array.Clear(buf);
        _dirty[id] = buf;
        return id;
    }

    /// <summary>Returns a page to the free list for later reuse.</summary>
    public void FreePage(uint pageId)
    {
        var buf = GetWritable(pageId);
        Array.Clear(buf);
        BinaryPrimitives.WriteUInt32LittleEndian(buf, FreeListHead);
        FreeListHead = pageId;
    }

    // ---- Transaction boundaries -------------------------------------------

    /// <summary>Records the meta state at the start of a transaction so <see cref="RollbackTransaction"/>
    /// can undo exactly this transaction's effect, even when earlier ones are still staged.</summary>
    public void BeginTransaction()
        => _metaSnapshot = (CatalogRoot.PageId, FreeListHead, PageCount, CommitTimestamp);

    /// <summary>Promotes the current transaction's writes to the staged layer (committed in
    /// memory). They become durable only on the next flush.</summary>
    public void StageTransaction()
    {
        foreach (var (id, data) in _dirty)
        {
            // The replaced staged copy's only owner was _staged (it never reached the flushing/
            // WAL/clean layers, and no reader runs while we hold the write lock) — recycle it.
            if (_staged.TryGetValue(id, out var old) && !ReferenceEquals(old, data))
                RecyclePageBuffer(old);
            _staged[id] = data;
        }
        _dirty.Clear();
        _metaSnapshot = null;
    }

    /// <summary>Discards the current transaction's writes and restores the meta fields,
    /// leaving any earlier staged transactions intact.</summary>
    public void RollbackTransaction()
    {
        foreach (var (_, data) in _dirty)
            RecyclePageBuffer(data); // this txn's private copies; nothing else ever saw them
        _dirty.Clear();
        if (_metaSnapshot is { } s)
        {
            CatalogRoot.PageId = s.catalogRoot;
            FreeListHead = s.free;
            PageCount = s.pages;
            CommitTimestamp = s.timestamp;
        }
        _metaSnapshot = null;
    }

    /// <summary>
    /// Snapshots the staged set (plus an up-to-date meta page) into the flushing layer.
    /// <b>Must run under the engine's exclusive write lock</b> so the meta fields and staged
    /// pages are a consistent cut. Returns false when there is nothing to flush. The snapshot
    /// stays readable through <see cref="GetReadable"/> until <see cref="CommitFlush"/> parks it
    /// in the WAL layer.
    /// </summary>
    public bool PrepareFlush()
    {
        // Fold any still-open transaction into the staged set (defensive; callers normally stage first).
        if (_dirty.Count > 0) StageTransaction();
        if (_staged.Count == 0) return false;

        // Make sure the staged set carries an up-to-date meta page.
        var meta = StagedWritableMeta();
        FlushMetaToBuffer(meta);

        Volatile.Write(ref _flushingGate, 1); // raise before the first page enters the layer
        foreach (var (id, data) in _staged)
            _flushing[id] = data;
        _staged.Clear();
        return true;
    }

    /// <summary>
    /// Makes the prepared snapshot durable: one WAL append + fsync (the commit point), then parks
    /// the pages in the wal-page layer until the next checkpoint. Runs <b>without</b> the write
    /// lock; the caller serialises flushes (only one CommitFlush at a time).
    /// </summary>
    public void CommitFlush()
    {
        if (_flushing.IsEmpty) return;

        // 1. Make the new page images durable in the log — the one fsync on the commit path.
        // UsedExtent lets the log skip each page's dead gap (and the meta page's huge zero tail);
        // PreviousDurableImage lets it log scattered small updates as byte patches instead of
        // full images (and skip pages that didn't effectively change).
        _wal.WriteTransaction(_flushing, UsedExtent, PreviousDurableImage);

        // 2. Park them in the wal-page layer. Per page: publish to _walPages first, then drop
        // from _flushing, so a concurrent reader always finds the image in one layer or the other.
        // The wal gate is raised before the first publish (a reader that misses _flushing must
        // already see the _walPages layer as live); the flushing gate falls only once empty.
        Volatile.Write(ref _walGate, 1);
        foreach (var (id, data) in _flushing)
        {
            _walPages[id] = data;
            _flushing.TryRemove(id, out _);
        }
        Volatile.Write(ref _flushingGate, 0);

        // 3. Bound the log: once it is large enough, apply everything to the main file in bulk.
        if (_wal.Length >= CheckpointWalBytes) Checkpoint();
    }

    /// <summary>
    /// Applies every WAL-durable page to the main file, fsyncs it, truncates the log, and promotes
    /// the pages into the clean cache. Called when the log passes its size threshold and on
    /// <see cref="Dispose"/>. Safe alongside concurrent readers; the caller serialises it with
    /// other flushes/checkpoints (the flush leader, or exclusivity at dispose time).
    /// </summary>
    public void Checkpoint()
    {
        if (_walPages.IsEmpty) return;

        foreach (var (id, data) in _walPages)
            WriteThroughToFile(id, data);
        RandomAccess.FlushToDisk(_file);

        // The main file now holds everything in the log; drop the log. (No WAL fsync needed:
        // if the truncation is lost, recovery replays images the main file already has.)
        _wal.Checkpoint();

        // The pages are durable in the main file — they may live in the evictable cache now.
        // Per page: promote to clean first, then unpark, so readers never miss both layers.
        foreach (var (id, data) in _walPages)
        {
            CleanPut(id, data);
            _walPages.TryRemove(id, out _);
        }
        Volatile.Write(ref _walGate, 0); // layer empty; readers may skip its probe again
    }

    /// <summary>Used byte ranges of a page for WAL framing: the meta page is a fixed prefix;
    /// node pages report header+slots / cell area; anything unrecognisable is fully used.</summary>
    private static (int headLen, int tailStart) UsedExtent(uint id, byte[] page)
    {
        if (id == MetaPageId) return (MetaUsedBytes, PageSize);
        if (Node.TryGetUsedExtents(page, out int headLen, out int tailStart)) return (headLen, tailStart);
        return (PageSize, PageSize);
    }

    /// <summary>
    /// The page's previous <b>durable</b> image, if it is in memory: the version in the WAL layer
    /// (durable in the log) or the clean cache (durable in the main file) — exactly the content
    /// recovery would reconstruct for this page before the record being written, which makes it
    /// the correct diff base for WAL patch entries. Null when neither holds the page (recently
    /// evicted, or brand new); the log then falls back to a full image.
    /// </summary>
    private byte[]? PreviousDurableImage(uint id)
        => _walPages.TryGetValue(id, out var w) ? w
         : _clean.TryGetValue(id, out var entry) ? Volatile.Read(ref entry.Data)
         : null;

    private byte[] StagedWritableMeta()
    {
        if (_staged.TryGetValue(MetaPageId, out var existing)) return existing;
        var src = GetReadable(MetaPageId);
        var copy = new byte[PageSize];
        Array.Copy(src, copy, PageSize);
        _staged[MetaPageId] = copy;
        return copy;
    }

    /// <summary>
    /// Empties the clean-page cache and the page-buffer pool, releasing their memory. Only
    /// main-file-durable pages live in the clean cache, so dropping them is always safe — a later
    /// read re-fetches from disk and repopulates. The layers holding not-yet-durable or
    /// not-yet-checkpointed pages (<c>_dirty</c>/<c>_staged</c>/<c>_flushing</c>/<c>_walPages</c>)
    /// are untouched, so clearing never affects durability. Safe alongside concurrent readers
    /// (their <c>_clean</c> probes are lock-free and a miss just re-reads the file); the caller
    /// must hold the engine's write lock so the pool — which is write-lock-owned — can be dropped.
    /// </summary>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _clean.Clear();
            _clock.Clear();
            _clockHand = 0;
        }
        _pagePool.Clear();
    }

    // ---- Clean cache (CLOCK) ----------------------------------------------

    private void CleanPut(uint pageId, byte[] data)
    {
        lock (_cacheLock)
        {
            if (_clean.TryGetValue(pageId, out var existing))
            {
                Volatile.Write(ref existing.Data, data);
                Volatile.Write(ref existing.Touched, 1);
                return;
            }

            var entry = new CacheEntry(pageId, data);
            if (_clock.Count < _maxCachePages)
            {
                _clock.Add(entry);
            }
            else
            {
                // Second-chance sweep: clear touch bits until an untouched victim turns up.
                // Bounded by two passes — the first pass clears every bit at worst.
                while (true)
                {
                    var candidate = _clock[_clockHand];
                    if (Volatile.Read(ref candidate.Touched) == 1)
                    {
                        Volatile.Write(ref candidate.Touched, 0);
                        _clockHand = (_clockHand + 1) % _clock.Count;
                        continue;
                    }
                    _clean.TryRemove(candidate.Id, out _);
                    _clock[_clockHand] = entry;
                    _clockHand = (_clockHand + 1) % _clock.Count;
                    break;
                }
            }
            _clean[pageId] = entry;
        }
    }

    // ---- File I/O (positional, no shared stream position) ------------------

    private byte[] ReadFromFile(uint pageId)
    {
        var buf = new byte[PageSize];
        long offset = (long)pageId * PageSize;
        int read = 0;
        while (read < PageSize)
        {
            int n = RandomAccess.Read(_file, buf.AsSpan(read), offset + read);
            if (n == 0) break; // reading past EOF yields a zeroed page
            read += n;
        }
        return buf;
    }

    private void WriteThroughToFile(uint pageId, byte[] data)
        => RandomAccess.Write(_file, data, (long)pageId * PageSize);

    public void Dispose()
    {
        // Leave a clean shutdown with everything in the main file and an empty log. (The owning
        // engine has already flushed; by now no readers or writers remain.)
        Shutdown(durable: true);
    }

    /// <summary>Test hook: drops the file handles with <b>no</b> checkpoint or flush, so the
    /// on-disk main file + log are exactly what a process crash at this moment would leave.
    /// The next <see cref="Open"/> must recover to the last committed state.</summary>
    internal void SimulateCrash() => Shutdown(durable: false);

    // The one teardown sequence; a "crash" is a shutdown that skips the durability step.
    private void Shutdown(bool durable)
    {
        if (durable) Checkpoint();
        _wal.Dispose();
        _file.Dispose();
    }
}
