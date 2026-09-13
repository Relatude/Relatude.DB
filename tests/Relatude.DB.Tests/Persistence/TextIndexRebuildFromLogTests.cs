using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.Persistence;

#region datamodel
// InstantTextIndexing so the text index value is written as part of every transaction (otherwise a
// background queue fills it and the log replayed below might not hold it yet)
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class RebuiltTextArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(IndexedByWords = true)]
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}
#endregion

/// <summary>
/// The in-memory word indexes rebuilt from the log when their state files are gone: the whole log
/// is replayed and the word indexes are built in bulk (WordIndexLoader, reached through the
/// IIndex.CompleteStateLoad hook, which the optimizing wrapper must forward). The rebuilt index
/// must answer every search like the index that was there before, take new documents, and write a
/// state file the next open can use.
/// </summary>
[TestClass]
public class TextIndexRebuildFromLogTests {
    static readonly string[] Words = ["waterproof", "canvas", "leather", "titanium", "bamboo", "wireless", "sturdy", "foldable", "lantern", "backpack", "kettle", "monitor"];

    static (DataStoreLocal data, NodeStore store) open(IIOProvider io) {
        var dm = new Datamodel();
        dm.Add<RebuiltTextArticle>();
        var data = DataStoreLocal.Open(dm, null, io);
        return (data, new NodeStore(data));
    }
    static string text(Random rng, int words) => string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[(int)(Math.Pow(rng.NextDouble(), 2) * Words.Length)]));
    static string liveText(RebuiltTextArticle a) => a.Title + " | " + a.Body;
    static bool holds(string text, string word) => text.Split(' ').Contains(word, StringComparer.OrdinalIgnoreCase);
    static void verify(NodeStore store, Dictionary<Guid, string> live, string because) {
        Assert.AreEqual(live.Count, store.Query<RebuiltTextArticle>().Count(), "node count " + because);
        foreach (var w in Words.Append("zeppelin")) {
            var expected = live.Where(kv => holds(kv.Value, w)).Select(kv => kv.Key).Order().ToList();
            var found = store.Query<RebuiltTextArticle>().WhereSearch(w).Execute().Select(a => a.Id).Order().ToList();
            CollectionAssert.AreEqual(expected, found, "search for " + w + " " + because);
            var byTitle = live.Count(kv => holds(kv.Value.Split(" | ")[0], w));
            Assert.AreEqual(byTitle, store.Query<RebuiltTextArticle>().Where(a => a.Title.MatchesSearch(w)).Count(), "title search for " + w + " " + because);
        }
        Assert.AreEqual(0, store.Query<RebuiltTextArticle>().WhereSearch("nosuchword").Count(), because);
    }

    [TestMethod]
    public void DeletedStateAndIndexFiles_AreRebuiltFromTheLog_WithTheSameSearchResults() {
        var io = new IOProviderMemory();
        var rng = new Random(3);
        var live = new Dictionary<Guid, string>();
        {
            var (data, store) = open(io);
            var all = Enumerable.Range(0, 300).Select(i => new RebuiltTextArticle {
                Id = Guid.NewGuid(),
                Title = "Article " + i + " " + Words[i % Words.Length],
                Body = text(rng, 20 + rng.Next(40)),
            }).ToList();
            foreach (var chunk in all.Chunk(50)) store.Insert(chunk);
            foreach (var a in all) live[a.Id] = liveText(a);
            // an update re-indexes a node (the old text is removed, the new one added), a delete removes it
            foreach (var a in all.Where((_, i) => i % 4 == 0)) {
                a.Body = text(rng, 10 + rng.Next(30));
                store.Update(a);
                live[a.Id] = liveText(a);
            }
            foreach (var a in all.Where((_, i) => i % 7 == 0)) {
                store.Delete(a.Id);
                live.Remove(a.Id);
            }
            verify(store, live, "before the rebuild");
            store.Dispose();
        }
        // drop the state and every index snapshot: the next open has to replay the whole log
        FileKeyUtility.State_DeleteAll(io);
        foreach (var key in FileKeyUtility.Index_GetAll(io)) io.DeleteFileIfItExists(key);
        Assert.AreEqual(0, FileKeyUtility.Index_GetAll(io).Length);
        {
            var (data, store) = open(io);
            verify(store, live, "after the rebuild from the log");
            var late = new RebuiltTextArticle { Id = Guid.NewGuid(), Title = "Late zeppelin", Body = "waterproof canvas" };
            store.Insert(late);
            live[late.Id] = liveText(late);
            verify(store, live, "after an insert into the rebuilt index");
            data.SaveIndexStates(forceRefresh: true);
            Assert.IsTrue(FileKeyUtility.Index_GetAll(io).Length > 0, "the rebuilt indexes must have written their state files");
            store.Dispose();
        }
        {
            var (_, store) = open(io);
            verify(store, live, "after reopening from the state the rebuilt index wrote");
            store.Dispose();
        }
    }
}
