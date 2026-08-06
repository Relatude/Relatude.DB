namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// Thread-safe page cache with a configurable byte budget. Page ids are dense (allocation is
/// sequential plus recycling), so the cache is a flat slot table indexed by page id: a hit is one
/// array load and a null check — no hashing, no bucket walk — at a bookkeeping cost of nine bytes
/// per page of file high-water, a fraction of a percent of the file size.
/// Reads are lock-free (a volatile slot read plus an unsynchronized touch flag). All mutations
/// share one lock: correctness depends on an <see cref="Invalidate"/> never being lost to a
/// concurrent table growth (a recycled page id must not serve its previous incarnation), and
/// serializing them is by far the simplest way to guarantee that. Mutations are either
/// writer-side or sit behind a disk read, so the lock is never on a fast path.
/// Eviction uses a second-chance sweep; because pages in a copy-on-write tree are immutable, a
/// cached page never needs write-back and can be dropped at any moment.
/// </summary>
internal sealed class PageCache
{
    private sealed class Table(byte[]?[] pages, byte[] touched)
    {
        public readonly byte[]?[] Pages = pages;
        public readonly byte[] Touched = touched;
    }

    private volatile Table _table = new(new byte[]?[MinSlots], new byte[MinSlots]);
    private readonly object _mutateLock = new();
    private int _count;

    private const int MinSlots = 4096;

    public int Capacity { get; }

    public PageCache(long budgetBytes, int pageSize)
    {
        Capacity = (int)Math.Max(16, budgetBytes / pageSize);
    }

    public byte[]? TryGet(uint pageId)
    {
        Table t = _table;
        if (pageId >= (uint)t.Pages.Length)
            return null;
        byte[]? page = Volatile.Read(ref t.Pages[pageId]);
        if (page is not null && t.Touched[pageId] == 0)
            t.Touched[pageId] = 1; // write only on transition: a hot page stays read-only for other cores
        return page;
    }

    public void Add(uint pageId, byte[] page)
    {
        lock (_mutateLock)
        {
            Table t = _table;
            if (pageId >= (uint)t.Pages.Length)
                t = Grow(pageId);
            if (t.Pages[pageId] is null)
            {
                t.Pages[pageId] = page;
                t.Touched[pageId] = 1;
                if (++_count > Capacity)
                    Evict(t);
            }
            else
            {
                t.Touched[pageId] = 1; // already cached (benign concurrent load of the same immutable page)
            }
        }
    }

    /// <summary>
    /// Must be called when a freed page id is reallocated with new content. Returns the evicted
    /// array (null if the page was not cached): at this point the recycle gate has already proven
    /// no reader can reach the old incarnation, so the caller may reuse the buffer for the new
    /// page instead of allocating a fresh one.
    /// </summary>
    public byte[]? Invalidate(uint pageId)
    {
        lock (_mutateLock)
        {
            Table t = _table;
            if (pageId < (uint)t.Pages.Length && t.Pages[pageId] is byte[] page)
            {
                t.Pages[pageId] = null;
                _count--;
                return page;
            }
            return null;
        }
    }

    public void Clear()
    {
        lock (_mutateLock)
        {
            _table = new Table(new byte[]?[MinSlots], new byte[MinSlots]);
            _count = 0;
        }
    }

    /// <summary>Called under the mutate lock. Publishes the grown table before returning, so no later mutation can land in the old one.</summary>
    private Table Grow(uint pageId)
    {
        Table old = _table;
        int newLen = old.Pages.Length;
        while (pageId >= (uint)newLen)
            newLen = checked(newLen * 2);
        var pages = new byte[]?[newLen];
        var touched = new byte[newLen];
        Array.Copy(old.Pages, pages, old.Pages.Length);
        Array.Copy(old.Touched, touched, old.Touched.Length);
        var grown = new Table(pages, touched);
        _table = grown;
        return grown;
    }

    /// <summary>Called under the mutate lock. Two passes: drop untouched entries first, then (if still over) touched ones.</summary>
    private void Evict(Table t)
    {
        int target = Capacity - Capacity / 8; // free ~12.5% headroom per sweep
        for (int pass = 0; pass < 2 && _count > target; pass++)
        {
            for (int i = 0; i < t.Pages.Length && _count > target; i++)
            {
                if (t.Pages[i] is null)
                    continue;
                if (t.Touched[i] != 0 && pass == 0)
                    t.Touched[i] = 0; // second chance
                else
                {
                    t.Pages[i] = null;
                    _count--;
                }
            }
        }
    }
}
