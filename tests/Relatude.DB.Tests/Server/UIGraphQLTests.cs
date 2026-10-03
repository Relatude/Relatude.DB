using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.GraphQL;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;

namespace Relatude.Server;

/// <summary>
/// GraphQL endpoints defined for a database: saved as json files on its storage by the admin commands,
/// served on their own urls by the server's middleware, and switched off or removed again.
/// </summary>
[TestClass]
public class UIGraphQLTests {

    [TestMethod]
    public async Task AnEndpointIsSavedListedServedAndRemoved() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-graphql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = start(root);
        try {
            var storeId = host.Server.Settings.DefaultStoreId;
            var empty = await command(host, "graphql-endpoints", new { storeId });
            Assert.AreEqual(0, prop(empty, "endpoints").GetArrayLength());
            Assert.IsTrue(prop(empty, "open").GetBoolean());
            Assert.AreEqual(JsonValueKind.Object, prop(empty, "catalog").ValueKind, "the editor gets the datamodel to pick from");

            var definition = new GraphQLEndpointDefinition { Name = "Test API", Url = "gql-test/", Mode = GraphQLEndpointMode.WholeDatamodel };
            var saved = await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement });
            var id = prop(saved, "id").GetGuid();
            Assert.AreEqual("test-api.json", prop(saved, "file").GetString(), "the file is named after the endpoint");

            var listed = await command(host, "graphql-endpoints", new { storeId });
            var entry = prop(listed, "endpoints").EnumerateArray().Single();
            Assert.AreEqual(id, prop(entry, "id").GetGuid());
            Assert.AreEqual("/gql-test", prop(entry, "url").GetString(), "the url is normalized");
            Assert.AreEqual("WholeDatamodel", prop(entry, "mode").GetString());

            var loaded = await command(host, "graphql-endpoint", new { storeId, id });
            Assert.AreEqual("Test API", prop(prop(loaded, "definition"), "name").GetString());
            Assert.AreEqual("WholeDatamodel", prop(prop(loaded, "definition"), "mode").GetString(), "enums travel by name");
            var preview = prop(loaded, "preview");
            StringAssert.Contains(prop(preview, "sdl").GetString(), "type Query");
            StringAssert.Contains(prop(preview, "types").GetString(), "export type ID = string;");
            StringAssert.Contains(prop(preview, "sample").GetString(), "const endpoint = \"/gql-test\";");
            Assert.AreEqual(0, prop(preview, "issues").GetArrayLength());

            // the middleware answers on the url, and only there
            var (status, body) = await request(host, "/gql-test", "{\"query\":\"{ __typename __schema { queryType { name } } }\"}");
            Assert.AreEqual(200, status, body);
            StringAssert.Contains(body, "\"__typename\":\"Query\"");
            Assert.IsTrue(await passesThrough(host, "/gql-test/other"));
            Assert.IsTrue(await passesThrough(host, "/somewhere-else"));
            var (trailing, _) = await request(host, "/gql-test/", "{\"query\":\"{ __typename }\"}");
            Assert.AreEqual(200, trailing, "a trailing slash is tolerated");

            // the playground runs the saved endpoint, and a draft definition without saving it
            var run = await command(host, "graphql-execute", new { storeId, id, query = "{ __typename }" });
            Assert.AreEqual("Query", prop(prop(prop(run, "result"), "data"), "__typename").GetString());
            var draft = new GraphQLEndpointDefinition { Name = "Draft", Url = "/draft", Mode = GraphQLEndpointMode.WholeDatamodel, EnableIntrospection = false };
            var draftRun = await command(host, "graphql-execute", new { storeId, definition = JsonDocument.Parse(draft.ToJson()).RootElement, query = "{ __schema { queryType { name } } }" });
            StringAssert.Contains(prop(draftRun, "result").ToString(), "Introspection is disabled");

            // validation: a second endpoint may not take the same url, nor live under the admin api
            var clash = new GraphQLEndpointDefinition { Name = "Clash", Url = "/GQL-TEST" };
            var (clashStatus, clashJson) = await post(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(clash.ToJson()).RootElement });
            Assert.AreEqual(500, clashStatus);
            StringAssert.Contains(prop(clashJson, "error").GetString(), "already used");
            var previewIssues = await command(host, "graphql-preview", new { storeId, definition = JsonDocument.Parse(new GraphQLEndpointDefinition { Name = "", Url = "//" }.ToJson()).RootElement });
            Assert.IsTrue(prop(previewIssues, "issues").EnumerateArray().Any(i => prop(i, "isError").GetBoolean()));

            // switching the endpoint off takes it off the url; an api key gates it
            definition.Id = id;
            definition.Enabled = false;
            await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement });
            Assert.IsTrue(await passesThrough(host, "/gql-test"), "a disabled endpoint is not served");
            definition.Enabled = true;
            definition.ApiKey = "the-secret-key";
            await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement });
            var (noKey, _) = await request(host, "/gql-test", "{\"query\":\"{ __typename }\"}");
            Assert.AreEqual(401, noKey);
            var (withKey, _) = await request(host, "/gql-test", "{\"query\":\"{ __typename }\"}", "the-secret-key");
            Assert.AreEqual(200, withKey);
            Assert.AreEqual(1, (await command(host, "graphql-endpoints", new { storeId })).GetProperty("endpoints").GetArrayLength(), "saving again rewrote the same file");

            // the file is what the server reads: a hand-written one appears after a reload
            var io = host.Server.GetOrNullIO(host.Server.Containers[storeId].Settings.IoDatabase)!;
            Relatude.DB.IO.IIOProviderExtensions.WriteAllTextUTF8(io, ["graphql", "by-hand.json"], "{ \"url\": \"/by-hand\", \"mode\": \"WholeDatamodel\" }");
            var reloaded = await command(host, "graphql-reload", new { storeId });
            Assert.AreEqual(2, prop(reloaded, "endpoints").GetArrayLength());
            var (handStatus, _) = await request(host, "/by-hand", "{\"query\":\"{ __typename }\"}");
            Assert.AreEqual(200, handStatus);

            await command(host, "graphql-delete", new { storeId, id });
            Assert.AreEqual(1, (await command(host, "graphql-endpoints", new { storeId })).GetProperty("endpoints").GetArrayLength());
            Assert.IsTrue(await passesThrough(host, "/gql-test"));
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task TheExplorerIsGivenTheSchemaAndAGuideForADraft() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-graphql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = start(root);
        try {
            var storeId = host.Server.Settings.DefaultStoreId;
            // a draft, never saved, with introspection off: the explorer reads the schema all the same
            var draft = new GraphQLEndpointDefinition { Name = "Draft", Url = "/draft", Mode = GraphQLEndpointMode.WholeDatamodel, EnableIntrospection = false };
            var explorer = await command(host, "graphql-explorer", new { storeId, definition = JsonDocument.Parse(draft.ToJson()).RootElement });
            var schema = prop(explorer, "schema");
            Assert.AreEqual("Query", prop(schema, "queryType").GetString());
            Assert.AreEqual(JsonValueKind.Null, prop(schema, "mutationType").ValueKind, "mutations are off by default");
            var types = prop(schema, "types").EnumerateArray().ToList();
            Assert.IsTrue(types.Any(t => prop(t, "name").GetString() == "Query" && prop(t, "role").GetString() == "root"));
            Assert.AreEqual(JsonValueKind.Object, prop(explorer, "samples").ValueKind);
            var guide = prop(explorer, "guide");
            Assert.AreEqual(JsonValueKind.Array, prop(guide, "examples").ValueKind);
            var unavailable = prop(guide, "unavailable").EnumerateArray().ToList();
            Assert.IsTrue(unavailable.Any(u => prop(u, "id").GetString() == "introspection-schema"), "introspection is off, and the guide says so");
            Assert.IsTrue(unavailable.Any(u => prop(u, "id").GetString() == "create"));
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task TheExplorerPageIsServedOnTheUrlOnlyWhenSwitchedOn() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-graphql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = start(root);
        try {
            var storeId = host.Server.Settings.DefaultStoreId;
            var definition = new GraphQLEndpointDefinition { Name = "Public", Url = "/pub", Mode = GraphQLEndpointMode.WholeDatamodel };
            var saved = await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement });
            definition.Id = prop(saved, "id").GetGuid();

            // switched off: a browser gets what any client gets without a query
            var off = await get(host, "/pub", "", "text/html");
            Assert.AreEqual(400, off.Status);
            Assert.AreEqual(404, (await get(host, "/pub", "?explorer-data", null)).Status, "no schema for the page while it is off");
            Assert.IsFalse((await command(host, "graphql-endpoints", new { storeId })).GetProperty("endpoints")[0].GetProperty("explorer").GetBoolean());

            definition.EnableExplorer = true;
            await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement });
            Assert.IsTrue((await command(host, "graphql-endpoints", new { storeId })).GetProperty("endpoints")[0].GetProperty("explorer").GetBoolean());
            var page = await get(host, "/pub", "", "text/html,application/xhtml+xml");
            if (Relatude.DB.GraphQL.Endpoints.ExplorerPage.Available) {
                Assert.AreEqual(200, page.Status);
                StringAssert.StartsWith(page.ContentType, "text/html");
                StringAssert.Contains(page.Body, "id=\"relatude-explorer-config\"");
                StringAssert.Contains(page.Body, "\"url\":\"/pub\"");
                StringAssert.Contains(page.Body, "src=\"?explorer-asset=explorer.js&v=", "the files are asked for on the same url");
                var script = await get(host, "/pub", "?explorer-asset=explorer.js&v=1", null);
                Assert.AreEqual(200, script.Status);
                StringAssert.StartsWith(script.ContentType, "text/javascript");
                Assert.AreEqual(404, (await get(host, "/pub", "?explorer-asset=..%2Fsecret.txt", null)).Status);
            } else {
                Assert.AreEqual(404, page.Status, "a build without the page says so");
            }
            // a client asking a question is not given the page, browser or not
            Assert.AreEqual(200, (await get(host, "/pub", "?query=%7B__typename%7D", "text/html")).Status);
            var data = await get(host, "/pub", "?explorer-data", null);
            Assert.AreEqual(200, data.Status, data.Body);
            var json = JsonDocument.Parse(data.Body).RootElement;
            Assert.AreEqual("Query", json.GetProperty("schema").GetProperty("queryType").GetString());
            Assert.AreEqual(JsonValueKind.Object, json.GetProperty("samples").ValueKind);
            Assert.AreEqual(JsonValueKind.Array, json.GetProperty("guide").GetProperty("examples").ValueKind);

            // the schema is no more open than the endpoint: the key is needed, and introspection must be on
            definition.ApiKey = "the-secret-key";
            await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement });
            if (Relatude.DB.GraphQL.Endpoints.ExplorerPage.Available) {
                var keyed = await get(host, "/pub", "", "text/html");
                Assert.AreEqual(200, keyed.Status, "the page itself is where the key is typed in");
                StringAssert.Contains(keyed.Body, "\"apiKey\":true");
                Assert.IsFalse(keyed.Body.Contains("the-secret-key"), "the key is never written into the page");
            }
            Assert.AreEqual(401, (await get(host, "/pub", "?explorer-data", null)).Status);
            Assert.AreEqual(200, (await get(host, "/pub", "?explorer-data", null, "the-secret-key")).Status);
            definition.EnableIntrospection = false;
            await command(host, "graphql-save", new { storeId, definition = JsonDocument.Parse(definition.ToJson()).RootElement });
            Assert.AreEqual(403, (await get(host, "/pub", "?explorer-data", null, "the-secret-key")).Status);
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static async Task<(int Status, string? ContentType, string Body)> get(TestServerHost host, string path, string query, string? accept, string? apiKey = null) {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        if (accept != null) http.Request.Headers.Accept = accept;
        if (apiKey != null) http.Request.Headers["X-Api-Key"] = apiKey;
        http.Response.Body = new MemoryStream();
        var passed = false;
        await host.Server.GraphQL.Middleware(http, () => { passed = true; return Task.CompletedTask; });
        Assert.IsFalse(passed, "the request was expected to be answered by the endpoint");
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, http.Response.ContentType, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    static TestServerHost start(string root) {
        var host = TestServerHost.Start(root);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        return host;
    }

    static async Task<(int Status, string Body)> request(TestServerHost host, string path, string body, string? apiKey = null) {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = path;
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (apiKey != null) http.Request.Headers["X-Api-Key"] = apiKey;
        http.Response.Body = new MemoryStream();
        var passed = false;
        await host.Server.GraphQL.Middleware(http, () => { passed = true; return Task.CompletedTask; });
        Assert.IsFalse(passed, "the request was expected to be answered by the endpoint");
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    static async Task<bool> passesThrough(TestServerHost host, string path) {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = path;
        var passed = false;
        await host.Server.GraphQL.Middleware(http, () => { passed = true; return Task.CompletedTask; });
        return passed;
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
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property " + name + " in " + e);
    }
}
