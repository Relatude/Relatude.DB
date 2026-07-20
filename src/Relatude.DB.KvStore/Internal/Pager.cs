using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace SuperFastIndex.Internal;

internal readonly record struct Meta(long TxId, long Timestamp, uint CatalogRoot, uint FreelistHead, uint PageCount);

/// <summary>
/// Page-oriented file manager implementing shadow paging (copy-on-write):
/// pages are never modified in place; a commit writes new pages first and then
/// atomically switches between two checksummed meta pages. Pages freed by a
/// transaction are recycled only once no active reader snapshot can reach them.
/// The freelist itself is persisted as a page chain rewritten on each commit,
/// always into freshly grown pages so it can never clobber live data.
/// </summary>
internal sealed class Pager : IPageSource, IDisposable
{
    public const int PageSize = 4096;
    private const ulong Magic = 0x5346495830303031ul; // "SFIX0001"
    private const int MetaPayload = 40;

    private readonly SafeFileHandle? _handle;
    private readonly SafeFileHandle[]? _readHandles;
    private readonly FileStream? _flushStream;
    private readonly Dictionary<uint, byte[]>? _mem; // non-null => memory-only: pages live here, never on disk
    public readonly PageCache Cache;

    /// <summary>True when the pager has no backing file: all pages live in memory and nothing is persisted.</summary>
    public bool IsMemoryOnly => _mem is not null;

    private Meta _meta;
    private uint _pageCount;                    // in-memory high-water mark (monotonic)
    private readonly Queue<uint> _recycled = new();          // reusable right now
    private readonly List<(long TxId, List<uint> Pages)> _pendingFree = new(); // reusable once readers drain
    private List<uint> _freelistChainPages = new();

    public Meta CurrentMeta => _meta;

