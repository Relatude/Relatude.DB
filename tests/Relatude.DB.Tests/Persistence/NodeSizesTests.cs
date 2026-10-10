using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Serialization;
using System.Text;
using NodeStore = Relatude.DB.Nodes.NodeStore; // disambiguate from the internal DataStores.Stores.NodeStore (visible via InternalsVisibleTo)

namespace Relatude.Persistence;

#region datamodel
[Node]
public class SizeArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public string Body { get; set; } = "";
    public int Number { get; set; }
}
[Node]
public class SizeTag {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}
#endregion

/// <summary>
/// The node sizes behind the storage page's dialog: every node's segment in the log file, and a
/// node's stored bytes taken apart by <see cref="FromBytes.NodeDataAnatomy"/>. The anatomy walks the
/// format without the reader, so these tests are what notices a change to the format made in one of
/// the two and not the other.
/// </summary>
[TestClass]
public class NodeSizesTests {
    static NodeStore openMemoryStore() {
        var dm = new Datamodel();
        dm.Add<SizeArticle>();
        dm.Add<SizeTag>();
        return new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal(), null, throwOnBadStateFile: true, throwOnBadLogFile: true));
    }

    [TestMethod]
    public void EveryWrittenNodeIsListedWithItsTypeAndLength() {
        using var store = openMemoryStore();
        for (var i = 0; i < 20; i++) store.Insert(new SizeArticle { Id = Guid.NewGuid(), Body = new string('x', 100 * i), Number = i });
        for (var i = 0; i < 5; i++) store.Insert(new SizeTag { Id = Guid.NewGuid(), Name = "tag " + i }, flushToDisk: i == 4);
        var db = (DataStoreLocal)store.Datastore;
        var snapshot = db.GetNodeSizes();
        var articleType = store.Datastore.Datamodel.NodeTypes.Values.Single(t => t.CodeName == nameof(SizeArticle)).Id;
        var tagType = store.Datastore.Datamodel.NodeTypes.Values.Single(t => t.CodeName == nameof(SizeTag)).Id;
        Assert.AreEqual(0, snapshot.NotYetWritten, "a flushed store has every node in the log");
        Assert.AreEqual(20, snapshot.Nodes.Count(n => n.NodeTypeId == articleType));
        Assert.AreEqual(5, snapshot.Nodes.Count(n => n.NodeTypeId == tagType));
        Assert.IsTrue(snapshot.Nodes.All(n => n.Length > 0 && n.Position > 0), "every node has a place in the file");
        Assert.IsTrue(snapshot.Nodes.All(n => n.Position + n.Length <= snapshot.LogFileSize), "every segment is inside the file");
        // a body a hundred characters longer is a node a hundred bytes longer: the length is the stored size
        var articles = snapshot.Nodes.Where(n => n.NodeTypeId == articleType).OrderBy(n => n.Length).ToArray();
        for (var i = 1; i < articles.Length; i++) Assert.AreEqual(100, articles[i].Length - articles[i - 1].Length, "length step at " + i);
    }

    [TestMethod]
    public void AnatomyAccountsForEveryByte() {
        using var store = openMemoryStore();
        var body = string.Concat(Enumerable.Repeat("Ærlig talt, ", 500)); // multi-byte characters: bytes are not characters
        var id = Guid.NewGuid();
        store.Insert(new SizeArticle { Id = id, Body = body, Number = 7 }, flushToDisk: true);
        var db = (DataStoreLocal)store.Datastore;
        var entry = db.GetNodeSizes().Nodes.Single();
        var stored = db.ReadStoredNodes([entry.Id], long.MaxValue).Single();
        Assert.AreEqual(id, stored.NodeId);
        Assert.IsNotNull(stored.Bytes);
        Assert.AreEqual(entry.Length, stored.Bytes!.Length);

        var anatomy = FromBytes.NodeDataAnatomy(stored.Bytes);
        Assert.AreEqual(id, anatomy.NodeId);
        Assert.AreEqual(entry.Id, anatomy.Id);
        Assert.AreEqual(entry.NodeTypeId, anatomy.NodeTypeId);
        Assert.AreEqual(stored.Bytes.Length, anatomy.Parts.Sum(p => p.Bytes), "the parts add up to the node");

        var bodyProperty = store.Datastore.Datamodel.NodeTypes[entry.NodeTypeId].AllPropertiesByName[nameof(SizeArticle.Body)];
        var bodyPart = anatomy.Parts.Single(p => p.Kind == NodeAnatomyPartKind.Property && p.PropertyId == bodyProperty.Id);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(body), bodyPart.ValueBytes, "the value is its encoded bytes");
        Assert.AreEqual(bodyPart.ValueBytes + 16 + 4 + 4, bodyPart.Bytes, "property id, type and length are stored in front of it");
        Assert.AreEqual(anatomy.Parts.Max(p => p.Bytes), bodyPart.Bytes, "the body is the largest part");
    }

    [TestMethod]
    public void ReadStoredNodesKeepsToItsBudget() {
        using var store = openMemoryStore();
        store.Insert(new SizeArticle { Id = Guid.NewGuid(), Body = new string('a', 5000) });
        store.Insert(new SizeArticle { Id = Guid.NewGuid(), Body = new string('b', 50) }, flushToDisk: true);
        var db = (DataStoreLocal)store.Datastore;
        var nodes = db.GetNodeSizes().Nodes.OrderByDescending(n => n.Length).ToArray();
        // the large one first does not fit, the small one after it still does
        var read = db.ReadStoredNodes([.. nodes.Select(n => n.Id)], 1000);
        Assert.AreEqual(2, read.Length, "a node over the budget is still listed");
        Assert.IsNull(read[0].Bytes, "the large node is not read");
        Assert.AreEqual(nodes[0].Length, read[0].Length, "but its length is known");
        Assert.IsNotNull(read[1].Bytes, "the small node is read");
        // gone nodes are left out
        Assert.AreEqual(0, db.ReadStoredNodes([int.MaxValue - 1], long.MaxValue).Length);
    }
}
