using Microsoft.AspNetCore.Http;
using Relatude.DB;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.NodeServer.Settings;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Relatude.Server;

/// <summary>
/// The definitions of a database's custom logs are kept with the application's settings:
/// relatude.settings/logs/ for the database without a short name, relatude.settings/{short name}/logs/
/// for the others. Short names are unique, only one database can be without one, and a new short name
/// takes the folder along.
/// </summary>
[TestClass]
public class LogDefinitionsFolderTests {
    static string newRoot(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-log-definitions-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    static void deleteRoot(string root) {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
    static LogSettings requests() {
        var s = new LogSettings { Key = "requests", Name = "Requests", FileInterval = FileInterval.Day, FirstDayOfWeek = DayOfWeek.Monday };
        s.Properties.Add("path", new LogProperty { Name = "Path", DataType = LogDataType.String });
        return s;
    }
    /// <summary>Where the definition of the "requests" log is, for a database with this short name (null for none).</summary>
    static string definitionFile(string root, string? shortName)
        => shortName == null ? Path.Combine(root, "relatude.settings", "logs", "requests.json") : Path.Combine(root, "relatude.settings", shortName, "logs", "requests.json");

    static TestServerHost startWithAdmin(string root, int databases, Action<RelatudeDBServerSettings>? configure = null) {
        var host = TestServerHost.Start(root, databases: databases, configure: configure);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }
    static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        var json = JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default);
        Assert.AreEqual(200, status, "command " + type + " failed: " + json);
        return json;
    }
    static Task<JsonElement> saveShortName(TestServerHost host, Guid storeId, string? shortName)
        => command(host, "settings-db-save", new { storeId, values = new Dictionary<string, object?> { ["ShortName"] = shortName }, reopen = true });
    static int rejected(JsonElement saved) => saved.GetProperty("rejected").GetArrayLength();
    static string reason(JsonElement saved) => saved.GetProperty("rejected")[0].GetProperty("reason").GetString()!;

    [TestMethod]
    public void ShortNamesAreFolderNamesAndEmptyMeansNone() {
        Assert.IsNull(DatabaseShortName.Problem(null));
        Assert.IsNull(DatabaseShortName.Problem(""));
        Assert.IsNull(DatabaseShortName.Problem("reports_2026-a"));
        Assert.IsNotNull(DatabaseShortName.Problem("my db"));
        Assert.IsNotNull(DatabaseShortName.Problem("a/b"));
        Assert.IsNotNull(DatabaseShortName.Problem("-x"));
        Assert.IsNotNull(DatabaseShortName.Problem(new string('x', 65)));
        Assert.IsNotNull(DatabaseShortName.Problem("Con"), "a device name on Windows");
        Assert.IsNotNull(DatabaseShortName.Problem("Logs"), "the folder of the database without a short name");
        Assert.IsNull(DatabaseShortName.Of(new NodeStoreContainerSettings()));
        Assert.IsNull(DatabaseShortName.Of(new NodeStoreContainerSettings { ShortName = "  " }));
        Assert.AreEqual("Reports", DatabaseShortName.Of(new NodeStoreContainerSettings { ShortName = " Reports " }));
        Assert.IsNull(DatabaseShortName.Of(new NodeStoreContainerSettings { ShortName = "no/way" }), "one that cannot name a folder is not used");
        Assert.IsTrue(DatabaseShortName.Same(null, null));
        Assert.IsTrue(DatabaseShortName.Same("a", "A"));
        Assert.IsFalse(DatabaseShortName.Same(null, "a"));
        Assert.AreEqual("relatude.settings/logs", DatabaseShortName.LogDefinitionsFolder(null));
        Assert.AreEqual("relatude.settings/main/logs", DatabaseShortName.LogDefinitionsFolder("main"));
    }

