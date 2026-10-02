using System.Text.Json;
using Relatude.DB.GraphQL;
using Relatude.DB.Nodes;
using Relatude.Utils;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

[TestClass]
public class GraphQLMutationTests {

    static (NodeStore store, RelatudeGraphQL gql, List<Article> all) OpenWritable(GraphQLOptions? options = null) {
        options ??= new GraphQLOptions();
        options.AllowMutations = true;
        return Open(options);
    }

    static Guid userId(NodeStore store, string name) => store.Query<User>().Where(u => u.Username == name).Execute().First().Id;

    [TestMethod]
    public void Schema_Has_Mutations_Only_When_Allowed() {
        var (store, readOnly, _) = Open();
        try {
            Assert.IsNull(readOnly.Schema.MutationType);
            Assert.IsFalse(readOnly.ToSDL().Contains("type Mutation"));
            var writable = new RelatudeGraphQL(store.Datastore, new GraphQLOptions { AllowMutations = true });
            var sdl = writable.ToSDL();
            StringAssert.Contains(sdl, "type Mutation");
            StringAssert.Contains(sdl, "createArticle(input: ArticleInput!): Article");
            StringAssert.Contains(sdl, "updateArticle(id: ID!, input: ArticleInput!): Article");
            StringAssert.Contains(sdl, "deleteArticle(id: ID!): Boolean!");
            StringAssert.Contains(sdl, "input ArticleInput");
            StringAssert.Contains(sdl, "  author: ID");
            StringAssert.Contains(sdl, "  children: [ID!]");
            StringAssert.Contains(sdl, "  size: Sizes");
            Assert.IsFalse(sdl.Contains("  file: FileInfo\n  }"), "files are not writable through the endpoint");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Create_With_Scalars_Enum_And_Relation() {
        var (store, gql, _) = OpenWritable();
        try {
            var alice = userId(store, "alice");
            var data = RequireData(gql.Execute($$"""
                mutation { createArticle(input: { name: "Fresh", integerNum: 99, doubleNum: 2.5, size: Large, author: "{{alice}}" }) {
                    id name integerNum doubleNum size body author { username } } }
                """));
            var created = (Dictionary<string, object?>)data["createArticle"]!;
            Assert.AreEqual("Fresh", created["name"]);
            Assert.AreEqual(99, created["integerNum"]);
            Assert.AreEqual(2.5, created["doubleNum"]);
            Assert.AreEqual("Large", created["size"]);
            Assert.AreEqual("", created["body"], "unset properties take their defaults");
            Assert.AreEqual("alice", Get(created, "author", "username"));
            var id = Guid.Parse((string)created["id"]!);
            var stored = store.Query<Article>().Where(a => a.PId == id).Include(a => a.Author).Execute().Single();
            Assert.AreEqual("Fresh", stored.Name);
            Assert.AreEqual(Sizes.Large, stored.Size);
            Assert.AreEqual("alice", stored.Author?.Username);
            Assert.AreEqual(16, store.Query<Article>().Count());
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Create_Through_Variables() {
        var (store, gql, _) = OpenWritable();
        try {
            var result = gql.Execute(new GraphQLRequest {
                Query = "mutation Create($input: ArticleInput!) { createArticle(input: $input) { name integerNum size } }",
                Variables = JsonSerializer.SerializeToElement(new { input = new { name = "Via variables", integerNum = 7, size = "Medium" } }),
            });
            var data = RequireData(result);
            Assert.AreEqual("Via variables", Get(data, "createArticle", "name"));
            Assert.AreEqual("Medium", Get(data, "createArticle", "size"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Update_Changes_Clears_And_Relates() {
        var (store, gql, all) = OpenWritable();
        try {
            var id = PublicId(store, 5);
            var parentId = PublicId(store, 1);
            var bobBefore = store.Query<Article>().Where(a => a.Id == 5).Include(a => a.Author).Execute().Single().Author?.Username;
            Assert.AreEqual("bob", bobBefore);
            var alice = userId(store, "alice");
            var data = RequireData(gql.Execute($$"""
                mutation { updateArticle(id: "{{id}}", input: { name: "Renamed", body: null, author: "{{alice}}", parent: "{{parentId}}" }) {
                    name body integerNum author { username } parent { integerNum } } }
                """));
            Assert.AreEqual("Renamed", Get(data, "updateArticle", "name"));
            Assert.AreEqual("", Get(data, "updateArticle", "body"), "null clears to the default");
            Assert.AreEqual(5, Get(data, "updateArticle", "integerNum"), "fields left out are unchanged");
            Assert.AreEqual("alice", Get(data, "updateArticle", "author", "username"));
            Assert.AreEqual(1, Get(data, "updateArticle", "parent", "integerNum"));

            // clearing a one-relation and replacing a many-relation
            var child2 = PublicId(store, 2);
            var child3 = PublicId(store, 3);
            var child9 = PublicId(store, 9);
            var cleared = RequireData(gql.Execute($$"""
                mutation { updateArticle(id: "{{id}}", input: { parent: null, children: ["{{child9}}"] }) { parent { id } children { integerNum } } }
                """));
            Assert.IsNull(Get(cleared, "updateArticle", "parent"));
            var children = (List<object?>)Get(cleared, "updateArticle", "children")!;
            Assert.AreEqual(1, children.Count);
            Assert.AreEqual(9, Get(children[0], "integerNum"));
            var replaced = RequireData(gql.Execute($$"""
                mutation { updateArticle(id: "{{id}}", input: { children: ["{{child2}}", "{{child3}}"] }) { children { integerNum } } }
                """));
            var nums = ((List<object?>)Get(replaced, "updateArticle", "children")!).Select(c => (int)Get(c, "integerNum")!).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { 2, 3 }, nums);
            var emptied = RequireData(gql.Execute($$"""mutation { updateArticle(id: "{{id}}", input: { children: [] }) { children { id } } }"""));
            Assert.AreEqual(0, ((List<object?>)Get(emptied, "updateArticle", "children")!).Count);
            Assert.AreEqual(all.Count, store.Query<Article>().Count());
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Delete_Returns_Whether_Something_Was_Deleted() {
        var (store, gql, _) = OpenWritable();
        try {
            var id = PublicId(store, 12);
            var first = RequireData(gql.Execute($$"""mutation { deleteArticle(id: "{{id}}") }"""));
            Assert.AreEqual(true, first["deleteArticle"]);
            Assert.AreEqual(14, store.Query<Article>().Count());
            var second = RequireData(gql.Execute($$"""mutation { deleteArticle(id: "{{id}}") }"""));
            Assert.AreEqual(false, second["deleteArticle"]);
            var missing = RequireData(gql.Execute($$"""mutation { deleteArticle(id: "{{Guid.NewGuid()}}") }"""));
            Assert.AreEqual(false, missing["deleteArticle"]);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Mutation_Errors_Are_Field_Errors() {
        var (store, gql, _) = OpenWritable();
        try {
            var unknown = gql.Execute($$"""mutation { updateArticle(id: "{{Guid.NewGuid()}}", input: { name: "x" }) { id } }""");
            Assert.IsNotNull(unknown.Data);
            Assert.IsNull(unknown.Data!["updateArticle"]);
            StringAssert.Contains(unknown.Errors![0].Message, "exists");

            var badEnum = gql.Execute("mutation { createArticle(input: { size: Huge }) { id } }");
            Assert.IsNull(badEnum.Data!["createArticle"]);
            StringAssert.Contains(badEnum.Errors![0].Message, "Huge");
            Assert.AreEqual(15, store.Query<Article>().Count(), "nothing was written");

            var badId = gql.Execute("mutation { createArticle(input: { author: \"not-a-guid\" }) { id } }");
            Assert.IsNull(badId.Data!["createArticle"]);
            StringAssert.Contains(badId.Errors![0].Message, "not-a-guid");

            var unknownField = gql.Execute("mutation { createArticle(input: { nope: 1 }) { id } }");
            Assert.IsNull(unknownField.Data!["createArticle"]);
            StringAssert.Contains(unknownField.Errors![0].Message, "nope");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void ReadOnly_Types_And_Endpoints() {
        var (store, _, _) = Open();
        try {
            var article = store.Datastore.Datamodel.NodeTypes.Values.First(t => t.CodeName == "Article");
            var user = store.Datastore.Datamodel.NodeTypes.Values.First(t => t.CodeName == "User");
            var def = new GraphQLEndpointDefinition {
                Mode = GraphQLEndpointMode.Selected, AllowMutations = true,
                Types = [new GraphQLTypeDefinition { NodeTypeId = article.Id }, new GraphQLTypeDefinition { NodeTypeId = user.Id, ReadOnly = true }],
            };
            var gql = new RelatudeGraphQL(store.Datastore, def);
            Assert.IsTrue(gql.Schema.MutationType!.TryGetField("createArticle", out _));
            Assert.IsFalse(gql.Schema.MutationType!.TryGetField("createUser", out _));
            var result = gql.Execute("mutation { createUser(input: { username: \"eve\" }) { id } }");
            Assert.IsNull(result.Data);
            StringAssert.Contains(result.Errors![0].Message, "createUser");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Transactions_Go_Through_The_Configured_Executor() {
        var calls = 0;
        var (store, gql, _) = OpenWritable(new GraphQLOptions {
            TransactionExecutor = (s, transaction, ctx) => { calls++; return s.Execute(transaction, null, ctx); },
        });
        try {
            RequireData(gql.Execute("mutation { createArticle(input: { name: \"hooked\" }) { id } }"));
            Assert.AreEqual(1, calls);
            Assert.AreEqual(16, store.Query<Article>().Count());
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Mutations_Execute_Serially_In_Document_Order() {
        var (store, gql, _) = OpenWritable();
        try {
            var id = PublicId(store, 4);
            var data = RequireData(gql.Execute($$"""
                mutation {
                  a: updateArticle(id: "{{id}}", input: { integerNum: 100 }) { integerNum }
                  b: updateArticle(id: "{{id}}", input: { integerNum: 200 }) { integerNum }
                  c: article: updateArticle(id: "{{id}}", input: { name: "last" }) { integerNum name }
                }
                """.Replace("c: article: ", "c: ")));
            Assert.AreEqual(100, Get(data, "a", "integerNum"));
            Assert.AreEqual(200, Get(data, "b", "integerNum"));
            Assert.AreEqual(200, Get(data, "c", "integerNum"));
            Assert.AreEqual("last", Get(data, "c", "name"));
        } finally { store.Dispose(); }
    }
}
