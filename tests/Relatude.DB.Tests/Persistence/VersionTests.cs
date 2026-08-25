using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using NodeStore = Relatude.DB.Nodes.NodeStore; // disambiguate from the internal DataStores.Stores.NodeStore (visible via InternalsVisibleTo)

namespace Relatude.Persistence;

#region datamodel
[Node]
public class VerArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public string Body { get; set; } = "";
    public int Number { get; set; }
}
#endregion

/// <summary>
/// FindOlderVersions: older versions of a node read straight from the version chains in the
/// transaction log files. Every node write appends the full node together with the position of the
/// node's previous version in the same file, so history is walked by chain — never cached. The
/// primary log reaches back to the last log rewrite; the secondary backup log survives rewrites and
/// extends the reach. The current version is never included and deleted nodes are not supported.
/// </summary>
[TestClass]
public class VersionTests {

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static Datamodel model() {
        var dm = new Datamodel();
        dm.Add<VerArticle>();
        return dm;
    }
    // strict on state and log files: a state file these tests wrote must read back cleanly,
    // a silent rebuild-from-log would mask chain-state bugs
    static NodeStore openMemoryStore(bool secondaryLog = false) {
        return new NodeStore(DataStoreLocal.Open(model(), new SettingsLocal { SecondaryBackupLog = secondaryLog }, null,
            throwOnBadStateFile: true, throwOnBadLogFile: true));
    }
    static NodeStore openDiskStore(string dir, bool secondaryLog = false) {
        return new NodeStore(DataStoreLocal.Open(model(), new SettingsLocal { SecondaryBackupLog = secondaryLog }, new IOProviderDisk(dir),
            throwOnBadStateFile: true, throwOnBadLogFile: true));
    }
    /// <summary>Inserts the node as version 0 and updates it to version 1..updates; version n has Body "vn" and Number n.</summary>
    static Guid insertAndUpdate(NodeStore store, int updates) {
        var id = Guid.NewGuid();
        store.Insert(new VerArticle { Id = id, Body = "v0", Number = 0 });
        update(store, id, 1, updates);
        return id;
    }
    static void update(NodeStore store, Guid id, int fromVersion, int toVersion) {
        for (var i = fromVersion; i <= toVersion; i++) {
            var a = store.Get<VerArticle>(id);
            a.Body = "v" + i;
            a.Number = i;
            store.Update(a);
        }
    }
    static void assertVersions(NodeVersion<VerArticle>[] versions, params int[] expectedNumbersNewestFirst) {
        Assert.AreEqual(expectedNumbersNewestFirst.Length, versions.Length, "version count");
        for (var i = 0; i < versions.Length; i++) {
            Assert.AreEqual(expectedNumbersNewestFirst[i], versions[i].Node.Number, "number of version " + i);
            Assert.AreEqual("v" + expectedNumbersNewestFirst[i], versions[i].Node.Body, "body of version " + i);
            Assert.IsFalse(string.IsNullOrEmpty(versions[i].Source), "source of version " + i);
            Assert.AreEqual(versions[i].Timestamp, versions[i].EstimatedCreationUtc.Ticks, "estimated creation matches timestamp");
            if (i > 0) Assert.IsTrue(versions[i].Timestamp < versions[i - 1].Timestamp, "timestamps strictly decreasing at " + i);
        }
    }

    [TestMethod]
    public void StrictlyOlderVersions_NewestFirst() {
        using var store = openMemoryStore();
        var id = insertAndUpdate(store, 5); // v0..v5, current is v5
        // no explicit flush anywhere: the api must flush queued transactions itself, and versions
        // written in the same flush batch must still chain correctly
        assertVersions(store.FindOlderVersions<VerArticle>(id), 4, 3, 2, 1, 0);
        // the current version is not part of the result
        Assert.IsFalse(store.FindOlderVersions<VerArticle>(id).Any(v => v.Node.Number == 5), "current version excluded");
        // unknown node, and a node without history, both give empty results
        Assert.AreEqual(0, store.FindOlderVersions(Guid.NewGuid()).Length, "unknown node");
        var freshId = Guid.NewGuid();
        store.Insert(new VerArticle { Id = freshId, Body = "v0", Number = 0 });
        Assert.AreEqual(0, store.FindOlderVersions(freshId).Length, "node without history");
    }

    [TestMethod]
    public void MaxCountLimitsResult() {
        using var store = openMemoryStore();
        var id = insertAndUpdate(store, 5);
        assertVersions(store.FindOlderVersions<VerArticle>(id, 2), 4, 3);
        Assert.AreEqual(0, store.FindOlderVersions(id, 0).Length);
    }

    [TestMethod]
    public void VersionsSurviveRestart() {
        var dir = tempDir();
        Guid id;
        using (var store = openDiskStore(dir)) {
            id = insertAndUpdate(store, 3);
            assertVersions(store.FindOlderVersions<VerArticle>(id), 2, 1, 0);
        }
        using (var store = openDiskStore(dir)) {
            // the chain must continue across the restart: the first new write links to the last
            // version written before the restart
            update(store, id, 4, 5);
            assertVersions(store.FindOlderVersions<VerArticle>(id), 4, 3, 2, 1, 0);
        }
        using (var store = openDiskStore(dir)) {
            assertVersions(store.FindOlderVersions<VerArticle>(id), 4, 3, 2, 1, 0);
        }
    }

