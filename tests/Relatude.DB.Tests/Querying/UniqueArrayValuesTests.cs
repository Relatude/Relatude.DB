using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;

namespace Relatude.Querying;

[Node]
public class UniqueTagDoc {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty]
    public string Name { get; set; } = "";
    [GuidArrayProperty(Indexed = true, UniqueValues = true)]
    public Guid[] TagIds { get; set; } = [];
    [StringArrayProperty(Indexed = true, UniqueValues = true)]
    public string[] Tags { get; set; } = [];
}

[TestClass]
public class UniqueArrayValuesTests {

    [TestMethod]
    public void UniqueValues_OnArrayProperties_ChecksElementsNotTheWholeArray() {
        // the unique constraint receives the node's whole array; it must test each element
        // against the index (a whole-array coercion used to collapse to Guid.Empty /
        // "System.String[]", making the constraint a silent no-op)
        var dm = new Datamodel();
        dm.Add<UniqueTagDoc>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        store.Insert(new UniqueTagDoc { Name = "A", TagIds = [g1], Tags = ["a"] });
        assertThrows(() => store.Insert(new UniqueTagDoc { Name = "B", TagIds = [g1], Tags = ["b"] }),
            "A duplicate guid element must violate the unique constraint");
        assertThrows(() => store.Insert(new UniqueTagDoc { Name = "C", TagIds = [g2], Tags = ["a"] }),
            "A duplicate string element must violate the unique constraint");
        store.Insert(new UniqueTagDoc { Name = "D", TagIds = [g2], Tags = ["b"] }); // all elements unique: allowed
        Assert.AreEqual(2, store.Query<UniqueTagDoc>().Count());
        store.Dispose();
    }

    static void assertThrows(Action action, string message) {
        try { action(); } catch { return; }
        Assert.Fail(message);
    }
}
