using Microsoft.AspNetCore.Http;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.NodeServer.Settings;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Relatude.Server;

/// <summary>
/// Where relatude.db.overrides.json is kept: with the default database, in the overrides folder of its storage, as
/// relatude.db.json and the configuration section describe that database - never as the overrides file
/// itself does, since it cannot decide where it is to be found. Moving a change of the database's folder
/// into relatude.db.json takes the file along; storage that cannot be reached at start does not stop
/// the server, but stops saves.
/// </summary>
[TestClass]
public class SettingsOverridesLocationTests {

    static string newRoot(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-overrides-location-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Server settings whose databases keep their files on local disk, in the given folders.</summary>
    static RelatudeDBServerSettings diskSettings(params string[] folders) {
        var template = RelatudeDBServerSettings.CreateDefault().ContainerSettings![0];
        var containers = folders.Select((folder, i) => {
            var container = TestServerHost.MemoryContainer(template, "Database " + i);
            container.AutoOpen = false;
            container.WaitUntilOpen = false;
            container.IOSettings![0].IOType = IOTypes.LocalDisk;
            container.IOSettings[0].Path = folder;
            return container;
        }).ToArray();
        return new RelatudeDBServerSettings { Id = Guid.NewGuid(), Name = "Location test", ContainerSettings = containers, DefaultStoreId = containers[0].Id };
    }

    static TestServerHost start(string root, RelatudeDBServerSettings settings, Dictionary<string, string?>? configuration = null) {
        var host = TestServerHost.Start(root, settings: settings, overridesFile: true, overridesWithDatabase: true, configuration: configuration);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }

    static async Task<(int Status, JsonElement Body)> send(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default));
    }
    static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
        var (status, json) = await send(host, type, payload);
        Assert.AreEqual(200, status, "command " + type + " failed: " + json);
        return json;
    }

    static string fileIn(string root, string folder) => Path.Combine(root, folder, SettingsOverridesLocation.FolderName, SettingsOverridesFile.FileName);
    // where versions before the overrides folder kept it: at the storage root itself
    static string legacyFileIn(string root, string folder) => Path.Combine(root, folder, SettingsOverridesFile.FileName);

    // ---- finding the place ----

    [TestMethod]
    public void Resolve_FollowsTheDefaultDatabasesFolder() {
        var root = newRoot("resolve");
        var settings = diskSettings("first", "second");
        settings.DefaultStoreId = settings.ContainerSettings![1].Id;
        var location = SettingsOverridesLocation.Resolve(settings, root);
        Assert.AreEqual(Path.GetFullPath(fileIn(root, "second")), location.DiskPath);
        Assert.AreEqual("second/overrides/" + SettingsOverridesFile.FileName, location.Display);
        Assert.AreEqual(settings.ContainerSettings[1].IoDatabase, location.IoId);
        Assert.IsNull(location.Note);
    }

    [TestMethod]
    public void Resolve_WithoutADefault_UsesTheFirstDatabase_AndWithoutDatabasesTheFallback() {
        var root = newRoot("fallback");
        var settings = diskSettings("first", "second");
        settings.DefaultStoreId = Guid.NewGuid();
        var first = SettingsOverridesLocation.Resolve(settings, root);
        Assert.AreEqual(Path.GetFullPath(fileIn(root, "first")), first.DiskPath);
        Assert.IsNotNull(first.Note);
        settings.ContainerSettings = [];
        var none = SettingsOverridesLocation.Resolve(settings, root);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(root, SettingsOverridesFile.FallbackRelativePath)), none.DiskPath);
    }

    [TestMethod]
    public void Resolve_InMemoryStorage_KeepsTheFileInTheProvider() {
        var root = newRoot("memory");
        var location = SettingsOverridesLocation.Resolve(TestServerHost.MemorySettings(1), root);
        Assert.IsNull(location.DiskPath);
        Assert.IsNotNull(location.Io);
        StringAssert.Contains(location.Display, "memory");
    }

    // ---- the server ----

    [TestMethod]
    public async Task Save_WritesIntoTheDefaultDatabasesFolder() {
        var root = newRoot("save");
        var host = start(root, diskSettings("db"));
        try {
            await command(host, "settings-server-save", new { values = new Dictionary<string, object> { ["Description"] = "Changed" } });
            StringAssert.Contains(File.ReadAllText(fileIn(root, "db")), "Changed");
            Assert.IsFalse(File.Exists(Path.Combine(root, SettingsOverridesFile.FallbackRelativePath)), "not where a new installation's database would be");
            Assert.AreEqual(0, host.Settings.Writes.Count);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheFolderIsTheOneConfigurationGives() {
        var root = newRoot("config");
        var host = start(root, diskSettings("db"), new() { ["RelatudeDB:ContainerSettings:0:IOSettings:0:Path"] = "configured" });
        try {
            await command(host, "settings-server-save", new { values = new Dictionary<string, object> { ["Description"] = "Changed" } });
            Assert.IsTrue(File.Exists(fileIn(root, "configured")));
            Assert.IsFalse(File.Exists(fileIn(root, "db")));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task InMemoryStorage_TheDatabaseSharesTheProviderWithTheFile() {
        var root = newRoot("shared");
        var settings = TestServerHost.MemorySettings(1);
        var ioId = settings.ContainerSettings![0].IoDatabase!.Value;
        var host = start(root, settings);
        try {
            await command(host, "settings-server-save", new { values = new Dictionary<string, object> { ["Description"] = "Changed" } });
            Assert.IsTrue(host.Server.GetIO(ioId).Exists(SettingsOverridesLocation.FileKey), "the database's own provider sees the file");
            Assert.AreSame(host.Server.OverridesFile!.Location.Io, host.Server.GetIO(ioId));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MovingTheDatabaseInTheAdminUI_LeavesTheFileWhereRelatudeDbJsonSays_UntilTheMoveIsMovedThere() {
        var root = newRoot("follow");
        var original = diskSettings("db1");
        var storeId = original.ContainerSettings![0].Id;
        var ioId = original.ContainerSettings[0].IOSettings![0].Id;
        var pathKey = "IOSettings[" + ioId + "].Path";
        var host = start(root, TestServerHost.Copy(original));
        try {
            await command(host, "settings-db-save", new { storeId, values = new Dictionary<string, object> { [pathKey] = "db2", ["Description"] = "Described" }, reopen = false });
            Assert.IsTrue(File.Exists(fileIn(root, "db1")), "the database moves at its next open; the file stays where relatude.db.json puts it");
        } finally {
            await host.DisposeAsync();
        }
        // a new start reads relatude.db.json as it was, finds the file in db1, and the database is in db2
        host = start(root, TestServerHost.Copy(original));
        try {
            Assert.AreEqual("db2", host.Server.Containers[storeId].Settings.IOSettings![0].Path);
            var c = "ContainerSettings[" + storeId + "].";
            await command(host, "settings-overrides-move", new { paths = new[] { c + pathKey } });
            Assert.AreEqual("db2", host.Settings.Writes.Single().ContainerSettings![0].IOSettings![0].Path);
            Assert.IsFalse(File.Exists(fileIn(root, "db1")), "the file follows the database once relatude.db.json moves it");
            var moved = File.ReadAllText(fileIn(root, "db2"));
            StringAssert.Contains(moved, "Described");
            Assert.IsFalse(moved.Contains("db2"), moved);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StorageThatCannotBeReached_StartsWithoutTheFile_AndRefusesSaves() {
        var root = newRoot("unreachable");
        var settings = diskSettings("../outside-the-root"); // refused by the disk provider: "Path not under root"
        var host = start(root, settings);
        try {
            Assert.IsNotNull(host.Server.OverridesFile?.Unavailable);
            var page = await command(host, "settings-server-get", new { });
            Assert.AreNotEqual(JsonValueKind.Null, page.GetProperty("overrides").GetProperty("error").ValueKind);
            var (status, _) = await send(host, "settings-server-save", new { values = new Dictionary<string, object> { ["Description"] = "Changed" } });
            Assert.AreNotEqual(200, status, "a save would write over the file that could not be read");
        } finally {
            await host.DisposeAsync();
        }
    }
}
