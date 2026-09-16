using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.Utils;
// both namespaces above hold a NodeStore; the one the tests drive is the public API
using NodeStore = Relatude.DB.Nodes.NodeStore;

namespace Relatude.Persistence;

#region datamodel
[Node]
public class LogArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Title { get; set; } = "";
}
#endregion

/// <summary>
/// Copying a log file up to a moment in time, which is what the admin UI's "go back in time" and
/// "make this the database file" are made of. The copy is written next to the log it came from and
/// the database opens on it, so what these verify is both halves: that the cut lands on a
/// transaction boundary at or before the moment asked for, and that a store opened on the copy
/// really is the database as it was then - state snapshot and indexes included, since those were
/// written against the file that is being left behind.
/// </summary>
[TestClass]
public class LogFileScanTests {

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static NodeStore openStore(string dir, string value = "Memory") {
        var dm = new Datamodel();
        dm.Add<LogArticle>();
        var settings = TestEngines.Settings(value: value);
        return new NodeStore(DataStoreLocal.Open(dm, settings, new IOProviderDisk(dir), null, null, null, null,
            TestEngines.Factory(dir, settings)));
    }

    static void insert(NodeStore store, int from, int count) {
        for (var i = from; i < from + count; i++) store.Insert(new LogArticle { Id = Guid.NewGuid(), Title = "article " + i });
    }

    static string[] logKey(int n) => FileKeyUtility.WAL_GetFileKey(n);

