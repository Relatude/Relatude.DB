using System.Text.Json;
using Relatude.DB.GraphQL;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

[TestClass]
public class GraphQLErrorTests {

    [TestMethod]
    public void Mutations_AreRejected() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute("mutation { createArticle(name: \"x\") { id } }");
            Assert.IsNull(result.Data);
            Assert.IsNotNull(result.Errors);
            StringAssert.Contains(result.Errors![0].Message, "read-only");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void UnknownField_FailsValidation_WithLocation() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute("{ articles { items { nope } } }");
            Assert.IsNull(result.Data);
            Assert.IsNotNull(result.Errors);
            StringAssert.Contains(result.Errors![0].Message, "nope");
            Assert.IsNotNull(result.Errors[0].Locations, "validation errors carry line/column");
            Assert.IsTrue(result.Errors[0].Locations![0].Line >= 1);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void SyntaxError_IsReported() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute("{ articles { ");
            Assert.IsNull(result.Data);
            Assert.IsNotNull(result.Errors);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void FragmentCycle_IsRejected() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute("""
                { articles { items { ...a } } }
                fragment a on Article { ...b }
                fragment b on Article { ...a }
                """);
            Assert.IsNull(result.Data);
            StringAssert.Contains(result.Errors![0].Message, "cycle");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void QueryDepth_IsCapped() {
        var (store, gql, _) = Open(new GraphQLOptions { MaxQueryDepth = 3 });
        try {
            var ok = gql.Execute("{ articles { totalCount } }");
            Assert.IsNull(ok.Errors);
            var tooDeep = gql.Execute("{ articles { items { children { children { name } } } } }");
            Assert.IsNull(tooDeep.Data);
            StringAssert.Contains(tooDeep.Errors![0].Message, "depth");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void BadEnumLiteral_BecomesFieldError() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute("{ articles(filter: { size: { eq: Huge } }) { totalCount } }");
            Assert.IsNotNull(result.Data);
            Assert.IsNull(result.Data!["articles"], "the failed root field is null (partial data)");
            Assert.IsNotNull(result.Errors);
            StringAssert.Contains(result.Errors![0].Message, "Huge");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void VariableTypeMismatch_IsARequestError() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute(new GraphQLRequest {
                Query = "query Q($n: Int!) { articles(filter: { integerNum: { eq: $n } }) { totalCount } }",
                Variables = JsonSerializer.SerializeToElement(new { n = "abc" }),
            });
            Assert.IsNull(result.Data);
            StringAssert.Contains(result.Errors![0].Message, "$n");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void MissingRequiredVariable_IsARequestError() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute("query Q($id: ID!) { article(id: $id) { name } }");
            Assert.IsNull(result.Data);
            StringAssert.Contains(result.Errors![0].Message, "$id");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void UnknownArgument_FailsValidation() {
        var (store, gql, _) = Open();
        try {
            var result = gql.Execute("{ articles(bogus: 1) { totalCount } }");
            Assert.IsNull(result.Data);
            StringAssert.Contains(result.Errors![0].Message, "bogus");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void SelectionShape_IsValidated() {
        var (store, gql, _) = Open();
        try {
            var noSelection = gql.Execute("{ articles { items } }"); // composite without subselection
            Assert.IsNull(noSelection.Data);
            var scalarSelection = gql.Execute("{ articles { totalCount { x } } }"); // scalar with subselection
            Assert.IsNull(scalarSelection.Data);
        } finally { store.Dispose(); }
    }
}
