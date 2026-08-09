using Microsoft.Data.Sqlite;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.Persistence;

/// <summary>
/// The FTS5 word index's document key. The node id lives in the table's rowid rather than in an
/// fts5 column: an fts5 column is full-text indexed, not key-indexed, so keying on one turned every
/// delete (and therefore every update, which is a delete plus an insert) into a scan of the whole
/// table, and made the id's digits searchable text. These tests pin the behaviors that follow from
/// the rowid layout, including what happens to a database still holding the old one.
/// </summary>
[TestClass]
public class SqliteWordIndexTests {

    const int MinWord = 2, MaxWord = 64;

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static IWordIndex openIndex(SqliteIndexStore engine, string id = "w-test") =>
        engine.OpenWordIndex(new SetRegister(0), id, "word test", new WordIndexOptions(MinWord, MaxWord, true, false));
    static void inTransaction(SqliteIndexStore engine, long timestamp, Action work) {
        engine.BeginTransaction();
        work();
        engine.CommitTransaction(timestamp);
        engine.MakeDurable();
    }
    static TermSet terms(string query) => TermSet.Parse(query, MinWord, MaxWord, allowInfix: false);
    static int[] search(IWordIndex idx, string query) =>
        [.. idx.SearchForIdSetUnranked(terms(query), true, 100).Enumerate().Order()];
    static string dbPath(string dir) => Path.Combine(dir, FileKeyUtility.IndexEngine_SqliteFolderKey, FileKeyUtility.IndexEngine_SqliteFileKey);

    [TestMethod]
    public void RemoveDeletesExactlyOneDocument() {
        var dir = tempDir();
        try {
            using var engine = new SqliteIndexStore(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            inTransaction(engine, 1000, () => {
                idx.Add(1, "waterproof canvas backpack");
                idx.Add(2, "waterproof leather satchel");
                idx.Add(3, "lightweight canvas tote");
            });
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, search(idx, "waterproof canvas"));
            inTransaction(engine, 2000, () => idx.Remove(2, "waterproof leather satchel"));
            CollectionAssert.AreEqual(new[] { 1 }, search(idx, "waterproof"), "only the removed document may disappear");
            CollectionAssert.AreEqual(new[] { 1, 3 }, search(idx, "canvas"));
            Assert.AreEqual(0, search(idx, "satchel").Length);
            // update = remove + add of the same node id, which the rowid layout must accept
            inTransaction(engine, 3000, () => {
                idx.Remove(1, "waterproof canvas backpack");
                idx.Add(1, "golden silk scarf");
            });
            Assert.AreEqual(0, search(idx, "waterproof").Length, "the old text must be gone after an update");
            CollectionAssert.AreEqual(new[] { 1 }, search(idx, "golden"));
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void NodeIdIsNotSearchableText() {
        var dir = tempDir();
        try {
            using var engine = new SqliteIndexStore(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            inTransaction(engine, 1000, () => {
                idx.Add(12345, "alpha bravo");
                idx.Add(99, "12345 charlie"); // the digits as real content, which must still match
            });
            CollectionAssert.AreEqual(new[] { 99 }, search(idx, "12345"),
                "a search for a number must match documents containing it, not the document whose node id it is");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void ReAddingTheSameNodeIdReplacesItsText() {
        // the WAL replay after a crash re-delivers adds the index may already hold
        var dir = tempDir();
        try {
            using var engine = new SqliteIndexStore(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            inTransaction(engine, 1000, () => idx.Add(7, "crimson sunset"));
            inTransaction(engine, 2000, () => idx.Add(7, "crimson sunset"));
            CollectionAssert.AreEqual(new[] { 7 }, search(idx, "crimson"), "the document must not be duplicated");
            inTransaction(engine, 3000, () => idx.Remove(7, "crimson sunset"));
            Assert.AreEqual(0, search(idx, "crimson").Length, "one remove must clear it, not uncover a second copy");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void LegacyIdColumnTable_IsDroppedAndRebuiltFromTheLog() {
        var dir = tempDir();
        var walId = Guid.NewGuid();
        try {
            // a database written by the previous layout: the node id as an fts5 column
            Directory.CreateDirectory(Path.Combine(dir, FileKeyUtility.IndexEngine_SqliteFolderKey));
            using (var con = new SqliteConnection("Data Source=" + dbPath(dir))) {
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "CREATE VIRTUAL TABLE Ww_test USING fts5(id, value, prefix ='2 3')";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO Ww_test (id, value) VALUES (1, 'legacy row')";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            using (var engine = new SqliteIndexStore(dir)) {
                engine.SetWalFileId(walId);
                var idx = openIndex(engine);
                // the old table is gone, so the index reports position 0 — the signal that makes
                // the startup loader replay the whole log into it
                Assert.AreEqual(0, idx.PersistedTimestamp, "a rebuilt index must report timestamp 0");
                Assert.AreEqual(0, search(idx, "legacy").Length, "the legacy rows must not survive under unknown ids");
                // the replay the store would then run
                inTransaction(engine, 1000, () => {
                    idx.RegisterAddDuringStateLoad(1, "legacy row");
                    idx.RegisterAddDuringStateLoad(2, "zebra crossing"); // no stop words: they are never indexed
                });
                CollectionAssert.AreEqual(new[] { 1 }, search(idx, "legacy"));
                inTransaction(engine, 2000, () => idx.Remove(1, "legacy row"));
                Assert.AreEqual(0, search(idx, "legacy").Length, "and deletes work on the rebuilt table");
            }
            // reopening the rebuilt index finds the new layout and leaves it alone
            using (var engine = new SqliteIndexStore(dir)) {
                var idx = openIndex(engine);
                Assert.AreEqual(2000, idx.PersistedTimestamp, "no second rebuild once the layout is current");
                CollectionAssert.AreEqual(new[] { 2 }, search(idx, "zebra"));
            }
        } finally {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
