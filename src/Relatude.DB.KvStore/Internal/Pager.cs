using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Relatude.DB.Common;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

internal readonly record struct Meta(long TxId, long Timestamp, uint CatalogRoot, uint FreelistHead, uint PageCount);

/// <summary>
/// Page-oriented file manager implementing shadow paging (copy-on-write):
/// pages are never modified in place; a commit writes new pages first and then
/// atomically switches between two checksummed meta pages. Pages freed by a
/// transaction are recycled only once no active reader snapshot can reach them.
/// The freelist itself is persisted as a page chain rewritten on each durable commit,
/// always into freshly grown pages so it can never clobber live data.
/// <para>
/// Commits are split in two: <see cref="Publish"/> applies a transaction (pages written,
/// in-memory meta advanced) without touching the durable meta pages, and <see cref="MakeDurable"/>
/// persists the latest published state (freelist chain + meta page). <see cref="Commit"/> does both.
/// After a crash the file reopens at the last durable meta: published-only transactions vanish
/// cleanly, because pages they freed are still referenced by that meta (shadow paging) and pages
/// they allocated are either beyond its page count or listed free in its freelist chain.
/// The recycle gate below (<see cref="PromoteFreeBatches"/>) is what keeps that true: a page freed
/// after the last durable meta must not be reused until the next durable meta lands, or a crash
/// would leave the durable meta pointing at overwritten pages.
/// </para>
/// </summary>
internal sealed class Pager : IPageSource, IDisposable
{
    public const int PageSize = 4096;
    private const ulong Magic = 0x5346495830303032ul; // "SFIX0002" (0002: subtree entry counts in branch pages)
    private const ulong MagicV1 = 0x5346495830303031ul; // "SFIX0001" — pre-count page format, no longer readable
    private const int MetaPayload = 40;

    private readonly SafeFileHandle? _handle;
    private readonly SafeFileHandle[]? _readHandles;
    private readonly FileStream? _flushStream;
    private readonly Dictionary<uint, byte[]>? _mem; // non-null => memory-only: pages live here, never on disk
    public readonly PageCache Cache;

    /// <summary>True when the pager has no backing file: all pages live in memory and nothing is persisted.</summary>
    public bool IsMemoryOnly => _mem is not null;

    // ---- memory-mapped IO ----
    // Bulk page write-outs go through a mapped view of the file: a 4 KiB buffered WriteFile costs
    // ~2 µs of kernel filter stack per call, a memcpy into the map a third of that — and a spill
    // or durable point can write tens of thousands of scattered pages. Coherence is free on
    // Windows: mapped views and regular read/write IO of the same file share the cache manager's
    // pages, so the striped read handles, the meta writes (which stay on the plain handle) and
    // the map always see each other's bytes. Durability is unchanged: a deep flush does
    // FlushViewOfFile + FlushFileBuffers before the meta page that references the data, and dirty
    // mapped pages survive a process crash exactly like buffered WriteFile ones (the memory
    // manager writes them back).
    //
    // The map is WRITER-ONLY (used under the engine's write lock): cache-miss reads use pread on
    // the striped handles, whose pages are charged to the system cache instead of this process.
    // Written map pages are trimmed out of the working set right after each write-out (see
    // WritePages) for the same reason — a persistent index engine exists to keep memory low, so
    // the process footprint must not scale with the file. Writer-only also means a superseded
    // view can be released the moment the map grows; nothing else can hold it.
    private sealed unsafe class MapState(MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* ptr, long pages)
    {
        public readonly MemoryMappedFile Mmf = mmf;
        public readonly MemoryMappedViewAccessor View = view;
        public readonly byte* Ptr = ptr;
        public readonly long Pages = pages;

        public void Release()
        {
            View.SafeMemoryMappedViewHandle.ReleasePointer();
            View.Dispose();
            Mmf.Dispose();
        }
    }

    private MapState? _map;
    private const long MinMapPages = 512; // 2 MiB floor: below this, mapping churn outweighs the wins

    // ---- deferred page writes ----
    // A publish does not write its pages to the file: they are parked here (and in the page
    // cache) and written when the state is made durable — or earlier, when the parked set
    // exceeds its budget. This is exactly the published-but-not-durable window the shadow-paging
    // contract already defines: the durable meta never references these pages (the recycle gate
    // guarantees it), so a crash before MakeDurable rolls back to it whether the bytes reached
    // the file or not. What deferral buys is coalescing — a page rewritten by five batches is
    // written once, not five times, and a page freed again before durability is written harmlessly
    // at most once. Readers that miss the page cache consult this map before the file, so a
    // parked page is always reachable. Entries are immutable committed pages; the writer mutates
    // the map only under the engine's write lock, readers only look up.
    private readonly ConcurrentDictionary<uint, byte[]> _pendingWrites = new();
    private readonly int _spillPages;

