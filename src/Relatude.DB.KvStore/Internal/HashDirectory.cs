using System.Buffers.Binary;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// The bucket directory of an extendible-hash index: <c>2^globalDepth</c> page ids, indexed by the
/// low hash bits of an id. Several directory slots share one bucket page whenever that bucket's
/// local depth is below the global depth; a page id of 0 means the slot has no bucket yet.
/// <para>
/// The directory lives in memory as an array of 1024-entry chunks (one chunk = one page on disk)
/// and is small — four bytes per bucket, so a hundred million entries cost a few megabytes — which
/// is what makes a lookup a single page read: slot index, bucket page, done. Chunks are shared
/// between versions and copied only when written, so a committed directory is immutable and every
/// pinned reader snapshot keeps the exact directory it was published with.
/// </para>
/// </summary>
internal sealed class HashDirectory
{
    public const int ChunkBits = 10;
    public const int ChunkSize = 1 << ChunkBits; // 1024 page ids = one 4 KiB page
    public const int ChunkMask = ChunkSize - 1;

    /// <summary>
    /// Ceiling for the global depth. Reaching it means the low hash bits stopped separating ids —
    /// only possible for Guid ids whose 64-bit hashes collide (int and ulong hashes cannot) — and
    /// throws rather than doubling a directory that can no longer help.
    /// </summary>
    public const int MaxGlobalDepth = 28;

    public readonly int GlobalDepth;
    public readonly uint[][] Chunks;
    public readonly uint[] ChunkPages;    // page holding each chunk (0 = never persisted)
    public readonly uint[] RootPages;     // chain of pages listing ChunkPages, so a later commit can free them

    public HashDirectory(int globalDepth, uint[][] chunks, uint[] chunkPages, uint[] rootPages)
    {
        GlobalDepth = globalDepth;
        Chunks = chunks;
        ChunkPages = chunkPages;
        RootPages = rootPages;
    }

    public static HashDirectory CreateEmpty() => new(0, [new uint[1]], [0], []);

    public DirView View => new(Chunks, GlobalDepth);

    /// <summary>The 16 high hash bits, kept in the bucket's slot array so a lookup rarely touches a cell.</summary>
    public static ushort TagOf(ulong hash) => (ushort)(hash >> 48);

    /// <summary>Entries in chunk <paramref name="chunkIndex"/> of a directory of <paramref name="globalDepth"/>: a full chunk unless the whole directory is smaller.</summary>
    public static int ChunkLength(int globalDepth, int chunkIndex)
        => Math.Min(ChunkSize, (1 << globalDepth) - chunkIndex * ChunkSize);

    public static int ChunkCount(int globalDepth) => Math.Max(1, (1 << globalDepth) >> ChunkBits);
}

/// <summary>Read access to a directory, whether it is a committed <see cref="HashDirectory"/> or the writer's in-flight copy.</summary>
internal readonly struct DirView(uint[][] chunks, int globalDepth)
{
    public int GlobalDepth => globalDepth;
    public int Size => 1 << globalDepth;
    public ulong Mask => (1ul << globalDepth) - 1;
    public uint this[int slot] => chunks[slot >> HashDirectory.ChunkBits][slot & HashDirectory.ChunkMask];

    /// <summary>Directory slot for <paramref name="hash"/>.</summary>
    public int SlotOf(ulong hash) => (int)(hash & Mask);
}

/// <summary>
/// The writer's copy of a directory for one transaction. The chunk table is private from the
/// start, individual chunks are copied the first time they are written, and only the chunks a
/// transaction actually touched are written back at commit (see <see cref="HashDirectoryStore"/>).
/// </summary>
internal sealed class MutableHashDir
{
    public int GlobalDepth;
    public uint[][] Chunks;
    public uint[] ChunkPages;
    public uint[] RootPages;
    private bool[] _owned;        // chunk array is private to this transaction and safe to write
    private bool[] _needsPersist; // chunk content differs from what ChunkPages[i] holds

    public MutableHashDir(HashDirectory committed)
    {
        GlobalDepth = committed.GlobalDepth;
        Chunks = (uint[][])committed.Chunks.Clone(); // the chunks themselves stay shared until written
        ChunkPages = (uint[])committed.ChunkPages.Clone();
        RootPages = committed.RootPages;
        _owned = new bool[Chunks.Length];
        _needsPersist = new bool[Chunks.Length];
    }

