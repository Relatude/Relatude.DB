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
/// The pivot half of the admin query page, driven through the command endpoint the browser uses:
/// the builder's model, the pivot itself, and a cell's groups turned back into a facet selection.
/// </summary>
[TestClass]
public class UIQueryPivotTests {
    static readonly string[] _titles = ["Alpha", "Beta", "Gamma"];

    static (TestServerHost host, Guid storeId, List<DemoArticle> articles) start(string root) {
        var host = TestServerHost.Start(root);
        // the admin API (and with it the UI command endpoint) is mapped by the app's own UseRelatudeDB,
        // which goes through the static runtime; the test host maps it directly instead
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        var storeId = host.Settings.Settings.ContainerSettings![0].Id;
        var store = host.Server.Containers[storeId].Store!;
        var articles = new List<DemoArticle>();
        for (var i = 1; i <= 30; i++) articles.Add(new DemoArticle { Id = Guid.NewGuid(), Title = _titles[i % 3], Content = "c" + i, Size = i });
        store.Insert(articles);
        return (host, storeId, articles);
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
    static JsonElement typeOf(JsonElement model, string name) => prop(model, "types").EnumerateArray().First(t => prop(t, "name").GetString() == name);

    [TestMethod]
    public async Task PivotModel_ListsWhatEachPropertyCanDo() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-pivot-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (host, storeId, _) = start(root);
        try {
            var model = await command(host, "query-model", new { storeId });
            var typeId = prop(typeOf(model, nameof(DemoArticle)), "id").GetGuid();
            var pivotModel = await command(host, "query-pivot-model", new { storeId, typeId });
            Assert.AreEqual(nameof(DemoArticle), prop(pivotModel, "typeName").GetString());
            var properties = prop(pivotModel, "properties").EnumerateArray().ToDictionary(p => prop(p, "name").GetString()!, p => p);
            Assert.IsTrue(properties.ContainsKey("Title"), "indexed string: groupable");
            Assert.IsTrue(prop(properties["Title"], "groupable").GetBoolean());
            Assert.IsFalse(prop(properties["Title"], "numeric").GetBoolean());
            Assert.IsTrue(prop(properties["Size"], "groupable").GetBoolean());
            Assert.IsTrue(prop(properties["Size"], "numeric").GetBoolean());
            Assert.IsTrue(prop(properties["Size"], "aggregatable").GetBoolean());
            Assert.IsFalse(properties.ContainsKey("Content"), "not indexed: neither groupable nor aggregatable, so not offered");
            // the base type ("all node types") has no groupable properties of its own
            var baseModel = await command(host, "query-pivot-model", new { storeId, typeId = (Guid?)null });
            Assert.AreEqual(0, prop(baseModel, "properties").GetArrayLength());
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Pivot_GroupsMeasuresAndDrillDown() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-pivot-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (host, storeId, articles) = start(root);
        try {
            var model = await command(host, "query-model", new { storeId });
            var typeId = prop(typeOf(model, nameof(DemoArticle)), "id").GetGuid();
            var pivotModel = await command(host, "query-pivot-model", new { storeId, typeId });
            var properties = prop(pivotModel, "properties").EnumerateArray().ToDictionary(p => prop(p, "name").GetString()!, p => prop(p, "id").GetGuid());

            var pivot = await command(host, "query-pivot", new {
                storeId, typeId,
                text = "", semanticRatio = (double?)null, minimumSimilarity = (double?)null, selections = Array.Empty<object>(),
                rows = new[] { new { propertyId = properties["Title"], mode = "values" } },
                columns = new[] { new { propertyId = properties["Size"], mode = "ranges" } },
                measures = new object[] {
                    new { function = "Count", propertyId = (Guid?)null },
                    new { function = "Sum", propertyId = (Guid?)properties["Size"] },
                    new { function = "Count", propertyId = (Guid?)null }, // a second count: named apart, not an error
                },
                rowOptions = new { maxGroups = 0, sortByMeasure = "Size.Sum", descending = true, otherGroup = false, includeMissing = false },
                columnOptions = new { maxGroups = 0, sortByMeasure = (string?)null, descending = true, otherGroup = false, includeMissing = false },
                subTotals = false, rowPage = 0, rowPageSize = 200,
            });
            Assert.AreEqual(articles.Count, prop(pivot, "sourceCount").GetInt32());
            CollectionAssert.AreEqual(new[] { "Count", "Size.Sum", "Count (2)" }, prop(pivot, "measures").EnumerateArray().Select(m => prop(m, "name").GetString()).ToArray());
            var rowGroups = prop(prop(pivot, "rows"), "groups").EnumerateArray().ToArray();
            var expectedOrder = articles.GroupBy(a => a.Title).OrderByDescending(g => g.Sum(a => a.Size)).Select(g => g.Key).ToArray();
            CollectionAssert.AreEqual(expectedOrder, rowGroups.Select(g => prop(g, "labels")[0].GetString()).ToArray(), "rows sorted by the sum measure");
            var rowTotals = prop(pivot, "rowTotals").EnumerateArray().ToArray();
            for (var r = 0; r < rowGroups.Length; r++) {
                var title = prop(rowGroups[r], "labels")[0].GetString();
                var inGroup = articles.Where(a => a.Title == title).ToList();
                Assert.AreEqual(inGroup.Count, prop(rowGroups[r], "count").GetInt32());
                var values = prop(rowTotals[r], "values").EnumerateArray().ToArray();
                Assert.AreEqual(inGroup.Count, values[0].GetDouble());
                Assert.AreEqual(inGroup.Sum(a => a.Size), values[1].GetDouble());
            }
            Assert.IsTrue(prop(prop(pivot, "columns"), "levels")[0].GetProperty("isRange").GetBoolean());
            Assert.IsTrue(prop(prop(pivot, "columns"), "groups").GetArrayLength() >= 2, "Size has 30 distinct values: several range buckets");
            Assert.AreEqual(articles.Sum(a => a.Size), prop(prop(pivot, "grandTotal"), "values")[1].GetDouble());
            Assert.IsTrue(prop(pivot, "query").GetString()!.Contains(".Pivot()"));
            Assert.IsFalse(prop(pivot, "capped").GetBoolean());

            // drill-down: the tokens of a row group, posted back as a facet selection, give that group's nodes
            var first = rowGroups[0];
            var token = prop(first, "values")[0].GetString();
            var search = await command(host, "query-search", new {
                storeId, typeId, text = "", page = 0, pageSize = 25, table = false, facets = false,
                selections = new[] { new { propertyId = properties["Title"], values = new[] { new { value = token, value2 = (string?)null } } } },
            });
            Assert.AreEqual(prop(first, "count").GetInt32(), prop(search, "total").GetInt32());

            // and a pivot on top of that same selection summarizes only those nodes
            var narrowed = await command(host, "query-pivot", new {
                storeId, typeId, text = "",
                selections = new[] { new { propertyId = properties["Title"], values = new[] { new { value = token, value2 = (string?)null } } } },
                rows = new[] { new { propertyId = properties["Size"], mode = "values" } },
                columns = Array.Empty<object>(),
                measures = new[] { new { function = "Count", propertyId = (Guid?)null } },
                subTotals = false, rowPage = 0, rowPageSize = 200,
            });
            Assert.AreEqual(prop(first, "count").GetInt32(), prop(narrowed, "sourceCount").GetInt32());
            Assert.AreEqual(1, prop(prop(narrowed, "columns"), "groups").GetArrayLength(), "no column grouping: one (all) column");
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
