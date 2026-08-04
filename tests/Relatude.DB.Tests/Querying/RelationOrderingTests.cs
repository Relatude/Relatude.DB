using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Relations;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

#region test model with an explicit many to many relation (the shared model only has one to many)
[Node]
public class OrdPerson {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    public OrdMembership.Teams Teams { get; set; } = new();
}
[Node]
public class OrdTeam {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    public OrdMembership.Members Members { get; set; } = new();
}
public class OrdMembership : ManyToMany<OrdPerson, OrdTeam> {
    public class Teams : ManyTo { }
    public class Members : ManyFrom { }
}
#endregion

// Reordering of relation lists: MoveRelation (by offset), MoveRelationToTop/Bottom, MoveRelationBefore/After
// and SetRelationOrder, incl. multi item moves, persistence and rollback.
[TestClass]
public class RelationOrderingTests {

    #region pure algorithm tests
    [TestMethod]
    public void RelatedListMoveTo() {
        var list = new RelatedList();
        for (var i = 1; i <= 5; i++) list.Add(i);
        list.MoveTo(1, 2);
        CollectionAssert.AreEqual(new[] { 2, 3, 1, 4, 5 }, list.ToIdSet().Enumerate().ToArray());
        list.MoveTo(5, 0);
        CollectionAssert.AreEqual(new[] { 5, 2, 3, 1, 4 }, list.ToIdSet().Enumerate().ToArray());
        list.MoveTo(5, 100); // clamped to bottom
        CollectionAssert.AreEqual(new[] { 2, 3, 1, 4, 5 }, list.ToIdSet().Enumerate().ToArray());
        list.MoveTo(4, -100); // clamped to top
        CollectionAssert.AreEqual(new[] { 4, 2, 3, 1, 5 }, list.ToIdSet().Enumerate().ToArray());
        list.MoveTo(3, 2); // already there, no change
        CollectionAssert.AreEqual(new[] { 4, 2, 3, 1, 5 }, list.ToIdSet().Enumerate().ToArray());
        Assert.ThrowsException<ItemNotInRelationException>(() => list.MoveTo(99, 0));
    }
    [TestMethod]
    public void DiffToMovesTransformsAndIsMinimal() {
        // single displaced item needs exactly one move:
        var moves = RelationOrderUtils.DiffToMoves([1, 2, 3, 4, 5], [1, 4, 2, 3, 5]);
        Assert.AreEqual(1, moves.Count);
        Assert.AreEqual(4, moves[0].moved);
        // random permutations always transform correctly when replayed with MoveTo semantics:
        var random = new Random(42);
        for (var n = 0; n < 200; n++) {
            var size = random.Next(1, 12);
            var current = Enumerable.Range(1, size).ToList();
            var desired = current.OrderBy(_ => random.Next()).ToList();
            var working = new List<int>(current);
            foreach (var (moved, fromIndex, toIndex) in RelationOrderUtils.DiffToMoves(current, desired)) {
                Assert.AreEqual(moved, working[fromIndex]);
                working.RemoveAt(fromIndex);
                working.Insert(toIndex, moved);
            }
            CollectionAssert.AreEqual(desired, working);
        }
        Assert.ThrowsException<ArgumentException>(() => RelationOrderUtils.DiffToMoves([1, 2], [1, 3]));
        Assert.ThrowsException<ArgumentException>(() => RelationOrderUtils.DiffToMoves([1, 2], [1]));
    }
    [TestMethod]
    public void MoveByOffsetMultiSelectSemantics() {
        int[] current = [1, 2, 3, 4, 5, 6];
        // selection keeps its internal order and moves as individual items:
        CollectionAssert.AreEqual(new[] { 2, 1, 4, 3, 5, 6 }, RelationOrderUtils.MoveByOffset(current, [1, 3], 1));
        // compacts against the top without reordering the selection:
        CollectionAssert.AreEqual(new[] { 2, 4, 1, 3, 5, 6 }, RelationOrderUtils.MoveByOffset(current, [2, 4], -5));
        // compacts against the bottom:
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 6, 3, 5 }, RelationOrderUtils.MoveByOffset(current, [3, 5], 100));
        // adjacent selection moving up one place:
        CollectionAssert.AreEqual(new[] { 1, 3, 4, 2, 5, 6 }, RelationOrderUtils.MoveByOffset(current, [3, 4], -1));
        // edges:
        CollectionAssert.AreEqual(new[] { 3, 5, 1, 2, 4, 6 }, RelationOrderUtils.MoveToEdge(current, [3, 5], top: true));
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 6, 3, 5 }, RelationOrderUtils.MoveToEdge(current, [3, 5], top: false));
        // anchored:
        CollectionAssert.AreEqual(new[] { 1, 3, 5, 2, 4, 6 }, RelationOrderUtils.MoveRelativeToAnchor(current, [3, 5], anchor: 2, after: false));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 5, 4, 6 }, RelationOrderUtils.MoveRelativeToAnchor(current, [3, 5], anchor: 2, after: true));
    }
    #endregion

    #region index level tests
    [TestMethod]
    public void IndexMoveManyToManyBothDirections() {
        var r = new ManyToManyIndex();
        r.Add(1, 10, DateTime.UtcNow);
        r.Add(1, 20, DateTime.UtcNow);
        r.Add(1, 30, DateTime.UtcNow);
        r.Add(2, 10, DateTime.UtcNow);
        r.Move(1, 30, false, 0); // reorder target list of source 1
        CollectionAssert.AreEqual(new[] { 30, 10, 20 }, r.Get(1, false).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2 }, r.Get(10, true).Enumerate().ToArray()); // other side untouched
        r.Move(10, 2, true, 0); // reorder source list of target 10
        CollectionAssert.AreEqual(new[] { 2, 1 }, r.Get(10, true).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 30, 10, 20 }, r.Get(1, false).Enumerate().ToArray()); // and back
        Assert.ThrowsException<ItemNotInRelationException>(() => r.Move(1, 99, false, 0));
        Assert.ThrowsException<ItemNotInRelationException>(() => r.Move(99, 1, false, 0));
    }
    [TestMethod]
    public void IndexMoveOneToManyAndSymmetric() {
        var oneToMany = new OneToManyIndex();
        oneToMany.Add(1, 10, DateTime.UtcNow);
        oneToMany.Add(1, 20, DateTime.UtcNow);
        oneToMany.Add(1, 30, DateTime.UtcNow);
        oneToMany.Move(1, 20, false, 0);
        CollectionAssert.AreEqual(new[] { 20, 10, 30 }, oneToMany.Get(1, false).Enumerate().ToArray());
        oneToMany.Move(10, 1, true, 5); // single valued side, validated no-op
        Assert.ThrowsException<ItemNotInRelationException>(() => oneToMany.Move(10, 2, true, 0));

        var symmetric = new ManyManyIndex();
        symmetric.Add(1, 2, DateTime.UtcNow);
        symmetric.Add(1, 3, DateTime.UtcNow);
        symmetric.Add(4, 1, DateTime.UtcNow);
        symmetric.Move(1, 4, true, 0); // direction is irrelevant for symmetric lists
        CollectionAssert.AreEqual(new[] { 4, 2, 3 }, symmetric.Get(1, false).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 1 }, symmetric.Get(2, false).Enumerate().ToArray());

        var oneOne = new OneOneIndex();
        oneOne.Add(1, 2, DateTime.UtcNow);
        oneOne.Move(1, 2, false, 3); // validated no-op
        oneOne.Move(2, 1, false, 0); // symmetric, both directions valid
        Assert.ThrowsException<ItemNotInRelationException>(() => oneOne.Move(1, 3, false, 0));

        var oneToOne = new OneToOneIndex();
        oneToOne.Add(1, 2, DateTime.UtcNow);
        oneToOne.Move(1, 2, false, 3); // validated no-op
        oneToOne.Move(2, 1, true, 0);
        Assert.ThrowsException<ItemNotInRelationException>(() => oneToOne.Move(2, 1, false, 0));
    }
    #endregion

    #region end to end, one to many (Article.Children)
    static int[] childOrder(NodeStore store, int parentId) =>
        store.Query<Article>().Where(a => a.Id == parentId).Include(a => a.Children).Execute().First()
            .Children.Select(c => c.Id).ToArray();
    static (NodeStore store, Article parent, Article[] children) openParentWithChildren(IIOProvider? io = null) {
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), null, io));
        var parent = new Article { Id = 1, Name = "Parent" };
        store.Insert(parent);
        var children = new Article[5];
        for (var i = 0; i < 5; i++) {
            children[i] = new Article { Id = 10 + i, Name = "Child " + i };
            store.Insert(children[i]);
            store.AddRelation(parent, a => a.Children, children[i]);
        }
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13, 14 }, childOrder(store, 1));
        return (store, parent, children);
    }
    [TestMethod]
    public void MoveWithinChildrenEndToEnd() {
        var (store, parent, c) = openParentWithChildren();
        store.MoveRelation(parent, a => a.Children, c[3], -2); // 13 two up
        CollectionAssert.AreEqual(new[] { 10, 13, 11, 12, 14 }, childOrder(store, 1));
        store.MoveRelation(parent, a => a.Children, c[3], 100); // clamped to bottom
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 14, 13 }, childOrder(store, 1));
        store.MoveRelationToTop(parent, a => a.Children, c[4]);
        CollectionAssert.AreEqual(new[] { 14, 10, 11, 12, 13 }, childOrder(store, 1));
        store.MoveRelationToBottom(parent, a => a.Children, c[4]);
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13, 14 }, childOrder(store, 1));
        store.MoveRelationBefore(parent, a => a.Children, c[4], c[1]); // 14 before 11
        CollectionAssert.AreEqual(new[] { 10, 14, 11, 12, 13 }, childOrder(store, 1));
        store.MoveRelationAfter(parent, a => a.Children, c[0], c[2]); // 10 after 12
        CollectionAssert.AreEqual(new[] { 14, 11, 12, 10, 13 }, childOrder(store, 1));
        store.SetRelationOrder(parent, a => a.Children, new object[] { c[0], c[1], c[2], c[3], c[4] });
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13, 14 }, childOrder(store, 1));
        store.Dispose();
    }
    [TestMethod]
    public void MoveMultipleChildrenEndToEnd() {
        var (store, parent, c) = openParentWithChildren();
        store.MoveRelation(parent, a => a.Children, new object[] { c[1], c[3] }, -1); // selection keeps order
        CollectionAssert.AreEqual(new[] { 11, 10, 13, 12, 14 }, childOrder(store, 1));
        store.MoveRelation(parent, a => a.Children, new object[] { c[1], c[3] }, -5); // compacts at top
        CollectionAssert.AreEqual(new[] { 11, 13, 10, 12, 14 }, childOrder(store, 1));
        store.MoveRelationToBottom(parent, a => a.Children, new object[] { c[1], c[3] });
        CollectionAssert.AreEqual(new[] { 10, 12, 14, 11, 13 }, childOrder(store, 1));
        store.MoveRelationBefore(parent, a => a.Children, new object[] { c[1], c[3] }, c[2]); // block before 12
        CollectionAssert.AreEqual(new[] { 10, 11, 13, 12, 14 }, childOrder(store, 1));
        store.MoveRelationAfter(parent, a => a.Children, new object[] { c[0], c[4] }, c[2]); // block after 12
        CollectionAssert.AreEqual(new[] { 11, 13, 12, 10, 14 }, childOrder(store, 1));
        store.Dispose();
    }
    [TestMethod]
    public void MoveErrorsEndToEnd() {
        var (store, parent, c) = openParentWithChildren();
        var unrelated = new Article { Id = 99, Name = "Unrelated" };
        store.Insert(unrelated);
        // moving an unrelated node throws:
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.MoveRelationToTop(parent, a => a.Children, unrelated));
        // anchor must be related:
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.MoveRelationBefore(parent, a => a.Children, c[0], unrelated));
        // anchor cannot be part of the moved selection:
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.MoveRelationAfter(parent, a => a.Children, new object[] { c[0], c[1] }, c[1]));
        // duplicate items throw:
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.MoveRelationToTop(parent, a => a.Children, new object[] { c[0], c[0] }));
        // SetRelationOrder requires exactly the related set:
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.SetRelationOrder(parent, a => a.Children, new object[] { c[0], c[1] }));
        // and nothing changed along the way:
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13, 14 }, childOrder(store, 1));
        store.Dispose();
    }
    [TestMethod]
    public void MoveRollsBackWithFailedTransaction() {
        var (store, parent, c) = openParentWithChildren();
        var t = new Transaction(store);
        t.MoveRelationToTop(parent, a => a.Children, c[4]);
        t.AddRelation(c[0], a => a.Parent, parent); // already related, forces the transaction to fail after the move
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.Execute(t));
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13, 14 }, childOrder(store, 1)); // order restored by rollback
        store.Dispose();
    }
    #endregion

    #region rollback via opposite actions
    [TestMethod]
    public void RollbackRestoresOrderAfterMultipleMoves() {
        // several move actions, each expanding to several reorder primitives, all rolled back in reverse
        // order when the commit callback fails at the very end of the transaction:
        var (store, parent, c) = openParentWithChildren();
        var t = new Transaction(store);
        t.SetRelationOrder(parent, a => a.Children, new object[] { c[4], c[3], c[2], c[1], c[0] }); // full reversal
        t.MoveRelation(parent, a => a.Children, new object[] { c[0], c[2] }, -10); // then a multi item move
        t.SetCommitCallback(_ => throw new Exception("failing on purpose"));
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.Execute(t));
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13, 14 }, childOrder(store, 1));
        // and the store is still fully usable after the rollback:
        store.MoveRelationToTop(parent, a => a.Children, c[2]);
        CollectionAssert.AreEqual(new[] { 12, 10, 11, 13, 14 }, childOrder(store, 1));
        store.Dispose();
    }
    [TestMethod]
    public void RollbackWhenLaterMoveFailsMidTransaction() {
        // the first move executes, the second fails while converting (unrelated item), so the first
        // move must be undone by its opposite action:
        var (store, parent, c) = openParentWithChildren();
        var unrelated = new Article { Id = 99, Name = "Unrelated" };
        store.Insert(unrelated);
        var t = new Transaction(store);
        t.MoveRelationToTop(parent, a => a.Children, c[3]);
        t.MoveRelationToTop(parent, a => a.Children, unrelated);
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.Execute(t));
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13, 14 }, childOrder(store, 1));
        store.Dispose();
    }
    [TestMethod]
    public void RollbackRestoresRemovedRelationPosition() {
        // the opposite of a remove is an add, which by itself appends at the end of the list, so the
        // rollback must restore the removed edge at its original position (index 1), not at the bottom:
        var (store, parent, c) = openParentWithChildren();
        var t = new Transaction(store);
        t.RemoveRelation<Article>(parent, a => a.Children, c[1]);
        t.AddRelation(c[0], a => a.Parent, parent); // already related, fails after the remove executed
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.Execute(t));
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13, 14 }, childOrder(store, 1));
        store.Dispose();
    }
    [TestMethod]
    public void RollbackRestoresBothSidesOfManyToMany() {
        var store = new NodeStore(DataStoreLocal.Open(manyToManyModel()));
        var anna = new OrdPerson { Id = Guid.NewGuid(), Name = "Anna" };
        var bo = new OrdPerson { Id = Guid.NewGuid(), Name = "Bo" };
        var chris = new OrdPerson { Id = Guid.NewGuid(), Name = "Chris" };
        OrdTeam red = new() { Id = Guid.NewGuid(), Name = "Red" }, blue = new() { Id = Guid.NewGuid(), Name = "Blue" }, green = new() { Id = Guid.NewGuid(), Name = "Green" };
        foreach (var n in new object[] { anna, bo, chris, red, blue, green }) store.Insert(n);
        foreach (var team in new[] { red, blue, green }) store.AddRelation(anna, p => p.Teams, team);
        store.AddRelation(bo, p => p.Teams, red);
        store.AddRelation(chris, p => p.Teams, red);

        var t = new Transaction(store);
        t.MoveRelationToTop(anna, p => p.Teams, green); // reorder one direction
        t.MoveRelation(red, x => x.Members, chris, -2); // reorder the other direction
        t.RemoveRelation<OrdPerson>(anna, p => p.Teams, blue); // remove an edge from the middle of anna's list
        t.SetCommitCallback(_ => throw new Exception("failing on purpose"));
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.Execute(t));

        CollectionAssert.AreEqual(new[] { red.Id, blue.Id, green.Id }, relatedIds(store, anna.Id, false)); // incl. blue back at index 1
        CollectionAssert.AreEqual(new[] { anna.Id, bo.Id, chris.Id }, relatedIds(store, red.Id, true));
        CollectionAssert.AreEqual(new[] { anna.Id }, relatedIds(store, blue.Id, true));
        store.Dispose();
    }
    [TestMethod]
    public void RollbackOfMixedInsertRelateMoveAndRemove() {
        // a transaction combining node insert, relate, moves and a remove, failing at the end:
        // every opposite action must run in reverse order, restoring membership, order and node set.
        var (store, parent, c) = openParentWithChildren();
        var t = new Transaction(store);
        var newChild = new Article { Id = 15, Name = "New child" };
        t.Insert(newChild);
        t.AddRelation(newChild, a => a.Parent, parent);
        t.MoveRelationToTop(parent, a => a.Children, newChild);
        t.RemoveRelation<Article>(parent, a => a.Children, c[2]);
        t.MoveRelationToBottom(parent, a => a.Children, c[0]);
        t.SetCommitCallback(_ => throw new Exception("failing on purpose"));
        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => store.Execute(t));

        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13, 14 }, childOrder(store, 1)); // order and membership restored, 12 back at index 2
        Assert.AreEqual(0, store.Query<Article>().Where(a => a.Id == 15).Execute().Count()); // inserted node rolled back
        store.Dispose();
    }
    #endregion

    #region end to end, many to many both directions and symmetric
    static Datamodel manyToManyModel() {
        var dm = new Datamodel();
        dm.Add<OrdPerson>();
        dm.Add<OrdTeam>();
        dm.Add<OrdMembership>();
        return dm;
    }
    static Guid[] relatedIds(NodeStore store, Guid fromId, bool fromTargetToSource) =>
        store.Datastore.GetRelatedNodeIdsFromRelationId(store.Mapper.GetRelationId<OrdMembership>(), fromId, fromTargetToSource).ToArray();
    [TestMethod]
    public void MoveManyToManyBothSidesEndToEnd() {
        var store = new NodeStore(DataStoreLocal.Open(manyToManyModel()));
        var anna = new OrdPerson { Id = Guid.NewGuid(), Name = "Anna" };
        var bo = new OrdPerson { Id = Guid.NewGuid(), Name = "Bo" };
        var chris = new OrdPerson { Id = Guid.NewGuid(), Name = "Chris" };
        OrdTeam red = new() { Id = Guid.NewGuid(), Name = "Red" }, blue = new() { Id = Guid.NewGuid(), Name = "Blue" }, green = new() { Id = Guid.NewGuid(), Name = "Green" };
        foreach (var n in new object[] { anna, bo, chris, red, blue, green }) store.Insert(n);
        foreach (var team in new[] { red, blue, green }) store.AddRelation(anna, p => p.Teams, team);
        foreach (var person in new[] { anna, bo, chris }) if (person != anna) store.AddRelation(person, p => p.Teams, red);
        CollectionAssert.AreEqual(new[] { red.Id, blue.Id, green.Id }, relatedIds(store, anna.Id, false));
        CollectionAssert.AreEqual(new[] { anna.Id, bo.Id, chris.Id }, relatedIds(store, red.Id, true));

        store.MoveRelationToTop(anna, p => p.Teams, green); // reorder teams of a person
        CollectionAssert.AreEqual(new[] { green.Id, red.Id, blue.Id }, relatedIds(store, anna.Id, false));
        CollectionAssert.AreEqual(new[] { anna.Id, bo.Id, chris.Id }, relatedIds(store, red.Id, true)); // member order untouched

        store.MoveRelation(red, t => t.Members, chris, -1); // reorder members of a team (reverse direction property)
        CollectionAssert.AreEqual(new[] { anna.Id, chris.Id, bo.Id }, relatedIds(store, red.Id, true));
        CollectionAssert.AreEqual(new[] { green.Id, red.Id, blue.Id }, relatedIds(store, anna.Id, false)); // team order untouched
        store.Dispose();
    }
    [TestMethod]
    public void MoveSymmetricFriendsEndToEnd() {
        var dm = new Datamodel();
        dm.Add<SymPerson>();
        dm.Add<SymMarriage>();
        dm.Add<SymFriendship>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        var a = new SymPerson { Id = Guid.NewGuid(), Name = "a" };
        var b = new SymPerson { Id = Guid.NewGuid(), Name = "b" };
        var c = new SymPerson { Id = Guid.NewGuid(), Name = "c" };
        var d = new SymPerson { Id = Guid.NewGuid(), Name = "d" };
        foreach (var p in new[] { a, b, c, d }) store.Insert(p);
        foreach (var friend in new[] { b, c, d }) store.AddRelation(a, p => p.Friends, friend);
        Guid[] friendsOf(Guid id) => store.Datastore.GetRelatedNodeIdsFromRelationId(store.Mapper.GetRelationId<SymFriendship>(), id, false).ToArray();
        CollectionAssert.AreEqual(new[] { b.Id, c.Id, d.Id }, friendsOf(a.Id));
        store.MoveRelationToTop(a, p => p.Friends, d);
        CollectionAssert.AreEqual(new[] { d.Id, b.Id, c.Id }, friendsOf(a.Id));
        store.MoveRelation(a, p => p.Friends, b, 1);
        CollectionAssert.AreEqual(new[] { d.Id, c.Id, b.Id }, friendsOf(a.Id));
        CollectionAssert.AreEqual(new[] { a.Id }, friendsOf(b.Id)); // other participants unaffected
        store.Dispose();
    }
    #endregion

    #region persistence
    [TestMethod]
    public void OrderSurvivesWalReplay() {
        var io = new IOProviderMemory();
        var (store, parent, c) = openParentWithChildren(io);
        store.MoveRelationToTop(parent, a => a.Children, c[3]);
        store.MoveRelationAfter(parent, a => a.Children, c[0], c[4]);
        CollectionAssert.AreEqual(new[] { 13, 11, 12, 14, 10 }, childOrder(store, 1));
        store.Dispose(); // no state file saved, so reopening replays the log incl. the reorder actions
        store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), null, io));
        CollectionAssert.AreEqual(new[] { 13, 11, 12, 14, 10 }, childOrder(store, 1));
        store.Dispose();
    }
    [TestMethod]
    public void OrderSurvivesStateFileRoundTrip() {
        var io = new IOProviderMemory();
        var (store, parent, c) = openParentWithChildren(io);
        store.MoveRelation(parent, a => a.Children, new object[] { c[1], c[4] }, -10);
        CollectionAssert.AreEqual(new[] { 11, 14, 10, 12, 13 }, childOrder(store, 1));
        store.Maintenance(MaintenanceAction.SaveIndexStates); // writes state.bin incl. relation list order
        store.Dispose();
        store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), null, io));
        CollectionAssert.AreEqual(new[] { 11, 14, 10, 12, 13 }, childOrder(store, 1));
        store.Dispose();
    }
    [TestMethod]
    public void OrderSurvivesLogRewrite() {
        // constructs list orders on BOTH sides of a many to many relation that no plain sequence of adds
        // can reproduce (the ordering constraints are cyclic), so this only passes if the log rewriter
        // emits reorder fix-ups after the adds.
        var io = new IOProviderMemory();
        var store = new NodeStore(DataStoreLocal.Open(manyToManyModel(), null, io));
        var p1 = new OrdPerson { Id = Guid.NewGuid(), Name = "p1" };
        var p2 = new OrdPerson { Id = Guid.NewGuid(), Name = "p2" };
        var t1 = new OrdTeam { Id = Guid.NewGuid(), Name = "t1" };
        var t2 = new OrdTeam { Id = Guid.NewGuid(), Name = "t2" };
        foreach (var n in new object[] { p1, p2, t1, t2 }) store.Insert(n);
        store.AddRelation(p1, p => p.Teams, t1);
        store.AddRelation(p1, p => p.Teams, t2);
        store.AddRelation(p2, p => p.Teams, t1);
        store.AddRelation(p2, p => p.Teams, t2);
        store.MoveRelationToTop(p2, p => p.Teams, t2); // p2: [t2, t1]
        store.MoveRelationToTop(t1, t => t.Members, p2); // t1: [p2, p1]
        void verify(NodeStore s) {
            CollectionAssert.AreEqual(new[] { t1.Id, t2.Id }, relatedIds(s, p1.Id, false));
            CollectionAssert.AreEqual(new[] { t2.Id, t1.Id }, relatedIds(s, p2.Id, false));
            CollectionAssert.AreEqual(new[] { p2.Id, p1.Id }, relatedIds(s, t1.Id, true));
            CollectionAssert.AreEqual(new[] { p1.Id, p2.Id }, relatedIds(s, t2.Id, true));
        }
        verify(store);
        store.Maintenance(MaintenanceAction.TruncateLog); // rewrites the log from the snapshot
        verify(store);
        store.Dispose();
        store = new NodeStore(DataStoreLocal.Open(manyToManyModel(), null, io)); // replays the rewritten log
        verify(store);
        store.Dispose();
    }
    #endregion
}