    public DirView View => new(Chunks, GlobalDepth);
    public int Size => 1 << GlobalDepth;
    public ulong Mask => (1ul << GlobalDepth) - 1;
    public uint this[int slot] => Chunks[slot >> HashDirectory.ChunkBits][slot & HashDirectory.ChunkMask];

    public bool NeedsPersist(int chunkIndex) => _needsPersist[chunkIndex];
    public void MarkPersisted(int chunkIndex) => _needsPersist[chunkIndex] = false;

    public void Set(int slot, uint pageId)
    {
        int ci = slot >> HashDirectory.ChunkBits;
        if (!_owned[ci])
        {
            Chunks[ci] = (uint[])Chunks[ci].Clone();
            _owned[ci] = true;
        }
        _needsPersist[ci] = true;
        Chunks[ci][slot & HashDirectory.ChunkMask] = pageId;
    }

    /// <summary>
    /// Points every slot that addresses the bucket at <paramref name="slot"/> (all slots agreeing
    /// with it on the low <paramref name="localDepth"/> bits) at <paramref name="pageId"/>.
    /// </summary>
    public void Repoint(int slot, int localDepth, uint pageId)
    {
        int step = 1 << localDepth;
        for (int j = slot & (step - 1); j < Size; j += step)
            Set(j, pageId);
    }

    /// <summary>
    /// Doubles the directory: slot i of the new directory holds what slot <c>i % oldSize</c> held,
    /// so every bucket keeps its local depth and only the split that triggered this changes.
    /// The upper half aliases the lower half's chunk arrays (copied on the first write to either),
    /// making a doubling cost pointers rather than megabytes — but it does need its own pages.
    /// </summary>
    public void Double()
    {
        int oldSize = 1 << GlobalDepth;
        int newSize = oldSize << 1;
        if (newSize <= HashDirectory.ChunkSize)
        {
            uint[] grown = new uint[newSize];
            uint[] old = Chunks[0];
            old.AsSpan(0, oldSize).CopyTo(grown);
            old.AsSpan(0, oldSize).CopyTo(grown.AsSpan(oldSize));
            Chunks[0] = grown;
            _owned[0] = true;
            _needsPersist[0] = true;
        }
        else
        {
            int oldChunks = Chunks.Length;
            int newChunks = oldChunks * 2;
            Array.Resize(ref Chunks, newChunks);
            Array.Resize(ref ChunkPages, newChunks);
            Array.Resize(ref _owned, newChunks);
            Array.Resize(ref _needsPersist, newChunks);
            for (int k = 0; k < oldChunks; k++)
            {
                Chunks[oldChunks + k] = Chunks[k];
                ChunkPages[oldChunks + k] = 0;
                _needsPersist[oldChunks + k] = true;
                // both indexes now share one array: neither may write without copying first
                _owned[k] = false;
                _owned[oldChunks + k] = false;
            }
        }
        GlobalDepth++;
    }

    /// <summary>The immutable directory published to readers at commit; shares the chunk arrays, which are frozen from here on.</summary>
    public HashDirectory ToImmutable() => new(GlobalDepth, Chunks, ChunkPages, RootPages);
}

/// <summary>
/// Persistence for <see cref="HashDirectory"/>: chunks are written one page each, listed by a
/// chain of root pages holding <c>[next:u32][count:u32][globalDepth:u32][chunkPage:u32 ...]</c>.
/// A commit rewrites only the chunks it dirtied plus the (tiny) root chain, and every replaced
/// page is freed through the transaction, so pinned readers keep reading the old directory.
/// </summary>
internal static class HashDirectoryStore
{
    private const int RootHeader = 12;
    private const int PagesPerRoot = (Pager.PageSize - RootHeader) / 4;

