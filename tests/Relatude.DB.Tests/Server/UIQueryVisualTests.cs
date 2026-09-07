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
/// The visual pivot's half of the admin query page, driven through the command endpoint the browser
/// uses: every node of a result as a card, with the group each card falls in per property - sent as
/// bytes - and the int id of a card turned back into the guid the form opens on.
/// </summary>
[TestClass]
public class UIQueryVisualTests {
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
    // the wire form of the per-card arrays: base64 of little-endian int32 ids / uint16 group indexes
    static int[] ids(JsonElement e, int count) {
        var bytes = e.GetBytesFromBase64();
        Assert.AreEqual(count * 4, bytes.Length);
        var result = new int[count];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }
    static ushort[] groups(JsonElement e, int count) {
        var bytes = e.GetBytesFromBase64();
        Assert.AreEqual(count * 2, bytes.Length);
        var result = new ushort[count];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    [TestMethod]
    public async Task Visual_EveryCardInItsGroups() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-visual-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (host, storeId, articles) = start(root);
        try {
            var model = await command(host, "query-model", new { storeId });
            var typeId = prop(typeOf(model, nameof(DemoArticle)), "id").GetGuid();
            var pivotModel = await command(host, "query-pivot-model", new { storeId, typeId });
            var properties = prop(pivotModel, "properties").EnumerateArray().ToDictionary(p => prop(p, "name").GetString()!, p => prop(p, "id").GetGuid());

            var visual = await command(host, "query-visual", new {
                storeId, typeId, text = "", semanticRatio = (double?)null, minimumSimilarity = (double?)null, selections = Array.Empty<object>(),
                properties = new[] {
                    new { propertyId = properties["Title"], mode = "values" },
                    new { propertyId = properties["Size"], mode = "ranges" },
                },
            });
            var count = prop(visual, "count").GetInt32();
            Assert.AreEqual(articles.Count, count);
            Assert.AreEqual(articles.Count, prop(visual, "total").GetInt32());
            var cardIds = ids(prop(visual, "ids"), count);
            Assert.AreEqual(count, cardIds.Distinct().Count(), "every card is a different node");
            var byName = prop(visual, "properties").EnumerateArray().ToDictionary(p => prop(p, "name").GetString()!, p => p);

            // by title: one group per value, in value order, every card in the group of its title
            var title = byName["Title"];
            Assert.IsFalse(prop(title, "isRange").GetBoolean());
            Assert.AreEqual(0, prop(title, "unassigned").GetInt32());
            var titleGroups = prop(title, "groups").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(_titles.OrderBy(t => t).ToArray(), titleGroups.Select(g => prop(g, "label").GetString()).ToArray());
            foreach (var g in titleGroups) Assert.AreEqual(articles.Count(a => a.Title == prop(g, "label").GetString()), prop(g, "count").GetInt32());
            var titleOf = groups(prop(title, "assignment"), count);
            var byGuid = articles.ToDictionary(a => a.Id);
            for (var i = 0; i < count; i++) {
                // a card's int id is what the picture carries; the guid is what the form needs
                var guid = prop(await command(host, "query-node-id", new { storeId, id = cardIds[i] }), "id").GetGuid();
                Assert.AreEqual(byGuid[guid].Title, prop(titleGroups[titleOf[i]], "label").GetString(), "card " + i + " is in the group of its own title");
            }

            // by size, as ranges: a scalar with many values is bucketed into ranges that together hold every card
            var size = byName["Size"];
            Assert.IsTrue(prop(size, "isRange").GetBoolean());
            var sizeGroups = prop(size, "groups").EnumerateArray().ToArray();
            Assert.IsTrue(sizeGroups.Length >= 2, "30 distinct sizes make several range buckets");
            Assert.AreEqual(count, sizeGroups.Sum(g => prop(g, "count").GetInt32()));
            var sizeOf = groups(prop(size, "assignment"), count);
            Assert.IsTrue(sizeOf.All(g => g < sizeGroups.Length), "no card outside the range buckets");
            // the ranges carry selection tokens (value and value2), so a legend entry can filter by them
            Assert.IsNotNull(prop(sizeGroups[0], "value").GetString());
            Assert.IsNotNull(prop(sizeGroups[0], "value2").GetString());

            // a facet selection narrows the picture to the cards behind it, like every other view
            var token = prop(titleGroups[0], "value").GetString();
            var narrowed = await command(host, "query-visual", new {
                storeId, typeId, text = "",
                selections = new[] { new { propertyId = properties["Title"], values = new[] { new { value = token, value2 = (string?)null } } } },
                properties = new[] { new { propertyId = properties["Size"], mode = "values" } },
            });
            Assert.AreEqual(prop(titleGroups[0], "count").GetInt32(), prop(narrowed, "count").GetInt32());
            var narrowedSize = prop(narrowed, "properties")[0];
            Assert.IsFalse(prop(narrowedSize, "isRange").GetBoolean(), "asked for values, not ranges");
            Assert.AreEqual(prop(narrowed, "count").GetInt32(), prop(narrowedSize, "groups").GetArrayLength(), "every size is its own group");
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
