using Relatude.DB.DataStores.Sets;

namespace Relatude.Common;

/// <summary>
/// A page, a take or a skip that cannot change the set is answered with the set itself. A view that
/// wants the whole result says so with a ceiling rather than a count - the admin page's visual pivot
/// asks for Page(0, 1000000) of whatever type it is drawing - and copying the set out for that would
/// cost an int array of every id in it, a cache entry to hold the copy, and a new state id that
/// nothing cached against the original could be reused for.
/// </summary>
[TestClass]
public class SetPagingTests {
    static IdSet set(params int[] ids) => new(ids, SetRegister.NewStateId());

    [TestMethod]
    public void ASetIsNotCopiedToPageAllOfIt() {
        var sets = new SetRegister(16 * 1024 * 1024);
        var all = set(5, 3, 9, 1);

        Assert.AreSame(all, sets.Page(all, 0, all.Count), "a page holding every id is the set");
        Assert.AreSame(all, sets.Page(all, 0, 1_000_000), "and so is a page larger than the set");
        Assert.AreSame(all, sets.Take(all, all.Count));
        Assert.AreSame(all, sets.Take(all, 1_000_000));
        Assert.AreSame(all, sets.Skip(all, 0));
        // the state id is what matters beyond the copy: it is the key every cached operation on the
        // set hangs from, so a page that keeps it keeps the cache of everything done to the set
        Assert.AreEqual(all.StateId, sets.Page(all, 0, 1_000_000).StateId);
    }

    [TestMethod]
    public void APageThatDoesChangeTheSetStillDoes() {
        var sets = new SetRegister(16 * 1024 * 1024);
        var all = set(5, 3, 9, 1);

        CollectionAssert.AreEqual(new[] { 5, 3 }, sets.Page(all, 0, 2).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 9, 1 }, sets.Page(all, 1, 2).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 5, 3, 9 }, sets.Take(all, 3).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 9, 1 }, sets.Skip(all, 2).Enumerate().ToArray());
        Assert.AreEqual(0, sets.Page(all, 4, 2).Count, "a page past the end is empty");
    }
}
