using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;

namespace Relatude.Store;

[Node]
public class EmbArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    [EmbeddedMapProperty(KeyProperty = nameof(EmbParagraph.Code))]
    public EmbeddedMap<string, EmbParagraph> Paragraphs { get; set; } = [];
}
public class EmbParagraph {
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

// Regression tests for the embedded map aliasing bug: the InnerNodeDataMap instance inside a stored
// NodeData was shared with mapped objects, so mutating a loaded object's EmbeddedMap raced the
// background WAL flush serializing the same instance ("Collection was modified" in writeStatic).
// EmbeddedMap now copies on first write, and maps are frozen when the owning node data is cached.
[TestClass]
public class EmbeddedMapAliasingTests {

    static NodeStore open() {
        var dm = new Datamodel();
        dm.Add<EmbArticle>();
        dm.Add<EmbParagraph>();
        var storeData = DataStoreLocal.Open(dm, new SettingsLocal(), null);
        return new NodeStore(storeData);
    }
    static Guid insertArticleWithTwoParagraphs(NodeStore store) {
        var id = Guid.NewGuid();
        var a = new EmbArticle { Id = id, Title = "A" };
        a.Paragraphs.Add(new EmbParagraph { Id = Guid.NewGuid(), Code = "p1", Text = "one" });
        a.Paragraphs.Add(new EmbParagraph { Id = Guid.NewGuid(), Code = "p2", Text = "two" });
        store.Insert(a);
        return id;
    }

    [TestMethod]
    public void MutatingLoadedMap_DoesNotChangeStoredData_UntilUpdate() {
        using var store = open();
        var id = insertArticleWithTwoParagraphs(store);

        var loaded = store.Get<EmbArticle>(id);
        loaded.Paragraphs.Add(new EmbParagraph { Id = Guid.NewGuid(), Code = "p3", Text = "three" });
        loaded.Paragraphs.Remove("p1");

        // before the fix the mutations above wrote straight into the store's cached node data:
        var fresh = store.Get<EmbArticle>(id);
        Assert.AreEqual(2, fresh.Paragraphs.Count);
        Assert.IsTrue(fresh.Paragraphs.Contains("p1"));
        Assert.IsFalse(fresh.Paragraphs.Contains("p3"));

        store.Update(loaded);
        var updated = store.Get<EmbArticle>(id);
        Assert.AreEqual(2, updated.Paragraphs.Count);
        Assert.IsFalse(updated.Paragraphs.Contains("p1"));
        Assert.IsTrue(updated.Paragraphs.Contains("p3"));
        Assert.AreEqual("two", updated.Paragraphs["p2"].Text);
    }

    [TestMethod]
    public void MutatingMapAfterUpdate_DoesNotChangeStoredData_UntilNextUpdate() {
        using var store = open();
        var id = insertArticleWithTwoParagraphs(store);

        var loaded = store.Get<EmbArticle>(id);
        loaded.Paragraphs.Add(new EmbParagraph { Id = Guid.NewGuid(), Code = "p3", Text = "three" });
        store.Update(loaded);

        // the map instance was just handed to the store, further mutations must not reach it:
        loaded.Paragraphs.Add(new EmbParagraph { Id = Guid.NewGuid(), Code = "p4", Text = "four" });

        var fresh = store.Get<EmbArticle>(id);
        Assert.AreEqual(3, fresh.Paragraphs.Count);
        Assert.IsFalse(fresh.Paragraphs.Contains("p4"));

        store.Update(loaded);
        var updated = store.Get<EmbArticle>(id);
        Assert.AreEqual(4, updated.Paragraphs.Count);
        Assert.IsTrue(updated.Paragraphs.Contains("p4"));
    }

    [TestMethod]
    public void StoredMap_IsFrozen_DirectMutationThrows() {
        using var store = open();
        var id = insertArticleWithTwoParagraphs(store);

        var dm = store.Datastore.Datamodel;
        var typeModel = dm.NodeTypes.Values.First(t => t.CodeName == nameof(EmbArticle));
        var prop = typeModel.AllProperties.Values.First(p => p.CodeName == nameof(EmbArticle.Paragraphs));

        Assert.IsTrue(store.Datastore.TryGet(id, out var nodeData));
        Assert.IsTrue(nodeData.TryGetValue(prop.Id, out var value));
        var map = (InnerNodeDataMap<string>)value;

        Assert.IsTrue(map.IsReadOnly);
        Assert.ThrowsException<InvalidOperationException>(() => map.Remove("p1"));
        Assert.ThrowsException<InvalidOperationException>(() => map.Clear());

        // reads must still work on a frozen map:
        Assert.AreEqual(2, map.Count);
        Assert.IsTrue(map.ContainsKey("p1"));
        Assert.AreEqual(2, map.Count(n => n != null));
    }
}
