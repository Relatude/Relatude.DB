using Relatude.DB.GraphQL;

namespace Relatude.GraphQL;

[TestClass]
public class GraphQLSchemaTests {

    [TestMethod]
    public void Schema_GeneratesExpectedTypesAndSdl() {
        var (store, gql, _) = GraphQLTestHelper.Open();
        try {
            var sdl = gql.ToSDL();
            StringAssert.Contains(sdl, "type Query");
            StringAssert.Contains(sdl, "interface Node");
            // Article has a concrete descendant (Article2), so it gets a synthesized interface
            StringAssert.Contains(sdl, "interface ArticleInterface");
            StringAssert.Contains(sdl, "type Article implements");
            StringAssert.Contains(sdl, "type Article2 implements");
            StringAssert.Contains(sdl, "enum Sizes");
            StringAssert.Contains(sdl, "input ArticleFilterInput");
            StringAssert.Contains(sdl, "article(id: ID!)");
            StringAssert.Contains(sdl, "integerNum: Int!");
            StringAssert.Contains(sdl, "scalar DateTime");

            Assert.IsTrue(gql.Schema.Types.ContainsKey("Article"));
            Assert.IsTrue(gql.Schema.Types.ContainsKey("Article2"));
            Assert.IsTrue(gql.Schema.Types.ContainsKey("User"));
            Assert.IsTrue(gql.Schema.Types.ContainsKey("Group"));
            Assert.IsTrue(gql.Schema.Types.ContainsKey("Sizes"));
            // system node types are excluded by default
            Assert.IsFalse(gql.Schema.Types.ContainsKey("ISystemUser"));

            // root fields for every exposed type
            Assert.IsTrue(gql.Schema.QueryType.TryGetField("article", out _));
            Assert.IsTrue(gql.Schema.QueryType.TryGetField("articles", out _));
            Assert.IsTrue(gql.Schema.QueryType.TryGetField("users", out _));
            Assert.IsTrue(gql.Schema.QueryType.TryGetField("groups", out _));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Schema_SdlIsStableAcrossBuilds() {
        var (store1, gql1, _) = GraphQLTestHelper.Open();
        var sdl1 = gql1.ToSDL();
        store1.Dispose();
        var (store2, gql2, _) = GraphQLTestHelper.Open();
        var sdl2 = gql2.ToSDL();
        store2.Dispose();
        Assert.AreEqual(sdl1, sdl2, "Schema generation must be deterministic.");
    }
}