    /// <param name="path">
    /// Backing file for the database, or <c>null</c> for a memory-only engine that persists nothing
    /// and always starts empty (identical semantics to a freshly created file).
    /// </param>
    public Pager(string? path, long cacheBytes)
    {
        Cache = new PageCache(cacheBytes, PageSize);

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
        var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            FileOptions.RandomAccess);
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
            _meta = new Meta(TxId: 0, Timestamp: 0, CatalogRoot: 0, FreelistHead: 0, PageCount: 2);
            WriteMetaSlot(0, _meta);
            WriteMetaSlot(1, _meta);
            _flushStream.Flush(true);
        }
        else
        {
            _meta = LoadNewestValidMeta();
            LoadFreelist(_meta.FreelistHead);
        }
        _pageCount = _meta.PageCount;
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
        var buf = new byte[PageSize];
        var handle = _readHandles![(uint)Environment.CurrentManagedThreadId % (uint)_readHandles.Length];
        int read = RandomAccess.Read(handle, buf, (long)pageId * PageSize);
        if (read != PageSize)
            throw new InvalidDataException($"Short read of page {pageId} ({read}/{PageSize} bytes). The file is corrupt or truncated.");
        return buf;
    }

    // ---- allocation ----

    /// <summary>Makes pages freed by transactions no active reader can still see reusable.</summary>
    public void PromoteFreeBatches(long minActiveReaderTxId)
    {
        int i = 0;
        while (i < _pendingFree.Count)
        {
            if (_pendingFree[i].TxId <= minActiveReaderTxId)
            {
                foreach (uint p in _pendingFree[i].Pages)
                    _recycled.Enqueue(p);
                _pendingFree.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    public uint AllocatePage()
    {
        if (_recycled.TryDequeue(out uint id))
        {
            Cache.Invalidate(id); // stale committed content must not be served for the new incarnation
            return id;
        }
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
        List<uint> freedByTxn, Dictionary<uint, byte[]> dirtyPages, bool deepDiskFlush)
    {
        // The previous freelist chain becomes garbage once the new meta lands.
        freedByTxn.AddRange(_freelistChainPages);

        uint freelistHead = WriteFreelistChain(newTxId, freedByTxn, dirtyPages);
        if (freedByTxn.Count > 0)
            _pendingFree.Add((newTxId, freedByTxn));

        WriteDirtyPages(dirtyPages);
        if (deepDiskFlush)
            _flushStream?.Flush(true); // data must be durable before the meta that references it

        _meta = new Meta(newTxId, timestamp, catalogRoot, freelistHead, _pageCount);
        WriteMetaSlot((int)(newTxId & 1), _meta);
        if (deepDiskFlush)
            _flushStream?.Flush(true);

        // Populate the cache with the (hot) just-written pages — unless the batch is large
        // relative to the cache, where doing so would evict everything a reader has warm
        // and spend the whole commit thrashing the eviction sweep.
        if (dirtyPages.Count <= Cache.Capacity / 2)
        {
            foreach (var (id, page) in dirtyPages)
                Cache.Add(id, page);
        }
    }

    /// <summary>Bytes currently used by the database file (logical page span when memory-only).</summary>
    public long FileLength => _mem is not null ? (long)_pageCount * PageSize : RandomAccess.GetLength(_handle!);

    /// <summary>
    /// Wipes the database back to a freshly created state: empty catalog, empty freelist,
    /// file truncated to the two meta pages. The txid stays monotonic (a wipe is a state
    /// change like any commit). Caller must guarantee no transaction and no active readers.
    /// </summary>
    public void Reset()
    {
        _recycled.Clear();
        _pendingFree.Clear();
        _freelistChainPages = new List<uint>();
        _pageCount = 2;
        Cache.Clear();

        _meta = new Meta(_meta.TxId + 1, Timestamp: 0, CatalogRoot: 0, FreelistHead: 0, PageCount: 2);
        if (_mem is not null)
        {
            _mem.Clear();
            WriteMetaSlot(0, _meta);
            WriteMetaSlot(1, _meta);
            return;
        }
        WriteMetaSlot(0, _meta);
        WriteMetaSlot(1, _meta); // both slots: the newest-valid scan must not resurrect old state
        _flushStream!.Flush(true);
        _flushStream.SetLength(2L * PageSize); // release the disk space
        _flushStream.Flush(true);
    }

    /// <summary>Meta-only commit used by <c>SetTimestamp</c>; keeps roots and freelist unchanged.</summary>
    public void CommitMetaOnly(long newTxId, long timestamp, bool deepDiskFlush)
    {
        _meta = _meta with { TxId = newTxId, Timestamp = timestamp, PageCount = _pageCount };
        WriteMetaSlot((int)(newTxId & 1), _meta);
        if (deepDiskFlush)
            _flushStream?.Flush(true);
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
        var ids = new uint[dirty.Count];
        dirty.Keys.CopyTo(ids, 0);
        Array.Sort(ids);

        // Coalesce contiguous page runs into single vectored writes.
        var run = new List<ReadOnlyMemory<byte>>();
        int i = 0;
        while (i < ids.Length)
        {
            int start = i;
            run.Clear();
            run.Add(dirty[ids[i]]);
            while (i + 1 < ids.Length && ids[i + 1] == ids[i] + 1)
            {
                i++;
                run.Add(dirty[ids[i]]);
            }
            RandomAccess.Write(_handle!, run, (long)ids[start] * PageSize);
            i++;
        }
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
        for (int i = 0; i < pageCountNeeded; i++)
            chain.Add(AllocatePage());

        // Consuming recycled ids above may have shrunk the list: total <= estimate always fits.
        int total = _recycled.Count + freedByTxn.Count;
        foreach (var b in _pendingFree)
            total += b.Pages.Count;
        var entries = new (long TxId, uint Page)[total];
        int w = 0;
        foreach (uint p in _recycled)
            entries[w++] = (0, p); // txid 0: reusable unconditionally after reopen
        foreach (var b in _pendingFree)
            foreach (uint p in b.Pages)
                entries[w++] = (b.TxId, p);
        foreach (uint p in freedByTxn)
            entries[w++] = (newTxId, p);

        int e = 0;
        for (int i = 0; i < pageCountNeeded; i++)
        {
            var buf = new byte[PageSize];
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

    private Meta LoadNewestValidMeta()
    {
        Meta? best = null;
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
                best = m;
        }
        return best ?? throw new InvalidDataException("No valid meta page found. The file is not a SuperFastIndex database or is corrupt.");
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
        _flushStream?.Dispose();
        if (_readHandles is not null)
            foreach (var h in _readHandles)
                h.Dispose();
        _handle?.Dispose();
    }
}