    [TestMethod]
    public void RewriteResetsPrimaryChains() {
        using var store = openMemoryStore();
        var db = (DataStoreLocal)store.Datastore;
        var id = insertAndUpdate(store, 3);
        assertVersions(store.FindOlderVersions<VerArticle>(id), 2, 1, 0);
        db.RewriteStore(true, FileKeyUtility.WAL_NextFileKey(db.IO));
        // a rewrite keeps only the current version of each node, so without a secondary log the history is gone
        Assert.AreEqual(0, store.FindOlderVersions(id).Length, "history gone after rewrite");
        // the rewritten record becomes the previous version of the next write
        update(store, id, 4, 4);
        assertVersions(store.FindOlderVersions<VerArticle>(id), 3);
    }

    [TestMethod]
    public void SecondaryLogKeepsHistoryAcrossRewrite() {
        var dir = tempDir();
        Guid id;
        using (var store = openDiskStore(dir, secondaryLog: true)) {
            var db = (DataStoreLocal)store.Datastore;
            id = insertAndUpdate(store, 3); // v0..v3
            db.RewriteStore(true, FileKeyUtility.WAL_NextFileKey(db.IO));
            // the primary chain is reset, but the secondary log survives the rewrite with the full history
            assertVersions(store.FindOlderVersions<VerArticle>(id), 2, 1, 0);
            update(store, id, 4, 5); // v4, v5 written to both files
            // the version the rewrite copied into the new primary has the same content as v3 in the
            // secondary and must collapse into it, not show up as an extra version
            var versions = store.FindOlderVersions<VerArticle>(id);
            assertVersions(versions, 4, 3, 2, 1, 0);
            Assert.IsTrue(versions.Any(v => v.Source == FileKeyUtility.WAL_GetSecondaryFileKey().AsKeyString()), "deep history read from the secondary log");
            Assert.IsTrue(versions.Any(v => v.Source == FileKeyUtility.WAL_GetLatestFileKey(db.IO).AsKeyString()), "recent history read from the primary log");
        }
        using (var store = openDiskStore(dir, secondaryLog: true)) {
            // reopens from the persisted chain state written at the rewrite; the deep history in
            // the secondary log stays reachable and the chains keep working across the restart
            assertVersions(store.FindOlderVersions<VerArticle>(id), 4, 3, 2, 1, 0);
            update(store, id, 6, 6);
            assertVersions(store.FindOlderVersions<VerArticle>(id), 5, 4, 3, 2, 1, 0);
        }
    }

    [TestMethod]
    public void SecondaryChainStateSurvivesRestart_WithTailReplay() {
        var dir = tempDir();
        Guid id;
        using (var store = openDiskStore(dir, secondaryLog: true)) {
            id = insertAndUpdate(store, 1); // v0, v1
            store.Datastore.SaveIndexStates(); // persists the chain heads at v1
            update(store, id, 2, 3); // v2, v3 reach the log files only after the state save
        }
        using (var store = openDiskStore(dir, secondaryLog: true)) {
            // the persisted heads are behind the log files; the tail replay must bring them up to
            // date, so v4 links to v3 and not to v1
            update(store, id, 4, 4);
            assertVersions(store.FindOlderVersions<VerArticle>(id), 3, 2, 1, 0);
        }
    }

    [TestMethod]
    public void RevertWindowRollbackTruncatesVersions() {
        using var store = openMemoryStore();
        var id = insertAndUpdate(store, 2); // v0..v2
        store.BeginRevertWindow();
        update(store, id, 3, 5);
        assertVersions(store.FindOlderVersions<VerArticle>(id), 4, 3, 2, 1, 0);
        store.RollbackRevertWindow();
        // the rolled back versions are gone from the log, and the chain is intact below the cut
        assertVersions(store.FindOlderVersions<VerArticle>(id), 1, 0);
        update(store, id, 3, 3);
        assertVersions(store.FindOlderVersions<VerArticle>(id), 2, 1, 0);
    }

    [TestMethod]
    public void DeleteAndReinsertSameGuid_StartsNewChain() {
        using var store = openMemoryStore();
        var id = insertAndUpdate(store, 1); // v0, v1
        store.Delete(id);
        store.Insert(new VerArticle { Id = id, Body = "v10", Number = 10 });
        update(store, id, 11, 11);
        // a delete ends the chain: the reinserted node starts a new history and the old versions are not reachable
        assertVersions(store.FindOlderVersions<VerArticle>(id), 10);
    }

    [TestMethod]
    public void LegacyV1000LogFile_OpensAndReturnsNoVersions() {
        var dir = tempDir();
        var io = new IOProviderDisk(dir);
        string[] fileKey = ["db.00000001.bin"]; // the legacy root level key: the store moves it to the data folder on startup
        using (var s = io.OpenAppend(fileKey)) { // a v1000 log file header: records appended to it must stay in the legacy format
            s.WriteMarker(WALFile._logStartMarker);
            s.WriteVerifiedLong(WALFile._logVersionNumberV1000);
            s.WriteGuid(Guid.NewGuid());
        }
        Guid id;
        using (var store = openDiskStore(dir)) {
            id = insertAndUpdate(store, 2);
            Assert.AreEqual(0, store.FindOlderVersions(id).Length, "legacy format carries no version chains");
        }
        using (var store = openDiskStore(dir)) {
            Assert.AreEqual(2, store.Get<VerArticle>(id).Number, "legacy records replay correctly");
            Assert.AreEqual(0, store.FindOlderVersions(id).Length);
        }
        Assert.IsFalse(io.Exists(fileKey), "the legacy root file must have been moved to the data folder");
        using (var s = io.OpenAppend(FileKeyUtility.MapLegacyRootFileKeyToDataFolder(fileKey))) {
            Assert.AreEqual(WALFile._logVersionNumberV1000, s.GetVerifiedLong(16), "appending must not upgrade the file format");
        }
    }
}