    // Pages written through the map since the last deep flush, so FlushViewOfFile can be asked
    // for exactly those ranges instead of scanning the whole view's PTEs (which turns a 10-page
    // durable commit into a multi-millisecond walk). Overflow falls back to a whole-view flush.
    private List<uint> _unflushedMapPages = new();
    private bool _unflushedOverflow;
    private const int UnflushedTrackingLimit = 262_144; // 1 GiB of 4 KiB pages: past this, one full flush is cheaper than the bookkeeping

    private Meta _meta;                         // latest PUBLISHED state; the durable meta pages may lag behind it
    private long _durableTxId;                  // TxId of the last durably written meta page
    private int _durableSlot;                   // meta slot of the last durable write; toggled per durable write so the two newest durable metas never share a slot
    private uint _pageCount;                    // in-memory high-water mark (monotonic)
    private readonly Queue<uint> _recycled = new();          // reusable right now
    private readonly List<(long TxId, List<uint> Pages)> _pendingFree = new(); // reusable once readers drain AND a durable meta no longer references them
    private List<uint> _freelistChainPages = new();

    // "Young" pages — allocated after the last durable meta — are invisible to that meta (their
    // id is beyond its file span, or listed free in its freelist chain, whose content recovery
    // never reads), so the durable-meta half of the recycle gate does not apply and they recycle
    // on the reader gate alone. That is what keeps a long run of published-but-not-yet-durable
    // transactions — the normal state between WAL flushes — from reusing nothing and growing the
    // file with every copy-on-write. Youth is decided two ways: ids at or beyond the durable
    // meta's page count are young by comparison (so bulk loads track nothing at all), and a
    // recycled id below it is young when it is in _reusedBelowDurable — a set bounded by the
    // durable-era page pool, i.e. by store size, never by how long the publish-only run gets.
    private uint _durablePageCount;
    private readonly HashSet<uint> _reusedBelowDurable = new();
    private readonly List<(long TxId, List<uint> Pages)> _pendingFreeYoung = new();

    public Meta CurrentMeta => _meta;

