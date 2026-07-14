using KvStore.Paging;

namespace KvStore.BTree;

/// <summary>
/// The mutable root-page pointer of one B+tree. Each named table (and the catalog) owns one;
/// the tree reads it on every descent and moves it on a root split. <see cref="Pager.NullPage"/>
/// means the tree has no pages yet — an empty table costs nothing on disk until its first insert
/// materialises a root leaf, and rolling back a table's first insert just resets the pointer.
/// </summary>
internal sealed class TreeRoot
{
    public uint PageId = Pager.NullPage;
}
