using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Tracer;
using Relatude.DB.Datamodels;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;

namespace Relatude.Server;

/// <summary>
/// What the dashboard shows while a database opens, and after it stopped. The first part of an open
/// happens before the container has a store to hand out - the model is loaded, the mappers built -
/// and that part used to read as "Closed", with a Start button, and with no trace at all. These hold
/// the container to saying "Opening" from the first step, naming the step, tracing the store it is
/// building, and keeping what the last store said once it is gone.
/// </summary>
[TestClass]
public class UIDashboardOpeningTests {

    static TestServerHost start(string root) {
        var host = TestServerHost.Start(root);
        // the admin API (and with it the UI command endpoint) is mapped by the app's own
        // UseRelatudeDB, which goes through the static runtime; the test host maps it directly
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }

    // posts a command the way the browser does and reads the json it would have received
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
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }
    static string[] lines(JsonElement trace) => [.. prop(trace, "entries").EnumerateArray().Select(e => prop(e, "text").GetString()!)];

    static string newRoot(string name) {
        var root = Path.Combine(Path.GetTempPath(), "relatude-dash-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [TestMethod]
    public async Task Opening_IsReportedFromTheFirstStep_BeforeTheStoreExists() {
        var root = newRoot("opening");
        var host = start(root);
        using var inModel = new ManualResetEventSlim();
        using var inStore = new ManualResetEventSlim();
        using var releaseModel = new ManualResetEventSlim();
        using var releaseStore = new ManualResetEventSlim();
        try {
            var storeId = host.Settings.Settings.ContainerSettings![0].Id;
            var container = host.Server.Containers[storeId];
            container.CloseIfOpen();
            // both hooks run inside the open: the first before any store exists, the second once the
            // store is built but before it has read anything
            host.Server.Options!.OnDatamodelInit = (_, _) => { inModel.Set(); releaseModel.Wait(TimeSpan.FromSeconds(30)); };
            host.Server.Options!.OnStoreInit = _ => { inStore.Set(); releaseStore.Wait(TimeSpan.FromSeconds(30)); };
            var opening = Task.Run(container.Open);

            Assert.IsTrue(inModel.Wait(TimeSpan.FromSeconds(30)), "the open never reached the model");
            var live = await command(host, "dashboard-live", new { storeId });
            Assert.AreEqual("Opening", prop(live, "state").GetString(), "no store yet, and still not closed");
            var progress = prop(live, "opening");
            Assert.AreEqual("Loading the datamodel", prop(progress, "step").GetString());
            Assert.IsNotNull(prop(progress, "sinceUtc").GetString(), "the page counts the clock from this");
            Assert.AreEqual(0, prop(progress, "progressPercentage").GetInt32(), "nothing to measure before the store reads its state");
            var dashboard = await command(host, "dashboard", new { storeId });
            Assert.AreEqual("Opening", prop(dashboard, "state").GetString());
            var info = await command(host, "server-info", new { });
            Assert.AreEqual("Opening", prop(prop(info, "containers").EnumerateArray().Single(), "state").GetString(), "the switcher and the rail say so too");
            var trace = await command(host, "logs-trace", new { storeId, take = 200 });
            Assert.IsTrue(prop(trace, "open").GetBoolean(), "an opening database is traced live");
            Assert.AreEqual(0, lines(trace).Length, "what the last store said belongs to the last store");

            releaseModel.Set();
            Assert.IsTrue(inStore.Wait(TimeSpan.FromSeconds(30)), "the open never reached the store");
            live = await command(host, "dashboard-live", new { storeId });
            Assert.AreEqual("Opening", prop(live, "state").GetString(), "the store exists, and says Closed; the open is still running");
            Assert.AreEqual("Preparing the object mappers", prop(prop(live, "opening"), "step").GetString());
            trace = await command(host, "logs-trace", new { storeId, take = 200 });
            CollectionAssert.Contains(lines(trace), "Database intialized", "the store being built is traced before it is handed out");

            releaseStore.Set();
            await opening;
            live = await command(host, "dashboard-live", new { storeId });
            Assert.AreEqual("Open", prop(live, "state").GetString());
            Assert.IsTrue(prop(live, "open").GetBoolean());
        } finally {
            releaseModel.Set();
            releaseStore.Set();
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Trace_IsKeptAfterClose_AndAFailedOpenSaysWhy() {
        var root = newRoot("kept");
        var host = start(root);
        try {
            var storeId = host.Settings.Settings.ContainerSettings![0].Id;
            var container = host.Server.Containers[storeId];

            await command(host, "store-close", new { storeId });
            var trace = await command(host, "logs-trace", new { storeId, take = 200 });
            Assert.IsFalse(prop(trace, "open").GetBoolean());
            Assert.IsNotNull(prop(trace, "keptUtc").GetString(), "a closed database says when its lines were kept");
            Assert.IsTrue(lines(trace).Any(l => l.StartsWith("NodeStore ready")), "what it said while it was open is still there");

            // a model source that cannot be loaded fails the open before any store exists
            var sources = container.Settings.DatamodelSources;
            container.Settings.DatamodelSources = [new DatamodelSource {
                Id = Guid.NewGuid(),
                Name = "Missing",
                Type = DatamodelSourceType.CompiledTypes,
                Reference = "No.Such.Assembly." + Guid.NewGuid().ToString("N"),
            }];
            Assert.ThrowsExactly<Exception>(container.Open);
            var live = await command(host, "dashboard-live", new { storeId });
            Assert.AreEqual("Error", prop(live, "state").GetString());
            trace = await command(host, "logs-trace", new { storeId, take = 200 });
            var first = prop(trace, "entries").EnumerateArray().First();
            Assert.AreEqual("Error", prop(first, "type").GetString());
            StringAssert.StartsWith(prop(first, "text").GetString(), "The database could not be opened: ", "the reason is the newest line");
            Assert.IsFalse(lines(trace).Any(l => l.StartsWith("NodeStore ready")), "the lines of the store before it are not this open's");

            // it opens once the source is back, and nothing of the failure sticks to it
            container.Settings.DatamodelSources = sources;
            container.Open();
            live = await command(host, "dashboard-live", new { storeId });
            Assert.AreEqual("Open", prop(live, "state").GetString(), "a database that opened is not in error, whatever happened before");
            trace = await command(host, "logs-trace", new { storeId, take = 200 });
            Assert.IsTrue(prop(trace, "open").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, prop(trace, "keptUtc").ValueKind, "live lines are not kept ones");
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void Tracer_AProgressLineReplacesOnlyTheProgressLineBeforeIt() {
        var tracer = new SimpleSystemLogTracer();
        string[] texts() => [.. tracer.GetEntries(0, 100).Select(e => e.Text)];

        tracer.Trace(SystemLogEntryType.Info, "Reading log file");
        tracer.Trace(SystemLogEntryType.Info, "10%", replace: true);
        CollectionAssert.AreEqual(new[] { "10%", "Reading log file" }, texts(), "the first progress line takes nothing's place");

        tracer.Trace(SystemLogEntryType.Info, "20%", replace: true);
        tracer.Trace(SystemLogEntryType.Info, "30%", replace: true);
        CollectionAssert.AreEqual(new[] { "30%", "Reading log file" }, texts(), "one line that moves");

        tracer.Trace(SystemLogEntryType.Warning, "Something else");
        tracer.Trace(SystemLogEntryType.Info, "40%", replace: true);
        CollectionAssert.AreEqual(new[] { "40%", "Something else", "30%", "Reading log file" }, texts(), "a line in between is never replaced");

        tracer.Trace(SystemLogEntryType.Info, "Read 1,000 actions");
        CollectionAssert.AreEqual(new[] { "Read 1,000 actions", "40%", "Something else", "30%", "Reading log file" }, texts());
    }
}
