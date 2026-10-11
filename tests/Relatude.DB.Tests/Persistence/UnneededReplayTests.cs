using System.Text.RegularExpressions;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.Utils;
using NodeStore = Relatude.DB.Nodes.NodeStore; // disambiguate from the internal DataStores.Stores.NodeStore (visible via InternalsVisibleTo)

namespace Relatude.Persistence;

[Node]
public class ReplayArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = string.Empty;
    [IntegerProperty(Indexed = true)]
    public int Number { get; set; }
}

/// <summary>
/// The open replays only the log it has to:
/// <list type="bullet">
///   <item>A transaction that writes nothing to the log (it changed nothing, or it failed) gives its timestamp
///   back. Kept, it stamped the engines past the log - read at the next open as lost writes, with every index
///   reset and rebuilt - or the state file past the engines, which made the open replay the whole log.</item>
///   <item>A copy of the log (a truncated backup or download) leaves the state snapshot in place; only a hot
///   swap replaces the log the snapshot describes.</item>
///   <item>When an index is behind the snapshot and the replay starts at the beginning of the log, what the
///   state and every index already hold is passed over without being parsed.</item>
/// </list>
/// </summary>
[TestClass]
public class UnneededReplayTests {

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static Datamodel datamodel() {
        var dm = new Datamodel();
        dm.Add<ReplayArticle>();
        return dm;
    }
    static DataStoreLocal openWithEngine(string dir) {
        var settings = new SettingsLocal { ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId };
        return DataStoreLocal.Open(datamodel(), settings, new IOProviderDisk(dir), null, null, null, null,
            () => IndexEngines.Single(TestEngines.ValueId, new NativeKvIndexStore(dir)));
    }
    static string traceOf(IDataStore data) => string.Join("\n", data.GetSystemTrace(0, 1000).Reverse().Select(e => e.Text));
    static ReplayArticle article(int i) => new() { Id = Guid.NewGuid(), Name = "Name" + i, Number = i % 10 };
    static bool throws(Action action) {
        try {
            action();
            return false;
        } catch {
            return true;
        }
    }

    [TestMethod]
    public void TransactionsThatChangeNothing_DoNotMoveTheClock() {
        var data = DataStoreLocal.Open(datamodel(), null, new IOProviderMemory());
        using var store = new NodeStore(data);
        var a = article(1);
        store.Insert(a);
        var before = data.Timestamp;
        Assert.AreEqual(0, store.Upsert(a).TransactionId, "an unchanged upsert writes nothing");
        Assert.AreEqual(0, store.InsertIfNotExists(a).TransactionId, "the node is stored already");
        Assert.AreEqual(0, store.DeleteIfExists(Guid.NewGuid()).TransactionId, "there is no such node");
        Assert.AreEqual(before, data.Timestamp, "a transaction that writes nothing to the log must give its timestamp back");
        a.Number = 7;
        var changed = store.Upsert(a);
        Assert.IsTrue(changed.TransactionId > before);
        Assert.AreEqual(changed.TransactionId, data.Timestamp);
        Assert.AreEqual(1, store.Query<ReplayArticle>().Where(x => x.Number == 7).Count());
    }

    [TestMethod]
    public void FailedTransactions_DoNotMoveTheClock() {
        var data = DataStoreLocal.Open(datamodel(), null, new IOProviderMemory());
        using var store = new NodeStore(data);
        var a = article(1);
        store.Insert(a);
        var before = data.Timestamp;
        Assert.IsTrue(throws(() => store.Insert(a)), "inserting a stored node again fails");
        Assert.AreEqual(before, data.Timestamp, "a failed transaction writes nothing to the log and must give its timestamp back");
        var b = article(2);
        store.Insert(b);
        Assert.IsTrue(data.Timestamp > before);
        Assert.AreEqual(2, store.Query<ReplayArticle>().Count());
    }

