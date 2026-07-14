using KvStore.Paging;

namespace KvStore.BTree;

/// <summary>Projects one entry's key/value page spans into a caller type. Runs under the read
/// lock; must copy/decode, never capture the spans.</summary>
internal delegate T EntrySelector<out T>(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);

/// <summary>Projects one value's page span into a caller type. Same contract as
/// <see cref="EntrySelector{T}"/>.</summary>
internal delegate T ValueSelector<out T>(ReadOnlySpan<byte> value);

/// <summary>
/// The comparer-agnostic face of <see cref="BPlusTree{TCmp}"/>, so the non-generic
/// <see cref="StorageEngine"/> can hold any specialisation. One interface dispatch per
/// operation; the per-comparison calls inside stay devirtualised.
/// </summary>
internal interface IBPlusTree
{
    bool TryGet(ReadOnlySpan<byte> key, out byte[] value);
    bool TryGet<T>(ReadOnlySpan<byte> key, ValueSelector<T> selector, out T value);
    bool TryFirst<T>(EntrySelector<T> selector, out T result);
    bool TryLast<T>(EntrySelector<T> selector, out T result);
    bool Insert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);
    bool Delete(ReadOnlySpan<byte> key);
    List<T> Range<T>(
        byte[]? start, byte[]? end, EntrySelector<T> selector,
        bool startInclusive, bool endInclusive, bool reverse, int capacityHint);
    void InvalidateTailHint();
}

