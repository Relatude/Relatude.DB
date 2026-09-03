using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.Demo.Models;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.Nodes;

namespace Relatude.Server;

/// <summary>
/// The group-by view of the admin query page, driven through the command endpoint the browser uses:
/// one row per group, sorted by a measure, paged, and a row's tokens turned back into a facet selection.
/// </summary>
[TestClass]
public class UIQueryGroupByTests {
    static readonly string[] _titles = ["Alpha", "Beta", "Gamma"];

    static (TestServerHost host, Guid storeId, List<DemoArticle> articles) start(string root) {
        var host = TestServerHost.Start(root);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        var storeId = host.Settings.Settings.ContainerSettings![0].Id;
        var store = host.Server.Containers[storeId].Store!;
        var articles = new List<DemoArticle>();
        for (var i = 1; i <= 30; i++) articles.Add(new DemoArticle { Id = Guid.NewGuid(), Title = _titles[i % 3], Content = "c" + i, Size = i });
        store.Insert(articles);
        return (host, storeId, articles);
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
    static JsonElement typeOf(JsonElement model, string name) => prop(model, "types").EnumerateArray().First(t => prop(t, "name").GetString() == name);

    [TestMethod]
    public async Task GroupBy_RowsSortedPagedAndDrilledInto() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-groupby-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (host, storeId, articles) = start(root);
        try {
            var model = await command(host, "query-model", new { storeId });
            var typeId = prop(typeOf(model, nameof(DemoArticle)), "id").GetGuid();
            var pivotModel = await command(host, "query-pivot-model", new { storeId, typeId });
            var properties = prop(pivotModel, "properties").EnumerateArray().ToDictionary(p => prop(p, "name").GetString()!, p => prop(p, "id").GetGuid());

            var grouped = await command(host, "query-groupby", new {
                storeId, typeId,
                text = "", semanticRatio = (double?)null, minimumSimilarity = (double?)null, selections = Array.Empty<object>(),
                keys = new[] { new { propertyId = properties["Title"], mode = "values" } },
                measures = new object[] {
                    new { function = "Sum", propertyId = (Guid?)properties["Size"] },
                    new { function = "Count", propertyId = (Guid?)null },   // the count is always a column: ignored as a measure
                    new { function = "Sum", propertyId = (Guid?)properties["Size"] }, // a repeat is one measure
                },
                includeMissing = true, sortBy = "Size.Sum", descending = true, page = 0, pageSize = 200,
            });
            Assert.AreEqual(articles.Count, prop(grouped, "sourceCount").GetInt32());
            Assert.AreEqual(_titles.Length, prop(grouped, "totalRows").GetInt32());
            CollectionAssert.AreEqual(new[] { "Size.Sum" }, prop(grouped, "measures").EnumerateArray().Select(m => prop(m, "name").GetString()).ToArray());
            Assert.AreEqual("Title", prop(prop(grouped, "keys")[0], "codeName").GetString());
            var rows = prop(grouped, "rows").EnumerateArray().ToArray();
            var expectedOrder = articles.GroupBy(a => a.Title).OrderByDescending(g => g.Sum(a => a.Size)).Select(g => g.Key).ToArray();
            CollectionAssert.AreEqual(expectedOrder, rows.Select(r => prop(r, "labels")[0].GetString()).ToArray(), "rows sorted by the sum, largest first");
            foreach (var row in rows) {
                var title = prop(row, "labels")[0].GetString();
                var inGroup = articles.Where(a => a.Title == title).ToList();
                Assert.AreEqual(inGroup.Count, prop(row, "count").GetInt32());
                Assert.AreEqual(inGroup.Sum(a => a.Size), prop(row, "measures")[0].GetDouble());
                Assert.IsFalse(prop(row, "isMissing").GetBoolean());
            }
            var query = prop(grouped, "query").GetString()!;
            StringAssert.Contains(query, ".GroupBy(\"");
            StringAssert.Contains(query, ".AddSum(\"");
            StringAssert.Contains(query, ".SetRowOptions(", "one key sorted by a measure: the engine sorts");
            StringAssert.Contains(query, ".SetRowPaging(0, 200)");

            // a page of one, ascending by count
            var paged = await command(host, "query-groupby", new {
                storeId, typeId, text = "", selections = Array.Empty<object>(),
                keys = new[] { new { propertyId = properties["Title"], mode = "values" } },
                measures = Array.Empty<object>(), includeMissing = true, sortBy = "Count", descending = false, page = 1, pageSize = 1,
            });
            Assert.AreEqual(_titles.Length, prop(paged, "totalRows").GetInt32());
            Assert.AreEqual(1, prop(paged, "rows").GetArrayLength());
            Assert.AreEqual(1, prop(paged, "page").GetInt32());
            var ascending = articles.GroupBy(a => a.Title).OrderBy(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToArray();
            Assert.AreEqual(ascending[1], prop(prop(paged, "rows")[0], "labels")[0].GetString());

            // ranges on a number: a label and both bounds per row
            var ranged = await command(host, "query-groupby", new {
                storeId, typeId, text = "", selections = Array.Empty<object>(),
                keys = new[] { new { propertyId = properties["Size"], mode = "ranges" } },
                measures = Array.Empty<object>(), includeMissing = false, sortBy = (string?)null, descending = true, page = 0, pageSize = 200,
            });
            var rangeRows = prop(ranged, "rows").EnumerateArray().ToArray();
            Assert.IsTrue(rangeRows.Length >= 2, "30 sizes: several ranges");
            Assert.IsTrue(prop(prop(ranged, "keys")[0], "isRange").GetBoolean());
            Assert.IsNotNull(prop(rangeRows[0], "values2")[0].GetString(), "a range row carries its upper bound");
            Assert.AreEqual(articles.Count, rangeRows.Sum(r => prop(r, "count").GetInt32()));

            // drill-down: a row's tokens, posted back as a facet selection, give that group's nodes
            var first = rows[0];
            var search = await command(host, "query-search", new {
                storeId, typeId, text = "", page = 0, pageSize = 25, table = false, facets = false,
                selections = new[] { new { propertyId = properties["Title"], values = new[] { new { value = prop(first, "values")[0].GetString(), value2 = (string?)null } } } },
            });
            Assert.AreEqual(prop(first, "count").GetInt32(), prop(search, "total").GetInt32());

            // no keys yet: an empty answer, not an error
            var empty = await command(host, "query-groupby", new { storeId, typeId, text = "", selections = Array.Empty<object>(), keys = Array.Empty<object>(), measures = Array.Empty<object>() });
            Assert.AreEqual(0, prop(empty, "totalRows").GetInt32());
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
