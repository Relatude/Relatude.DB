using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.Datamodels;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.NodeServer.ModelEditor;
using Relatude.WriteRuleModels;

namespace Relatude.Server;

/// <summary>
/// What the admin UI is told about a property's rules, and what it gets back when a save breaks one:
/// the legal values of a text property come with the form and the table, so either can offer a list,
/// and the model editor edits them as the list of strings they are.
/// </summary>
[TestClass]
public class UIQueryWriteRulesTests {

    static TestServerHost start(string root) => TestServerHost.Start(root, configure: s => {
        s.ContainerSettings![0].DatamodelSources = [new DatamodelSource {
            Id = Guid.NewGuid(), Name = "Compiled", Type = DatamodelSourceType.CompiledTypes,
            Reference = typeof(WrPost).Assembly.GetName().Name, Namespace = typeof(WrPost).Namespace, // not the .Invalid types beneath it
        }];
    });
    static async Task<(int status, JsonElement json)> send(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default));
    }
    static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
        var (status, json) = await send(host, type, payload);
        Assert.AreEqual(200, status, "command " + type + " failed: " + json);
        return json;
    }
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }
    static string[]? strings(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : [.. e.EnumerateArray().Select(x => x.GetString()!)];

    [TestMethod]
    public async Task LegalValues_ReachTheFormAndTheTable_AndASaveOutsideThemIsRefused() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-write-rules-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = start(root);
        try {
            typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
            var storeId = host.Settings.Settings.ContainerSettings![0].Id;
            var store = host.Server.Containers[storeId].Store!;
            var post = new WrPost { Id = Guid.NewGuid(), Slug = "first", Status = "draft", Stars = 2 };
            store.Insert(post);

            // the form
            var node = await command(host, "query-node", new { storeId, id = post.Id });
            var fields = prop(node, "properties").EnumerateArray().ToDictionary(f => prop(f, "name").GetString()!);
            Assert.AreEqual("text", prop(fields["Status"], "editor").GetString());
            CollectionAssert.AreEqual(new[] { "draft", "published" }, strings(prop(fields["Status"], "choices")));
            Assert.IsNull(strings(prop(fields["Free"], "choices")), "a text without legal values is a free text field");
            Assert.AreEqual(@"^[a-z0-9-]+$", prop(fields["Slug"], "pattern").GetString());
            Assert.AreEqual("enum", prop(fields["Stars"], "editor").GetString(), "an integer with legal values is picked from them");

            // the table
            var model = await command(host, "query-model", new { storeId });
            var typeId = prop(prop(model, "types").EnumerateArray().First(t => prop(t, "name").GetString() == nameof(WrPost)), "id").GetGuid();
            var table = await command(host, "query-search", new {
                storeId, typeId, text = "", selections = Array.Empty<object>(), expanded = Array.Empty<Guid>(),
                page = 0, pageSize = 10, table = true, facets = false, sortBy = "", sortDescending = false,
            });
            var status = prop(table, "columns").EnumerateArray().Single(c => prop(c, "name").GetString() == "Status");
            CollectionAssert.AreEqual(new[] { "draft", "published" }, strings(prop(status, "choices")));

            // a save outside the list fails, and the node keeps its value
            var statusId = prop(fields["Status"], "id").GetString()!;
            var (code, error) = await send(host, "query-save", new { storeId, id = post.Id, values = new Dictionary<string, object> { [statusId] = "archived" } });
            Assert.AreNotEqual(200, code, "the save must be refused: " + error);
            StringAssert.Contains(error.ToString(), "legal values");
            Assert.AreEqual("draft", store.Get<WrPost>(post.Id).Status);
            await command(host, "query-save", new { storeId, id = post.Id, values = new Dictionary<string, object> { [statusId] = "published" } });
            Assert.AreEqual("published", store.Get<WrPost>(post.Id).Status);
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void Catalog_EditsLegalValuesAsTheListTheyAre() {
        // one catalog entry serves both: the member's type decides the editor
        var schema = JsonSerializer.SerializeToElement(DatamodelCatalog.Schema, RelatudeDBJsonOptions.Default);
        string editor(string propertyType) {
            var byType = prop(schema, "propertyByType");
            var field = prop(byType, propertyType).EnumerateArray().Single(f => prop(f, "path").GetString() == "LegalValues");
            return prop(field, "editor").GetString()!;
        }
        Assert.AreEqual("stringList", editor("String"));
        Assert.AreEqual("intList", editor("Integer"));
    }
}