    /// <param name="path">
    /// Backing file for the database, or <c>null</c> for a memory-only engine that persists nothing
    /// and always starts empty (identical semantics to a freshly created file).
    /// </param>
    /// <param name="pendingWriteBytes">Budget for published-but-unwritten pages; past it they are written out early (without becoming durable).</param>
    public Pager(string? path, long cacheBytes, long pendingWriteBytes = 128L * 1024 * 1024)
    {
        Cache = new PageCache(cacheBytes, PageSize);
        _spillPages = (int)Math.Max(64, pendingWriteBytes / PageSize);

        if (path is null)
        {
            // Memory-only: the dictionary replaces the file. There is no reopen, so the store
            // always begins empty — exactly the "new database" path below, minus the durable writes.
            _mem = new Dictionary<uint, byte[]>();
            _meta = new Meta(TxId: 0, Timestamp: 0, CatalogRoot: 0, FreelistHead: 0, PageCount: 2);
            WriteMetaSlot(0, _meta);
            WriteMetaSlot(1, _meta);
            _pageCount = _meta.PageCount;
            return;
        }

        bool isNew = !File.Exists(path) || new FileInfo(path).Length < 2 * PageSize;
        // FileShare.Read makes this the single writer of the page file, so it is the handle a restart
        // contends with while the previous process is still stopping. Wait it out rather than failing
        // the whole database open - see FileOpenRetry.
        var handle = FileOpenRetry.Open(path, () => File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.Read, FileOptions.RandomAccess));
        _handle = handle;
        _flushStream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1);

        // Windows serializes synchronous positioned reads per file object, so concurrent
        // readers sharing one handle bottleneck in the kernel. Stripe cache misses across
        // several read-only handles (each gets its own file object) by thread id.
        _readHandles = new SafeFileHandle[Math.Clamp(Environment.ProcessorCount, 2, 8)];
        for (int i = 0; i < _readHandles.Length; i++)
            _readHandles[i] = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                FileOptions.RandomAccess);

        if (isNew)
        {
            InitializeEmptyFile();
        }
        else if (IsSupersededFormat())
        {
            // A valid file in an older page format: it cannot be read, but index data is derived —
            // start empty and let the store rebuild everything from the data-store WAL.
            _flushStream.SetLength(2L * PageSize);
            InitializeEmptyFile();
        }
        else
        {
            _meta = LoadNewestValidMeta(out _durableSlot);
            LoadFreelist(_meta.FreelistHead);
        }
        _pageCount = _meta.PageCount;
        _durableTxId = _meta.TxId;
        _durablePageCount = _meta.PageCount;
        // No eager mapping: the map exists for bulk write-outs and is created by the first one.
        // Read-mostly and small-write sessions never map (and never pad) the file at all.
    }

    /// <summary>
    /// Guarantees the mapped view covers at least <paramref name="pagesNeeded"/> pages, growing
    /// the file geometrically so remaps stay rare. Writer-thread only. On any failure (32-bit
    /// address space, exotic file systems) the pager silently stays on plain read/write IO.
    /// </summary>
    private void EnsureMapped(long pagesNeeded)
    {
        if (_mem is not null || Environment.Is64BitProcess == false)
            return;
        MapState? current = _map;
        if (current is not null && current.Pages >= pagesNeeded)
            return;
        try
        {
            // Pad modestly (~12.5%, at least 1 MiB) beyond what is needed: enough that steady
            // growth remaps a handful of times per size doubling — a remap costs tens of
            // microseconds — without inflating the file the way a capacity-doubling policy
            // would. The padding is visible file size until the next clean close trims it.
            long newPages = Math.Max(pagesNeeded + Math.Max(pagesNeeded >> 3, 256), MinMapPages);
            long newLength = newPages * PageSize;

            // A mapping's capacity must cover the entire file, and the physical file can exceed
            // the logical page span: growth padding left behind by a process that never reached
            // the Dispose trim, or pages written beyond the durable meta before a crash. Map all
            // of it (rounded up to a page boundary — extending is always safe, shrinking never is).
            long fileLength = RandomAccess.GetLength(_handle!);
            if (fileLength > newLength)
            {
                newLength = (fileLength + PageSize - 1) / PageSize * PageSize;
                newPages = newLength / PageSize;
            }
            if (fileLength < newLength)
                _flushStream!.SetLength(newLength);

            var mmf = MemoryMappedFile.CreateFromFile(_flushStream!, mapName: null, newLength,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            var view = mmf.CreateViewAccessor(0, newLength, MemoryMappedFileAccess.ReadWrite);
            unsafe
            {
                byte* ptr = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                current?.Release(); // writer-only: nothing else can hold the superseded view
                _map = new MapState(mmf, view, ptr, newPages);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            // No map: WritePages falls back to plain IO.
        }
    }

    private void ReleaseMap()
    {
        if (_map is not null)
        {
            _map.Release();
            _map = null;
        }
    }

    private void InitializeEmptyFile()
    {
        _meta = new Meta(TxId: 0, Timestamp: 0, CatalogRoot: 0, FreelistHead: 0, PageCount: 2);
        WriteMetaSlot(0, _meta);
        WriteMetaSlot(1, _meta);
        _flushStream!.Flush(true);
        _durableTxId = _meta.TxId;
    }

    /// <summary>True when either meta slot is a checksum-valid meta page of a superseded format version.</summary>
    private bool IsSupersededFormat()
    {
        Span<byte> buf = stackalloc byte[PageSize];
        for (int slot = 0; slot < 2; slot++)
        {
            if (RandomAccess.Read(_handle!, buf, (long)slot * PageSize) != PageSize)
                continue;
            if (BinaryPrimitives.ReadUInt64LittleEndian(buf) != MagicV1)
                continue;
            if (BinaryPrimitives.ReadUInt64LittleEndian(buf[MetaPayload..]) == Fnv1a64(buf[..MetaPayload]))
                return true;
        }
        return false;
    }

    // ---- page IO ----

    public byte[] GetPage(uint pageId)
    {
        byte[]? page = Cache.TryGet(pageId);
        if (page is not null)
            return page;
        page = ReadPageFromDisk(pageId);
        Cache.Add(pageId, page);
        return page;
    }

    private byte[] ReadPageFromDisk(uint pageId)
    {
        if (_mem is not null)
        {
            if (!_mem.TryGetValue(pageId, out var mp))
                throw new InvalidDataException($"Page {pageId} is not present in the in-memory store.");
            return mp;
        }

        // Published but not yet written to the file: the pending map is the source of truth.
        // Committed pages are immutable, so handing out the parked array itself is safe.
        if (_pendingWrites.TryGetValue(pageId, out var pending))
            return pending;

        // pread, deliberately not the map: these pages land in the system file cache (shared,
        // evictable, not attributed to this process) instead of growing our working set.
        var buf = GC.AllocateUninitializedArray<byte>(PageSize); // fully overwritten by the read below
        var handle = _readHandles![(uint)Environment.CurrentManagedThreadId % (uint)_readHandles.Length];
        int read = RandomAccess.Read(handle, buf, (long)pageId * PageSize);
        if (read != PageSize)
            throw new InvalidDataException($"Short read of page {pageId} ({read}/{PageSize} bytes). The file is corrupt or truncated.");
        return buf;
    }

    // ---- allocation ----

    /// <summary>
    /// Makes pages freed by transactions no active reader can still see reusable.
    /// Also gated on the last durable meta: a page freed by a published-but-not-yet-durable
    /// transaction is still referenced by the durable meta, and reusing it would corrupt the
    /// state a crash falls back to. (Memory-only engines have no durable meta to protect.)
    /// </summary>
    public void PromoteFreeBatches(long minActiveReaderTxId)
    {
        long gate = _mem is not null ? minActiveReaderTxId : Math.Min(minActiveReaderTxId, _durableTxId);
        Promote(_pendingFree, gate);
        Promote(_pendingFreeYoung, minActiveReaderTxId); // young pages: the durable meta never saw them
    }

    private void Promote(List<(long TxId, List<uint> Pages)> batches, long gate)
    {
        int i = 0;
        while (i < batches.Count)
        {
            if (batches[i].TxId <= gate)
            {
                foreach (uint p in batches[i].Pages)
                {
                    _recycled.Enqueue(p);
                    // A promoted page is free in every state anyone can still reach, so parked
                    // content for it would be written for nothing — drop it. Transient pages
                    // (freed again before a durable point) thus never touch the file at all.
                    _pendingWrites.TryRemove(p, out _);
                }
                batches.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    public uint AllocatePage() => AllocatePage(out _);

    /// <summary>
    /// Allocates a page id. When recycling evicts the old incarnation from the page cache,
    /// <paramref name="reusableBuffer"/> hands its array to the caller: the recycle gate has
    /// already proven no reader can reach it, so reusing it saves both the fresh 4 KiB allocation
    /// and the GC churn of the discarded one. (Memory-only pagers never hand buffers out — there
    /// the cached array IS the store's copy of the old page.)
    /// </summary>
    public uint AllocatePage(out byte[]? reusableBuffer)
    {
        if (_recycled.TryDequeue(out uint id))
        {
            byte[]? evicted = Cache.Invalidate(id); // stale committed content must not be served for the new incarnation
            if (_mem is null)
            {
                if (id < _durablePageCount)
                    _reusedBelowDurable.Add(id); // free in the durable meta, reused after it: young despite its low id
                reusableBuffer = evicted;
            }
            else
            {
                reusableBuffer = null;
            }
            return id;
        }
        reusableBuffer = null;
        return _pageCount++;
    }

    /// <summary>Returns pages allocated by a cancelled transaction to the immediately-reusable pool.</summary>
    public void Recycle(IEnumerable<uint> pages)
    {
        foreach (uint p in pages)
            _recycled.Enqueue(p);
    }

    // ---- commit ----

    public void Commit(long newTxId, long timestamp, uint catalogRoot,
        List<uint> freedByTxn, KeyValuePair<uint, byte[]>[] dirtyPages, bool deepDiskFlush)
    {
        Publish(newTxId, timestamp, catalogRoot, freedByTxn, dirtyPages);
        MakeDurable(deepDiskFlush);
    }

    /// <summary>
    /// Applies a transaction without writing the durable meta: pages land in the file (or memory),
    /// the in-memory meta advances, but a crash rolls back to the last <see cref="MakeDurable"/>.
    /// Pages freed here stay quarantined (see <see cref="PromoteFreeBatches"/>) until the next
    /// durable meta no longer references them.
    /// </summary>
    public void Publish(long newTxId, long timestamp, uint catalogRoot,
        List<uint> freedByTxn, KeyValuePair<uint, byte[]>[] dirtyPages)
    {
        if (freedByTxn.Count > 0)
        {
            if (_mem is not null)
            {
                _pendingFree.Add((newTxId, freedByTxn));
            }
            else
            {
                // Freed pages the last durable meta has never referenced recycle on the reader
                // gate alone; the rest must additionally outlive that meta.
                List<uint>? young = null, old = null;
                foreach (uint p in freedByTxn)
                    ((p >= _durablePageCount || _reusedBelowDurable.Contains(p) ? young ??= new() : old ??= new())).Add(p);
                if (young is not null)
                    _pendingFreeYoung.Add((newTxId, young));
                if (old is not null)
                    _pendingFree.Add((newTxId, old));
            }
        }

        if (_mem is not null)
        {
            // Committed pages are immutable until freed and reallocated (which writes a fresh
            // array), so storing the reference is safe — no copy needed.
            foreach (var (id, page) in dirtyPages)
                _mem[id] = page;
        }
        else
        {
            // Deferred: pages are parked and written at MakeDurable (or at the spill below),
            // deduplicating every page that is rewritten before then. Readers reach parked
            // pages through the page cache or the pending map.
            foreach (var (id, page) in dirtyPages)
                _pendingWrites[id] = page;
        }

        _meta = new Meta(newTxId, timestamp, catalogRoot, _meta.FreelistHead, _pageCount);

        // Populate the cache with the (hot) just-written pages — unless the batch is large
        // relative to the cache, where doing so would evict everything a reader has warm
        // and spend the whole commit thrashing the eviction sweep.
        if (dirtyPages.Length <= Cache.Capacity / 2)
        {
            foreach (var (id, page) in dirtyPages)
                Cache.Add(id, page);
        }

        if (_mem is null && _pendingWrites.Count > _spillPages)
            WritePendingPages(); // early persistence, not durability: the meta still lags
    }

    /// <summary>
    /// Persists the latest published state: writes a fresh freelist chain covering everything
    /// pending, flushes all pages written since the last durable meta, then writes the meta page.
    /// No-op when nothing was published since the last durable write (and for memory-only pagers).
    /// </summary>
    public void MakeDurable(bool deepDiskFlush)
    {
        if (_mem is not null)
            return;
        if (_meta.TxId == _durableTxId)
            return;

        WritePendingPages(); // everything published since the last durable point, deduplicated

        // The previous durable freelist chain becomes garbage once the new meta lands, but the
        // current durable meta still references it — quarantine it like any published free.
        var freedNow = new List<uint>(_freelistChainPages);
        var chainPages = new Dictionary<uint, byte[]>();
        uint freelistHead = WriteFreelistChain(_meta.TxId, freedNow, chainPages);
        if (freedNow.Count > 0)
            _pendingFree.Add((_meta.TxId, freedNow));

        WriteDirtyPages(chainPages);
        if (deepDiskFlush)
            FlushMapRanges(); // dirty mapped pages must reach the file system before FlushFileBuffers can order them to media
        _flushStream!.Flush(deepDiskFlush); // data must be durable before the meta that references it

        _meta = _meta with { FreelistHead = freelistHead, PageCount = _pageCount };
        _durableSlot ^= 1;
        WriteMetaSlot(_durableSlot, _meta);
        _flushStream!.Flush(deepDiskFlush);
        _durableTxId = _meta.TxId;
        _durablePageCount = _meta.PageCount; // everything below is now referenced (or listed free) by the new durable meta
        _reusedBelowDurable.Clear();
    }

    /// <summary>
    /// Bytes the database logically occupies (its page high-water mark). The physical file may be
    /// padded beyond this while open — mapped-write capacity is grown in large steps — and is
    /// trimmed back to this size on <see cref="Dispose"/>.
    /// </summary>
    public long FileLength => (long)_pageCount * PageSize;

    /// <summary>
    /// Wipes the database back to a freshly created state: empty catalog, empty freelist,
    /// file truncated to the two meta pages. The txid stays monotonic (a wipe is a state
    /// change like any commit). Caller must guarantee no transaction and no active readers.
    /// </summary>
    public void Reset()
    {
        _recycled.Clear();
        _pendingFree.Clear();
        _pendingFreeYoung.Clear();
        _durablePageCount = 2;
        _reusedBelowDurable.Clear();
        _pendingWrites.Clear();
        _unflushedMapPages.Clear();
        _unflushedOverflow = false;
        _freelistChainPages = new List<uint>();
        _pageCount = 2;
        Cache.Clear();

        _meta = new Meta(_meta.TxId + 1, Timestamp: 0, CatalogRoot: 0, FreelistHead: 0, PageCount: 2);
        _durableTxId = _meta.TxId;
        if (_mem is not null)
        {
            _mem.Clear();
            WriteMetaSlot(0, _meta);
            WriteMetaSlot(1, _meta);
            return;
        }
        ReleaseMap(); // a mapped region cannot be truncated away
        WriteMetaSlot(0, _meta);
        WriteMetaSlot(1, _meta); // both slots: the newest-valid scan must not resurrect old state
        _flushStream!.Flush(true);
        _flushStream.SetLength(2L * PageSize); // release the disk space
        _flushStream.Flush(true);
    }

    /// <summary>Timestamp-only commit used by <c>SetTimestamp</c>; keeps roots unchanged. Routed
    /// through <see cref="MakeDurable"/> so any published-but-not-durable state lands with it.</summary>
    public void CommitMetaOnly(long newTxId, long timestamp, bool deepDiskFlush)
    {
        _meta = _meta with { TxId = newTxId, Timestamp = timestamp, PageCount = _pageCount };
        MakeDurable(deepDiskFlush);
    }

    private void WriteDirtyPages(Dictionary<uint, byte[]> dirty)
    {
        if (dirty.Count == 0)
            return;
        if (_mem is not null)
        {
            // Committed pages are immutable until freed and reallocated (which writes a fresh
            // array), so storing the reference is safe — no copy needed.
            foreach (var (id, page) in dirty)
                _mem[id] = page;
            return;
        }
        var pages = new KeyValuePair<uint, byte[]>[dirty.Count];
        ((ICollection<KeyValuePair<uint, byte[]>>)dirty).CopyTo(pages, 0);
        WritePages(pages);
    }

    /// <summary>Writes the parked published pages to the file and empties the pending map. Not a durability point.</summary>
    private void WritePendingPages()
    {
        if (_pendingWrites.IsEmpty)
            return;
        // The writer (who holds the engine's write lock) is the only mutator, so this snapshot is
        // exact. Clearing afterwards is safe for concurrent readers: the file already holds the
        // same bytes, and the map writes happen-before the clear they would have to miss on.
        WritePages(_pendingWrites.ToArray());
        _pendingWrites.Clear();
    }

    /// <summary>Small batches take this many pages at most through plain buffered writes: a later
    /// deep flush then needs no FlushViewOfFile at all (FlushFileBuffers covers WriteFile-dirtied
    /// cache pages by itself), and below this size the syscalls cost less than the view flush.</summary>
    private const int SmallBatchPages = 512;

    /// <summary>Disk-mode page write: one memcpy per page through the mapped view — a third of the
    /// cost of a buffered WriteFile per page — for bulk batches; plain buffered IO for small ones
    /// (cheaper to make durable, see <see cref="SmallBatchPages"/>) and for when mapping failed.</summary>
    private void WritePages(KeyValuePair<uint, byte[]>[] pages)
    {
        if (pages.Length == 0)
            return;
        if (pages.Length > SmallBatchPages)
            EnsureMapped(_pageCount);
        MapState? map = _map;
        if (pages.Length > SmallBatchPages && map is not null && _pageCount <= map.Pages)
        {
            uint minId = uint.MaxValue, maxId = 0;
            unsafe
            {
                foreach (var (id, page) in pages)
                {
                    page.CopyTo(new Span<byte>(map.Ptr + (long)id * PageSize, PageSize));
                    if (id < minId) minId = id;
                    if (id > maxId) maxId = id;
                }

                // Trim the written span out of the working set: the pages stay dirty in the OS
                // cache and are written back exactly as before, but the process footprint no
                // longer grows with every bulk write-out (a bulk load would otherwise appear to
                // hold the whole file). VirtualUnlock on unlocked pages does exactly this trim
                // and "fails" with ERROR_NOT_LOCKED by design — the return value is meaningless.
                if (OperatingSystem.IsWindows())
                    VirtualUnlock(map.Ptr + (long)minId * PageSize, (nuint)((long)(maxId - minId + 1) * PageSize));
            }
            if (!_unflushedOverflow)
            {
                if (_unflushedMapPages.Count + pages.Length > UnflushedTrackingLimit)
                {
                    _unflushedOverflow = true;
                    _unflushedMapPages.Clear();
                }
                else
                {
                    foreach (var (id, _) in pages)
                        _unflushedMapPages.Add(id);
                }
            }
            return;
        }

        // Small batches and the no-mapping fallback: sorted, with contiguous runs as single
        // vectored writes.
        Array.Sort(pages, static (a, b) => a.Key.CompareTo(b.Key));
        var run = new List<ReadOnlyMemory<byte>>();
        int i = 0;
        while (i < pages.Length)
        {
            int start = i;
            run.Clear();
            run.Add(pages[i].Value);
            while (i + 1 < pages.Length && pages[i + 1].Key == pages[i].Key + 1)
            {
                i++;
                run.Add(pages[i].Value);
            }
            RandomAccess.Write(_handle!, run, (long)pages[start].Key * PageSize);
            i++;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool FlushViewOfFile(byte* lpBaseAddress, nuint dwNumberOfBytesToFlush);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool VirtualUnlock(byte* lpAddress, nuint dwSize);

    /// <summary>
    /// Pushes every page written through the map since the last deep flush to the file system —
    /// by exact ranges where the bookkeeping allows, so a small durable commit flushes its dozen
    /// pages instead of walking the PTEs of the entire view. Ranges are flushed through the
    /// current (largest) view, which covers every page any earlier view could have written.
    /// </summary>
    private void FlushMapRanges()
    {
        MapState? map = _map;
        if (map is null)
            return; // no mapped writes ever happened; plain IO needs no view flush
        if (_unflushedOverflow || !OperatingSystem.IsWindows())
        {
            map.View.Flush();
            _unflushedOverflow = false;
            _unflushedMapPages.Clear();
            return;
        }
        if (_unflushedMapPages.Count == 0)
            return;

        // Ranges are merged across gaps: flushing a clean page costs a PTE visit and nothing
        // else, so a handful of wide ranges beats hundreds of exact ones (each FlushViewOfFile
        // is a syscall). If the writes are scattered beyond what merging can absorb, one
        // whole-view flush is the cheaper walk.
        const uint MergeGapPages = 2048; // 8 MiB of clean pages is cheaper to walk than one extra syscall
        const int MaxRanges = 64;

        _unflushedMapPages.Sort();
        int ranges = 1;
        for (int i = 1; i < _unflushedMapPages.Count && ranges <= MaxRanges; i++)
        {
            if (_unflushedMapPages[i] > _unflushedMapPages[i - 1] + MergeGapPages)
                ranges++;
        }
        unsafe
        {
            if (ranges > MaxRanges)
            {
                map.View.Flush();
            }
            else
            {
                int i = 0;
                while (i < _unflushedMapPages.Count)
                {
                    uint first = _unflushedMapPages[i];
                    uint last = first;
                    while (i + 1 < _unflushedMapPages.Count && _unflushedMapPages[i + 1] <= last + MergeGapPages)
                    {
                        last = Math.Max(last, _unflushedMapPages[i + 1]);
                        i++;
                    }
                    if (!FlushViewOfFile(map.Ptr + (long)first * PageSize, (nuint)((long)(last - first + 1) * PageSize)))
                        throw new IOException($"FlushViewOfFile failed (error {Marshal.GetLastWin32Error()}).");
                    i++;
                }
            }
        }
        _unflushedMapPages.Clear();
    }

    // ---- freelist persistence ----
    // Chain page: [next:u32][count:u32][(txId:i64, pageId:u32) * count]
    private const int FreelistHeader = 8;
    private const int FreelistEntrySize = 12;
    private const int EntriesPerPage = (PageSize - FreelistHeader) / FreelistEntrySize;

    private uint WriteFreelistChain(long newTxId, List<uint> freedByTxn, Dictionary<uint, byte[]> dirtyPages)
    {
        int estimate = _recycled.Count + freedByTxn.Count;
        foreach (var b in _pendingFree)
            estimate += b.Pages.Count;
        foreach (var b in _pendingFreeYoung)
            estimate += b.Pages.Count;
        if (estimate == 0)
        {
            _freelistChainPages = new List<uint>();
            return 0;
        }

        // Allocate the chain BEFORE snapshotting the entries, and recycled-first like any
        // other page (safe: nothing the previous meta references is ever in the recycled
        // pool, and this commit's own frees are not promotable yet). Growing the chain at
        // EOF only would feed its own freed pages back into the list it serializes, making
        // the file grow exponentially with commit count.
        int pageCountNeeded = (estimate + EntriesPerPage - 1) / EntriesPerPage;
        var chain = new List<uint>(pageCountNeeded);
        var buffers = new byte[pageCountNeeded][];
        for (int i = 0; i < pageCountNeeded; i++)
        {
            chain.Add(AllocatePage(out byte[]? reusable));
            buffers[i] = reusable!; // may be null: filled below
        }

        // Consuming recycled ids above may have shrunk the list: total <= estimate always fits.
        int total = _recycled.Count + freedByTxn.Count;
        foreach (var b in _pendingFree)
            total += b.Pages.Count;
        foreach (var b in _pendingFreeYoung)
            total += b.Pages.Count;
        var entries = new (long TxId, uint Page)[total];
        int w = 0;
        foreach (uint p in _recycled)
            entries[w++] = (0, p); // txid 0: reusable unconditionally after reopen
        foreach (var b in _pendingFree)
            foreach (uint p in b.Pages)
                entries[w++] = (b.TxId, p);
        foreach (var b in _pendingFreeYoung)
            foreach (uint p in b.Pages)
                entries[w++] = (b.TxId, p); // young only relative to the outgoing meta; ordinary frees to the one being written
        foreach (uint p in freedByTxn)
            entries[w++] = (newTxId, p);

        int e = 0;
        for (int i = 0; i < pageCountNeeded; i++)
        {
            var buf = buffers[i] ?? GC.AllocateUninitializedArray<byte>(PageSize); // the gap after the last entry is never read
            uint next = i + 1 < pageCountNeeded ? chain[i + 1] : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(buf, next);
            int inPage = Math.Clamp(total - e, 0, EntriesPerPage);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)inPage);
            int off = FreelistHeader;
            for (int j = 0; j < inPage; j++, e++, off += FreelistEntrySize)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off), entries[e].TxId);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 8), entries[e].Page);
            }
            dirtyPages[chain[i]] = buf;
        }

        _freelistChainPages = chain;
        return chain[0];
    }

    private void LoadFreelist(uint head)
    {
        _freelistChainPages = new List<uint>();
        var byTx = new Dictionary<long, List<uint>>();
        uint page = head;
        while (page != 0)
        {
            _freelistChainPages.Add(page);
            byte[] buf = ReadPageFromDisk(page);
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
            int off = FreelistHeader;
            for (int j = 0; j < count; j++, off += FreelistEntrySize)
            {
                long txId = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(off));
                uint pid = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 8));
                if (txId == 0)
                    _recycled.Enqueue(pid);
                else
                    (byTx.TryGetValue(txId, out var list) ? list : byTx[txId] = new List<uint>()).Add(pid);
            }
            page = next;
        }
        foreach (var (txId, pages) in byTx)
            _pendingFree.Add((txId, pages));
        _pendingFree.Sort((a, b) => a.TxId.CompareTo(b.TxId));
    }

    // ---- meta pages ----

    private void WriteMetaSlot(int slot, Meta m)
    {
        Span<byte> buf = stackalloc byte[PageSize];
        buf.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(buf, Magic);
        BinaryPrimitives.WriteInt64LittleEndian(buf[8..], m.TxId);
        BinaryPrimitives.WriteInt64LittleEndian(buf[16..], m.Timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[24..], m.CatalogRoot);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[28..], m.FreelistHead);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[32..], m.PageCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[36..], PageSize);
        BinaryPrimitives.WriteUInt64LittleEndian(buf[MetaPayload..], Fnv1a64(buf[..MetaPayload]));
        if (_mem is not null)
            _mem[(uint)slot] = buf.ToArray(); // memory store needs an owned copy; disk path stays on the stack
        else
            RandomAccess.Write(_handle!, buf, (long)slot * PageSize);
    }

    private Meta LoadNewestValidMeta(out int bestSlot)
    {
        Meta? best = null;
        bestSlot = 0;
        Span<byte> buf = stackalloc byte[PageSize];
        for (int slot = 0; slot < 2; slot++)
        {
            if (RandomAccess.Read(_handle!, buf, (long)slot * PageSize) != PageSize)
                continue;
            if (BinaryPrimitives.ReadUInt64LittleEndian(buf) != Magic)
                continue;
            if (BinaryPrimitives.ReadUInt64LittleEndian(buf[MetaPayload..]) != Fnv1a64(buf[..MetaPayload]))
                continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(buf[36..]) != PageSize)
                throw new InvalidDataException("Database was created with a different page size.");
            var m = new Meta(
                BinaryPrimitives.ReadInt64LittleEndian(buf[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(buf[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(buf[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(buf[28..]),
                BinaryPrimitives.ReadUInt32LittleEndian(buf[32..]));
            if (best is null || m.TxId > best.Value.TxId)
            {
                best = m;
                bestSlot = slot;
            }
        }
        return best ?? throw new InvalidDataException("No valid meta page found. The file is not a Index database or is corrupt.");
    }

    private static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037ul;
        foreach (byte b in data)
            h = (h ^ b) * 1099511628211ul;
        return h;
    }

    public void Dispose()
    {
        ReleaseMap();
        if (_flushStream is not null)
        {
            try
            {
                // Trim the mapped-write growth padding so the file on disk is its logical size.
                if (RandomAccess.GetLength(_handle!) > (long)_pageCount * PageSize)
                    _flushStream.SetLength((long)_pageCount * PageSize);
            }
            catch (IOException) { /* trimming is cosmetic; never fail a dispose over it */ }
            _flushStream.Dispose();
        }
        if (_readHandles is not null)
            foreach (var h in _readHandles)
                h.Dispose();
        _handle?.Dispose();
    }
}
