using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.GraphQL;
using Relatude.DB.GraphQL.Endpoints;
using Relatude.DB.GraphQL.Language;
using Relatude.DB.Query.Data;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

/// <summary>What the admin UI's explorer is given: the schema described type by type, and the guide's examples.</summary>
[TestClass]
public class GraphQLExplorerTests {

    static GraphQLEndpointDefinition writable() => new() {
        Name = "Demo", Url = "/graphql", Mode = GraphQLEndpointMode.WholeDatamodel, AllowMutations = true,
        Views = [new GraphQLViewDefinition { Name = "bigArticles", Query = "Article.Where(a => a.IntegerNum > 10)", Description = "Above ten." }],
    };

    static readonly string[] _allTopics = [
        "first", "paging", "one", "filter", "filter-range", "filter-in", "filter-logic", "enums", "search", "order", "relations",
        "relation-filter", "ids", "files", "geo", "variables", "aliases", "fragments", "inline-fragments", "directives",
        "typename", "multiple-roots", "operation-name", "views", "introspection-type", "introspection-schema", "create", "update", "delete",
    ];

    [TestMethod]
    public void Guide_Examples_Cover_Every_Topic_And_Run() {
        var (store, _, _) = Open();
        try {
            var gql = new RelatudeGraphQL(store.Datastore, writable());
            INodeData? sampler(NodeTypeModel t) => (store.Datastore.Query(t.CodeName + ".Page(0, 1)", [], null) as IStoreNodeDataCollection)?.NodeValues.FirstOrDefault();
            var guide = ExplorerGuide.Build(gql.Schema, sampler);

            var covered = guide.Examples.Select(e => e.Id).Concat(guide.Unavailable.Select(u => u.Id)).ToList();
            CollectionAssert.AreEquivalent(_allTopics, covered, "every topic is either shown or explained away, once");
            var shown = guide.Examples.Select(e => e.Id).ToHashSet();
            foreach (var id in new[] { "first", "paging", "one", "filter", "filter-range", "filter-in", "filter-logic", "enums", "search", "order",
                "relations", "relation-filter", "ids", "views", "variables", "aliases", "fragments", "inline-fragments", "directives", "typename",
                "multiple-roots", "operation-name", "introspection-type", "introspection-schema", "create", "update", "delete" }) {
                Assert.IsTrue(shown.Contains(id), "the test model has what " + id + " needs");
            }
            Assert.IsTrue(guide.Unavailable.All(u => u.Reason.Length > 0));

            var one = guide.Examples.Single(e => e.Id == "one");
            Assert.IsTrue(Guid.TryParse(JsonDocument.Parse(one.Variables!).RootElement.GetProperty("id").GetString(), out _), "the id is a stored node's");
            StringAssert.Contains(guide.Examples.Single(e => e.Id == "inline-fragments").Query, "... on Article2");
            StringAssert.Contains(guide.Examples.Single(e => e.Id == "fragments").Query, "fragment ");
            StringAssert.Contains(guide.Examples.Single(e => e.Id == "update").Variables!, "paste", "an update never carries a real id");
            StringAssert.Contains(guide.Examples.Single(e => e.Id == "delete").Variables!, "paste");
            Assert.IsTrue(guide.Examples.Where(e => e.IsMutation).Select(e => e.Id).SequenceEqual(["create", "update", "delete"]));

            // every example parses; every one that needs no hand-filled id runs without errors
            foreach (var example in guide.Examples) {
                Assert.IsFalse(example.Query.Contains('\r'), example.Id);
                Parser.Parse(example.Query);
                if (example.Id is "update" or "delete") continue;
                var result = gql.Execute(new GraphQLRequest {
                    Query = example.Query,
                    OperationName = example.Id == "operation-name" ? "FirstThree" : null,
                    Variables = example.Variables == null ? null : JsonSerializer.Deserialize<JsonElement>(example.Variables),
                });
                Assert.IsNull(result.Errors, $"{example.Id}: " + string.Join(" | ", result.Errors?.Select(e => e.Message) ?? []));
            }

            // the filter examples are drawn from stored values, so they find something
            foreach (var id in new[] { "filter", "filter-in", "filter-range" }) {
                var example = guide.Examples.Single(e => e.Id == id);
                var result = gql.Execute(new GraphQLRequest { Query = example.Query, Variables = example.Variables == null ? null : JsonSerializer.Deserialize<JsonElement>(example.Variables) });
                var total = (int)Get(RequireData(result), result.Data!.Keys.First(), "totalCount")!;
                Assert.IsTrue(total > 0, id + " matches a stored node");
            }
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Guide_Explains_What_An_Empty_Read_Only_Endpoint_Cannot_Show() {
        var (store, _, _) = Open();
        try {
            var gql = new RelatudeGraphQL(store.Datastore, new GraphQLEndpointDefinition { Name = "Empty", Url = "/e", Mode = GraphQLEndpointMode.Selected, EnableIntrospection = false });
            var guide = ExplorerGuide.Build(gql.Schema, null);
            Assert.AreEqual(0, guide.Examples.Count);
            StringAssert.Contains(guide.Unavailable.Single(u => u.Id == "first").Reason, "no node types");
            StringAssert.Contains(guide.Unavailable.Single(u => u.Id == "create").Reason, "not allowed");
            StringAssert.Contains(guide.Unavailable.Single(u => u.Id == "introspection-type").Reason, "switched off");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Schema_Is_Described_With_Roles_And_Field_Kinds() {
        var (store, _, _) = Open();
        try {
            var gql = new RelatudeGraphQL(store.Datastore, writable());
            var info = SchemaDescription.Describe(gql.Schema);
            Assert.AreEqual("Query", info.QueryType);
            Assert.AreEqual("Mutation", info.MutationType);
            var types = info.Types.ToDictionary(t => t.Name);
            Assert.AreEqual("root", types["Query"].Role);
            Assert.AreEqual("node", types["Article2"].Role);
            Assert.AreEqual("Relatude.Utils.Article2", types["Article2"].NodeType);
            Assert.AreEqual("INTERFACE", types["ArticleInterface"].Kind, "a class with subtypes is referred to through an interface");
            CollectionAssert.Contains(types["ArticleInterface"].PossibleTypes, "Article2");
            Assert.AreEqual("result", types["ArticleResult"].Role);
            Assert.AreEqual("filter", types["ArticleFilterInput"].Role);
            Assert.AreEqual("operator", types["StringFilterInput"].Role);
            Assert.AreEqual("orderBy", types["ArticleOrderBy"].Role);
            Assert.AreEqual("input", types["ArticleInput"].Role);
            Assert.AreEqual("scalar", types["String"].Role);

            var query = types["Query"].Fields!.ToDictionary(f => f.Name);
            Assert.AreEqual("list", query["articles"].Kind);
            Assert.AreEqual("ArticleResult!", query["articles"].Type);
            Assert.AreEqual("single", query["article"].Kind);
            Assert.AreEqual("view", query["bigArticles"].Kind);
            StringAssert.Contains(query["bigArticles"].Description, "Article.Where(a => a.IntegerNum > 10)", "a view tells the query it starts from");
            var page = query["articles"].Args.Single(a => a.Name == "page");
            Assert.AreEqual("0", page.DefaultValue);
            Assert.AreEqual("false", query["articles"].Args.Single(a => a.Name == "descending").DefaultValue);
            Assert.IsNull(query["articles"].Args.Single(a => a.Name == "pageSize").DefaultValue);

            var article = types["Article"].Fields!.ToDictionary(f => f.Name);
            Assert.AreEqual("system", article["id"].Kind);
            Assert.AreEqual("relation", article["author"].Kind);
            Assert.AreEqual("enum", article["size"].Kind);
            Assert.AreEqual("Size", article["size"].Property, "the datamodel property behind a field");
            Assert.IsTrue(types["ArticleResult"].Fields!.Single(f => f.Name == "items").Many);
            Assert.AreEqual("create", types["Mutation"].Fields!.Single(f => f.Name == "createArticle").Kind);
            CollectionAssert.Contains(types["Sizes"].EnumValues, types["Sizes"].EnumValues![0]);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Guide_And_Facet_Search_Open_On_The_Default_Type() {
        var (store, _, _) = Open();
        try {
            static string listOf(RelatudeGraphQL gql, Guid typeId) => gql.Schema.QueryType.Fields
                .First(f => f.Source == Relatude.DB.GraphQL.Schema.FieldSource.RootList && f.TargetNodeType?.Id == typeId).Name;
            var automatic = new RelatudeGraphQL(store.Datastore, writable());
            var first = automatic.FacetScope.Types[0];
            Assert.AreEqual(first, automatic.FacetScope.DefaultType, "without a default the facet search opens on the first type");
            var autoQuery = ExplorerGuide.Build(automatic.Schema, null).Examples.Single(e => e.Id == "first").Query;

            // any other exposed type, named as the default, is what both open on
            var other = automatic.FacetScope.Types.First(t => !autoQuery.Contains(listOf(automatic, t.Id) + "(") && t != first);
            var def = writable();
            def.DefaultNodeTypeId = other.Id;
            var chosen = new RelatudeGraphQL(store.Datastore, def);
            Assert.AreEqual(other.Id, chosen.FacetScope.DefaultType!.Id);
            Assert.AreEqual(first.Id, chosen.FacetScope.Types[0].Id, "the order of the type list is left alone");
            StringAssert.Contains(ExplorerGuide.Build(chosen.Schema, null).Examples.Single(e => e.Id == "first").Query, listOf(chosen, other.Id) + "(");

            // a default the endpoint does not expose leaves the choice to them, and is warned about
            var selected = new GraphQLEndpointDefinition { Name = "Few", Url = "/few", Types = [new GraphQLTypeDefinition { NodeTypeId = first.Id }], DefaultNodeTypeId = other.Id };
            var few = new RelatudeGraphQL(store.Datastore, selected);
            Assert.AreEqual(first.Id, few.FacetScope.DefaultType!.Id);
            var issues = GraphQLEndpointValidator.Validate(selected, store.Datastore.Datamodel, []);
            Assert.IsTrue(issues.Any(i => !i.IsError && i.Message.Contains("not exposed")));
        } finally { store.Dispose(); }
    }
}
