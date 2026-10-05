using Relatude.DB.IO;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.Server;

/// <summary>
/// The datamodel overrides an installation makes are kept in overrides/datamodel.overrides.json on the
/// database's storage, with the other changes made in the admin UI; the ones every installation shares in
/// relatude.settings/[short name]/datamodel.overrides.json. Older versions kept the first in datamodels/;
/// such a file is moved the first time the location is asked for, and an older copy beside a newer file
/// is left alone.
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
    static string legacyFile(string root) => Path.Combine(root, "db1", "datamodels", "datamodel.overrides.json");
    static string currentFile(string root) => Path.Combine(root, "db1", "overrides", "datamodel.overrides.json");

    [TestMethod]
    public void TheKeyIsInTheOverridesFolder() {
        Assert.AreEqual("overrides/datamodel.overrides.json", FileKeyUtility.Datamodel_OverridesFileKey.AsKeyString());
        Assert.AreEqual("datamodels/datamodel.overrides.json", FileKeyUtility.Datamodel_LegacyOverridesFileKey.AsKeyString());
        Assert.AreEqual("Datamodel overrides", FileKeyUtility.FileTypeDescription("overrides/datamodel.overrides.json"));
    }

    [TestMethod]
    public async Task AnOldOverridesFileIsMovedIntoTheOverridesFolder() {
        var root = newRoot("move");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile(root))!);
        File.WriteAllText(legacyFile(root), overridesJson);
        var host = TestServerHost.Start(root, settings: diskSettings());
        try {
            var container = host.Server.Containers.Values.Single();
            var file = container.OverridesFile;
            Assert.AreEqual("overrides/datamodel.overrides.json", file.IoKey!.AsKeyString());
            Assert.IsTrue(File.Exists(currentFile(root)));
            Assert.IsFalse(File.Exists(legacyFile(root)));
            Assert.AreEqual(overridesJson, File.ReadAllText(currentFile(root)), "moved as it is");
            Assert.IsTrue(file.Exists(host.Server));
            Assert.IsNotNull(file.Read(host.Server));
            Assert.IsTrue(host.Server.GetStartUpLog().Any(l => l.Item2.Contains("Moved the datamodel overrides")));
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task AnOlderCopyBesideTheNewFileIsLeftAloneAndNotRead() {
        var root = newRoot("both");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile(root))!);
        Directory.CreateDirectory(Path.GetDirectoryName(currentFile(root))!);
        File.WriteAllText(legacyFile(root), "{ this would not parse }");
        File.WriteAllText(currentFile(root), overridesJson);
        var host = TestServerHost.Start(root, settings: diskSettings());
        try {
            var file = host.Server.Containers.Values.Single().OverridesFile;
            Assert.IsNotNull(file.Read(host.Server), "the new file is the one read");
            Assert.IsTrue(File.Exists(legacyFile(root)), "never overwritten or deleted");
            Assert.IsTrue(host.Server.GetStartUpLog().Any(l => l.Item2.Contains("older copy")));
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
            Assert.AreEqual("relatude.settings/local/datamodel.overrides.json", container.OverridesFile.SharedLocation);
            Assert.IsTrue(host.Server.GetStartUpLog().Any(l => l.Item2.Contains("only this installation's")), "said at start");
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
