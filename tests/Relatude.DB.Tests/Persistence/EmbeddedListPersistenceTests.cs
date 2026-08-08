using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.Persistence;

[Node]
public interface IEmbSection {
    Guid Id { get; set; }
    string Heading { get; set; }
    Embedded<EmbSectionItem> Items { get; }
}
public class EmbSectionItem {
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

// Regression tests for Embedded<T> (EmbeddedValueType.InnerNodeList) log replay: the datamodel only
// cached the embedded key property type for InnerNodeMap, so deserializing an InnerNodeList from the
// transaction log threw "Key property type is not calculated." and the whole transaction was discarded
// as corrupt — nodes with an Embedded<T> property silently disappeared on reopen.
[TestClass]
public class EmbeddedListPersistenceTests {

    static Datamodel getDatamodel() {
        var dm = new Datamodel();
        dm.Add<IEmbSection>();
        dm.Add<EmbSectionItem>();
        return dm;
    }

    [TestMethod]
    public void EmbeddedList_SurvivesLogReplay() {
        var io = new IOProviderMemory();
        var datamodel = getDatamodel();
        var store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));

        var section = store.Create<IEmbSection>();
        section.Heading = "S1";
        section.Items.Add(new EmbSectionItem { Id = Guid.NewGuid(), Text = "one" });
        section.Items.Add(new EmbSectionItem { Id = Guid.NewGuid(), Text = "two" });
        store.Insert(section);
        var id = section.Id;
        store.Dispose();

        // reopen from the transaction log:
        store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));
        var loaded = store.Get<IEmbSection>(id);
        Assert.AreEqual("S1", loaded.Heading);
        Assert.AreEqual(2, loaded.Items.Count);
        CollectionAssert.AreEquivalent(new[] { "one", "two" }, loaded.Items.Select(i => i.Text).ToArray());
        store.Dispose();
    }

    [TestMethod]
    public void EmbeddedList_SurvivesLogTruncateAndReplay() {
        var io = new IOProviderMemory();
        var datamodel = getDatamodel();
        var store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));

        var section = store.Create<IEmbSection>();
        section.Heading = "S1";
        section.Items.Add(new EmbSectionItem { Id = Guid.NewGuid(), Text = "one" });
        store.Insert(section);
        var id = section.Id;

        // rewrites the log segments, forcing embedded data through serialization both ways:
        store.Maintenance(MaintenanceAction.ClearCache);
        store.Maintenance(MaintenanceAction.TruncateLog);
        var loaded = store.Get<IEmbSection>(id);
        Assert.AreEqual(1, loaded.Items.Count);
        store.Dispose();

        store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));
        loaded = store.Get<IEmbSection>(id);
        Assert.AreEqual("S1", loaded.Heading);
        Assert.AreEqual(1, loaded.Items.Count);
        Assert.AreEqual("one", loaded.Items.Single().Text);
        store.Dispose();
    }
}