    /// <summary>
    /// Ten nodes, a remembered moment, five more. The copy taken at that moment must hold exactly
    /// the first ten, and the database opened on it must answer as it did then - even though the
    /// state snapshot and the indexes on disk were written when there were fifteen.
    /// </summary>
    [DataTestMethod]
    [DataRow("Memory")]
    [DataRow("Native")]
    public void CopyUntil_OpensTheDatabaseAsItWasAtThatMoment(string value) {
        var dir = tempDir();
        try {
            long moment;
            using (var store = openStore(dir, value)) {
                insert(store, 0, 10);
                moment = store.Timestamp;
                insert(store, 10, 5);
                Assert.AreEqual(15, store.Query<LogArticle>().Count());
                store.Maintenance(MaintenanceAction.SaveIndexStates); // the snapshot on disk now covers all fifteen; the copy must not be read against it
            }
            var io = new IOProviderDisk(dir);
            var cut = LogFileScan.CopyUntil(io, logKey(1), io, logKey(2), new DateTime(moment, DateTimeKind.Utc));
            Assert.AreEqual(5, cut.TransactionsDropped, "the five inserts after the moment");
            Assert.AreEqual(10, cut.TransactionsKept, "the ten inserts before it");
            Assert.AreEqual(moment, cut.LastKeptTimestamp, "the cut ends on the remembered transaction");
            Assert.IsTrue(cut.BytesKept < cut.FileSize && cut.BytesDropped > 0);
            Assert.AreEqual(cut.KeepEnd, io.GetFileSizeOrZeroIfUnknown(logKey(2)), "the copy is exactly the kept side");

            // the store opens on the newest log file, which is the copy
            using (var store = openStore(dir, value)) {
                Assert.AreEqual(10, store.Query<LogArticle>().Count(), "nodes written after the moment are gone");
                Assert.AreEqual(1, store.Query<LogArticle>().Where(a => a.Title == "article 9").Count(), "the last kept node is there");
                Assert.AreEqual(0, store.Query<LogArticle>().Where(a => a.Title == "article 10").Count(), "the first dropped node is not");
                Assert.AreEqual(moment, store.Timestamp, "the database ends at the moment it was copied up to");
                // and it is writable from there
                insert(store, 100, 1);
                Assert.AreEqual(11, store.Query<LogArticle>().Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The file it was cut from is untouched, so making that one the database again undoes
    /// the whole thing - the way back the admin UI offers from the Files page.</summary>
    [TestMethod]
    public void TheFileItWasCutFromStillOpensEverything() {
        var dir = tempDir();
        try {
            long moment;
            using (var store = openStore(dir)) {
                insert(store, 0, 10);
                moment = store.Timestamp;
                insert(store, 10, 5);
            }
            var io = new IOProviderDisk(dir);
            LogFileScan.CopyUntil(io, logKey(1), io, logKey(2), new DateTime(moment, DateTimeKind.Utc));
            using (var store = openStore(dir)) Assert.AreEqual(10, store.Query<LogArticle>().Count());

            // adopting the original: copied onto the next key, with an id of its own
            var size = io.GetFileSizeOrZeroIfUnknown(logKey(1));
            LogFileScan.Copy(io, logKey(1), io, logKey(3), size, Guid.NewGuid());
            using (var store = openStore(dir)) Assert.AreEqual(15, store.Query<LogArticle>().Count(), "all fifteen are back");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Until_BeforeTheFirstTransaction_KeepsNothing() {
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) insert(store, 0, 3);
            var io = new IOProviderDisk(dir);
            var header = LogFileScan.ReadHeader(io, logKey(1));
            Assert.IsNotNull(header.FirstUtc);
            var first = header.FirstTimestamp;
            var cut = LogFileScan.Until(io, logKey(1), new DateTime(first - TimeSpan.TicksPerSecond, DateTimeKind.Utc));
            Assert.AreEqual(0, cut.TransactionsKept);
            Assert.AreEqual(LogFileScan.HeaderLength, cut.KeepEnd, "nothing but the header would be copied");
            Assert.AreEqual(first, cut.FirstTimestamp);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Until_AfterTheLastTransaction_KeepsEverything() {
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) insert(store, 0, 4);
            var io = new IOProviderDisk(dir);
            var cut = LogFileScan.Until(io, logKey(1), DateTime.UtcNow.AddDays(1));
            Assert.AreEqual(0, cut.TransactionsDropped);
            Assert.AreEqual(cut.FileSize, cut.KeepEnd, "the whole file is on the kept side");
            Assert.AreEqual(cut.LastTimestamp, cut.LastKeptTimestamp);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The copy is a log file of its own. Everything derived from a log - the index engines
    /// above all - is bound to that id, and the rebuild they owe the copy depends on it differing.</summary>
    [TestMethod]
    public void TheCopyGetsItsOwnFileId() {
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) insert(store, 0, 3);
            var io = new IOProviderDisk(dir);
            var source = LogFileScan.ReadHeader(io, logKey(1));
            LogFileScan.CopyUntil(io, logKey(1), io, logKey(2), DateTime.UtcNow.AddDays(1));
            var copy = LogFileScan.ReadHeader(io, logKey(2));
            Assert.AreNotEqual(source.FileId, copy.FileId);
            Assert.AreEqual(source.FormatVersion, copy.FormatVersion);
            Assert.AreEqual(source.FirstTimestamp, copy.FirstTimestamp, "the transactions themselves are copied byte for byte");
            // a copy with no new id is byte identical, which is what the upload path relies on
            LogFileScan.Copy(io, logKey(1), io, logKey(3), io.GetFileSizeOrZeroIfUnknown(logKey(1)), null);
            Assert.AreEqual(source.FileId, LogFileScan.ReadHeader(io, logKey(3)).FileId);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Anything that is not a log file is refused before it can be put in place as one.</summary>
    [TestMethod]
    public void ReadHeader_RefusesWhatIsNotALogFile() {
        var io = new IOProviderMemory();
        io.WriteAllBytes(["not-a-log.bin"], new byte[200]);
        Assert.ThrowsException<IOException>(() => LogFileScan.ReadHeader(io, ["not-a-log.bin"]));
        io.WriteAllBytes(["tiny.bin"], new byte[8]);
        Assert.ThrowsException<IOException>(() => LogFileScan.ReadHeader(io, ["tiny.bin"]));
        Assert.ThrowsException<IOException>(() => LogFileScan.ReadHeader(io, ["missing.bin"]));
    }

    /// <summary>A log whose last transaction was torn by a crash: the copy ends where the file last
    /// read cleanly, exactly as the log reader treats it.</summary>
    [TestMethod]
    public void ATornTailIsNotCopied() {
        var dir = tempDir();
        try {
            long moment;
            using (var store = openStore(dir)) {
                insert(store, 0, 5);
                moment = store.Timestamp;
            }
            var io = new IOProviderDisk(dir);
            var whole = LogFileScan.Until(io, logKey(1), DateTime.UtcNow.AddDays(1));
            // half of the last transaction is lost
            var torn = whole.FileSize - 20;
            io.TruncateFile(logKey(1), torn);
            var cut = LogFileScan.Until(io, logKey(1), DateTime.UtcNow.AddDays(1));
            Assert.AreEqual(whole.TransactionsKept - 1, cut.TransactionsKept, "the torn transaction is not part of the file");
            Assert.IsTrue(cut.KeepEnd < torn);
            Assert.AreNotEqual(moment, cut.LastKeptTimestamp);
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
