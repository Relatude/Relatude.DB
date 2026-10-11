using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;

namespace Relatude.Server;

/// <summary>
/// Stopping a query from the admin UI's Query page: a command sent with a run id gets a cancellation
/// token, `command-cancel` with the same id cancels it - even when the cancel overtakes the command - and
/// the query module's commands hand that token to the store through the query context.
/// </summary>
[TestClass]
public class UIQueryCancelTests {

    static TestServerHost start(string root) {
        var host = TestServerHost.Start(root);
        // the admin API (and with it the UI command endpoint) is mapped by the app's own UseRelatudeDB;
        // the test host maps it directly
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }
    // posts a command the way the browser does, with the run id the browser makes when it can stop it
    static async Task<(int Status, JsonElement Json)> command(TestServerHost host, string type, object payload, string? runId = null) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload, runId }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default));
    }
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }

    [TestMethod]
    public async Task AStoppedSearchAnswersAsStopped_AndTheSameSearchUnstoppedAnswers() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-ui-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = start(root);
        try {
            var storeId = host.Settings.Settings.ContainerSettings![0].Id;
            var search = new { storeId, typeId = (Guid?)null, text = (string?)null, page = 0, pageSize = 10 };

            // the cancel reaches the server before the command it stops: the command starts out cancelled
            var runId = Guid.NewGuid().ToString("N");
            var cancel = await command(host, "command-cancel", new { runId });
            Assert.AreEqual(200, cancel.Status);
            Assert.IsFalse(cancel.Json.GetBoolean(), "nothing was running under the id yet");
            var stopped = await command(host, "query-search", search, runId);
            Assert.AreEqual(StatusCodes.Status499ClientClosedRequest, stopped.Status, stopped.Json.ToString());
            Assert.IsTrue(prop(stopped.Json, "cancelled").GetBoolean());

            // the id is used up: the same search under a new id, or none, answers
            var answered = await command(host, "query-search", search, Guid.NewGuid().ToString("N"));
            Assert.AreEqual(200, answered.Status, answered.Json.ToString());
            var plain = await command(host, "query-search", search);
            Assert.AreEqual(200, plain.Status, plain.Json.ToString());

            // a cancel for an id that never comes is harmless
            Assert.AreEqual(200, (await command(host, "command-cancel", new { runId = "never-sent" })).Status);
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
