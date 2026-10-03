using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.GraphQL;
using Relatude.DB.GraphQL.Endpoints;
using Relatude.DB.IO;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

/// <summary>The pieces around the executor: the json files, the validator, the TypeScript writer and the HTTP handler.</summary>
[TestClass]
public class GraphQLEndpointToolsTests {

    [TestMethod]
    public void Store_Saves_Loads_And_Deletes_Json_Files() {
        var io = new IOProviderMemory();
        var store = new GraphQLEndpointStore(io);
        Assert.AreEqual(0, store.Load().Count);

        var def = new GraphQLEndpointDefinition { Name = "Public API", Url = "/api/graphql", Mode = GraphQLEndpointMode.WholeDatamodel };
        var saved = store.Save(def, null);
        Assert.AreNotEqual(Guid.Empty, def.Id);
        CollectionAssert.AreEqual(new[] { "graphql", "public-api.json" }, saved.Key);
        var second = store.Save(new GraphQLEndpointDefinition { Name = "Public API", Url = "/other" }, null);
        Assert.AreEqual("public-api-2.json", second.FileName, "a taken file name gets a counter");

        // a hand-written file: no id, comments, sparse
        io.WriteAllTextUTF8(["graphql", "handmade.json"], "{ /* by hand */ \"url\": \"/hand\", \"mode\": \"Selected\" }");
        io.WriteAllTextUTF8(["graphql", "broken.json"], "{ not json");
        io.WriteAllTextUTF8(["graphql", "notes.txt"], "ignored");

        var files = store.Load();
        Assert.AreEqual(4, files.Count);
        var handmade = files.Single(f => f.FileName == "handmade.json");
        Assert.IsNotNull(handmade.Definition);
        Assert.AreEqual("handmade", handmade.Definition!.Name, "a nameless file is named after itself");
        Assert.AreEqual(GraphQLEndpointStore.IdFromKey(["graphql", "handmade.json"]), handmade.Definition.Id);
        Assert.AreEqual(GraphQLEndpointStore.IdFromKey(["graphql", "HANDMADE.json"]), handmade.Definition.Id, "the derived id does not depend on case");
        var broken = files.Single(f => f.FileName == "broken.json");
        Assert.IsNull(broken.Definition);
        Assert.IsNotNull(broken.Error);
        var reloaded = files.Single(f => f.FileName == "public-api.json").Definition!;
        Assert.AreEqual(def.Id, reloaded.Id);
        Assert.AreEqual("/api/graphql", reloaded.Url);

        // saving to the existing key keeps the file name
        reloaded.Url = "/changed";
        var again = store.Save(reloaded, saved.Key);
        Assert.AreEqual("public-api.json", again.FileName);
        Assert.AreEqual("/changed", store.Load().Single(f => f.FileName == "public-api.json").Definition!.Url);

        store.Delete(saved.Key);
        Assert.AreEqual(3, store.Load().Count);
    }

