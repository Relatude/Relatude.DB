using System.Buffers;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>Read access to committed or transaction-local pages.</summary>
internal interface IPageSource
{
    byte[] GetPage(uint pageId);
}

/// <summary>Copy-on-write page operations available inside a write transaction.</summary>
internal interface IWritePageSource : IPageSource
{
    (uint Id, byte[] Page) Allocate();

    /// <summary>Returns a transaction-owned, freely mutable copy of the page (identity if already owned).</summary>
    (uint Id, byte[] Page) Cow(uint pageId);

    void Free(uint pageId);
}

internal enum InsertOutcome : byte
{
    AddedNew,
    Replaced,
    NoChange, // key already mapped to an identical value: nothing was copied or written
}

/// <summary>Whether other keys sharing a prefix exist next to the affected leaf position.</summary>
internal enum PrefixPresence : byte
{
    No,
    Yes,
    /// <summary>The position sat on a leaf boundary, so a neighbouring leaf could hold a match; the caller must fall back to a lookup.</summary>
    Unknown,
}

/// <summary>
/// Optional extras for a single Insert/Delete, resolved during the one descent the operation
/// already makes (so callers need no separate lookups): capturing the previous value of a
/// replaced/deleted key, and detecting whether sibling keys share a prefix with the affected key.
/// </summary>
internal ref struct WriteExtras
{
    /// <summary>Destination for the old value when a key is replaced or deleted; leave empty to skip capture.</summary>
    public Span<byte> OldValue;
    public int OldValueLength;

    /// <summary>&gt; 0 requests a prefix-presence check over the first PrefixLength bytes of the key.</summary>
    public int PrefixLength;
    public PrefixPresence Presence;

    public InsertOutcome Outcome;
}

/// <summary>
/// Copy-on-write B+Tree over opaque byte keys and values, compared byte-wise.
/// Because modified pages always get new ids, every committed root is an immutable
/// snapshot readable without locks while a writer builds the next version.
/// Pages are read first and copied only once a change is certain, so operations that end
/// as no-ops (missing delete key, identical re-insert) never dirty anything.
/// Underfull nodes are not rebalanced (pages are reclaimed when they empty out
/// completely) — a standard trade for COW trees that keeps writes fast and simple.
/// </summary>
internal static class BTree
{
    // ---- point lookup ----

    public static bool TryGet(IPageSource src, uint root, ReadOnlySpan<byte> key, out byte[] leaf, out int pos)
    {
        if (root == 0)
        {
            leaf = [];
            pos = 0;
            return false;
        }
        byte[] page = src.GetPage(root);
        while (!NodePage.IsLeaf(page))
            page = src.GetPage(NodePage.ChildAt(page, NodePage.UpperBound(page, key)));
        pos = NodePage.LowerBound(page, key, out bool exact);
        leaf = page;
        return exact;
    }

    // ---- order-statistic count ----

    /// <summary>
    /// Number of entries whose key is strictly less than <paramref name="key"/> (0 for an empty tree).
    /// One descent: at each branch the subtree counts of the children left of the taken path are
    /// summed, and the leaf contributes the position of the first key ≥ <paramref name="key"/>.
    /// </summary>
    public static int CountLessThan(IPageSource src, uint root, ReadOnlySpan<byte> key)
    {
        if (root == 0)
            return 0;
        int n = 0;
        byte[] page = src.GetPage(root);
        while (!NodePage.IsLeaf(page))
        {
            int idx = NodePage.UpperBound(page, key);
            // Child i (< idx) only holds keys < separator i ≤ key, so its whole subtree counts.
            for (int i = 0; i < idx; i++)
                n += NodePage.BranchCount(page, i);
            page = src.GetPage(NodePage.ChildAt(page, idx));
        }
        return n + NodePage.LowerBound(page, key, out _);
    }

    // ---- insert ----

    private struct SplitResult
    {
        public uint PageId;
        public bool Split;
        public ReadOnlyMemory<byte> SepKey;
        public uint RightId;
        public int LeftCount;  // entries under PageId after the operation (split only)
        public int RightCount; // entries under RightId after the operation (split only)
    }

    /// <summary>Inserts or replaces; returns the new root page id.</summary>
    public static uint Insert(IWritePageSource txn, uint root, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, out bool replaced)
    {
        var extras = new WriteExtras();
        uint newRoot = Insert(txn, root, key, value, ref extras);
        replaced = extras.Outcome != InsertOutcome.AddedNew;
        return newRoot;
    }

