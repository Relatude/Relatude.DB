using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.GraphQL;
using Relatude.DB.GraphQL.Endpoints;
using Relatude.DB.GraphQL.Language;
using Relatude.DB.Query.Data;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

/// <summary>The C# code written for an endpoint, and the example queries of its playground.</summary>
[TestClass]
public class GraphQLCodeWritersTests {

    static GraphQLEndpointDefinition writable() => new() {
        Name = "Demo", Url = "/graphql", Mode = GraphQLEndpointMode.WholeDatamodel, AllowMutations = true, ApiKey = "k-1234567890",
        Views = [new GraphQLViewDefinition { Name = "bigArticles", Query = "Article.Where(a => a.IntegerNum > 10)", Description = "Above ten." }],
    };

    [TestMethod]
    public void CSharp_Types_Follow_The_Schema() {
        var (store, _, _) = Open();
        try {
            var gql = new RelatudeGraphQL(store.Datastore, writable());
            var code = CSharpWriter.WriteTypes(gql.Schema);
            StringAssert.Contains(code, "namespace GraphQLClient;");
            StringAssert.Contains(code, "public enum Sizes { Small, Medium, Large }");
            StringAssert.Contains(code, "public class Node {");
            StringAssert.Contains(code, "[JsonPropertyName(\"id\")] public string Id { get; set; } = \"\";");
            StringAssert.Contains(code, "[JsonPropertyName(\"displayName\")] public string? DisplayName { get; set; }");
            StringAssert.Contains(code, "public class ArticleInterface : Node {");
            StringAssert.Contains(code, "public class Article : ArticleInterface {");
            StringAssert.Contains(code, "public class Article2 : ArticleInterface {");
            StringAssert.Contains(code, "[JsonPropertyName(\"integerNum\")] public int IntegerNum { get; set; }");
            StringAssert.Contains(code, "[JsonPropertyName(\"children\")] public List<ArticleInterface> Children { get; set; } = new();");
            StringAssert.Contains(code, "[JsonPropertyName(\"author\")] public User? Author { get; set; }");
            StringAssert.Contains(code, "[JsonPropertyName(\"size\")] public Sizes Size { get; set; }");
            StringAssert.Contains(code, "public class ArticleResult {");
            StringAssert.Contains(code, "[JsonPropertyName(\"items\")] public List<ArticleInterface> Items { get; set; } = new();");
            StringAssert.Contains(code, "public class ArticleInput {");
            StringAssert.Contains(code, "[JsonPropertyName(\"name\")] public string? Name { get; set; }");
            StringAssert.Contains(code, "[JsonPropertyName(\"author\")] public string? Author { get; set; }");
            StringAssert.Contains(code, "public class StringFilterInput {");
            Assert.IsFalse(code.Contains("class Query"), "the root types are not data");
            // a derived class does not repeat its base class's members
            var article2 = code[code.IndexOf("public class Article2", StringComparison.Ordinal)..];
            article2 = article2[..article2.IndexOf('}')];
            Assert.IsFalse(article2.Contains("IntegerNum"), article2);
            StringAssert.Contains(article2, "Name2");
            Assert.AreEqual("ArticleValue", CSharpWriter.PropertyName("article", "Article"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void CSharp_Sample_Client_Is_Written() {
        var (store, _, _) = Open();
        try {
            var gql = new RelatudeGraphQL(store.Datastore, writable());
            var code = CSharpWriter.WriteSample(gql.Schema);
            StringAssert.Contains(code, "public sealed class GraphQLClient(HttpClient http, string endpoint, string apiKey) {");
            StringAssert.Contains(code, "request.Headers.Add(\"X-Api-Key\", apiKey);");
            StringAssert.Contains(code, "public async Task<T> QueryAsync<T>(string query, object? variables = null, CancellationToken ct = default) {");
            StringAssert.Contains(code, "Async(string? search = null, int page = 0) {");
            StringAssert.Contains(code, "Async(string id) {");
            StringAssert.Contains(code, "public async Task<ArticleResult> BigArticlesAsync(int page = 0) {");
            // the sample is written for the richest type, which is Article2 here
            StringAssert.Contains(code, "public async Task<Article2> CreateArticle2Async(Article2Input input) {");
            StringAssert.Contains(code, "public async Task<Article2> UpdateArticle2Async(string id, Article2Input input) {");
            StringAssert.Contains(code, "mutation Delete($id: ID!)");
            var open = new RelatudeGraphQL(store.Datastore, new GraphQLEndpointDefinition { Name = "Open", Url = "/o", Mode = GraphQLEndpointMode.WholeDatamodel });
            var openCode = CSharpWriter.WriteSample(open.Schema);
            StringAssert.Contains(openCode, "public sealed class GraphQLClient(HttpClient http, string endpoint) {");
            Assert.IsFalse(openCode.Contains("X-Api-Key"));
            Assert.IsFalse(openCode.Contains("mutation"), "a read-only endpoint gets no mutation methods");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Examples_Are_Grouped_By_Type_And_Run() {
        var (store, _, _) = Open();
        try {
            var gql = new RelatudeGraphQL(store.Datastore, writable());
            INodeData? sampler(NodeTypeModel t) => (store.Datastore.Query(t.CodeName + ".Page(0, 1)", [], null) as IStoreNodeDataCollection)?.NodeValues.FirstOrDefault();
            var groups = ExampleQueries.Build(gql.Schema, sampler);
            var labels = groups.Select(g => g.Label).ToList();
            CollectionAssert.Contains(labels, "Article");
            CollectionAssert.Contains(labels, "User");
            CollectionAssert.Contains(labels, "Views");
            CollectionAssert.Contains(labels, "Schema");
            var article = groups.Single(g => g.Label == "Article");
            var ids = article.Examples.Select(e => e.Id).ToList();
            CollectionAssert.IsSubsetOf(new[] { "page", "one", "filter", "search", "ordered", "related", "ids", "create", "update", "delete" }, ids);
            var one = article.Examples.Single(e => e.Id == "one");
            var id = JsonDocument.Parse(one.Variables!).RootElement.GetProperty("id").GetString()!;
            Assert.IsTrue(Guid.TryParse(id, out _), "the sampled node's id is real: " + id);
            StringAssert.Contains(article.Examples.Single(e => e.Id == "delete").Variables!, "paste", "a delete never carries a real id");
            StringAssert.Contains(article.Examples.Single(e => e.Id == "update").Variables!, "paste");
            Assert.AreEqual(1, groups.Single(g => g.Label == "Views").Examples.Count);

            // every example parses, and every read example runs without errors against the store
            foreach (var group in groups) {
                foreach (var example in group.Examples) {
                    Parser.Parse(example.Query);
                    if (example.Id is "update" or "delete" or "filter-related") continue;
                    var result = gql.Execute(new GraphQLRequest {
                        Query = example.Query,
                        Variables = example.Variables == null ? null : JsonSerializer.Deserialize<JsonElement>(example.Variables),
                    });
                    Assert.IsNull(result.Errors, $"{group.Label} / {example.Title}: " + string.Join(" | ", result.Errors?.Select(e => e.Message) ?? []));
                }
            }
            Assert.AreEqual(17, store.Query<Relatude.Utils.Article>().Count(), "the create examples of Article and Article2 each added one");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Examples_Without_A_Sampler_Use_Placeholders() {
        var (store, _, _) = Open();
        try {
            var gql = new RelatudeGraphQL(store.Datastore, new GraphQLEndpointDefinition { Name = "RO", Url = "/ro", Mode = GraphQLEndpointMode.WholeDatamodel, EnableIntrospection = false });
            var groups = ExampleQueries.Build(gql.Schema, null);
            var article = groups.Single(g => g.Label == "Article");
            StringAssert.Contains(article.Examples.Single(e => e.Id == "one").Variables!, "paste a node id");
            Assert.IsFalse(article.Examples.Any(e => e.Id == "create"), "no mutations on a read-only endpoint");
            var schema = groups.Single(g => g.Label == "Schema");
            Assert.AreEqual("{ __typename }\n", schema.Examples.Single().Query, "introspection is off, so only the root type name is offered");
        } finally { store.Dispose(); }
    }
}
