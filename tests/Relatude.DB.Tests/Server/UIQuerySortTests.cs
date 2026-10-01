using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.Demo.Models;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;

namespace Relatude.Server;

/// <summary>
/// Sorting the admin query page by the node's own dates, driven through the command endpoint the
/// browser uses: the table's Created and Changed columns sort, and so does the list, on any type.
/// </summary>
[TestClass]
public class UIQuerySortTests {

    static (TestServerHost host, Guid storeId, List<Guid> inserted) start(string root) {
        var host = TestServerHost.Start(root);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        var storeId = host.Settings.Settings.ContainerSettings![0].Id;
        var store = host.Server.Containers[storeId].Store!;
        var inserted = new List<Guid>();
        for (var i = 1; i <= 12; i++) { // one by one with the clock moving, so creation order is insertion order
            var article = new DemoArticle { Id = Guid.NewGuid(), Title = "a" + i, Content = "c" + i, Size = i };
            store.Insert(article);
            inserted.Add(article.Id);
            Thread.Sleep(2);
        }
        return (host, storeId, inserted);
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
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }
    static Task<JsonElement> search(TestServerHost host, Guid storeId, Guid? typeId, bool table, string sortBy, bool descending) => command(host, "query-search", new {
        storeId, typeId, text = "", selections = Array.Empty<object>(), expanded = Array.Empty<Guid>(),
        page = 0, pageSize = 100, table, facets = false, sortBy, sortDescending = descending,
    });
    static List<Guid> hitIds(JsonElement result, List<Guid> keep) {
        var set = keep.ToHashSet();
        return [.. prop(result, "hits").EnumerateArray().Select(h => prop(h, "id").GetGuid()).Where(set.Contains)];
    }

    [TestMethod]
    public async Task Search_SortsByTheNodesOwnDates() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-sort-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (host, storeId, inserted) = start(root);
        try {
            var model = await command(host, "query-model", new { storeId });
            var typeId = prop(prop(model, "types").EnumerateArray().First(t => prop(t, "name").GetString() == nameof(DemoArticle)), "id").GetGuid();

            // the table: both date columns sort, newest first by change
            var table = await search(host, storeId, typeId, true, "__changed", true);
            var columns = prop(table, "columns").EnumerateArray().ToDictionary(c => prop(c, "key").GetString()!, c => prop(c, "sortable").GetBoolean());
            Assert.IsTrue(columns["__created"] && columns["__changed"], "the node's own dates are sortable columns");
            Assert.IsFalse(columns["__name"], "the other node fields are not");
            Assert.IsTrue(prop(table, "sortApplied").GetBoolean());
            StringAssert.Contains(prop(table, "query").GetString(), ".OrderBy(n => n._changedUtc, true)");
            CollectionAssert.AreEqual(Enumerable.Reverse(inserted).ToList(), hitIds(table, inserted));

            // the list on the base type: every type, oldest first by creation
            var list = await search(host, storeId, null, false, "__created", false);
            Assert.IsTrue(prop(list, "sortApplied").GetBoolean());
            StringAssert.Contains(prop(list, "query").GetString(), ".OrderBy(n => n._createdUtc)");
            CollectionAssert.AreEqual(inserted, hitIds(list, inserted));
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
