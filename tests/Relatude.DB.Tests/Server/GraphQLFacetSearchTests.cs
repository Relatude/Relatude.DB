using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.Demo.Models;
using Relatude.DB.GraphQL;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;

namespace Relatude.Server;

/// <summary>
/// The facet search a GraphQL endpoint serves beside its schema (EnableFacetSearch): the "?facets=" requests its page
/// makes, answered by the server's GraphQLFacetSearch - only when switched on, behind the endpoint's key, and kept to
/// the types and properties the endpoint exposes.
/// </summary>
[TestClass]
public class GraphQLFacetSearchTests {
    static readonly string[] _titles = ["Alpha", "Beta", "Gamma"];

    [TestMethod]
    public async Task TheFacetSearchIsServedOnlyWhenSwitchedOnAndOnlyOverWhatTheEndpointExposes() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-graphql-facets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = TestServerHost.Start(root);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        try {
            var storeId = host.Server.Settings.DefaultStoreId;
            var store = host.Server.Containers[storeId].Store!;
            var articles = Enumerable.Range(1, 30).Select(i => new DemoArticle { Id = Guid.NewGuid(), Title = _titles[i % 3], Content = "c" + i, Size = i }).ToList();
            store.Insert(articles);
            var dm = store.Datastore.Datamodel;
            var type = dm.NodeTypes.Values.Single(t => t.CodeName == nameof(DemoArticle));
            var title = type.AllPropertiesByName["Title"];
            var size = type.AllPropertiesByName["Size"];
            var file = type.AllPropertiesByName["File"];

            // DemoArticle with its title only: Size and File are not part of the endpoint
            var definition = new GraphQLEndpointDefinition {
                Name = "Shop", Url = "/shop", Mode = GraphQLEndpointMode.Selected,
                Types = [new GraphQLTypeDefinition { NodeTypeId = type.Id, Properties = [new GraphQLPropertyDefinition { PropertyId = title.Id, Name = "heading" }] }],
            };
            definition.Id = prop(await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement }), "id").GetGuid();

            // switched off: no facets, and no page for a browser (the explorer is off too)
            Assert.AreEqual(404, (await send(host, "model", null)).Status);
            Assert.AreEqual(400, (await page(host)).Status);
            Assert.IsFalse(prop(prop(await command(host, "graphql-endpoints", new { storeId }), "endpoints")[0], "facets").GetBoolean());

            definition.EnableFacetSearch = true;
            await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement });
            Assert.IsTrue(prop(prop(await command(host, "graphql-endpoints", new { storeId }), "endpoints")[0], "facets").GetBoolean());
            if (Relatude.DB.GraphQL.Endpoints.ExplorerPage.Available) {
                var opened = await page(host);
                Assert.AreEqual(200, opened.Status, "the facet search alone is enough for the page");
                StringAssert.Contains(opened.Body, "\"facets\":true");
                StringAssert.Contains(opened.Body, "\"explorer\":false");
            }

            // the model: the exposed type under its datamodel name, counted
            var model = json(await send(host, "model", null));
            var listed = prop(model, "types").EnumerateArray().Single();
            Assert.AreEqual(type.Id, prop(listed, "id").GetGuid());
            Assert.AreEqual(nameof(DemoArticle), prop(listed, "name").GetString(), "the endpoint's renames are for its schema");
            Assert.AreEqual(30, prop(listed, "count").GetInt32());

            // the rail: the title is a facet and the size is not, and a selection by the size is ignored
            var search = json(await send(host, "search", new { typeId = type.Id, text = "", selections = Array.Empty<object>() }));
            Assert.AreEqual(30, prop(search, "total").GetInt32());
            var facets = prop(search, "facets").EnumerateArray().Select(f => prop(f, "propertyId").GetGuid()).ToArray();
            CollectionAssert.Contains(facets, title.Id);
            CollectionAssert.DoesNotContain(facets, size.Id);
            var alpha = json(await send(host, "search", new { typeId = type.Id, text = "", selections = new[] { new { propertyId = title.Id, values = new[] { new { value = "Alpha", value2 = (string?)null } } } } }));
            Assert.AreEqual(10, prop(alpha, "total").GetInt32());
            var bySize = json(await send(host, "search", new { typeId = type.Id, text = "", selections = new[] { new { propertyId = size.Id, values = new[] { new { value = "3", value2 = (string?)null } } } } }));
            Assert.AreEqual(30, prop(bySize, "total").GetInt32(), "a selection by a property the endpoint does not expose filters nothing");

            // what the picture can be grouped by
            var pivot = json(await send(host, "pivot-model", new { typeId = type.Id }));
            var groupable = prop(pivot, "properties").EnumerateArray().Select(p => prop(p, "id").GetGuid()).ToArray();
            CollectionAssert.Contains(groupable, title.Id);
            CollectionAssert.DoesNotContain(groupable, size.Id);

            // the cards: every node, grouped by the title and not by the size
            var visual = json(await send(host, "visual", new {
                typeId = type.Id, text = "", selections = Array.Empty<object>(),
                properties = new[] { new { propertyId = title.Id, mode = "values" }, new { propertyId = size.Id, mode = "ranges" } },
                sortBy = size.Id,
            }));
            var count = prop(visual, "count").GetInt32();
            Assert.AreEqual(30, count);
            var groups = prop(visual, "properties").EnumerateArray().Select(p => prop(p, "propertyId").GetGuid()).ToArray();
            CollectionAssert.AreEqual(new[] { title.Id }, groups);
            Assert.AreEqual(JsonValueKind.Null, prop(visual, "order").ValueKind, "no sort by a property the endpoint does not expose");
            var ids = prop(visual, "ids").GetBytesFromBase64();
            var cardIds = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32LittleEndian(ids.AsSpan(i * 4, 4))).ToArray();

            // the cards are named from what the endpoint shows: the title
            var cards = prop(json(await send(host, "cards", new { ids = cardIds.Take(5).ToArray() })), "cards").EnumerateArray().ToArray();
            Assert.AreEqual(5, cards.Length);
            foreach (var card in cards) {
                CollectionAssert.Contains(_titles, prop(card, "name").GetString());
                Assert.AreEqual(JsonValueKind.Null, prop(card, "image").ValueKind);
            }

            // a picture from a property the endpoint does not show is never made
            var images = await send(host, "card-images", new { level = 128, items = new[] { new { id = cardIds[0], p = file.Id } } });
            Assert.AreEqual(200, images.Status);
            Assert.AreEqual(10, images.Bytes.Length, "one record, no bytes");
            Assert.AreEqual(cardIds[0], BinaryPrimitives.ReadInt32LittleEndian(images.Bytes));
            Assert.AreEqual(2, images.Bytes[4], "status: no picture");

            // a click on a card: its node as the endpoint shows it - the title under its datamodel name, not the size
            var node = json(await send(host, "node", new { id = cardIds[0] }));
            Assert.AreEqual(nameof(DemoArticle), prop(node, "type").GetString());
            Assert.AreEqual(store.Datastore.GetGuid(cardIds[0]), prop(node, "id").GetGuid());
            var shown = prop(node, "properties").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { "Title" }, shown.Select(p => prop(p, "name").GetString()).ToArray());
            Assert.AreEqual(prop(node, "name").GetString(), prop(shown[0], "value").GetString(), "named from the title, the one thing it shows");
            CollectionAssert.Contains(_titles, prop(shown[0], "value").GetString());
            Assert.AreEqual(404, (await send(host, "node", new { id = int.MaxValue })).Status);

            // a type the endpoint does not expose is not searched, and an unknown request is not answered
            Assert.AreEqual(400, (await send(host, "search", new { typeId = Guid.NewGuid(), text = "" })).Status);
            Assert.AreEqual(404, (await send(host, "nonsense", new { })).Status);

            // the key gates the facets as it gates the queries
            definition.ApiKey = "the-secret-key";
            await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement });
            Assert.AreEqual(401, (await send(host, "model", null)).Status);
            Assert.AreEqual(200, (await send(host, "model", null, "the-secret-key")).Status);
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static async Task<(int Status, string Body)> page(TestServerHost host) {
        var r = await call(host, "GET", "", null, null, "text/html");
        return (r.Status, Encoding.UTF8.GetString(r.Bytes));
    }

    static Task<(int Status, byte[] Bytes)> send(TestServerHost host, string action, object? body, string? apiKey = null)
        => call(host, body == null ? "GET" : "POST", "?facets=" + action, body, apiKey, null);

    static async Task<(int Status, byte[] Bytes)> call(TestServerHost host, string method, string query, object? body, string? apiKey, string? accept) {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = "/shop";
        http.Request.QueryString = new QueryString(query);
        if (accept != null) http.Request.Headers.Accept = accept;
        if (apiKey != null) http.Request.Headers["X-Api-Key"] = apiKey;
        if (body != null) {
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, RelatudeDBJsonOptions.Default)));
        }
        http.Response.Body = new MemoryStream();
        var passed = false;
        await host.Server.GraphQL.Middleware(http, () => { passed = true; return Task.CompletedTask; });
        Assert.IsFalse(passed, "the request was expected to be answered by the endpoint");
        return (http.Response.StatusCode, ((MemoryStream)http.Response.Body).ToArray());
    }

    static JsonElement json((int Status, byte[] Bytes) response) {
        var text = Encoding.UTF8.GetString(response.Bytes);
        Assert.AreEqual(200, response.Status, text);
        return JsonDocument.Parse(text).RootElement;
    }

    static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default)));
        var result = await host.Server.UI!.Commands.Execute(http);
        var json = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, RelatudeDBJsonOptions.Default);
        Assert.AreEqual(200, ((IStatusCodeHttpResult)result).StatusCode ?? 200, "command " + type + " failed: " + json);
        return json;
    }

    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }
}
