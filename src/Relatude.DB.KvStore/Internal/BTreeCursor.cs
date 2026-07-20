namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// Bidirectional cursor over a B+Tree snapshot. COW trees have no leaf sibling links
/// (linking would cascade copies across the whole leaf level), so the cursor keeps
/// the descent path and climbs it to cross leaf boundaries in either direction.
/// A cursor moves one way per positioning: Seek/SeekFirst pair with MoveNext,
/// SeekLast/SeekLastBelow pair with MovePrevious.
/// </summary>
internal sealed class BTreeCursor(IPageSource src)
{
    private const int MaxDepth = 32;

    private readonly byte[][] _path = new byte[MaxDepth][];
    private readonly int[] _childIdx = new int[MaxDepth];
    private int _depth; // number of branch levels above the leaf
    private byte[]? _leaf;
    private int _leafPos;

    public ReadOnlySpan<byte> Key => NodePage.GetKey(_leaf!, _leafPos);
    public ReadOnlySpan<byte> Value => NodePage.LeafValue(_leaf!, _leafPos);

    /// <summary>Positions at the smallest key ≥ <paramref name="startKey"/>; returns false if none.</summary>
    public bool Seek(uint root, ReadOnlySpan<byte> startKey)
    {
        if (root == 0)
            return false;
        _depth = 0;
        byte[] page = src.GetPage(root);
        while (!NodePage.IsLeaf(page))
        {
            int idx = NodePage.UpperBound(page, startKey);
            _path[_depth] = page;
            _childIdx[_depth] = idx;
            _depth++;
            page = src.GetPage(NodePage.ChildAt(page, idx));
        }
        _leaf = page;
        _leafPos = NodePage.LowerBound(page, startKey, out _);
        return _leafPos < NodePage.Count(page) || AdvanceLeaf();
    }

    /// <summary>Positions at the first key in the tree; returns false if the tree is empty.</summary>
    public bool SeekFirst(uint root)
    {
        if (root == 0)
            return false;
        _depth = 0;
        byte[] page = src.GetPage(root);
        while (!NodePage.IsLeaf(page))
        {
            _path[_depth] = page;
            _childIdx[_depth] = 0;
            _depth++;
            page = src.GetPage(NodePage.ChildAt(page, 0));
        }
        _leaf = page;
        _leafPos = 0;
        return NodePage.Count(page) > 0 || AdvanceLeaf();
    }

    /// <summary>Positions at the last key in the tree; returns false if the tree is empty.</summary>
    public bool SeekLast(uint root)
    {
        if (root == 0)
            return false;
        _depth = 0;
        byte[] page = src.GetPage(root);
        while (!NodePage.IsLeaf(page))
        {
            int last = NodePage.Count(page); // rightmost child index
            _path[_depth] = page;
            _childIdx[_depth] = last;
            _depth++;
            page = src.GetPage(NodePage.ChildAt(page, last));
        }
        _leaf = page;
        _leafPos = NodePage.Count(page) - 1;
        return _leafPos >= 0 || RetreatLeaf();
    }

    /// <summary>Positions at the greatest key &lt; <paramref name="stopKey"/>; returns false if none.</summary>
    public bool SeekLastBelow(uint root, ReadOnlySpan<byte> stopKey)
    {
        if (root == 0)
            return false;
        _depth = 0;
        byte[] page = src.GetPage(root);
        while (!NodePage.IsLeaf(page))
        {
            int idx = NodePage.UpperBound(page, stopKey);
            _path[_depth] = page;
            _childIdx[_depth] = idx;
            _depth++;
            page = src.GetPage(NodePage.ChildAt(page, idx));
        }
        _leaf = page;
        // The last key < stopKey sits just before the first key >= it; if that is
        // position -1, the predecessor (if any) is the last key of the leaf to the left.
        _leafPos = NodePage.LowerBound(page, stopKey, out _) - 1;
        return _leafPos >= 0 || RetreatLeaf();
    }

    public bool MoveNext()
    {
        _leafPos++;
        return _leafPos < NodePage.Count(_leaf!) || AdvanceLeaf();
    }

    public bool MovePrevious()
    {
        _leafPos--;
        return _leafPos >= 0 || RetreatLeaf();
    }

    private bool AdvanceLeaf()
    {
        int level = _depth - 1;
        while (level >= 0)
        {
            byte[] branch = _path[level];
            int idx = _childIdx[level] + 1;
            if (idx <= NodePage.Count(branch))
            {
                _childIdx[level] = idx;
                byte[] page = src.GetPage(NodePage.ChildAt(branch, idx));
                _depth = level + 1;
                while (!NodePage.IsLeaf(page))
                {
                    _path[_depth] = page;
                    _childIdx[_depth] = 0;
                    _depth++;
                    page = src.GetPage(NodePage.ChildAt(page, 0));
                }
                _leaf = page;
                _leafPos = 0;
                if (NodePage.Count(page) > 0)
                    return true;
                level = _depth - 1; // an empty non-root leaf cannot exist, but stay defensive
                continue;
            }
            level--;
        }
        return false;
    }

    private bool RetreatLeaf()
    {
        int level = _depth - 1;
        while (level >= 0)
        {
            byte[] branch = _path[level];
            int idx = _childIdx[level] - 1;
            if (idx >= 0)
            {
                _childIdx[level] = idx;
                byte[] page = src.GetPage(NodePage.ChildAt(branch, idx));
                _depth = level + 1;
                while (!NodePage.IsLeaf(page))
                {
                    int last = NodePage.Count(page); // rightmost child index
                    _path[_depth] = page;
                    _childIdx[_depth] = last;
                    _depth++;
                    page = src.GetPage(NodePage.ChildAt(page, last));
                }
                _leaf = page;
                _leafPos = NodePage.Count(page) - 1;
                if (_leafPos >= 0)
                    return true;
                level = _depth - 1; // an empty non-root leaf cannot exist, but stay defensive
                continue;
            }
            level--;
        }
        return false;
    }
}
