using Relatude.DB;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;
using System.Reflection;
using System.Text.Json;

namespace Relatude.Server;

/// <summary>
/// Where relatude.db.json is kept: relatude.settings/ below the root data folder. A file an older
/// version kept in the root data folder itself, or in relatude.settings/db/, is moved there when the
/// server starts - as it is, comments and all - and read where it is when the move fails.
/// </summary>
[TestClass]
public class SettingsFileLocationTests {
    const string legacyText = "{\n  // a comment the move must keep\n  \"Name\": \"Old place\"\n}";

    static string newRoot(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-settings-location-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    static void deleteRoot(string root) {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
    // the two places older versions kept the file in
    static string rootFile(string root) => Path.Combine(root, "relatude.db.json");
    static string dbFolderFile(string root) => Path.Combine(root, "relatude.settings", "db", "relatude.db.json");

    [TestMethod]
    public void Paths_AreBelowTheRootInRelatudeSettings() {
        var root = Path.Combine(Path.GetTempPath(), "app");
        Assert.AreEqual(Path.Combine(root, "relatude.settings", "relatude.db.json"), SettingsFileLocation.FilePath(root));
        CollectionAssert.AreEqual(new[] { dbFolderFile(root), rootFile(root) }, SettingsFileLocation.LegacyFilePaths(root), "the most recent older place first");
        Assert.AreEqual("relatude.settings/relatude.db.json", Defaults.SettingsFilePath);
        Assert.AreEqual("relatude.settings/relatude.db.json", SettingsFileLocation.Display(SettingsFileLocation.FilePath(root), root));
        Assert.IsTrue(SettingsFileLocation.IsLegacyPlace(dbFolderFile(root), root));
        Assert.IsTrue(SettingsFileLocation.IsLegacyPlace(rootFile(root), root));
        Assert.IsFalse(SettingsFileLocation.IsLegacyPlace(SettingsFileLocation.FilePath(root), root));
    }

    [TestMethod]
    public void RootOf_IsAboveTheSettingsFolderAndTheFileFolderOtherwise() {
        var root = Path.Combine(Path.GetTempPath(), "app");
        Assert.AreEqual(root, SettingsFileLocation.RootOf(SettingsFileLocation.FilePath(root)));
        Assert.AreEqual(root, SettingsFileLocation.RootOf(dbFolderFile(root)));
        Assert.AreEqual(root, SettingsFileLocation.RootOf(rootFile(root)));
        Assert.AreEqual(root, SettingsFileLocation.RootOf(Path.Combine(root, "RELATUDE.SETTINGS", "DB", "relatude.db.json")), "folder names are matched without case");
        Assert.AreEqual(Path.Combine(root, "config", "db"), SettingsFileLocation.RootOf(Path.Combine(root, "config", "db", "relatude.db.json")),
            "a db folder that is not in relatude.settings is just the folder the file is in");
    }

    [TestMethod]
    public void Find_MovesTheLegacyFileIntoTheSettingsFolder() {
        var root = newRoot("move");
        try {
            File.WriteAllText(rootFile(root), legacyText);
            var infos = new List<string>();
            var warnings = new List<string>();
            var found = SettingsFileLocation.Find(root, infos.Add, warnings.Add);
            Assert.AreEqual(SettingsFileLocation.FilePath(root), found);
            Assert.IsFalse(File.Exists(rootFile(root)), "nothing is left in the old place");
            Assert.AreEqual(legacyText, File.ReadAllText(found), "moved as it is, comments and all");
            Assert.AreEqual(1, infos.Count);
            StringAssert.Contains(infos[0], "relatude.settings/relatude.db.json");
            Assert.AreEqual(0, warnings.Count);

            // the next start finds it in its place and moves nothing
            infos.Clear();
            Assert.AreEqual(found, SettingsFileLocation.Find(root, infos.Add, warnings.Add));
            Assert.AreEqual(0, infos.Count + warnings.Count);
        } finally {
            deleteRoot(root);
        }
    }

    [TestMethod]
    public void Find_MovesTheFileOutOfRelatudeSettingsDbAndRemovesTheEmptyFolder() {
        var root = newRoot("from-db-folder");
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(dbFolderFile(root))!);
            File.WriteAllText(dbFolderFile(root), legacyText);
            var infos = new List<string>();
            var found = SettingsFileLocation.Find(root, infos.Add, msg => Assert.Fail("no warning expected: " + msg));
            Assert.AreEqual(SettingsFileLocation.FilePath(root), found);
            Assert.AreEqual(legacyText, File.ReadAllText(found));
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(dbFolderFile(root))), "nothing else was kept in relatude.settings/db");
            StringAssert.Contains(infos.Single(), "relatude.settings/db/relatude.db.json");
        } finally {
            deleteRoot(root);
        }
    }

    [TestMethod]
    public void Find_TakesTheMostRecentOlderPlaceAndWarnsAboutTheOther() {
        var root = newRoot("two-old-places");
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(dbFolderFile(root))!);
            File.WriteAllText(dbFolderFile(root), legacyText);
            File.WriteAllText(rootFile(root), "{ \"Name\": \"Oldest\" }");
            var warnings = new List<string>();
            var found = SettingsFileLocation.Find(root, _ => { }, warnings.Add);
            Assert.AreEqual(legacyText, File.ReadAllText(found), "relatude.settings/db held the one the last version read");
            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains(warnings[0], "relatude.db.json in an older place");
            Assert.IsTrue(File.Exists(rootFile(root)), "the other one is left alone");
        } finally {
            deleteRoot(root);
        }
    }

    [TestMethod]
    public void Find_MovesTheLegacyFileIntoAnEmptySettingsFolder() {
        var root = newRoot("empty-folder");
        try {
            Directory.CreateDirectory(SettingsFileLocation.FolderPath(root));
            File.WriteAllText(rootFile(root), legacyText);
            var found = SettingsFileLocation.Find(root, _ => { }, msg => Assert.Fail("no warning expected: " + msg));
            Assert.AreEqual(SettingsFileLocation.FilePath(root), found);
            Assert.AreEqual(legacyText, File.ReadAllText(found), "a folder without the file would otherwise have the loader write fresh settings over nothing");
            Assert.IsFalse(File.Exists(rootFile(root)));
        } finally {
            deleteRoot(root);
        }
    }

    [TestMethod]
    public void Find_WithBothFilesReadsTheNewOneAndWarnsAboutTheOld() {
        var root = newRoot("both");
        try {
            Directory.CreateDirectory(SettingsFileLocation.FolderPath(root));
            File.WriteAllText(SettingsFileLocation.FilePath(root), "{ \"Name\": \"New place\" }");
            File.WriteAllText(rootFile(root), legacyText);
            var warnings = new List<string>();
            var found = SettingsFileLocation.Find(root, _ => { }, warnings.Add);
            Assert.AreEqual(SettingsFileLocation.FilePath(root), found);
            Assert.AreEqual(1, warnings.Count);
            Assert.AreEqual(legacyText, File.ReadAllText(rootFile(root)), "the old file is left alone, never overwritten or deleted");
            Assert.AreEqual("{ \"Name\": \"New place\" }", File.ReadAllText(found));
        } finally {
            deleteRoot(root);
        }
    }

    [TestMethod]
    public void Find_WithNoFileReturnsTheNewPlaceAndCreatesNothing() {
        var root = newRoot("none");
        try {
            var found = SettingsFileLocation.Find(root, msg => Assert.Fail(msg), msg => Assert.Fail(msg));
            Assert.AreEqual(SettingsFileLocation.FilePath(root), found);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, Defaults.SettingsFolderPath)), "only the loader's first write creates the folder");
        } finally {
            deleteRoot(root);
        }
    }

    [TestMethod]
    public void Find_WhenTheMoveFailsReadsTheLegacyFileWhereItIs() {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("an open file blocks a move on Windows only");
        var root = newRoot("locked");
        try {
            var legacy = rootFile(root);
            File.WriteAllText(legacy, legacyText);
            var warnings = new List<string>();
            string found;
            using (new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read)) { // no FileShare.Delete: a move is refused
                found = SettingsFileLocation.Find(root, _ => { }, warnings.Add);
            }
            Assert.AreEqual(legacy, found, "the start goes on with the file where it is");
            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains(warnings[0], "next start tries again");
            Assert.IsFalse(File.Exists(SettingsFileLocation.FilePath(root)));

            // and the next start does
            Assert.AreEqual(SettingsFileLocation.FilePath(root), SettingsFileLocation.Find(root, _ => { }, warnings.Add));
            Assert.AreEqual(legacyText, File.ReadAllText(SettingsFileLocation.FilePath(root)));
        } finally {
            deleteRoot(root);
        }
    }

    [TestMethod]
    public void Existing_PrefersTheNewPlaceThenTheOldAndMovesNothing() {
        var root = newRoot("existing");
        try {
            Assert.AreEqual(SettingsFileLocation.FilePath(root), SettingsFileLocation.Existing(root), "a file still to be written goes to the new place");
            Assert.IsFalse(SettingsFileLocation.HasSettingsFile(root));
            File.WriteAllText(rootFile(root), legacyText);
            Assert.AreEqual(rootFile(root), SettingsFileLocation.Existing(root));
            Assert.IsTrue(SettingsFileLocation.HasSettingsFile(root));
            Assert.IsTrue(File.Exists(rootFile(root)), "the command line tool only reads");
            Directory.CreateDirectory(SettingsFileLocation.FolderPath(root));
            File.WriteAllText(SettingsFileLocation.FilePath(root), legacyText);
            Assert.AreEqual(SettingsFileLocation.FilePath(root), SettingsFileLocation.Existing(root));
        } finally {
            deleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Loader_WritesItsFirstFileIntoAFolderThatDoesNotExistYet() {
        var root = newRoot("first-write");
        try {
            var loader = new LocalSettingsLoaderFile(SettingsFileLocation.FilePath(root));
            var settings = await loader.ReadAsync();
            Assert.IsTrue(File.Exists(SettingsFileLocation.FilePath(root)), "fresh settings are written into relatude.settings");
            var again = await new LocalSettingsLoaderFile(SettingsFileLocation.FilePath(root)).ReadAsync();
            Assert.AreEqual(settings.Id, again.Id);
        } finally {
            deleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Server_StartedOnTheOldLayoutMovesTheFileAndReadsIt() {
        var root = newRoot("server");
        TestServerHost? host = null;
        try {
            var settings = TestServerHost.MemorySettings(1);
            settings.Name = "Moved at start";
            File.WriteAllText(rootFile(root), "// written by an older version\n" + JsonSerializer.Serialize(settings, LocalSettingsLoaderFile.JsonOptions));
            host = TestServerHost.Start(root, options: o => o.SettingsLoader = null); // the server's own loader
            Assert.AreEqual(settings.Id, host.Server.Settings.Id);
            Assert.AreEqual("Moved at start", host.Server.Settings.Name);
            Assert.IsFalse(File.Exists(rootFile(root)));
            Assert.IsTrue(File.ReadAllText(SettingsFileLocation.FilePath(root)).StartsWith("// written by an older version"), "the file was moved, not rewritten");
            var display = (string)typeof(RelatudeDBServer).GetProperty("SettingsFileDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host.Server)!;
            Assert.AreEqual("relatude.settings/relatude.db.json", display, "what the admin UI names as the settings file");
            Assert.IsTrue(host.Server.GetStartUpLog().Any(l => l.Item2.Contains("Moved relatude.db.json")), "the move is in the start-up log");
        } finally {
            if (host != null) await host.DisposeAsync();
            deleteRoot(root);
        }
    }
}
