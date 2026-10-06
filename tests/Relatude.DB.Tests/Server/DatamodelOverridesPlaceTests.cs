using Relatude.DB.IO;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.Server;

/// <summary>
/// The datamodel overrides an installation makes are kept in DATA, overrides/datamodel.overrides.json on the
/// database's storage, with the other changes made in the admin UI; the ones every installation has in
/// SETTINGS, relatude.settings/[short name]/datamodel.json.
/// </summary>
[TestClass]
public class DatamodelOverridesPlaceTests {
    const string overridesJson = "{ \"NodeTypes\": { \"11111111-0000-0000-0000-000000000001\": { \"Hidden\": true } } }";

    static string newRoot(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-datamodel-overrides-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    /// <summary>One closed database keeping its files on local disk, in db1 below the root.</summary>
    static RelatudeDBServerSettings diskSettings() {
        var template = RelatudeDBServerSettings.CreateDefault().ContainerSettings![0];
        var container = TestServerHost.MemoryContainer(template, "Database");
        container.AutoOpen = false;
        container.WaitUntilOpen = false;
        container.IOSettings![0].IOType = IOTypes.LocalDisk;
        container.IOSettings[0].Path = "db1";
        return new RelatudeDBServerSettings { Id = Guid.NewGuid(), Name = "Overrides place", ContainerSettings = [container], DefaultStoreId = container.Id };
    }
    static string currentFile(string root) => Path.Combine(root, "db1", "overrides", "datamodel.overrides.json");

    [TestMethod]
    public void TheKeyIsInTheOverridesFolder() {
        Assert.AreEqual("overrides/datamodel.overrides.json", FileKeyUtility.Datamodel_OverridesFileKey.AsKeyString());
        Assert.AreEqual("Datamodel overrides", FileKeyUtility.FileTypeDescription("overrides/datamodel.overrides.json"));
    }

    [TestMethod]
    public async Task TheDataFileIsInTheOverridesFolderOfTheDatabasesStorage() {
        var root = newRoot("data");
        Directory.CreateDirectory(Path.GetDirectoryName(currentFile(root))!);
        File.WriteAllText(currentFile(root), overridesJson);
        var host = TestServerHost.Start(root, settings: diskSettings());
        try {
            var file = host.Server.Containers.Values.Single().OverridesFile;
            Assert.AreEqual("db1/overrides/datamodel.overrides.json", file.DataLocation, "relative to the application's folder, for people");
            Assert.IsTrue(file.Exists(host.Server));
            Assert.IsNotNull(file.Read(host.Server));
            Assert.AreEqual("relatude.settings/datamodel.json", file.SettingsLocation);
            Assert.IsFalse(file.SettingsExists);
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task AShortNameSavedForThisInstallationAlone_IsWarnedAbout() {
        // the shared files are found by the short name, so one only this installation has reads other ones
        var root = newRoot("short");
        var settings = diskSettings();
        var id = settings.ContainerSettings![0].Id;
        var overrides = Path.Combine(root, SettingsOverridesFile.FallbackRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(overrides)!);
        File.WriteAllText(overrides, "{ \"ContainerSettings\": [ { \"Id\": \"" + id + "\", \"ShortName\": \"local\" } ] }");
        var host = TestServerHost.Start(root, settings: settings, overridesFile: true);
        try {
            var container = host.Server.Containers.Values.Single();
            Assert.AreEqual("local", container.Settings.ShortName, "the overrides file is in force");
            Assert.AreEqual("relatude.settings/local/datamodel.json", container.OverridesFile.SettingsLocation);
            Assert.IsTrue(host.Server.GetStartUpLog().Any(l => l.Item2.Contains("only this installation's")), "said at start");
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