    [TestMethod]
    public void ATransactionThatChangesNothing_AsTheLastBeforeClose_DoesNotRebuildAtOpen() {
        var dir = tempDir();
        try {
            var a = article(0);
            using (var store = new NodeStore(openWithEngine(dir))) {
                store.Insert(a);
                for (var i = 1; i < 20; i++) store.Insert(article(i));
                store.Upsert(a); // changes nothing; it used to stamp the engine with a timestamp the log never got
            }
            var data = openWithEngine(dir);
            using (var store = new NodeStore(data)) {
                var trace = traceOf(data);
                Assert.IsFalse(trace.Contains("ahead of the log file"), trace);
                Assert.IsFalse(trace.Contains("Rebuilding index from log"), trace);
                Assert.AreEqual(20, store.Query<ReplayArticle>().Count());
                Assert.AreEqual(2, store.Query<ReplayArticle>().Where(x => x.Number == 3).Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void AFailedTransaction_BeforeASnapshot_DoesNotMakeTheOpenReadTheWholeLog() {
        var dir = tempDir();
        try {
            var data = openWithEngine(dir);
            using (var store = new NodeStore(data)) {
                var a = article(0);
                store.Insert(a);
                for (var i = 1; i < 20; i++) store.Insert(article(i));
                Assert.IsTrue(throws(() => store.Insert(a)));
                // the snapshot used to carry the failed transaction's timestamp, newer than the engine
                data.SaveIndexStates();
            }
            data = openWithEngine(dir);
            using (var store = new NodeStore(data)) {
                var trace = traceOf(data);
                StringAssert.Contains(trace, "Reading state file");
                Assert.IsFalse(trace.Contains("reading the log from the beginning"), trace);
                Assert.AreEqual(2, store.Query<ReplayArticle>().Where(x => x.Number == 3).Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void SetTimestamp_MovesTheEnginesWithTheLog() {
        var dir = tempDir();
        try {
            var data = openWithEngine(dir);
            using (var store = new NodeStore(data)) {
                for (var i = 0; i < 20; i++) store.Insert(article(i));
                data.SetTimestamp(data.Timestamp + 1000);
                data.SaveIndexStates();
            }
            data = openWithEngine(dir);
            using (var store = new NodeStore(data)) {
                var trace = traceOf(data);
                StringAssert.Contains(trace, "Reading state file");
                Assert.IsFalse(trace.Contains("reading the log from the beginning"), trace);
                Assert.AreEqual(2, store.Query<ReplayArticle>().Where(x => x.Number == 3).Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void ATruncatedCopy_KeepsTheStateSnapshot() {
        var io = new IOProviderMemory();
        var data = DataStoreLocal.Open(datamodel(), null, io);
        using (var store = new NodeStore(data)) {
            for (var i = 0; i < 20; i++) store.Insert(article(i));
            data.SaveIndexStates();
            Assert.IsNotNull(FileKeyUtility.State_GetNewestFileKey(io));
            data.RewriteStore(false, FileKeyUtility.WAL_GetFileKeyForBackup(DateTime.UtcNow, false), io); // what a truncated backup does
            Assert.IsNotNull(FileKeyUtility.State_GetNewestFileKey(io), "a copy leaves the live log, and the snapshot of it, as they are");
        }
        data = DataStoreLocal.Open(datamodel(), null, io, null, null, null, null, null, true, true);
        using (var store = new NodeStore(data)) {
            var trace = traceOf(data);
            StringAssert.Contains(trace, "Reading state file");
            Assert.IsFalse(trace.Contains("No state file"), trace);
            Assert.AreEqual(20, store.Query<ReplayArticle>().Count());
        }
    }

    [TestMethod]
    public void IndexesBehindTheSnapshot_PassOverWhatEverythingHolds() {
        var io = new IOProviderMemory();
        var data = DataStoreLocal.Open(datamodel(), null, io);
        Dictionary<string, byte[]> firstIndexFiles;
        using (var store = new NodeStore(data)) {
            for (var i = 0; i < 100; i++) store.Insert(article(i));
            data.SaveIndexStates();
            firstIndexFiles = FileKeyUtility.Index_GetAllNumbered(io).ToDictionary(k => k.AsKeyString(), k => io.ReadAllBytes(k));
            for (var i = 100; i < 130; i++) store.Insert(article(i));
            data.SaveIndexStates();
        }
        // the indexes go back to the first snapshot while the state file stays at the second: they are missing
        // the last 30 inserts, which lie before the state file's position in the log
        foreach (var key in FileKeyUtility.Index_GetAllNumbered(io)) io.DeleteFileIfItExists(key);
        foreach (var (key, bytes) in firstIndexFiles) io.WriteAllBytes(key.SplitKey(), bytes);

        data = DataStoreLocal.Open(datamodel(), null, io, null, null, null, null, null, true, true);
        using (var store = new NodeStore(data)) {
            var trace = traceOf(data);
            StringAssert.Contains(trace, "reading the log from the beginning");
            var passedOver = Regex.Match(trace, @"Passed over ([\d\s,.]+) transactions");
            Assert.IsTrue(passedOver.Success, trace);
            var count = int.Parse(new string(passedOver.Groups[1].Value.Where(char.IsDigit).ToArray()));
            Assert.IsTrue(count >= 99, "the first 100 inserts (but the one at the indexes' own timestamp) are held by everything: " + count);
            // the indexes got the 30 inserts they were missing, and only once
            Assert.AreEqual(130, store.Query<ReplayArticle>().Count());
            Assert.AreEqual(13, store.Query<ReplayArticle>().Where(x => x.Number == 3).Count());
            Assert.AreEqual(1, store.Query<ReplayArticle>().Where(x => x.Name == "Name120").Count());
            Assert.AreEqual(1, store.Query<ReplayArticle>().Where(x => x.Name == "Name50").Count());
        }
    }
}
