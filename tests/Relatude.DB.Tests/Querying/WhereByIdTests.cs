using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.Store;

namespace Relatude.Querying;

// The Where(id)/Where(ids) query overloads translate to the engine's WhereInIds method - they used
// to emit .Where(<constant>), which the parser rejects since "where" only takes lambdas. These tests
// pin the id filters down, including seeding a Traverse over a native relation wrapper property from
// a single id (the pattern used by the PageUrlManager sample in Website.Simple).
[TestClass]
public class WhereByIdTests {

    static NodeStore open() {
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        var data = new DataStoreLocal(dm, new SettingsLocal(), new IOProviderMemory());
        data.Open(true, true);
        return new NodeStore(data);
    }
    static Guid insert(NodeStore db, string title, string slug, Guid? parentId = null) {
        var page = new UrlPage() { Id = Guid.NewGuid(), Title = title, Slug = slug };
        db.Insert(page);
        if (parentId.HasValue) db.Execute(new Transaction(db).Relation.Relate<UrlPageTree>(parentId.Value, page.Id));
        return page.Id;
    }

    [TestMethod]
    public void WhereByGuid_FiltersToOneNode() {
        using var db = open();
        var a = insert(db, "A", "a");
        insert(db, "B", "b");
        var result = db.Query<UrlPage>().Where(a).Execute().ToArray();
        Assert.AreEqual(1, result.Length);
        Assert.AreEqual(a, result[0].Id);
    }

    [TestMethod]
    public void WhereByIntId_FiltersToOneNode_AndUnknownIdMatchesNothing() {
        using var db = open();
        var a = insert(db, "A", "a");
        Assert.IsTrue(db.Datastore.TryGetNodeMeta(a, out var meta));
        var result = db.Query<UrlPage>().Where(meta.InternalId).Execute().ToArray();
        Assert.AreEqual(1, result.Length);
        Assert.AreEqual(a, result[0].Id);
        Assert.AreEqual(0, db.Query<UrlPage>().Where(int.MaxValue).Execute().Count());
    }

    [TestMethod]
    public void WhereByGuids_FiltersToGivenNodes_AndEmptyListMatchesNothing() {
        using var db = open();
        var a = insert(db, "A", "a");
        var b = insert(db, "B", "b");
        insert(db, "C", "c");
        Assert.AreEqual(2, db.Query<UrlPage>().Where(new[] { a, b }).Execute().Count());
        Assert.AreEqual(0, db.Query<UrlPage>().Where(Array.Empty<Guid>()).Execute().Count());
    }

    [TestMethod]
    public void WhereByGuid_SeedsATraverseOverANativeRelationProperty() {
        using var db = open();
        var parent = insert(db, "P", "p");
        var child1 = insert(db, "C1", "c1", parent);
        var child2 = insert(db, "C2", "c2", parent);
        insert(db, "Other", "other");
        var reached = db.Query<UrlPage>().Where(parent).Traverse<UrlPage>(p => p.Children, maxLevel: 1).Execute().ToArray();
        Assert.AreEqual(2, reached.Length);
        Assert.IsTrue(reached.Any(p => p.Id == child1));
        Assert.IsTrue(reached.Any(p => p.Id == child2));
    }
}
