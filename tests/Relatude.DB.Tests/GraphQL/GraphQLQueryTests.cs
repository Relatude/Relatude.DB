using System.Text.Json;
using Relatude.DB.GraphQL;
using Relatude.Utils;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

[TestClass]
public class GraphQLQueryTests {

    [TestMethod]
    public void Scalars_Enums_And_SystemFields() {
        var (store, gql, _) = Open();
        try {
            var id = PublicId(store, 4);
            var data = RequireData(gql.Execute($$"""
                { article(id: "{{id}}") { id name integerNum doubleNum size body createdUtc __typename } }
                """));
            Assert.AreEqual(id.ToString(), Get(data, "article", "id"));
            Assert.AreEqual("Article 04", Get(data, "article", "name"));
            Assert.AreEqual(4, Get(data, "article", "integerNum"));
            Assert.AreEqual(6.0, Get(data, "article", "doubleNum"));
            Assert.AreEqual("Medium", Get(data, "article", "size")); // 4 % 3 == 1 -> Medium
            Assert.AreEqual("body 4", Get(data, "article", "body"));
            Assert.AreEqual("Article", Get(data, "article", "__typename"));
            Assert.IsTrue(DateTime.TryParse((string)Get(data, "article", "createdUtc")!, out _));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Filter_Comparisons_MatchLinq() {
        var (store, gql, all) = Open();
        try {
            var data = RequireData(gql.Execute("""
                { articles(filter: { integerNum: { gt: 5, lte: 15 } }) { totalCount } }
                """));
            var expected = all.Count(a => a.IntegerNum > 5 && a.IntegerNum <= 15);
            Assert.AreEqual(expected, Get(data, "articles", "totalCount"));
            Assert.AreEqual(10, expected);

            // a value that looks like query-language injection is just a parameter
            var injection = RequireData(gql.Execute("""
                { articles(filter: { name: { eq: "x\" || 1 == 1" } }) { totalCount } }
                """));
            Assert.AreEqual(0, Get(injection, "articles", "totalCount"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Filter_And_Or_Not_In() {
        var (store, gql, all) = Open();
        try {
            var data = RequireData(gql.Execute("""
                {
                  either: articles(filter: { or: [ { integerNum: { eq: 1 } }, { integerNum: { eq: 2 } } ] }) { totalCount }
                  negated: articles(filter: { not: { integerNum: { lte: 13 } } }) { totalCount }
                  included: articles(filter: { integerNum: { in: [1, 2, 3] } }) { totalCount }
                  none: articles(filter: { integerNum: { in: [] } }) { totalCount }
                  excluded: articles(filter: { integerNum: { nin: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] } }) { totalCount }
                  combined: articles(filter: { integerNum: { gt: 2 }, and: [ { integerNum: { lt: 5 } } ] }) { totalCount }
                }
                """));
            Assert.AreEqual(2, Get(data, "either", "totalCount"));
            Assert.AreEqual(all.Count(a => a.IntegerNum > 13), Get(data, "negated", "totalCount"));
            Assert.AreEqual(3, Get(data, "included", "totalCount"));
            Assert.AreEqual(0, Get(data, "none", "totalCount"));
            Assert.AreEqual(5, Get(data, "excluded", "totalCount"));
            Assert.AreEqual(2, Get(data, "combined", "totalCount")); // 3 and 4
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Enum_Filter_And_Output() {
        var (store, gql, all) = Open();
        try {
            var data = RequireData(gql.Execute("""
                { articles(filter: { size: { eq: Medium } }, pageSize: 50) { totalCount items { size } } }
                """));
            var expected = all.Count(a => a.Size == Sizes.Medium);
            Assert.AreEqual(expected, Get(data, "articles", "totalCount"));
            var items = (List<object?>)Get(data, "articles", "items")!;
            Assert.IsTrue(items.All(i => (string?)Get(i, "size") == "Medium"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Paging_TotalCount_And_OrderBy() {
        var (store, gql, all) = Open();
        try {
            var data = RequireData(gql.Execute("""
                { articles(orderBy: integerNum, descending: true, page: 1, pageSize: 5) {
                    totalCount pageIndex pageSize items { integerNum } } }
                """));
            Assert.AreEqual(15, Get(data, "articles", "totalCount"));
            Assert.AreEqual(1, Get(data, "articles", "pageIndex"));
            Assert.AreEqual(5, Get(data, "articles", "pageSize"));
            var items = (List<object?>)Get(data, "articles", "items")!;
            var numbers = items.Select(i => (int)Get(i, "integerNum")!).ToArray();
            CollectionAssert.AreEqual(new[] { 10, 9, 8, 7, 6 }, numbers);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void SingleById_Missing_And_IdsArgument() {
        var (store, gql, _) = Open();
        try {
            var id2 = PublicId(store, 2);
            var id5 = PublicId(store, 5);
            var data = RequireData(gql.Execute($$"""
                {
                  found: article(id: "{{id2}}") { name }
                  missing: article(id: "{{Guid.NewGuid()}}") { name }
                  picked: articles(ids: ["{{id2}}", "{{id5}}"]) { totalCount items { integerNum } }
                }
                """));
            Assert.AreEqual("Article 02", Get(data, "found", "name"));
            Assert.IsNull(Get(data, "missing"));
            Assert.AreEqual(2, Get(data, "picked", "totalCount"));
            var nums = ((List<object?>)Get(data, "picked", "items")!).Select(i => (int)Get(i, "integerNum")!).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { 2, 5 }, nums);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Nested_Relations_SingleAndMany_MultiLevel() {
        var (store, gql, _) = Open();
        try {
            var id2 = PublicId(store, 2);
            var id1 = PublicId(store, 1);
            var data = RequireData(gql.Execute($$"""
                {
                  article(id: "{{id2}}") {
                    name
                    author { username group { groupname } }
                    parent { name }
                  }
                  root: article(id: "{{id1}}") {
                    children { name }
                    author { username }
                  }
                }
                """));
            Assert.AreEqual("alice", Get(data, "article", "author", "username")); // article 2 is even -> alice
            Assert.AreEqual("Editors", Get(data, "article", "author", "group", "groupname"));
            Assert.AreEqual("Article 01", Get(data, "article", "parent", "name"));
            Assert.AreEqual("bob", Get(data, "root", "author", "username")); // article 1 is odd -> bob
            var children = ((List<object?>)Get(data, "root", "children")!).Select(c => (string?)Get(c, "name")).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { "Article 02", "Article 03" }, children);
            // unrelated single relation resolves to null (article 1 has no parent)
            var noParent = RequireData(gql.Execute($$"""{ article(id: "{{id1}}") { parent { name } } }"""));
            Assert.IsNull(Get(noParent, "article", "parent"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Interface_InlineFragments_And_Typename() {
        var (store, gql, _) = Open();
        try {
            var data = RequireData(gql.Execute("""
                { articles(pageSize: 50) { items { __typename name ... on Article2 { name2 } } } }
                """));
            var items = (List<object?>)Get(data, "articles", "items")!;
            Assert.AreEqual(15, items.Count, "the Article root includes Article2 descendants");
            var articles = items.Where(i => (string?)Get(i, "__typename") == "Article").ToList();
            var article2s = items.Where(i => (string?)Get(i, "__typename") == "Article2").ToList();
            Assert.AreEqual(12, articles.Count);
            Assert.AreEqual(3, article2s.Count);
            Assert.IsTrue(article2s.All(i => ((string?)Get(i, "name2"))!.StartsWith("extra ")));
            Assert.IsTrue(articles.All(i => !((Dictionary<string, object?>)i!).ContainsKey("name2")));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Fragments_Aliases_Variables_And_SkipDirective() {
        var (store, gql, _) = Open();
        try {
            var id3 = PublicId(store, 3);
            var request = new GraphQLRequest {
                Query = """
                    query Q($id: ID!, $noBody: Boolean!) {
                      first: article(id: $id) { title: name ...details }
                    }
                    fragment details on ArticleInterface {
                      integerNum
                      body @skip(if: $noBody)
                    }
                    """,
                Variables = JsonSerializer.SerializeToElement(new { id = id3.ToString(), noBody = true }),
            };
            var data = RequireData(gql.Execute(request));
            Assert.AreEqual("Article 03", Get(data, "first", "title"));
            Assert.AreEqual(3, Get(data, "first", "integerNum"));
            Assert.IsFalse(((Dictionary<string, object?>)Get(data, "first")!).ContainsKey("body"), "@skip(if: true) removes the field");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void ExecutionTime_IsReportedPerFieldAndPerRequest() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute("""
                { a: articles { durationMs totalCount } b: articles(pageSize: 1) { durationMs } }
                """);
            var data = RequireData(result);
            foreach (var key in new[] { "a", "b" }) {
                var ms = Get(data, key, "durationMs");
                Assert.IsInstanceOfType(ms, typeof(double), $"{key}.durationMs should be a number");
                Assert.IsTrue((double)ms! >= 0, $"{key}.durationMs should not be negative");
            }
            Assert.IsNotNull(result.Extensions);
            var total = (double)result.Extensions!["durationMs"]!;
            Assert.IsTrue(total >= 0);
            // the request covers both fetches plus parsing and projection
            Assert.IsTrue(total >= (double)Get(data, "a", "durationMs")!, "the request total should include the field fetch");
            StringAssert.Contains(result.ToJson(), "\"extensions\"");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void ExecutionTime_IsReportedForFailedRequests() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute("mutation { nope }");
            Assert.IsNull(result.Data);
            Assert.IsNotNull(result.Extensions);
            Assert.IsTrue((double)result.Extensions!["durationMs"]! >= 0);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Search_FiltersByFreeText() {
        var (store, gql, _) = Open();
        try {
            // Article bodies are indexed ("body N"); text indexing is asynchronous, so poll briefly
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var count = 0;
            while (DateTime.UtcNow < deadline) {
                var data = RequireData(gql.Execute("""{ articles(search: "body") { totalCount } }"""));
                count = (int)Get(data, "articles", "totalCount")!;
                if (count > 0) break;
                Thread.Sleep(100);
            }
            Assert.IsTrue(count > 0, "expected the free-text search to match the seeded article bodies");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void RelationFilter_ByRelatedNodeId() {
        var (store, gql, all) = Open();
        try {
            var bob = store.Query<Relatude.Utils.User>().Execute().First(u => u.Username == "bob");
            var data = RequireData(gql.Execute($$"""
                { articles(filter: { author: { eq: "{{bob.Id}}" } }) { totalCount } }
                """));
            Assert.AreEqual(all.Count(a => a.IntegerNum % 2 == 1), Get(data, "articles", "totalCount")); // odd articles -> bob
        } finally { store.Dispose(); }
    }
}
