using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;

namespace Relatude.Server;

/// <summary>
/// The database's own logs in the Logs section: described by <c>custom-logs-info</c> only when the
/// page asks for them, and read through the same commands as a log defined there - entries,
/// graphs, distributions - by their keys, while the switches that belong to the Activity page stay
/// out of reach.
/// </summary>
[TestClass]
public class UICustomLogsBuiltInTests {

    [TestMethod]
    public async Task TheBuiltInLogsAreDescribedOnlyWhenAskedForAndReadByTheirKeys() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-builtin-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = start(root);
        try {
            var storeId = host.Server.Settings.DefaultStoreId;
            var logger = host.Server.Containers[storeId].GetLogger();
            logger.EnableLog("system", true);
            logger.EnableStatistics("system", true);
            logger.RecordSystem(SystemLogEntryType.Info, "seen from the logs section");
            logger.FlushToDiskNow();

            var plain = await command(host, "custom-logs-info", new { storeId });
            Assert.AreEqual(0, prop(plain, "builtIn").GetArrayLength(), "the database's own logs cost a look each, so they come only when asked for");

            var info = await command(host, "custom-logs-info", new { storeId, includeBuiltIn = true });
            var builtIn = prop(info, "builtIn").EnumerateArray().ToArray();
            var system = builtIn.Single(l => prop(l, "key").GetString() == "system");
            Assert.IsTrue(prop(system, "enabledLog").GetBoolean(), "the live switch, as the Activity page set it");
            Assert.IsTrue(prop(system, "enabledStatistics").GetBoolean());
            Assert.IsTrue(builtIn.Any(l => prop(l, "key").GetString() == "query"), "every system log is described, recording or not");
            Assert.AreEqual(0, prop(info, "logs").GetArrayLength(), "and none of them is counted among the logs defined in the section");

            var page = await command(host, "custom-logs-extract", new { storeId, logKey = "system", lastMs = 3_600_000, take = 100 });
            StringAssert.Contains(page.ToString(), "seen from the logs section", "the entries of a system log are read by its key");

            var series = await command(host, "custom-logs-series", new { storeId, logKey = "system", property = (string?)null, statistic = "Count", interval = "Minute", lastMs = 3_600_000 });
            Assert.AreEqual(JsonValueKind.Object, series.ValueKind);

            var failed = await commandStatus(host, "custom-logs-rebuild-statistics", new { storeId, logKey = "query" });
            Assert.AreNotEqual(200, failed, "rebuilding asks the log's own statistics switch, which is off for the query log");
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static TestServerHost start(string root) {
        var host = TestServerHost.Start(root);
        // the admin API (and with it the UI command endpoint) is mapped by the app's own
        // UseRelatudeDB, which goes through the static runtime; the test host maps it directly
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }

    static async Task<(int Status, JsonElement Json)> post(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default));
    }
    static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
        var (status, json) = await post(host, type, payload);
        Assert.AreEqual(200, status, "command " + type + " failed: " + json);
        return json;
    }
    static async Task<int> commandStatus(TestServerHost host, string type, object payload) => (await post(host, type, payload)).Status;
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }
}
