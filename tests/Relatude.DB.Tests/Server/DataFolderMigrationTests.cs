using Relatude.DB;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.Server;

/// <summary>
/// The data folder used to be called relatude.db and is relatude.data now: at start the server renames an
/// old one and points the settings files at it, as text, so their comments stay.
/// </summary>
[TestClass]
public class DataFolderMigrationTests {
    string _root = "";
    readonly List<string> _info = [];
    readonly List<string> _warnings = [];
    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "relatude-data-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "relatude.settings"));
    }
    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_root, true); } catch { }
    }
    string settingsFile => Path.Combine(_root, "relatude.settings", "relatude.db.json");
    string old => Path.Combine(_root, "relatude.db");
    string current => Path.Combine(_root, "relatude.data");
    void run() => DataFolderMigration.Run(_root, settingsFile, _info.Add, _warnings.Add);
    const string settingsText = """
        // the application's settings
        {
          "DBAdminUIUrlPath": "/relatude.db",
          "ContainerSettings": [
            { "IOSettings": [ { "Path": "relatude.db" }, { "Path": "relatude.db/files" }, { "Path": "relatude.db2" } ],
              "DatamodelSources": [ { "Filepath": "relatude.db\\modelsources\\shop" } ] }
          ]
        }
        """;

    [TestMethod]
    public void TheOldFolderIsRenamed_AndTheSettingsPointAtTheNewOne() {
        File.WriteAllText(settingsFile, settingsText);
        Directory.CreateDirectory(Path.Combine(old, "data"));
        File.WriteAllText(Path.Combine(old, "data", "db.0.bin"), "log");
        Directory.CreateDirectory(Path.Combine(old, "overrides"));
        File.WriteAllText(Path.Combine(old, "overrides", "relatude.db.overrides.json"), "{ \"ContainerSettings\": [ { \"IOSettings\": [ { \"Path\": \"relatude.db/second\" } ] } ] }");
        run();
        Assert.IsFalse(Directory.Exists(old));
        Assert.AreEqual("log", File.ReadAllText(Path.Combine(current, "data", "db.0.bin")));
        var text = File.ReadAllText(settingsFile);
        StringAssert.StartsWith(text, "// the application's settings", "comments are kept");
        StringAssert.Contains(text, "\"Path\": \"relatude.data\"");
        StringAssert.Contains(text, "\"Path\": \"relatude.data/files\"");
        StringAssert.Contains(text, "\"Filepath\": \"relatude.data\\\\modelsources\\\\shop\"");
        StringAssert.Contains(text, "\"Path\": \"relatude.db2\"", "another folder is left alone");
        StringAssert.Contains(text, "\"/relatude.db\"", "the admin UI's url is not a folder");
        StringAssert.Contains(File.ReadAllText(Path.Combine(current, "overrides", "relatude.db.overrides.json")), "\"relatude.data/second\"", "the overrides file inside the folder follows");
        Assert.AreEqual(0, _warnings.Count, string.Join(" | ", _warnings));
        Assert.IsTrue(_info.Any(i => i.Contains("Renamed the data folder")));
    }

    [TestMethod]
    public void NothingMovesWhenBothAreThere() {
        File.WriteAllText(settingsFile, settingsText);
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(current);
        run();
        Assert.IsTrue(Directory.Exists(old));
        Assert.AreEqual(settingsText, File.ReadAllText(settingsFile), "the settings decide which is used");
        Assert.AreEqual(1, _warnings.Count);
    }

    [TestMethod]
    public void SettingsNamingTheOldFolderAreChanged_EvenBeforeThereIsData() {
        File.WriteAllText(settingsFile, settingsText);
        run();
        StringAssert.Contains(File.ReadAllText(settingsFile), "\"Path\": \"relatude.data\"");
        Assert.IsFalse(Directory.Exists(current), "nothing to rename");
    }

    [TestMethod]
    public void AnOldFolderTheSettingsDoNotUseIsLeftAlone() {
        File.WriteAllText(settingsFile, """{ "ContainerSettings": [ { "IOSettings": [ { "Path": "elsewhere" } ] } ] }""");
        Directory.CreateDirectory(old);
        run();
        Assert.IsTrue(Directory.Exists(old));
        Assert.IsFalse(Directory.Exists(current));
    }

    [TestMethod]
    public void OnlyPathsNamingTheFolderAreReplaced() {
        var replaced = DataFolderMigration.Replace("""["relatude.db", "relatude.db/x", "relatude.db.json", "relatude.db.temp", "/relatude.db", "relatude.db2", "x/relatude.db"]""");
        Assert.AreEqual("""["relatude.data", "relatude.data/x", "relatude.db.json", "relatude.db.temp", "/relatude.db", "relatude.db2", "x/relatude.db"]""", replaced);
        Assert.AreEqual("relatude.data", Defaults.DataFolderPath);
        Assert.AreEqual("relatude.data.temp", Defaults.TempFolderPath);
    }

    [TestMethod]
    public void TheOldScratchFolderIsDeleted() {
        Directory.CreateDirectory(Path.Combine(_root, "relatude.db.temp", "x"));
        DataFolderMigration.RemoveLegacyTempFolder(_root, _info.Add);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "relatude.db.temp")));
    }

    [TestMethod]
    public async Task TheServerRenamesTheOldFolderAtStart() {
        Directory.CreateDirectory(Path.Combine(old, "installation"));
        File.WriteAllText(Path.Combine(old, "installation", "kept.txt"), "kept");
        var host = TestServerHost.Start(_root);
        try {
            Assert.IsTrue(File.Exists(Path.Combine(current, "installation", "kept.txt")));
            Assert.IsFalse(Directory.Exists(old));
        } finally {
            await host.DisposeAsync();
        }
    }
}
