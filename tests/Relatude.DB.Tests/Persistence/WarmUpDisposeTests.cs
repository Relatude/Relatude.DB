using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.Persistence;

[Node]
public class WuProduct {
    [InternalIdProperty] public int Id { get; set; }
    [StringProperty(Indexed = true)] public string Category { get; set; } = "";
    [IntegerProperty(Indexed = true)] public int Rating { get; set; }
    [DoubleProperty(Indexed = true)] public double Price { get; set; }
    [BooleanProperty(Indexed = true)] public bool Active { get; set; }
    [StringArrayProperty(Indexed = true)] public string[] Tags { get; set; } = [];
}

// The warm-up started after open (index mirrors, facet caches, then a save of the facet-set sidecar
// nativekv/facetsets.bin) runs in the background and takes the store lock, which Dispose does not.
// Dispose and Close must end it before returning: left running, it read engines being disposed and
// wrote the sidecar after the store was gone, so deleting the folder right after the dispose failed
// with the file in use (seen once in IndexAheadOfLogTests).
[TestClass]
public class WarmUpDisposeTests {

    const int NodeCount = 2000;
    const int Rounds = 30;
    static readonly string[] _cats = ["Toys", "Games", "Tools", "Food", "Books", "Garden"];
    static readonly string[] _tags = ["new", "sale", "eco", "premium", "limited"];

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static DataStoreLocal open(string dir) {
        var dm = new Datamodel();
        dm.Add<WuProduct>();
        var settings = new SettingsLocal {
            ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId,
        };
        return DataStoreLocal.Open(dm, settings, new IOProviderDisk(dir), null, null, null, null,
            () => IndexEngines.Single(TestEngines.ValueId, new NativeKvIndexStore(dir)));
    }
    // a store with data in every facet property, and no sidecar: each open of a copy then builds the
    // facet sets in its warm-up and saves them, racing the dispose that follows
    static string createTemplate() {
        var dir = tempDir();
        using (var store = new NodeStore(open(dir))) {
            store.Insert(Enumerable.Range(0, NodeCount).Select(i => new WuProduct {
                Category = _cats[i % _cats.Length], Rating = i % 5, Price = 10 + i % 40, Active = i % 3 == 0,
                Tags = [_tags[i % _tags.Length], _tags[(i / 2) % _tags.Length]],
            }).ToList());
        }
        foreach (var sidecar in Directory.GetFiles(dir, FileKeyUtility.IndexEngine_FacetSetsFileKey, SearchOption.AllDirectories)) File.Delete(sidecar);
        return dir;
    }
    static string copyOf(string template) {
        var dir = tempDir();
        foreach (var d in Directory.GetDirectories(template, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dir, Path.GetRelativePath(template, d)));
        foreach (var f in Directory.GetFiles(template, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(dir, Path.GetRelativePath(template, f)));
        return dir;
    }

    [TestMethod]
    public void DisposeRightAfterOpen_EndsWarmUp_FolderDeletes() {
        var template = createTemplate();
        try {
            for (var round = 0; round < Rounds; round++) {
                var dir = copyOf(template);
                var db = open(dir);
                if (round % 3 != 0) Thread.Sleep(round % 7); // vary where in the warm-up the dispose lands
                db.Dispose();
                Assert.IsFalse(db.IsWarmingIndexes, "warm-up still running after Dispose, round " + round);
                Directory.Delete(dir, true); // throws while the warm-up holds facetsets.bin open
            }
        } finally {
            try { Directory.Delete(template, true); } catch { }
        }
    }

    [TestMethod]
    public void CloseRightAfterOpen_EndsWarmUp() {
        var template = createTemplate();
        try {
            for (var round = 0; round < Rounds / 3; round++) {
                var dir = copyOf(template);
                var db = open(dir);
                if (round % 2 != 0) Thread.Sleep(round % 5);
                db.Close();
                Assert.IsFalse(db.IsWarmingIndexes, "warm-up still running after Close, round " + round);
                db.Dispose();
                Directory.Delete(dir, true);
            }
        } finally {
            try { Directory.Delete(template, true); } catch { }
        }
    }
}