    public static HashDirectory Load(IPageSource src, uint rootPage)
    {
        if (rootPage == 0)
            return HashDirectory.CreateEmpty();

        var rootPages = new List<uint>();
        var chunkPages = new List<uint>();
        int globalDepth = 0;
        uint page = rootPage;
        while (page != 0)
        {
            rootPages.Add(page);
            byte[] buf = src.GetPage(page);
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
            if (rootPages.Count == 1)
                globalDepth = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8));
            for (int i = 0; i < count; i++)
                chunkPages.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(RootHeader + 4 * i)));
            page = next;
        }

        if (chunkPages.Count != HashDirectory.ChunkCount(globalDepth))
            throw new InvalidDataException($"Hash index directory is corrupt: {chunkPages.Count} chunks for a depth of {globalDepth}.");

        var chunks = new uint[chunkPages.Count][];
        for (int ci = 0; ci < chunks.Length; ci++)
        {
            byte[] buf = src.GetPage(chunkPages[ci]);
            int len = HashDirectory.ChunkLength(globalDepth, ci);
            var chunk = new uint[len];
            for (int i = 0; i < len; i++)
                chunk[i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4 * i));
            chunks[ci] = chunk;
        }
        return new HashDirectory(globalDepth, chunks, chunkPages.ToArray(), rootPages.ToArray());
    }

    /// <summary>Writes the dirty chunks and the root chain into <paramref name="txn"/>; returns the new root page.</summary>
    public static uint Persist(IWritePageSource txn, MutableHashDir dir)
    {
        for (int ci = 0; ci < dir.Chunks.Length; ci++)
        {
            if (!dir.NeedsPersist(ci))
                continue;
            if (dir.ChunkPages[ci] != 0)
                txn.Free(dir.ChunkPages[ci]);
            var (pageId, page) = txn.Allocate();
            uint[] chunk = dir.Chunks[ci];
            for (int i = 0; i < chunk.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4 * i), chunk[i]);
            // Pages come back uninitialized: a short chunk (a directory below one full chunk) must
            // not leave the tail as garbage, since a later doubling reads the whole page back.
            if (chunk.Length < HashDirectory.ChunkSize)
                page.AsSpan(4 * chunk.Length).Clear();
            dir.ChunkPages[ci] = pageId;
            dir.MarkPersisted(ci);
        }

        foreach (uint old in dir.RootPages)
            txn.Free(old);

        int rootCount = (dir.ChunkPages.Length + PagesPerRoot - 1) / PagesPerRoot;
        var roots = new uint[rootCount];
        var pages = new byte[rootCount][];
        for (int i = 0; i < rootCount; i++)
            (roots[i], pages[i]) = txn.Allocate(); // allocated up front so each page can name its successor
        for (int i = 0; i < rootCount; i++)
        {
            byte[] page = pages[i];
            int first = i * PagesPerRoot;
            int count = Math.Min(PagesPerRoot, dir.ChunkPages.Length - first);
            BinaryPrimitives.WriteUInt32LittleEndian(page, i + 1 < rootCount ? roots[i + 1] : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), (uint)count);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), (uint)dir.GlobalDepth);
            for (int j = 0; j < count; j++)
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(RootHeader + 4 * j), dir.ChunkPages[first + j]);
        }
        dir.RootPages = roots;
        return roots[0];
    }

    /// <summary>
    /// Frees every page of the directory rooted at <paramref name="rootPage"/> — buckets and the
    /// overflow chains their entries reference included. <paramref name="idSize"/> is the index's
    /// encoded id size (<see cref="IdCodec.SizeOfKind"/>), needed to walk the cells of a bucket
    /// belonging to an index this session never opened.
    /// </summary>
    public static void FreeAll(IWritePageSource txn, uint rootPage, int idSize)
    {
        if (rootPage == 0)
            return;
        var buckets = new HashSet<uint>(); // one bucket is named by many slots; freeing it twice would corrupt the freelist
        uint page = rootPage;
        while (page != 0)
        {
            byte[] buf = txn.GetPage(page);
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
            for (int i = 0; i < count; i++)
            {
                uint chunkPage = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(RootHeader + 4 * i));
                byte[] chunk = txn.GetPage(chunkPage);
                for (int j = 0; j < HashDirectory.ChunkSize; j++)
                {
                    uint bucket = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4 * j));
                    if (bucket != 0)
                        buckets.Add(bucket);
                }
                txn.Free(chunkPage);
            }
            txn.Free(page);
            page = next;
        }
        foreach (uint bucket in buckets)
        {
            // Chains first: the bucket page has to still be readable to name them.
            byte[] bucketPage = txn.GetPage(bucket);
            int cells = HashPage.Count(bucketPage);
            for (int i = 0; i < cells; i++)
            {
                if (HashPage.IsOverflow(bucketPage, i))
                    OverflowStore.Free(txn, HashPage.OverflowRef(bucketPage, i, idSize).FirstPage);
            }
            txn.Free(bucket);
        }
    }
}