    /// <summary>Inserts or replaces, resolving the requested <see cref="WriteExtras"/> during the same descent.</summary>
    public static uint Insert(IWritePageSource txn, uint root, scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value, ref WriteExtras extras)
    {
        if (key.Length > NodePage.MaxKeySize)
            throw new ArgumentException($"Encoded key is {key.Length} bytes; the maximum is {NodePage.MaxKeySize}.");
        if (value.Length > NodePage.MaxValueSize)
            throw new ArgumentException($"Encoded value is {value.Length} bytes; the maximum is {NodePage.MaxValueSize}.");

        extras.Outcome = InsertOutcome.AddedNew;
        extras.Presence = PrefixPresence.No;
        extras.OldValueLength = 0;

        if (root == 0)
        {
            var (id, page) = txn.Allocate();
            NodePage.InitLeaf(page);
            NodePage.TryInsertLeafCell(page, 0, key, value);
            return id;
        }

        SplitResult r = InsertRec(txn, root, key, value, ref extras);
        if (!r.Split)
            return r.PageId;

        var (rootId, rootPage) = txn.Allocate();
        NodePage.InitBranch(rootPage, r.RightId);
        NodePage.SetRightmostCount(rootPage, r.RightCount);
        NodePage.TryInsertBranchCell(rootPage, 0, r.SepKey.Span, r.PageId, r.LeftCount);
        return rootId;
    }

    private static SplitResult InsertRec(IWritePageSource txn, uint pageId, scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value, ref WriteExtras extras)
    {
        byte[] page = txn.GetPage(pageId);

        if (NodePage.IsLeaf(page))
        {
            int pos = NodePage.LowerBound(page, key, out bool exact);
            if (extras.PrefixLength > 0)
                extras.Presence = ScanPrefixNeighbours(page, pos, exact, key[..extras.PrefixLength]);

            if (exact)
            {
                ReadOnlySpan<byte> old = NodePage.LeafValue(page, pos);
                if (old.SequenceEqual(value))
                {
                    extras.Outcome = InsertOutcome.NoChange;
                    return new SplitResult { PageId = pageId };
                }
                extras.Outcome = InsertOutcome.Replaced;
                if (!extras.OldValue.IsEmpty)
                {
                    old.CopyTo(extras.OldValue);
                    extras.OldValueLength = old.Length;
                }
            }

            (uint id, page) = txn.Cow(pageId);
            if (exact)
                NodePage.RemoveCell(page, pos);
            if (NodePage.TryInsertLeafCell(page, pos, key, value))
                return new SplitResult { PageId = id };
            return SplitLeaf(txn, id, page, pos, key, value);
        }

        int idx = NodePage.UpperBound(page, key);
        SplitResult r = InsertRec(txn, NodePage.ChildAt(page, idx), key, value, ref extras);
        if (extras.Outcome == InsertOutcome.NoChange)
            return new SplitResult { PageId = pageId };

        (uint cowId, page) = txn.Cow(pageId);
        if (!r.Split)
        {
            if (idx < NodePage.Count(page))
                NodePage.SetBranchChild(page, idx, r.PageId);
            else
                NodePage.SetRightmost(page, r.PageId);
            if (extras.Outcome == InsertOutcome.AddedNew)
                NodePage.SetChildCountAt(page, idx, NodePage.ChildCountAt(page, idx) + 1);
            return new SplitResult { PageId = cowId };
        }

        // Split halves report their post-insert totals, so the slot counts are set, not adjusted.
        if (idx < NodePage.Count(page))
        {
            NodePage.SetBranchChild(page, idx, r.RightId);
            NodePage.SetBranchCount(page, idx, r.RightCount);
        }
        else
        {
            NodePage.SetRightmost(page, r.RightId);
            NodePage.SetRightmostCount(page, r.RightCount);
        }

        // The split's left half is keyed by the separator, inserted before the (now right-pointing) old slot.
        if (NodePage.TryInsertBranchCell(page, idx, r.SepKey.Span, r.PageId, r.LeftCount))
            return new SplitResult { PageId = cowId };
        return SplitBranch(txn, cowId, page, idx, r.SepKey.Span, r.PageId, r.LeftCount);
    }

    /// <summary>
    /// Same-prefix keys are contiguous in sort order, so if any exist one must sit directly
    /// next to the affected position; only positions on the edge of a leaf are inconclusive.
    /// For an exact match the neighbours are pos-1/pos+1; for an insertion point, pos-1/pos.
    /// </summary>
    private static PrefixPresence ScanPrefixNeighbours(byte[] leaf, int pos, bool exact, ReadOnlySpan<byte> prefix)
    {
        int count = NodePage.Count(leaf);
        int prev = pos - 1;
        int next = exact ? pos + 1 : pos;
        if (prev >= 0 && NodePage.GetKey(leaf, prev).StartsWith(prefix))
            return PrefixPresence.Yes;
        if (next < count && NodePage.GetKey(leaf, next).StartsWith(prefix))
            return PrefixPresence.Yes;
        return prev < 0 || next >= count ? PrefixPresence.Unknown : PrefixPresence.No;
    }

