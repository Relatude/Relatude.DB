using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.Server;

/// <summary>
/// The index engines a database runs are three lists of engine entries plus one default id per kind
/// in <see cref="SettingsLocal"/>; <see cref="Guid.Empty"/> is the memory index. These tests cover
/// the pieces around that: validating the settings, routing by id in <see cref="IndexEngines"/>,
/// the folder each engine gets, the host's factory, and the migration of a settings file from the
/// enum-per-kind shape that came before.
/// </summary>
[TestClass]
public class IndexEngineSettingsTests {

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "relatude.db.tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void EmptySettingsMeanMemoryIndexes() {
        var s = new SettingsLocal();
        s.ValidateIndexEngines();
        Assert.AreEqual(Guid.Empty, s.DefaultValueIndex);
        Assert.IsNull(s.DefaultValueEngine);
        Assert.IsNull(s.DefaultTextEngine);
        Assert.IsNull(s.DefaultVectorEngine);
        // no engine, so the host builds no factory: everything stays in memory
        var log = new List<string>();
        Assert.IsNull(NodeStoreContainer.CreateIndexEngineFactory(s, Path.Combine(tempDir(), "indexes"), false, null, log));
        Assert.IsTrue(log.Any(l => l.Contains("none", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", log));
    }

    [TestMethod]
    public void TheSeededSettingsRunNativeEngines() {
        var s = SettingsLocal.CreateWithNativeEngines();
        s.ValidateIndexEngines();
        Assert.AreEqual(IndexEngineTypes.Native, s.DefaultValueEngine!.TypeName);
        Assert.AreEqual(IndexEngineTypes.Native, s.DefaultTextEngine!.TypeName);
        Assert.IsNull(s.DefaultVectorEngine, "vector indexes need an AI provider, so nothing is seeded for them");
        Assert.AreNotEqual(s.DefaultValueIndex, s.DefaultTextIndex, "an id names a folder, so each engine needs its own");
    }

    [TestMethod]
    public void ValidationNamesTheBrokenSetting() {
        var id = Guid.NewGuid();
        var s = new SettingsLocal { ValueIndexes = [new() { Id = id, TypeName = "Native" }], DefaultValueIndex = Guid.NewGuid() };
        var error = Assert.ThrowsException<Exception>(s.ValidateIndexEngines);
        StringAssert.Contains(error.Message, "DefaultValueIndex");
        StringAssert.Contains(error.Message, id.ToString());

        s = new SettingsLocal { ValueIndexes = [new() { Id = id, TypeName = "Native" }], TextIndexes = [new() { Id = id, TypeName = "Native" }] };
        StringAssert.Contains(Assert.ThrowsException<Exception>(s.ValidateIndexEngines).Message, "more than once");

        s = new SettingsLocal { TextIndexes = [new() { Id = Guid.Empty, TypeName = "Native" }] };
        StringAssert.Contains(Assert.ThrowsException<Exception>(s.ValidateIndexEngines).Message, "without an Id");

        s = new SettingsLocal { VectorIndexes = [new() { Id = id, TypeName = "HNSW", MaxMemoryUsageInMb = -1 }] };
        StringAssert.Contains(Assert.ThrowsException<Exception>(s.ValidateIndexEngines).Message, "MaxMemoryUsageInMb");

        s = new SettingsLocal { VectorIndexes = [new() { Id = id }] };
        StringAssert.Contains(Assert.ThrowsException<Exception>(s.ValidateIndexEngines).Message, "TypeName");

        // an engine nothing points at is fine: it is configuration waiting to be chosen
        new SettingsLocal { ValueIndexes = [new() { Id = id, TypeName = "Native" }] }.ValidateIndexEngines();
        // and a data store refuses broken settings up front, before any index is created
        var broken = new SettingsLocal { DefaultTextIndex = Guid.NewGuid() };
        StringAssert.Contains(Assert.ThrowsException<Exception>(() => DataStoreLocal.Open(new Relatude.DB.Datamodels.Datamodel(), broken)).Message, "DefaultTextIndex");
    }

    [TestMethod]
    public void EnginesAreRoutedById() {
        var valueId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        using var value = new NativeKvIndexStore(null);
        using var other = new NativeKvIndexStore(null);
        using var engines = new IndexEngines([(valueId, value), (otherId, other)]);
        Assert.IsTrue(engines.Any);
        Assert.AreSame(value, engines.ValueEngine(valueId));
        Assert.AreSame(other, engines.ValueEngine(otherId));
        Assert.IsNull(engines.ValueEngine(Guid.Empty), "Guid.Empty is the memory index");
        Assert.IsNull(engines.TextEngine(Guid.Empty));
        StringAssert.Contains(Assert.ThrowsException<Exception>(() => engines.ValueEngine(Guid.NewGuid())).Message, "not created");
        Assert.AreEqual(2, engines.TransactionalEngines.Count());
        Assert.IsFalse(IndexEngines.Empty.Any);
        // registering under the memory id is a programming error, not a way to replace the memory index
        Assert.ThrowsException<ArgumentException>(() => new IndexEngines([(Guid.Empty, value)]));
        Assert.ThrowsException<ArgumentException>(() => new IndexEngines([(valueId, value), (valueId, other)]));
    }

    [TestMethod]
    public void EachEngineGetsItsOwnFolderAndLegacyFoldersAreDeleted() {
        var dir = tempDir();
        try {
            var indexPath = Path.Combine(dir, "indexes");
            // what an installation from before the change has: one folder per engine type
            foreach (var legacy in new[] { "nativekv", "textindex", "vectorindex-hnsw" }) {
                Directory.CreateDirectory(Path.Combine(indexPath, legacy));
                File.WriteAllText(Path.Combine(indexPath, legacy, "data.bin"), "old");
            }
            var stranger = Path.Combine(indexPath, "not-an-engine-folder");
            Directory.CreateDirectory(stranger);

            var settings = new SettingsLocal {
                ValueIndexes = [new() { Id = Guid.NewGuid(), TypeName = IndexEngineTypes.Native, MaxMemoryUsageInMb = 8 }, new() { Id = Guid.NewGuid(), TypeName = IndexEngineTypes.Native, MaxMemoryUsageInMb = 512 }],
                TextIndexes = [new() { Id = Guid.NewGuid(), TypeName = IndexEngineTypes.Native, MaxMemoryUsageInMb = 8 }],
            };
            settings.DefaultValueIndex = settings.ValueIndexes[1].Id; // the big one; the small one is configuration only
            settings.DefaultTextIndex = settings.TextIndexes[0].Id;
            var log = new List<string>();
            var factory = NodeStoreContainer.CreateIndexEngineFactory(settings, indexPath, false, null, log);
            Assert.IsNotNull(factory);
            foreach (var legacy in new[] { "nativekv", "textindex", "vectorindex-hnsw" }) {
                Assert.IsFalse(Directory.Exists(Path.Combine(indexPath, legacy)), legacy + " should have been deleted");
            }
            Assert.IsTrue(Directory.Exists(stranger), "only the known legacy folders are touched");
            Assert.AreEqual(3, log.Count(l => l.Contains("Deleted the index engine folder")), string.Join(" | ", log));

            using (var engines = factory!()) {
                var value = engines.ValueEngine(settings.DefaultValueIndex)!;
                var text = engines.TextEngine(settings.DefaultTextIndex)!;
                Assert.IsNotNull(value);
                Assert.IsNotNull(text);
                // the engine nothing points at was not created, and asking for it says so
                StringAssert.Contains(Assert.ThrowsException<Exception>(() => engines.ValueEngine(settings.ValueIndexes[0].Id)).Message, "not created");
            }
            var valueFolder = NodeStoreContainer.EngineFolderPath(indexPath, settings.DefaultValueEngine!);
            var textFolder = NodeStoreContainer.EngineFolderPath(indexPath, settings.DefaultTextEngine!);
            Assert.AreEqual(Path.Combine(indexPath, settings.DefaultValueIndex.ToString("N")), valueFolder);
            Assert.IsTrue(Directory.Exists(Path.Combine(valueFolder, "nativekv")), "the engine keeps its own subfolder inside its folder");
            Assert.IsTrue(Directory.Exists(Path.Combine(textFolder, "textindex")));
            Assert.IsFalse(Directory.Exists(Path.Combine(indexPath, settings.ValueIndexes[0].Id.ToString("N"))), "an engine that is not created claims no folder");
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public void SqliteServesValueAndTextFromOneInstanceWhenBothDefaultToIt() {
        var dir = tempDir();
        try {
            var settings = new SettingsLocal {
                ValueIndexes = [new() { Id = Guid.NewGuid(), TypeName = IndexEngineTypes.Sqlite, MaxMemoryUsageInMb = 16 }],
                TextIndexes = [new() { Id = Guid.NewGuid(), TypeName = IndexEngineTypes.Sqlite, MaxMemoryUsageInMb = 32 }],
            };
            settings.DefaultValueIndex = settings.ValueIndexes[0].Id;
            settings.DefaultTextIndex = settings.TextIndexes[0].Id;
            var log = new List<string>();
            using var engines = NodeStoreContainer.CreateIndexEngineFactory(settings, Path.Combine(dir, "indexes"), false, null, log)!();
            Assert.AreSame(engines.ValueEngine(settings.DefaultValueIndex), engines.TextEngine(settings.DefaultTextIndex), "one database, one connection, one transaction");
            Assert.AreEqual(1, engines.TransactionalEngines.Count(), "the shared instance is driven once");
            Assert.IsTrue(log.Any(l => l.Contains("share one SQLite database")), "the budget that is not used should be mentioned: " + string.Join(" | ", log));
            // the text engine's folder is not claimed: the data lives with the value engine
            Assert.IsFalse(Directory.Exists(NodeStoreContainer.EngineFolderPath(Path.Combine(dir, "indexes"), settings.DefaultTextEngine!)));
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A settings file written before the engine lists existed chose one engine per kind with an enum
    /// and a "persisted by default" flag, and the vector engine on the AI settings. The loader turns
    /// that into the lists on the way in - once, since the old keys are dropped - so an installation
    /// keeps the engines it had rather than silently falling back to memory indexes.
    /// </summary>
    [TestMethod]
    public void LegacyEngineSettingsAreMigratedIntoTheLists() {
        var legacy = """
            {
              "Name": "old",
              "ContainerSettings": [
                {
                  "Id": "5b2b1f5e-0d1c-4a8e-9a6b-2f3c4d5e6f70",
                  "Name": "db",
                  "AISettings": { "TypeName": "Dummy", "IndexType": "HNSW", "IndexCacheSizeInMb": 512 },
                  "LocalSettings": {
                    "NodeCacheSizeGb": 2,
                    "UsePersistedValueIndexesByDefault": true,
                    "PersistedValueIndexEngine": "Sqlite",
                    "UsePersistedTextIndexesByDefault": false,
                    "PersistedTextIndexEngine": "Lucene",
                    "EnableTextIndexByDefault": true
                  }
                },
                {
                  "Id": "6c3c2a6f-1e2d-4b9f-8b7c-3a4b5c6d7e81",
                  "Name": "memory-only",
                  "LocalSettings": { "PersistedValueIndexEngine": "Memory", "PersistedTextIndexEngine": "Memory" }
                },
                {
                  "Id": "7d4d3b70-2f3e-4ca0-9c8d-4b5c6d7e8f92",
                  "Name": "already-new",
                  "LocalSettings": { "ValueIndexes": null, "DefaultValueIndex": "00000000-0000-0000-0000-000000000000" }
                }
              ]
            }
            """;
        var root = (JsonObject)JsonNode.Parse(legacy, new JsonNodeOptions { PropertyNameCaseInsensitive = true })!;
        Assert.IsTrue(LocalSettingsLoaderFile.MigrateLegacyIndexEngineSettings(root));
        var settings = JsonSerializer.Deserialize<RelatudeDBServerSettings>(root.ToJsonString(), LocalSettingsLoaderFile.JsonOptions)!;

        var db = settings.ContainerSettings![0].LocalSettings!;
        Assert.AreEqual(2, db.NodeCacheSizeGb, "unrelated settings are untouched");
        Assert.IsTrue(db.EnableTextIndexByDefault);
        Assert.AreEqual(IndexEngineTypes.Sqlite, db.DefaultValueEngine!.TypeName, "a persisted Sqlite value engine becomes an entry the default points at");
        Assert.AreEqual(1, db.ValueIndexes!.Length);
        Assert.IsNull(db.DefaultTextEngine, "text was not persisted by default, so it stays in memory even though an engine was named");
        Assert.IsNull(db.TextIndexes);
        Assert.AreEqual(IndexEngineTypes.HNSW, db.DefaultVectorEngine!.TypeName, "the AI settings' index type becomes the vector engine");
        Assert.AreEqual(512, db.DefaultVectorEngine.MaxMemoryUsageInMb, "the vector cache size becomes its memory budget");
        db.ValidateIndexEngines();

        var memoryOnly = settings.ContainerSettings[1].LocalSettings!;
        Assert.AreEqual(Guid.Empty, memoryOnly.DefaultValueIndex);
        Assert.AreEqual(Guid.Empty, memoryOnly.DefaultTextIndex);
        Assert.IsNull(memoryOnly.ValueIndexes);

        var alreadyNew = settings.ContainerSettings[2].LocalSettings!;
        Assert.AreEqual(Guid.Empty, alreadyNew.DefaultValueIndex);

        // the old keys are gone, so the next read has nothing to migrate
        var json = root.ToJsonString();
        foreach (var key in new[] { "UsePersistedValueIndexesByDefault", "PersistedValueIndexEngine", "UsePersistedTextIndexesByDefault", "PersistedTextIndexEngine", "IndexType", "IndexCacheSizeInMb" }) {
            Assert.IsFalse(json.Contains("\"" + key + "\""), key + " should have been removed");
        }
        Assert.IsFalse(LocalSettingsLoaderFile.MigrateLegacyIndexEngineSettings(root), "a migrated file is not migrated again");

        // a file on the new shape is left alone
        var fresh = (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(RelatudeDBServerSettings.CreateDefault(), LocalSettingsLoaderFile.JsonOptions))!;
        Assert.IsFalse(LocalSettingsLoaderFile.MigrateLegacyIndexEngineSettings(fresh));
    }
}
