using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Tests;

/// <summary>
/// byte[] as an index value type in the KV engines: codec round-trip through the disk-backed
/// B+Tree (including reopen from disk, embedded 0x00 escaping and empty arrays), and content
/// (not reference) equality in the dictionary-based engines.
/// </summary>
[TestClass]
public class KvByteArrayIndexTests {

    static readonly byte[][] _values = [
        [1, 2, 3],
        [0, 0xFF, 0, 0],            // embedded zeros: exercises the codec's escape path
        [],                         // empty array
        [0xFF, 0xFF],
        [0],
    ];

    static void addAll(IStorageEngine engine, ISortedIndex<byte[]> index) {
        engine.BeginTransaction();
        for (var id = 0; id < _values.Length; id++) index.Set(id, _values[id]);
        engine.CommitTransaction(1, true);
    }
    static void verifyAll(ISortedIndex<byte[]> index) {
        Assert.AreEqual(_values.Length, index.Count);
        for (var id = 0; id < _values.Length; id++) {
            CollectionAssert.AreEqual(_values[id], index.GetValue(id), $"id {id} did not round-trip");
            // lookups must match by content, not by reference
            CollectionAssert.Contains(index.GetIds((byte[])_values[id].Clone()).ToList(), id);
        }
        foreach (var entry in index.Entries) CollectionAssert.AreEqual(_values[entry.Key], entry.Value);
    }

    [TestMethod]
    public void BPlusTree_ByteArray_RoundTripsThroughDiskAndReopen() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "bytes.db");
        try {
            using (var engine = new BPlusTreeStorageEngine(filePath, new BPlusTreeEngineOptions())) {
                var index = engine.OpenOrCreateIndex<byte[]>("bytes");
                addAll(engine, index);
                verifyAll(index);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath, new BPlusTreeEngineOptions())) {
                verifyAll(engine.OpenOrCreateIndex<byte[]>("bytes")); // decode path: read back from disk
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void HashDictionary_ByteArray_MatchesByContent() {
        using var engine = new HashDictionaryStorageEngine();
        var index = engine.OpenOrCreateIndex<byte[]>("bytes");
        addAll(engine, index);
        verifyAll(index);
    }

    [TestMethod]
    public void AppendLog_ByteArray_MatchesByContent() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            using var engine = new AppendLogStorageEngine(Path.Combine(dir, "bytes.log"));
            var index = engine.OpenOrCreateIndex<byte[]>("bytes");
            addAll(engine, index);
            verifyAll(index);
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