/// <summary>
/// A disk-backed B+tree mapping byte-ordered keys to byte-array values, layered over the
/// <see cref="Pager"/>. Values live only in leaves; leaves are singly linked by
/// <c>NextLeaf</c> so range scans walk them in key order without revisiting the tree.
///
/// <para>Inserts recurse to the target leaf and propagate node splits back up; a split at the
/// root grows the tree one level. Mutations never run concurrently — the owning
/// <see cref="StorageEngine"/> holds the write lock — so the tree edits pages in place via the pager.</para>
///
/// <para>v1 deletes remove the entry but do not merge/rebalance underfull nodes (see
/// <see cref="Delete"/>); the tree stays correct and freed leaves refill on later inserts.</para>
/// </summary>
internal sealed class BPlusTree<TCmp> : IBPlusTree
    where TCmp : struct, IByteKeyComparer
{
    private readonly Pager _pager;
    private readonly TCmp _cmp;
    private readonly TreeRoot _root;

    public BPlusTree(Pager pager, TCmp cmp, TreeRoot root)
    {
        _pager = pager;
        _cmp = cmp;
        _root = root;
    }

    private readonly record struct Split(byte[] Key, uint RightChild);

    // The rightmost leaf, remembered across inserts so the ascending-key pattern (bulk loads,
    // time-series appends) skips the root-to-leaf descent entirely. Best-effort: revalidated on
    // every use and cleared whenever it can't be trusted (rollback, or the leaf stops being
    // rightmost after a split).
    private uint _tailLeaf = Pager.NullPage;

    // The rightmost leaf's last key, when known — a NEGATIVE filter only: a key at or below it
    // can never tail-append, so random-update workloads skip the tail-page probe with one inlined
    // compare. It may lag the true maximum (only the probe below refreshes it), so a key passing
    // the filter is always re-verified against the page; staleness can only cost the fast path,
    // never correctness.
    private byte[]? _tailLastKey;

    /// <summary>Forgets the rightmost-leaf shortcut. Called after a rollback, which may undo the
    /// page the hint points at.</summary>
    public void InvalidateTailHint()
    {
        _tailLeaf = Pager.NullPage;
        _tailLastKey = null;
    }

    // ---- Lookup -----------------------------------------------------------

    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        bool found = TryGet(key, static v => v.ToArray(), out var result);
        value = result ?? Array.Empty<byte>();
        return found;
    }

    /// <summary>Looks up a key and projects the value span through <paramref name="selector"/>
    /// without an intermediate copy. <paramref name="value"/> is default when absent.</summary>
    public bool TryGet<T>(ReadOnlySpan<byte> key, ValueSelector<T> selector, out T value)
    {
        uint pageId = _root.PageId;
        if (pageId == Pager.NullPage) // no pages yet: the table has never been written
        {
            value = default!;
            return false;
        }

        // When the comparer ranks keys, carry the chosen subtree's separator rank bounds down the
        // descent (read from separator cells the search just touched) so the leaf search can start
        // at an interpolated slot instead of cold-reading the leaf's endpoint keys. Point lookups
        // are memory-latency bound, so every avoided probe is a saved cache miss.
        bool ranked = _cmp.TryRank(key, out ulong keyRank);
        ulong loRank = 0, hiRank = 0;
        bool haveLo = false, haveHi = false;

        while (true)
        {
            var page = _pager.GetReadable(pageId);
            if (Node.IsLeaf(page))
            {
                int idx = ranked && haveLo && haveHi
                    ? Node.FindInLeafSeeded(page, key, _cmp, keyRank, loRank, hiRank)
                    : Node.FindInLeaf(page, key, _cmp);
                if (idx >= 0)
                {
                    value = selector(Node.LeafValue(page, idx));
                    return true;
                }
                value = default!;
                return false;
            }
            pageId = ranked
                ? Node.ChildForKeyWithBounds(page, key, _cmp, ref loRank, ref hiRank, ref haveLo, ref haveHi)
                : Node.ChildForKey(page, key, _cmp);
        }
    }

    /// <summary>
    /// Projects the entry with the smallest key through <paramref name="selector"/> — one
    /// root-to-leaf descent, no scan. Walks past empty leaves (v1 deletes don't merge them, so
    /// the leftmost leaf can be empty while later ones hold entries). False when the tree holds
    /// no entries.
    /// </summary>
    public bool TryFirst<T>(EntrySelector<T> selector, out T result)
    {
        result = default!;
        if (_root.PageId == Pager.NullPage) return false;

        LeftmostLeaf(out var page);
        while (true)
        {
            if (Node.CellCount(page) > 0)
            {
                Node.LeafEntry(page, 0, out var k, out var v);
                result = selector(k, v);
                return true;
            }
            uint next = Node.NextLeaf(page);
            if (next == Pager.NullPage) return false;
            page = _pager.GetReadable(next);
        }
    }

    /// <summary>
    /// Projects the entry with the largest key through <paramref name="selector"/> — one
    /// root-to-leaf descent, no scan. Leaves are only forward-linked, so an empty rightmost leaf
    /// can't be stepped out of; instead the descent tries each internal node's children
    /// right-to-left until a subtree yields an entry (empty leaves are rare, so the common case
    /// is a single straight descent). False when the tree holds no entries.
    /// </summary>
    public bool TryLast<T>(EntrySelector<T> selector, out T result)
    {
        result = default!;
        return _root.PageId != Pager.NullPage && TryLastInSubtree(_root.PageId, selector, ref result);
    }

    private bool TryLastInSubtree<T>(uint pageId, EntrySelector<T> selector, ref T result)
    {
        var page = _pager.GetReadable(pageId);
        if (Node.IsLeaf(page))
        {
            int n = Node.CellCount(page);
            if (n == 0) return false;
            Node.LeafEntry(page, n - 1, out var k, out var v);
            result = selector(k, v);
            return true;
        }

        for (int i = Node.CellCount(page) - 1; i >= 0; i--)
            if (TryLastInSubtree(Node.InternalChild(page, i), selector, ref result)) return true;
        return TryLastInSubtree(Node.FirstChild(page), selector, ref result);
    }

    // ---- Insert -----------------------------------------------------------

    /// <summary>Inserts or overwrites a key. Returns true if the key was new.</summary>
    public bool Insert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (_root.PageId == Pager.NullPage)
        {
            // First insert into this table: materialise its root leaf now (creation is lazy so
            // an empty table costs nothing and its creation rolls back with the transaction).
            _root.PageId = _pager.AllocatePage();
            Node.InitLeaf(_pager.GetWritable(_root.PageId));
        }
        else if (TryTailAppend(key, value))
        {
            return true;
        }

        // Carry separator rank bounds down the descent (see TryGet) so the leaf search can start
        // from an interpolated slot — inserts pay the same memory-latency descent as lookups.
        bool ranked = _cmp.TryRank(key, out ulong keyRank);

        bool isNew = InsertRec(_root.PageId, key, value,
            ranked, keyRank, 0, 0, false, false, out Split? split);
        if (split is { } s)
        {
            // Root split: build a new root one level up.
            uint newRoot = _pager.AllocatePage();
            var page = _pager.GetWritable(newRoot);
            Node.WriteInternal(page, _root.PageId, new[] { (s.Key, s.RightChild) });
            _root.PageId = newRoot;
        }
        return isNew;
    }

    /// <summary>
    /// The append fast path: a key strictly greater than the last key of the (non-empty)
    /// rightmost leaf — the global maximum — belongs at its tail, no descent needed. Splices it
    /// in place via <see cref="Node.TryInsertLeafCell"/>; a full page falls back to the descent
    /// (a split needs the ancestor stack). Every condition is re-checked, so a stale hint can
    /// never misplace a key — at worst it costs one wasted page read.
    /// </summary>
    private bool TryTailAppend(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (_tailLeaf == Pager.NullPage) return false;

        // Negative filter first: at or below the cached maximum can never append (see _tailLastKey).
        if (_tailLastKey is not null && _cmp.Compare(key, _tailLastKey) <= 0) return false;

        var page = _pager.GetReadable(_tailLeaf);
        if (!Node.IsLeaf(page) || Node.NextLeaf(page) != Pager.NullPage)
        {
            InvalidateTailHint(); // no longer the rightmost leaf (split, or reused page)
            return false;
        }

        int n = Node.CellCount(page);
        if (n == 0) return false;
        var last = Node.LeafKey(page, n - 1);
        if (_cmp.Compare(key, last) <= 0)
        {
            _tailLastKey = last.ToArray(); // remember the max so the next miss is one compare
            return false;
        }

        if (!Node.TryInsertLeafCell(_pager.GetWritable(_tailLeaf), n, key, value)) return false;
        // The appended key is the new maximum; drop the cache rather than copy the key on every
        // append — bulk loads keep appending (no filter needed), and a random-update workload
        // re-caches on its first miss.
        _tailLastKey = null;
        return true;
    }

    private bool InsertRec(uint pageId, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value,
        bool ranked, ulong keyRank, ulong loRank, ulong hiRank, bool haveLo, bool haveHi,
        out Split? split)
    {
        split = null;
        var readPage = _pager.GetReadable(pageId);

        if (Node.IsLeaf(readPage))
        {
            int idx = ranked && haveLo && haveHi
                ? Node.FindInLeafSeeded(readPage, key, _cmp, keyRank, loRank, hiRank)
                : Node.FindInLeaf(readPage, key, _cmp);
            bool isNew = idx < 0;

            // Remember the rightmost leaf for the append fast path. If this insert splits it,
            // SplitLeaf moves the hint to the new right node. The cached max is stale now (this
            // insert may raise it); the filter tolerates a lagging max, never a leading one.
            if (Node.NextLeaf(readPage) == Pager.NullPage)
            {
                _tailLeaf = pageId;
                _tailLastKey = null;
            }

            // Fast path: splice a brand-new cell straight into the page. Avoids materialising and
            // re-serialising the whole leaf on the common insert. Falls through on a full page
            // (→ split) or an overwrite (→ rebuild, to reclaim the old cell).
            if (isNew && Node.TryInsertLeafCell(_pager.GetWritable(pageId), ~idx, key, value))
                return true;

            // Overwrite fast path: a same-length value is patched into the existing cell in place
            // (layout unchanged, nothing to reclaim). Differing lengths rebuild as before.
            if (!isNew && Node.TryReplaceLeafValue(_pager.GetWritable(pageId), idx, value))
                return false;

            var entries = Node.ReadLeafEntries(readPage);
            uint nextLeaf = Node.NextLeaf(readPage);
            if (isNew) entries.Insert(~idx, (key.ToArray(), value.ToArray()));
            else entries[idx] = (key.ToArray(), value.ToArray());

            var page = _pager.GetWritable(pageId);
            if (Node.WriteLeaf(page, entries, nextLeaf))
                return isNew;

            // Appending past the end of the rightmost leaf (the ascending-key pattern): split at the
            // insertion point instead of the middle, so the left leaf stays fully packed and only the
            // new entry moves right. A middle split would leave every leaf half-empty forever, since
            // no later key can ever land in it.
            bool appendAtTail = isNew && nextLeaf == Pager.NullPage && ~idx == entries.Count - 1;
            SplitLeaf(pageId, page, entries, nextLeaf, appendAtTail, out split);
            return isNew;
        }
        else
        {
            uint childId = ranked
                ? Node.ChildForKeyWithBounds(readPage, key, _cmp, ref loRank, ref hiRank, ref haveLo, ref haveHi)
                : Node.ChildForKey(readPage, key, _cmp);
            bool isNew = InsertRec(childId, key, value, ranked, keyRank, loRank, hiRank, haveLo, haveHi, out Split? childSplit);
            if (childSplit is not { } cs) return isNew;

            // Insert the promoted separator + new right child into this node. readPage is still
            // this page's readable image — the recursion only wrote descendant pages.
            var entries = Node.ReadInternalEntries(readPage);
            uint firstChild = Node.FirstChild(readPage);
            int pos = InternalInsertPos(entries, cs.Key);
            entries.Insert(pos, (cs.Key, cs.RightChild));

            var page = _pager.GetWritable(pageId);
            if (Node.WriteInternal(page, firstChild, entries))
                return isNew;

            SplitInternal(page, firstChild, entries, out split);
            return isNew;
        }
    }

    private int InternalInsertPos(List<(byte[] key, uint child)> entries, byte[] key)
    {
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_cmp.Compare(entries[mid].key, key) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private void SplitLeaf(uint pageId, byte[] page, List<(byte[] key, byte[] val)> entries, uint nextLeaf, bool appendAtTail, out Split? split)
    {
        // Tail-append split: keep every pre-existing entry (they fit — they were on the page) and
        // move only the new last entry to the fresh right leaf, which subsequent appends then fill.
        int mid = appendAtTail
            ? entries.Count - 1
            : BalancedSplitIndex(entries.Count, i => Node.LeafEntrySize(entries[i].key.Length, entries[i].val.Length));
        var left = entries.GetRange(0, mid);
        var right = entries.GetRange(mid, entries.Count - mid);

        uint rightId = _pager.AllocatePage();
        var rightPage = _pager.GetWritable(rightId);
        Node.WriteLeaf(rightPage, right, nextLeaf);          // new leaf links to old successor
        Node.WriteLeaf(page, left, rightId);                 // old leaf now links to new leaf

        if (nextLeaf == Pager.NullPage)
        {
            _tailLeaf = rightId; // the new node is now the rightmost leaf
            _tailLastKey = right[^1].key;
        }

        split = new Split((byte[])right[0].key.Clone(), rightId);
    }

    private void SplitInternal(byte[] page, uint firstChild, List<(byte[] key, uint child)> entries, out Split? split)
    {
        int m = BalancedSplitIndex(entries.Count, i => Node.InternalEntrySize(entries[i].key.Length));
        if (m >= entries.Count) m = entries.Count - 1; // a separator must be promoted from the right

        var leftEntries = entries.GetRange(0, m);
        var promoted = entries[m];
        var rightEntries = entries.GetRange(m + 1, entries.Count - m - 1);

        uint rightId = _pager.AllocatePage();
        var rightPage = _pager.GetWritable(rightId);
        Node.WriteInternal(rightPage, promoted.child, rightEntries); // promoted.child = p_(m+1)
        Node.WriteInternal(page, firstChild, leftEntries);

        split = new Split(promoted.key, rightId);
    }

    /// <summary>Picks the smallest prefix length whose serialized size reaches half of the total,
    /// keeping both halves comfortably within a page.</summary>
    private static int BalancedSplitIndex(int count, Func<int, int> sizeOf)
    {
        int total = 0;
        for (int i = 0; i < count; i++) total += sizeOf(i);
        int half = total / 2, acc = 0, i2 = 0;
        for (; i2 < count - 1; i2++)
        {
            acc += sizeOf(i2);
            if (acc >= half) break;
        }
        return Math.Max(1, i2 + 1);
    }

    // ---- Delete -----------------------------------------------------------

    /// <summary>
    /// Removes a key if present. Returns true if a key was removed.
    /// v1 does not merge underfull nodes; emptied leaves remain in place and are refilled
    /// by later inserts. Tree invariants for search/insert/scan are preserved.
    /// </summary>
    public bool Delete(ReadOnlySpan<byte> key)
    {
        uint pageId = _root.PageId;
        if (pageId == Pager.NullPage) return false;
        while (true)
        {
            var page = _pager.GetReadable(pageId);
            if (Node.IsLeaf(page))
            {
                int idx = Node.FindInLeaf(page, key, _cmp);
                if (idx < 0) return false;
                var entries = Node.ReadLeafEntries(page);
                uint nextLeaf = Node.NextLeaf(page);
                entries.RemoveAt(idx);
                Node.WriteLeaf(_pager.GetWritable(pageId), entries, nextLeaf);
                return true;
            }
            pageId = Node.ChildForKey(page, key, _cmp);
        }
    }

    // ---- Range scan -------------------------------------------------------

    /// <summary>
    /// Returns all entries whose key falls between <paramref name="start"/> and
    /// <paramref name="end"/>, in ascending order (descending if <paramref name="reverse"/>).
    /// A null start scans from the beginning; a null end scans to the end. Each present bound is
    /// included when its <c>*Inclusive</c> flag is set, otherwise excluded — so the default
    /// (<paramref name="startInclusive"/> true, <paramref name="endInclusive"/> false) is the
    /// half-open interval <c>[start, end)</c>.
    ///
    /// <para>Reverse scans cost the same as forward ones: leaves are only singly linked
    /// (<c>NextLeaf</c>), so we always walk the chain forward and reverse the materialised list in
    /// place — a single O(n) pass plus an O(n) reverse, with no extra page reads or tree
    /// descents.</para>
    /// </summary>
    public List<T> Range<T>(
        byte[]? start, byte[]? end, EntrySelector<T> selector,
        bool startInclusive = true, bool endInclusive = false, bool reverse = false,
        int capacityHint = 0)
    {
        var result = new List<T>(capacityHint);
        if (_root.PageId == Pager.NullPage) return result; // the table has never been written

        uint leafId;
        int from = 0;

        // Resolve the start bound once: descend to its leaf and binary-search the first in-range
        // slot. Every entry from there on (in this leaf and all following ones) is >= start, so
        // the per-entry loops never compare against it.
        byte[] page;
        if (start is null)
        {
            leafId = LeftmostLeaf(out page);
        }
        else
        {
            leafId = DescendToLeaf(start, out page);
            int idx = Node.FindInLeaf(page, start, _cmp);
            from = idx >= 0 ? (startInclusive ? idx : idx + 1) : ~idx;
        }

        while (leafId != Pager.NullPage)
        {
            int count = Node.CellCount(page);
            if (end is null)
            {
                for (int i = from; i < count; i++)
                {
                    Node.LeafEntry(page, i, out var k, out var v);
                    result.Add(selector(k, v));
                }
            }
            else
            {
                for (int i = from; i < count; i++)
                {
                    Node.LeafEntry(page, i, out var k, out var v);
                    int c = _cmp.Compare(k, end);
                    // Past the upper bound: keys only grow from here, so we're done.
                    if (c > 0 || (c == 0 && !endInclusive)) goto done;
                    result.Add(selector(k, v));
                }
            }
            from = 0;
            leafId = Node.NextLeaf(page);
            if (leafId != Pager.NullPage) page = _pager.GetReadable(leafId);
        }
    done:
        if (reverse) result.Reverse();
        return result;
    }

    /// <summary>Descends to the leaf that would hold <paramref name="key"/>, returning its id and
    /// (already fetched) page.</summary>
    private uint DescendToLeaf(ReadOnlySpan<byte> key, out byte[] page)
    {
        uint pageId = _root.PageId;
        while (true)
        {
            page = _pager.GetReadable(pageId);
            if (Node.IsLeaf(page)) return pageId;
            pageId = Node.ChildForKey(page, key, _cmp);
        }
    }

    private uint LeftmostLeaf(out byte[] page)
    {
        uint pageId = _root.PageId;
        while (true)
        {
            page = _pager.GetReadable(pageId);
            if (Node.IsLeaf(page)) return pageId;
            pageId = Node.FirstChild(page);
        }
    }
}