    [TestMethod]
    public async Task ADatabaseWithoutAShortNameKeepsItsLogDefinitionsInRelatudeSettingsLogs() {
        var root = newRoot("none");
        var host = TestServerHost.Start(root);
        try {
            var custom = host.Server.Containers.Values.Single().GetLogger().CustomLogs;
            custom.Create(requests());
            Assert.IsTrue(File.Exists(definitionFile(root, null)));
            Assert.AreEqual("relatude.settings/logs", custom.DefinitionsFolder);
            Assert.AreEqual("relatude.settings/logs/requests.json", custom.DefinitionFileOf("requests"));
        } finally {
            await host.DisposeAsync();
            deleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AShortNameTakesTheFolderAlongAndBack() {
        var root = newRoot("rename");
        var host = startWithAdmin(root, 1);
        try {
            var container = host.Server.Containers.Values.Single();
            container.GetLogger().CustomLogs.Create(requests());
            var saved = await saveShortName(host, container.Settings.Id, "main");
            Assert.AreEqual(0, rejected(saved), saved.ToString());
            Assert.IsTrue(saved.GetProperty("reopened").GetBoolean());
            var custom = container.GetLogger().CustomLogs;
            Assert.IsTrue(custom.HasLog("requests"), "the log is found under the new short name");
            Assert.IsTrue(File.Exists(definitionFile(root, "main")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "relatude.settings", "logs")), "the old folder is not left behind empty");
            Assert.AreEqual("relatude.settings/main/logs", custom.DefinitionsFolder);

            Assert.AreEqual(0, rejected(await saveShortName(host, container.Settings.Id, "")));
            Assert.IsTrue(container.GetLogger().CustomLogs.HasLog("requests"));
            Assert.IsTrue(File.Exists(definitionFile(root, null)));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "relatude.settings", "main")), "relatude.settings/main had nothing else in it");
        } finally {
            await host.DisposeAsync();
            deleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ShortNamesAreUniqueAndOnlyOneDatabaseIsWithoutOne() {
        var root = newRoot("unique");
        var host = startWithAdmin(root, 2);
        try {
            var first = host.Server.GetContainers().Single(c => c.Settings.Id == host.Server.Settings.DefaultStoreId);
            var second = host.Server.GetContainers().Single(c => c.Settings.Id != host.Server.Settings.DefaultStoreId);
            Assert.IsTrue(host.Server.GetStartUpLog().Any(l => l.Item2.Contains("have no short name")), "both without one is warned about at start");
            var emptyRefused = await saveShortName(host, second.Settings.Id, "");
            Assert.AreEqual(1, rejected(emptyRefused));
            StringAssert.Contains(reason(emptyRefused), "only one database can be without one");
            Assert.AreEqual(1, rejected(await saveShortName(host, second.Settings.Id, "bad name")));
            Assert.AreEqual(1, rejected(await saveShortName(host, second.Settings.Id, "logs")));
            Assert.AreEqual(0, rejected(await saveShortName(host, second.Settings.Id, "reports")));
            Assert.AreEqual("relatude.settings/reports/logs", second.GetLogger().CustomLogs.DefinitionsFolder);
            Assert.AreEqual("relatude.settings/logs", first.GetLogger().CustomLogs.DefinitionsFolder);
            var sameRefused = await saveShortName(host, first.Settings.Id, "REPORTS");
            Assert.AreEqual(1, rejected(sameRefused), "names are compared without case");
            StringAssert.Contains(reason(sameRefused), "already has the short name");
        } finally {
            await host.DisposeAsync();
            deleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ADatabaseCreatedInTheAdminUIGetsAShortNameWhenAnotherHasNone() {
        var root = newRoot("create");
        var host = startWithAdmin(root, 1);
        try {
            var created = await command(host, "database-create", new { name = "Reports 2026", autoOpen = false });
            var settings = host.Server.Containers[created.GetProperty("storeId").GetGuid()].Settings;
            Assert.AreEqual("reports-2026", settings.ShortName, "the first database has none, so the new one is named after itself");
            // with every database named, the next one can do without
            host.Server.GetContainers().Single(c => c.Settings.Id == host.Server.Settings.DefaultStoreId).Settings.ShortName = "main";
            var another = await command(host, "database-create", new { name = "Archive", autoOpen = false });
            Assert.IsNull(host.Server.Containers[another.GetProperty("storeId").GetGuid()].Settings.ShortName);
        } finally {
            await host.DisposeAsync();
            deleteRoot(root);
        }
    }

    [TestMethod]
    public async Task DefinitionsInTheEarlierLayoutAreMoved() {
        var root = newRoot("earlier");
        // relatude.settings/logs/db/ was where a database without a short name kept them for a while
        var earlier = Path.Combine(root, "relatude.settings", "logs", "db");
        Directory.CreateDirectory(earlier);
        File.WriteAllText(Path.Combine(earlier, "requests.json"), requests().ToJson());
        var host = TestServerHost.Start(root);
        try {
            var custom = host.Server.Containers.Values.Single().GetLogger().CustomLogs;
            Assert.IsTrue(custom.HasLog("requests"));
            Assert.IsTrue(File.Exists(definitionFile(root, null)));
            Assert.IsFalse(Directory.Exists(earlier));
            Assert.IsTrue(host.Server.GetStartUpLog().Any(l => l.Item2.Contains("Moved the log definitions")));
        } finally {
            await host.DisposeAsync();
            deleteRoot(root);
        }
    }
}
