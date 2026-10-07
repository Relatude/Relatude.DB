using Relatude.DB.Datamodels;
using Relatude.DB.GraphQL;
using Relatude.DB.Nodes;
using Relatude.Utils;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

/// <summary>Endpoints defined by a <see cref="GraphQLEndpointDefinition"/>: selected types and properties, renames, exact names, views.</summary>
[TestClass]
public class GraphQLEndpointDefinitionTests {

    static NodeTypeModel type(NodeStore store, string codeName) => store.Datastore.Datamodel.NodeTypes.Values.First(t => t.CodeName == codeName);
    static Guid prop(NodeTypeModel t, string codeName) => t.AllProperties.Values.First(p => p.CodeName == codeName).Id;

    [TestMethod]
    public void Selected_Types_And_Properties_With_Renames() {
        var (store, _, all) = Open();
        try {
            var article = type(store, "Article");
            var def = new GraphQLEndpointDefinition {
                Name = "Posts", Url = "/posts", Mode = GraphQLEndpointMode.Selected,
                Types = [
                    new GraphQLTypeDefinition {
                        NodeTypeId = article.Id, Name = "Post", SingleName = "post", ListName = "posts",
                        Properties = [
                            new GraphQLPropertyDefinition { PropertyId = prop(article, "Name"), Name = "title" },
                            new GraphQLPropertyDefinition { PropertyId = prop(article, "IntegerNum") },
                            new GraphQLPropertyDefinition { PropertyId = prop(article, "Author") }, // User is not exposed: dropped with a warning
                        ],
                    },
                ],
            };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            var sdl = gql.ToSDL();
            StringAssert.Contains(sdl, "type Post implements");
            StringAssert.Contains(sdl, "title: String");
            StringAssert.Contains(sdl, "integerNum: Int!");
            Assert.IsFalse(sdl.Contains("doubleNum"), "unselected properties are not exposed");
            Assert.IsFalse(sdl.Contains("type User"), "unselected types are not exposed");
            var post = (Relatude.DB.GraphQL.Schema.GqlObjectType)gql.Schema.Types["Post"];
            Assert.IsFalse(post.TryGetField("author", out _), "a relation to an unexposed type is dropped");
            StringAssert.Contains(sdl, "author: RelatedNodeFilterInput", "filtering by the related node's id still works");
            Assert.IsTrue(gql.Warnings.Any(w => w.Contains("author") && w.Contains("User")), string.Join(" | ", gql.Warnings));
            Assert.IsFalse(gql.Schema.QueryType.TryGetField("articles", out _));

            var id = PublicId(store, 3);
            var data = RequireData(gql.Execute($$"""
                { post(id: "{{id}}") { title integerNum }
                  posts(filter: { title: { eq: "Article 05" } }) { totalCount items { title } }
                  all: posts(orderBy: integerNum, descending: true, pageSize: 2) { items { integerNum } } }
                """));
            Assert.AreEqual("Article 03", Get(data, "post", "title"));
            Assert.AreEqual(1, Get(data, "posts", "totalCount"));
            Assert.AreEqual(15, Get(data, "all", "items", 0, "integerNum"));
            Assert.AreEqual(15, all.Count);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Selected_Type_Without_Property_List_Exposes_Everything_Of_It() {
        var (store, _, _) = Open();
        try {
            var def = new GraphQLEndpointDefinition { Mode = GraphQLEndpointMode.Selected, Types = [new GraphQLTypeDefinition { NodeTypeId = type(store, "User").Id }] };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            Assert.IsTrue(gql.Schema.Types.TryGetValue("User", out var user));
            var fields = ((Relatude.DB.GraphQL.Schema.GqlObjectType)user!).Fields.Select(f => f.Name).ToList();
            CollectionAssert.Contains(fields, "username");
            Assert.IsFalse(fields.Contains("articles"), "Article is not exposed, so the relation field is left out");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Inherited_Property_Keeps_The_Base_Types_Field_Name() {
        var (store, _, _) = Open();
        try {
            var article = type(store, "Article");
            var article2 = type(store, "Article2");
            var def = new GraphQLEndpointDefinition {
                Mode = GraphQLEndpointMode.Selected,
                Types = [
                    new GraphQLTypeDefinition { NodeTypeId = article.Id, Properties = [new GraphQLPropertyDefinition { PropertyId = prop(article, "Name"), Name = "title" }] },
                    new GraphQLTypeDefinition { NodeTypeId = article2.Id, Properties = [
                        new GraphQLPropertyDefinition { PropertyId = prop(article2, "Name"), Name = "heading" }, // loses to the base type's name
                        new GraphQLPropertyDefinition { PropertyId = prop(article2, "Name2") },
                    ] },
                ],
            };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            var sdl = gql.ToSDL();
            StringAssert.Contains(sdl, "interface ArticleInterface");
            var a2 = (Relatude.DB.GraphQL.Schema.GqlObjectType)gql.Schema.Types["Article2"];
            Assert.IsTrue(a2.TryGetField("title", out _), "the interface's field name is inherited");
            Assert.IsFalse(a2.TryGetField("heading", out _));
            Assert.IsTrue(a2.TryGetField("name2", out _));
            Assert.IsTrue(gql.Warnings.Any(w => w.Contains("heading")), string.Join(" | ", gql.Warnings));
            var data = RequireData(gql.Execute("{ articles(filter: { title: { eq: \"Article 14\" } }) { items { title ... on Article2 { name2 } } } }"));
            Assert.AreEqual("extra 14", Get(data, "articles", "items", 0, "name2"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Whole_Datamodel_With_Exact_Names() {
        var (store, _, _) = Open();
        try {
            var def = new GraphQLEndpointDefinition { Mode = GraphQLEndpointMode.WholeDatamodel, ExactNames = true, AllowMutations = true };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            var sdl = gql.ToSDL();
            StringAssert.Contains(sdl, "Name: String");
            StringAssert.Contains(sdl, "IntegerNum: Int!");
            StringAssert.Contains(sdl, "Article(id: ID!): ArticleInterface");
            StringAssert.Contains(sdl, "Articles(");
            StringAssert.Contains(sdl, "CreateArticle(input: ArticleInput!): Article");
            StringAssert.Contains(sdl, "  id: ID!"); // system fields keep their GraphQL names
            Assert.IsFalse(sdl.Contains("integerNum"), "no camel casing in exact mode");
            var id = PublicId(store, 7);
            var data = RequireData(gql.Execute($$"""{ Article(id: "{{id}}") { id Name IntegerNum Author { Username } } Users { totalCount } }"""));
            Assert.AreEqual("Article 07", Get(data, "Article", "Name"));
            Assert.AreEqual("bob", Get(data, "Article", "Author", "Username"));
            Assert.AreEqual(2, Get(data, "Users", "totalCount"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Views_Are_Root_Fields_With_The_Usual_Arguments() {
        var (store, _, all) = Open();
        try {
            var def = new GraphQLEndpointDefinition {
                Mode = GraphQLEndpointMode.WholeDatamodel,
                Views = [
                    new GraphQLViewDefinition { Name = "bigArticles", Query = "Article.Where(a => a.IntegerNum > 10)", Description = "Articles above ten." },
                    new GraphQLViewDefinition { Name = "sorted", Query = "Article.Where(a => a.IntegerNum <= 3).OrderBy(a => a.IntegerNum, true)" },
                    new GraphQLViewDefinition { Name = "broken", Query = "Nothing.Where(a => a.X == 1)" },
                    new GraphQLViewDefinition { Name = "paged", Query = "Article.Page(0, 2)" },
                    new GraphQLViewDefinition { Name = "", Query = "Article" },
                ],
            };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            Assert.IsTrue(gql.Schema.QueryType.TryGetField("bigArticles", out var view));
            Assert.AreEqual("Articles above ten.", view!.Description);
            Assert.IsFalse(gql.Schema.QueryType.TryGetField("broken", out _));
            Assert.IsFalse(gql.Schema.QueryType.TryGetField("paged", out _));
            Assert.IsTrue(gql.Warnings.Any(w => w.Contains("broken") && w.Contains("Nothing")), string.Join(" | ", gql.Warnings));
            Assert.IsTrue(gql.Warnings.Any(w => w.Contains("paged") && w.Contains("Page")), string.Join(" | ", gql.Warnings));
            Assert.IsTrue(gql.Warnings.Any(w => w.Contains("without a name")), string.Join(" | ", gql.Warnings));
            StringAssert.Contains(gql.ToSDL(), "bigArticles(filter: ArticleFilterInput");

            var data = RequireData(gql.Execute("""
                { bigArticles { totalCount }
                  filtered: bigArticles(filter: { integerNum: { lte: 12 } }, orderBy: integerNum) { totalCount items { integerNum } }
                  sorted(pageSize: 2) { items { integerNum } }
                  searched: bigArticles(ids: []) { totalCount } }
                """));
            Assert.AreEqual(all.Count(a => a.IntegerNum > 10), Get(data, "bigArticles", "totalCount"));
            Assert.AreEqual(2, Get(data, "filtered", "totalCount"));
            Assert.AreEqual(11, Get(data, "filtered", "items", 0, "integerNum"));
            Assert.AreEqual(3, Get(data, "sorted", "items", 0, "integerNum"));
            Assert.AreEqual(0, Get(data, "searched", "totalCount"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void View_On_An_Unexposed_Type_Is_Left_Out() {
        var (store, _, _) = Open();
        try {
            var def = new GraphQLEndpointDefinition {
                Mode = GraphQLEndpointMode.Selected,
                Types = [new GraphQLTypeDefinition { NodeTypeId = type(store, "User").Id }],
                Views = [new GraphQLViewDefinition { Name = "articles", Query = "Article.Where(a => a.IntegerNum > 1)" }],
            };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            Assert.IsFalse(gql.Schema.QueryType.TryGetField("articles", out _));
            Assert.IsTrue(gql.Warnings.Any(w => w.Contains("articles") && w.Contains("not part of the endpoint")), string.Join(" | ", gql.Warnings));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Unknown_Ids_Are_Reported_Not_Fatal() {
        var (store, _, _) = Open();
        try {
            var article = type(store, "Article");
            var def = new GraphQLEndpointDefinition {
                Mode = GraphQLEndpointMode.Selected,
                Types = [
                    new GraphQLTypeDefinition { NodeTypeId = Guid.NewGuid(), Name = "Ghost" },
                    new GraphQLTypeDefinition { NodeTypeId = article.Id, Properties = [new GraphQLPropertyDefinition { PropertyId = Guid.NewGuid(), Name = "gone" }, new GraphQLPropertyDefinition { PropertyId = prop(article, "Name") }] },
                ],
            };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            Assert.IsTrue(gql.Schema.Types.ContainsKey("Article"));
            Assert.IsTrue(gql.Warnings.Any(w => w.Contains("Ghost")));
            Assert.IsTrue(gql.Warnings.Any(w => w.Contains("gone")));
            RequireData(gql.Execute("{ articles { totalCount } }"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Definition_Json_Round_Trips() {
        var def = new GraphQLEndpointDefinition {
            Id = Guid.NewGuid(), Name = "Public", Url = "/api/graphql", Mode = GraphQLEndpointMode.Selected, ExactNames = true, AllowMutations = true,
            ApiKeys = [new() { Name = "Web", Key = "secret-key", Expires = new DateTime(2027, 10, 7, 0, 0, 0, DateTimeKind.Utc) }, new() { Name = "App", Key = "other-key" }],
            Types = [new GraphQLTypeDefinition { NodeTypeId = Guid.NewGuid(), Name = "Post", ReadOnly = true, Properties = [new GraphQLPropertyDefinition { PropertyId = Guid.NewGuid(), Name = "title" }] }],
            Views = [new GraphQLViewDefinition { Name = "recent", Query = "Article.Where(a => a.IntegerNum > 1)" }],
        };
        var json = def.ToJson();
        StringAssert.Contains(json, "\"mode\": \"Selected\"");
        var back = GraphQLEndpointDefinition.FromJson(json);
        Assert.AreEqual(def.Id, back.Id);
        Assert.AreEqual("Post", back.Types[0].Name);
        Assert.IsTrue(back.Types[0].ReadOnly);
        Assert.AreEqual("title", back.Types[0].Properties![0].Name);
        Assert.AreEqual("recent", back.Views[0].Name);
        Assert.IsTrue(back.ExactNames && back.AllowMutations);
        Assert.AreEqual(2, back.ApiKeys.Count);
        Assert.AreEqual("Web", back.ApiKeys[0].Name);
        Assert.AreEqual("secret-key", back.ApiKeys[0].Key);
        Assert.AreEqual(new DateTime(2027, 10, 7, 0, 0, 0, DateTimeKind.Utc), back.ApiKeys[0].Expires);
        Assert.AreEqual(DateTimeKind.Utc, back.ApiKeys[0].Expires!.Value.Kind);
        Assert.IsNull(back.ApiKeys[1].Expires);
        Assert.IsFalse(json.Contains("\"apiKey\""), "only the list is written");
        // hand-written files may be sparse and commented
        var sparse = GraphQLEndpointDefinition.FromJson("""
            {
              // only what differs from the defaults
              "name": "Everything", "url": "/all", "mode": "WholeDatamodel",
            }
            """);
        Assert.AreEqual(GraphQLEndpointMode.WholeDatamodel, sparse.Mode);
        Assert.AreEqual(25, sparse.DefaultPageSize);
        Assert.IsNotNull(sparse.Types);
        Assert.AreEqual("/all", GraphQLEndpointDefinition.NormalizeUrl(" all/ "));
        Assert.IsNull(GraphQLEndpointDefinition.NormalizeUrl("/"));
        Assert.IsNull(GraphQLEndpointDefinition.NormalizeUrl("a b"));
    }

    [TestMethod]
    public void Definition_Json_Reads_The_Old_Single_Key_Into_The_List() {
        var old = GraphQLEndpointDefinition.FromJson("""{ "name": "Old", "url": "/old", "apiKey": "the-old-key" }""");
        Assert.AreEqual(1, old.ApiKeys.Count);
        Assert.AreEqual(GraphQLEndpointDefinition.LegacyApiKeyName, old.ApiKeys[0].Name);
        Assert.AreEqual("the-old-key", old.ApiKeys[0].Key);
        Assert.IsNull(old.ApiKeys[0].Expires, "the old key never expired");
        Assert.IsTrue(old.RequiresApiKey);
        var written = old.ToJson();
        Assert.IsFalse(written.Contains("\"apiKey\""), written);
        StringAssert.Contains(written, "\"apiKeys\"");

        // a file with both keeps the list and adds the old key only when it is not already in it
        var both = GraphQLEndpointDefinition.FromJson("""{ "name": "Both", "apiKey": "k1-key-1234", "apiKeys": [{ "name": "First", "key": "k1-key-1234" }] }""");
        Assert.AreEqual(1, both.ApiKeys.Count);
        Assert.AreEqual("First", both.ApiKeys[0].Name);

        // an expiry written without a zone is UTC
        var dated = GraphQLEndpointDefinition.FromJson("""{ "name": "Dated", "apiKeys": [{ "name": "A", "key": "a-key-12345", "expires": "2027-01-01" }] }""");
        Assert.AreEqual(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), dated.ApiKeys[0].Expires);
        Assert.AreEqual(DateTimeKind.Utc, dated.ApiKeys[0].Expires!.Value.Kind);

        Assert.IsFalse(GraphQLEndpointDefinition.FromJson("""{ "name": "Open", "apiKey": "" }""").RequiresApiKey);
    }
}