    [TestMethod]
    public void Validator_Finds_The_Usual_Mistakes() {
        var (store, _, _) = Open();
        try {
            var dm = store.Datastore.Datamodel;
            var article = dm.NodeTypes.Values.First(t => t.CodeName == "Article");
            var other = new GraphQLEndpointDefinition { Id = Guid.NewGuid(), Name = "Other", Url = "/Taken/" };
            var def = new GraphQLEndpointDefinition {
                Id = Guid.NewGuid(), Name = "", Url = "taken", MaxPageSize = 5, DefaultPageSize = 10, AllowMutations = true, ApiKey = "abc",
                Mode = GraphQLEndpointMode.Selected,
                Types = [
                    new GraphQLTypeDefinition { NodeTypeId = article.Id, Name = "My Post" },
                    new GraphQLTypeDefinition { NodeTypeId = article.Id },
                    new GraphQLTypeDefinition { NodeTypeId = Guid.NewGuid(), Name = "Ghost" },
                ],
                Views = [
                    new GraphQLViewDefinition { Name = "bad name", Query = "Article" },
                    new GraphQLViewDefinition { Name = "v", Query = "Article.Page(0, 1)" },
                    new GraphQLViewDefinition { Name = "v", Query = "" },
                ],
            };
            var issues = GraphQLEndpointValidator.Validate(def, dm, [other]);
            var errors = issues.Where(i => i.IsError).Select(i => i.Message).ToList();
            var warnings = issues.Where(i => !i.IsError).Select(i => i.Message).ToList();
            Assert.IsTrue(errors.Any(e => e.Contains("needs a name")), string.Join(" | ", errors));
            Assert.IsTrue(errors.Any(e => e.Contains("/taken") && e.Contains("Other")), string.Join(" | ", errors));
            Assert.IsTrue(errors.Any(e => e.Contains("maximum page size is smaller")), string.Join(" | ", errors));
            Assert.IsTrue(errors.Any(e => e.Contains("listed twice")), string.Join(" | ", errors));
            Assert.IsTrue(errors.Any(e => e.Contains("bad name")), string.Join(" | ", errors));
            Assert.IsTrue(errors.Any(e => e.Contains("Page")), string.Join(" | ", errors));
            Assert.IsTrue(errors.Any(e => e.Contains("used twice")), string.Join(" | ", errors));
            Assert.IsTrue(errors.Any(e => e.Contains("query is empty")), string.Join(" | ", errors));
            Assert.IsTrue(warnings.Any(w => w.Contains("Ghost")), string.Join(" | ", warnings));
            Assert.IsTrue(warnings.Any(w => w.Contains("My Post") && w.Contains("MyPost")), string.Join(" | ", warnings));
            Assert.IsTrue(warnings.Any(w => w.Contains("API key is short")), string.Join(" | ", warnings));

            var fine = GraphQLEndpointValidator.Validate(new GraphQLEndpointDefinition { Name = "ok", Url = "/ok", Mode = GraphQLEndpointMode.WholeDatamodel }, dm, [other]);
            Assert.AreEqual(0, fine.Count, string.Join(" | ", fine.Select(i => i.Message)));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void TypeScript_Types_And_Sample_Are_Written() {
        var (store, _, _) = Open();
        try {
            var def = new GraphQLEndpointDefinition {
                Name = "Demo", Url = "/graphql", Mode = GraphQLEndpointMode.WholeDatamodel, AllowMutations = true, ApiKey = "k-1234567890",
                Views = [new GraphQLViewDefinition { Name = "bigArticles", Query = "Article.Where(a => a.IntegerNum > 10)" }],
            };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            var types = TypeScriptWriter.WriteTypes(gql.Schema);
            StringAssert.Contains(types, "export type ID = string;");
            StringAssert.Contains(types, "export type Sizes = \"Small\" | \"Medium\" | \"Large\";");
            StringAssert.Contains(types, "export interface Node {");
            StringAssert.Contains(types, "export interface Article extends Node, ArticleInterface {");
            StringAssert.Contains(types, "  integerNum: number;");
            StringAssert.Contains(types, "  author: UserInterface | null;".Replace("UserInterface", "User"));
            StringAssert.Contains(types, "  children: ArticleInterface[];");
            StringAssert.Contains(types, "export interface ArticleResult {");
            StringAssert.Contains(types, "export interface ArticleFilterInput {");
            StringAssert.Contains(types, "  name?: StringFilterInput | null;");
            StringAssert.Contains(types, "export interface ArticleInput {");
            StringAssert.Contains(types, "  author?: ID | null;");
            StringAssert.Contains(types, "export interface Mutation {");

            var sample = TypeScriptWriter.WriteSample(gql.Schema);
            StringAssert.Contains(sample, "const endpoint = \"/graphql\";");
            StringAssert.Contains(sample, "\"X-Api-Key\": apiKey");
            StringAssert.Contains(sample, "export async function gql<T>");
            StringAssert.Contains(sample, "export async function list");
            StringAssert.Contains(sample, "export async function get");
            StringAssert.Contains(sample, "export async function bigArticles(");
            StringAssert.Contains(sample, "export async function create");
            StringAssert.Contains(sample, "export async function delete");
        } finally { store.Dispose(); }
    }

    static async Task<(int Status, string Body, string? ContentType)> call(RelatudeGraphQL gql, string method, string? body = null, string? query = null, Action<HttpRequest>? setup = null) {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        if (query != null) http.Request.QueryString = new QueryString(query);
        if (body != null) {
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        }
        setup?.Invoke(http.Request);
        http.Response.Body = new MemoryStream();
        await GraphQLHttpHandler.HandleAsync(http, gql, null);
        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http.Response.StatusCode, text, http.Response.ContentType);
    }

    [TestMethod]
    public async Task Http_Handler_Post_Get_Sdl_And_Errors() {
        var (store, gql, _) = Open();
        try {
            var (status, body, contentType) = await call(gql, "POST", "{\"query\":\"{ articles { totalCount } }\"}");
            Assert.AreEqual(200, status);
            StringAssert.StartsWith(contentType, "application/graphql-response+json");
            Assert.AreEqual(15, JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("articles").GetProperty("totalCount").GetInt32());

            var (getStatus, getBody, _) = await call(gql, "GET", query: "?query=" + Uri.EscapeDataString("{ articles { totalCount } }"));
            Assert.AreEqual(200, getStatus);
            StringAssert.Contains(getBody, "\"totalCount\":15");

            var (sdlStatus, sdl, sdlType) = await call(gql, "GET", query: "?sdl");
            Assert.AreEqual(200, sdlStatus);
            StringAssert.StartsWith(sdlType, "text/plain");
            StringAssert.Contains(sdl, "type Query");

            var (badStatus, badBody, _) = await call(gql, "POST", "{\"query\":\"{ nope }\"}");
            Assert.AreEqual(400, badStatus, "a request with no data is the client's mistake");
            StringAssert.Contains(badBody, "nope");

            var (jsonStatus, _, _) = await call(gql, "POST", "{ not json");
            Assert.AreEqual(400, jsonStatus);

            var (rawStatus, rawBody, _) = await call(gql, "POST", setup: r => {
                r.ContentType = "application/graphql";
                r.Body = new MemoryStream(Encoding.UTF8.GetBytes("{ articles { totalCount } }"));
            });
            Assert.AreEqual(200, rawStatus);
            StringAssert.Contains(rawBody, "\"totalCount\":15");

            var (putStatus, _, _) = await call(gql, "PUT", "{}");
            Assert.AreEqual(405, putStatus);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public async Task Http_Handler_Api_Key_And_Get_Switch() {
        var (store, _, _) = Open();
        try {
            var def = new GraphQLEndpointDefinition { Name = "Keyed", Url = "/k", Mode = GraphQLEndpointMode.WholeDatamodel, ApiKey = "s3cret-key", EnableGetRequests = false };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            var (noKey, noKeyBody, _) = await call(gql, "POST", "{\"query\":\"{ articles { totalCount } }\"}");
            Assert.AreEqual(401, noKey);
            StringAssert.Contains(noKeyBody, "API key");
            var (wrongKey, _, _) = await call(gql, "POST", "{\"query\":\"{ articles { totalCount } }\"}", setup: r => r.Headers["X-Api-Key"] = "nope");
            Assert.AreEqual(401, wrongKey);
            var (header, _, _) = await call(gql, "POST", "{\"query\":\"{ articles { totalCount } }\"}", setup: r => r.Headers["X-Api-Key"] = "s3cret-key");
            Assert.AreEqual(200, header);
            var (bearer, _, _) = await call(gql, "POST", "{\"query\":\"{ articles { totalCount } }\"}", setup: r => r.Headers.Authorization = "Bearer s3cret-key");
            Assert.AreEqual(200, bearer);
            var (get, _, _) = await call(gql, "GET", query: "?query=" + Uri.EscapeDataString("{ articles { totalCount } }"), setup: r => r.Headers["X-Api-Key"] = "s3cret-key");
            Assert.AreEqual(405, get, "GET is switched off on this endpoint");
            var (sdl, _, _) = await call(gql, "GET", query: "?sdl", setup: r => r.Headers["X-Api-Key"] = "s3cret-key");
            Assert.AreEqual(200, sdl, "the schema text is still served");
            var (sdlNoKey, _, _) = await call(gql, "GET", query: "?sdl");
            Assert.AreEqual(401, sdlNoKey, "but only with the key");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public async Task Http_Handler_Keeps_The_Schema_Text_In_When_Introspection_Is_Off() {
        var (store, _, _) = Open();
        try {
            var gql = new RelatudeGraphQL(store.Datastore, new GraphQLEndpointDefinition { Name = "Hidden", Url = "/h", Mode = GraphQLEndpointMode.WholeDatamodel, EnableIntrospection = false });
            var (status, body, _) = await call(gql, "GET", query: "?sdl");
            Assert.AreEqual(403, status);
            StringAssert.Contains(body, "Introspection is switched off");
            Assert.IsFalse(body.Contains("type Query"), "nothing of the schema is told");
            var (introspection, introspectionBody, _) = await call(gql, "POST", "{\"query\":\"{ __schema { types { name } } }\"}");
            StringAssert.Contains(introspectionBody, "Introspection is disabled", "the same terms as introspection itself");
            var (query, _, _) = await call(gql, "POST", "{\"query\":\"{ articles { totalCount } }\"}");
            Assert.AreEqual(200, query, "queries are not affected");
            Assert.AreNotEqual(200, introspection);
        } finally { store.Dispose(); }
    }
}
