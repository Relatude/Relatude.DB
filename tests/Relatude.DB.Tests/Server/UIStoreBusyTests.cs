using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.Nodes;

namespace Relatude.Server;

/// <summary>
/// The admin UI while a long transaction runs. The transaction holds the store's write lock until it is
/// done, and the UI used to count nodes with queries in what every page waits for - the app's first load,
/// the database list broadcast every second, the header's revert status, the type lists of the Query and
/// Data model pages - so the whole UI stood still until the transaction finished. These tests hold the
/// write lock the way a transaction does and check that those answer anyway.
/// </summary>
[TestClass]
public class UIStoreBusyTests {
    static readonly TimeSpan wait = TimeSpan.FromSeconds(10);

    static TestServerHost start(string root) {
        var host = TestServerHost.Start(root);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }
    static async Task<(int Status, JsonElement Json)> command(TestServerHost host, string type, object? payload, string? runId = null) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload, runId }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default));
    }
    // the command on a thread of its own, answered within the time a page would wait for it
    static (int Status, JsonElement Json) answered(TestServerHost host, string type, object? payload) {
        var call = Task.Run(() => command(host, type, payload));
        Assert.IsTrue(call.Wait(wait), "\"" + type + "\" waited for the transaction");
        Assert.AreEqual(200, call.Result.Status, call.Result.Json.ToString());
        return call.Result;
    }
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }
    static Action holdWriteLock(NodeStore store) {
        var rw = (ReaderWriterLockSlim)typeof(DataStoreLocal).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store.Datastore)!;
        var held = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var thread = new Thread(() => {
            rw.EnterWriteLock();
            held.Set();
            release.Wait();
            rw.ExitWriteLock();
        }) { IsBackground = true };
        thread.Start();
        Assert.IsTrue(held.Wait(wait));
        return () => {
            release.Set();
            thread.Join();
        };
    }

    [TestMethod]
    public async Task ThePagesAnswerWhileATransactionHoldsTheStore() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-ui-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = start(root);
        try {
            var storeId = host.Settings.Settings.ContainerSettings![0].Id;
            var store = host.Server.Containers[storeId].Store!;
            var release = holdWriteLock(store);
            try {
                // the app's first load, and the server overview: node counts the store keeps, no query
                var info = answered(host, "server-info", null);
                var container = prop(info.Json, "containers").EnumerateArray().Single(c => prop(c, "id").GetGuid() == storeId);
                Assert.AreEqual(JsonValueKind.Number, prop(container, "nodeCount").ValueKind, container.ToString());
                answered(host, "server-overview", null);
                // the header's revert control, on every page
                answered(host, "revert-status", new { storeId });
                // and what the other pages follow live
                foreach (var feed in new[] { "dashboard", "dashboard-live", "conversions", "memory", "scheduler" }) answered(host, feed, new { storeId });
                answered(host, "databases", null);
                answered(host, "shared-tasks", null);

                // the Query page's type list: the counts are unknown rather than waited for
                var model = answered(host, "query-model", new { storeId });
                var types = prop(model.Json, "types").EnumerateArray().ToArray();
                Assert.IsTrue(types.Length > 0);
                Assert.IsTrue(types.All(t => prop(t, "count").ValueKind == JsonValueKind.Null), model.Json.ToString());

                // the data model page says its counts are incomplete, and so does asking again
                var page = answered(host, "datamodel", new { storeId });
                Assert.IsFalse(prop(page.Json, "typeCountsComplete").GetBoolean());
                Assert.IsFalse(prop(answered(host, "datamodel-type-counts", new { storeId }).Json, "typeCountsComplete").GetBoolean());

                // a search waits for the lock - and says so to the page - until it is stopped
                var runId = Guid.NewGuid().ToString("N");
                var search = Task.Run(() => command(host, "query-search", new { storeId, typeId = (Guid?)null, text = (string?)null, page = 0, pageSize = 10 }, runId));
                Assert.IsFalse(search.Wait(300), "the search ran while the write lock was held");
                var waiting = answered(host, "query-wait", new { storeId });
                Assert.IsTrue(prop(waiting.Json, "readersWaiting").GetInt32() >= 1, waiting.Json.ToString());
                Assert.AreEqual(200, (await command(host, "command-cancel", new { runId })).Status);
                Assert.IsTrue(search.Wait(wait), "Stop did not reach a search waiting for the lock");
                Assert.AreEqual(StatusCodes.Status499ClientClosedRequest, search.Result.Status, search.Result.Json.ToString());
            } finally {
                release();
            }

            // with the store free again every count is there
            var after = answered(host, "query-model", new { storeId });
            Assert.IsTrue(prop(after.Json, "types").EnumerateArray().All(t => prop(t, "count").ValueKind == JsonValueKind.Number));
            Assert.IsTrue(prop(answered(host, "datamodel-type-counts", new { storeId }).Json, "typeCountsComplete").GetBoolean());
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