    private static SplitResult SplitLeaf(IWritePageSource txn, uint leftId, byte[] page, int newPos,
        ReadOnlySpan<byte> newKey, ReadOnlySpan<byte> newValue)
    {
        byte[] tmp = ArrayPool<byte>.Shared.Rent(NodePage.PageSize);
        try
        {
            page.CopyTo(tmp, 0);
            int n = NodePage.Count(tmp) + 1;

            long total = NodePage.LeafCellSize(newKey.Length, newValue.Length);
            for (int i = 0; i < n - 1; i++)
                total += NodePage.LeafCellSize(NodePage.GetKey(tmp, i).Length, NodePage.LeafValue(tmp, i).Length);

            // First index that goes right: accumulate left halves until ~half the bytes, keeping both sides non-empty.
            int splitAt = 1;
            long acc = 0;
            for (int i = 0; i < n - 1; i++)
            {
                int size = i == newPos
                    ? NodePage.LeafCellSize(newKey.Length, newValue.Length)
                    : LeafVirtualCellSize(tmp, i, newPos);
                acc += size;
                if (acc >= total / 2)
                {
                    splitAt = i + 1;
                    break;
                }
                splitAt = i + 2;
            }
            if (splitAt > n - 1)
                splitAt = n - 1; // keep the right half non-empty even when one giant cell dominates

            var (rightId, right) = txn.Allocate();
            NodePage.InitLeaf(right);
            NodePage.InitLeaf(page);

            for (int i = 0; i < n; i++)
            {
                byte[] target = i < splitAt ? page : right;
                int pos = i < splitAt ? i : i - splitAt;
                bool ok = i == newPos
                    ? NodePage.TryInsertLeafCell(target, pos, newKey, newValue)
                    : NodePage.TryInsertLeafCell(target, pos,
                        NodePage.GetKey(tmp, i < newPos ? i : i - 1),
                        NodePage.LeafValue(tmp, i < newPos ? i : i - 1));
                if (!ok)
                    throw new InvalidOperationException("Internal error: split halves must always fit.");
            }

            return new SplitResult
            {
                PageId = leftId,
                Split = true,
                SepKey = NodePage.GetKeyMemory(right, 0),
                RightId = rightId,
                LeftCount = splitAt,
                RightCount = n - splitAt,
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    private static int LeafVirtualCellSize(byte[] tmp, int virtualIndex, int newPos)
    {
        int i = virtualIndex < newPos ? virtualIndex : virtualIndex - 1;
        return NodePage.LeafCellSize(NodePage.GetKey(tmp, i).Length, NodePage.LeafValue(tmp, i).Length);
    }

    private static SplitResult SplitBranch(IWritePageSource txn, uint leftId, byte[] page, int newPos,
        ReadOnlySpan<byte> newKey, uint newChild, int newCount)
    {
        byte[] tmp = ArrayPool<byte>.Shared.Rent(NodePage.PageSize);
        try
        {
            page.CopyTo(tmp, 0);
            int n = NodePage.Count(tmp) + 1;

            long total = NodePage.BranchCellSize(newKey.Length);
            for (int i = 0; i < n - 1; i++)
                total += NodePage.BranchCellSize(NodePage.GetKey(tmp, i).Length);

            // Virtual cell `promote` moves up to the parent; [0, promote) stays left, (promote, n) goes right.
            int promote = 0;
            long acc = 0;
            for (int i = 0; i < n - 1; i++)
            {
                acc += i == newPos
                    ? NodePage.BranchCellSize(newKey.Length)
                    : NodePage.BranchCellSize(NodePage.GetKey(tmp, i < newPos ? i : i - 1).Length);
                promote = i + 1;
                if (acc >= total / 2)
                    break;
            }

            byte[] sepKey = (promote == newPos ? newKey : NodePage.GetKey(tmp, promote < newPos ? promote : promote - 1)).ToArray();
            uint promoteChild = promote == newPos ? newChild : NodePage.BranchChild(tmp, promote < newPos ? promote : promote - 1);
            int promoteCount = promote == newPos ? newCount : NodePage.BranchCount(tmp, promote < newPos ? promote : promote - 1);
            int oldRightmostCount = NodePage.RightmostCount(tmp);

            var (rightId, right) = txn.Allocate();
            NodePage.InitBranch(right, NodePage.Rightmost(tmp));
            NodePage.SetRightmostCount(right, oldRightmostCount);
            NodePage.InitBranch(page, promoteChild); // keys in [sep-of-left-half, sep) live under the promoted cell's child
            NodePage.SetRightmostCount(page, promoteCount);

            int leftSum = promoteCount, rightSum = oldRightmostCount;
            for (int i = 0; i < n; i++)
            {
                if (i == promote)
                    continue;
                byte[] target = i < promote ? page : right;
                int pos = i < promote ? i : i - promote - 1;
                int count = i == newPos ? newCount : NodePage.BranchCount(tmp, i < newPos ? i : i - 1);
                bool ok = i == newPos
                    ? NodePage.TryInsertBranchCell(target, pos, newKey, newChild, count)
                    : NodePage.TryInsertBranchCell(target, pos,
                        NodePage.GetKey(tmp, i < newPos ? i : i - 1),
                        NodePage.BranchChild(tmp, i < newPos ? i : i - 1), count);
                if (!ok)
                    throw new InvalidOperationException("Internal error: split halves must always fit.");
                if (i < promote) leftSum += count;
                else rightSum += count;
            }

            return new SplitResult
            {
                PageId = leftId, Split = true, SepKey = sepKey, RightId = rightId,
                LeftCount = leftSum, RightCount = rightSum,
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    // ---- delete ----

    private readonly record struct DeleteResult(uint PageId, bool Removed, bool Empty);

    /// <summary>Deletes a key if present; returns the new root page id. A miss copies nothing.</summary>
    public static uint Delete(IWritePageSource txn, uint root, ReadOnlySpan<byte> key, out bool removed)
    {
        var extras = new WriteExtras();
        return Delete(txn, root, key, out removed, ref extras);
    }

    /// <summary>Deletes a key if present, resolving the requested <see cref="WriteExtras"/> during the same descent.</summary>
    public static uint Delete(IWritePageSource txn, uint root, scoped ReadOnlySpan<byte> key, out bool removed, ref WriteExtras extras)
    {
        extras.Presence = PrefixPresence.No;
        extras.OldValueLength = 0;

        if (root == 0)
        {
            removed = false;
            return 0;
        }

        DeleteResult r = DeleteRec(txn, root, key, ref extras);
        removed = r.Removed;
        if (!removed)
            return root;
        if (r.Empty)
        {
            txn.Free(r.PageId);
            return 0;
        }

        // Collapse trivial roots so the height shrinks as the tree drains.
        uint newRoot = r.PageId;
        byte[] page = txn.GetPage(newRoot);
        while (!NodePage.IsLeaf(page) && NodePage.Count(page) == 0)
        {
            txn.Free(newRoot);
            newRoot = NodePage.Rightmost(page);
            page = txn.GetPage(newRoot);
        }
        return newRoot;
    }

    private static DeleteResult DeleteRec(IWritePageSource txn, uint pageId, scoped ReadOnlySpan<byte> key, ref WriteExtras extras)
    {
        byte[] page = txn.GetPage(pageId);

        if (NodePage.IsLeaf(page))
        {
            int pos = NodePage.LowerBound(page, key, out bool exact);
            if (!exact)
                return new DeleteResult(pageId, false, false);

            if (!extras.OldValue.IsEmpty)
            {
                ReadOnlySpan<byte> old = NodePage.LeafValue(page, pos);
                old.CopyTo(extras.OldValue);
                extras.OldValueLength = old.Length;
            }
            if (extras.PrefixLength > 0)
                extras.Presence = ScanPrefixNeighbours(page, pos, exact: true, key[..extras.PrefixLength]);

            (uint id, page) = txn.Cow(pageId);
            NodePage.RemoveCell(page, pos);
            return new DeleteResult(id, true, NodePage.Count(page) == 0);
        }

        int idx = NodePage.UpperBound(page, key);
        DeleteResult r = DeleteRec(txn, NodePage.ChildAt(page, idx), key, ref extras);
        if (!r.Removed)
            return new DeleteResult(pageId, false, false);

        (uint cowId, page) = txn.Cow(pageId);
        if (!r.Empty)
        {
            if (idx < NodePage.Count(page))
                NodePage.SetBranchChild(page, idx, r.PageId);
            else
                NodePage.SetRightmost(page, r.PageId);
            NodePage.SetChildCountAt(page, idx, NodePage.ChildCountAt(page, idx) - 1);
            return new DeleteResult(cowId, true, false);
        }

        txn.Free(r.PageId);
        int count = NodePage.Count(page);
        if (idx < count)
        {
            NodePage.RemoveCell(page, idx); // drops the separator together with the emptied child (its count is 0)
        }
        else if (count > 0)
        {
            NodePage.SetRightmost(page, NodePage.BranchChild(page, count - 1));
            NodePage.SetRightmostCount(page, NodePage.BranchCount(page, count - 1));
            NodePage.RemoveCell(page, count - 1);
        }
        else
        {
            return new DeleteResult(cowId, true, true); // last child gone: this branch is empty too
        }
        return new DeleteResult(cowId, true, false);
    }
}
