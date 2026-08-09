using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.DataStores.Sets;

namespace Relatude.Persistence;

/// <summary>
/// The WAL-file binding of the persisted index engines: index data only counts when it belongs to
/// the log file it is opened against (e.g. someone copies just the log file from another
/// installation — the indexes must be rebuilt). An engine bound to a foreign log id is reset and
/// then ADOPTS the new id, so the rebuild happens once, not on every startup.
/// </summary>
[TestClass]
public class WalFileBindingTests {

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static IndexEngines openEngines(string dir) => new(new NativeKvIndexStore(dir), new LuceneTextIndexEngine(dir));
    // mirrors the real startup order: indexes open first, the WAL binding check runs afterwards
    static (IValueIndex<string> value, IWordIndex word) openIndexes(IndexEngines engines) {
        var value = engines.Value!.OpenValueIndex<string>(new SetRegister(100), "v-test", "value test", PropertyType.String);
        var word = engines.Text!.OpenWordIndex(new SetRegister(100), "w-test", "word test", new WordIndexOptions(2, 64, true, false));
        return (value, word);
    }
    static void addData(IndexEngines engines, (IValueIndex<string> value, IWordIndex word) idx, int id, long timestamp) {
        engines.BeginTransaction();
        idx.value.Add(id, "hello" + id);
        idx.word.Add(id, "hello world " + id);
        engines.CommitTransaction(timestamp);
        engines.MakeDurable();
    }
    static void assertBinding(IndexEngines engines, Guid walFileId, long timestamp) {
        Assert.AreEqual(walFileId, engines.Value!.GetWalFileId(), "value engine WAL id");
        Assert.AreEqual(walFileId, engines.Text!.GetWalFileId(), "text engine WAL id");
        Assert.AreEqual(timestamp, engines.Value!.GetTimestamp(), "value engine timestamp");
        Assert.AreEqual(timestamp, engines.Text!.GetTimestamp(), "text engine timestamp");
    }

    [TestMethod]
    public void ForeignWalFileId_ResetsEnginesOnceAndAdoptsTheNewId() {
        var dir = tempDir();
        var logA = Guid.NewGuid();
        var logB = Guid.NewGuid();
        try {
            // fresh engines adopt the first log id and persist data against it
            using (var engines = openEngines(dir)) {
                var idx = openIndexes(engines);
                engines.BindToWalFile(logA, _ => { });
                addData(engines, idx, 1, timestamp: 1000);
                assertBinding(engines, logA, 1000);
            }
            // the log is now a different file (copied from elsewhere): both engines must reset
            // (timestamp 0 forces the rebuild) and adopt the new id — and stay usable afterwards
            using (var engines = openEngines(dir)) {
                var idx = openIndexes(engines);
                engines.BindToWalFile(logB, _ => { });
                assertBinding(engines, logB, 0);
                addData(engines, idx, 2, timestamp: 2000);
            }
            // next startup against the same log: the adopted binding holds, nothing resets again
            // (under the old behavior the stored id was still logA here, wiping the data every start)
            using (var engines = openEngines(dir)) {
                openIndexes(engines);
                engines.BindToWalFile(logB, _ => { });
                assertBinding(engines, logB, 2000);
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
