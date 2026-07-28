using Relatude.DB.GraphQL;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

[TestClass]
public class GraphQLIntrospectionTests {

    // the shape GraphiQL and graphql-codegen send (trimmed of deprecation-only noise)
    const string StandardIntrospectionQuery = """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types { ...FullType }
            directives { name description locations args { ...InputValue } }
          }
        }
        fragment FullType on __Type {
          kind name description
          fields(includeDeprecated: true) {
            name description
            args { ...InputValue }
            type { ...TypeRef }
            isDeprecated deprecationReason
          }
          inputFields { ...InputValue }
          interfaces { ...TypeRef }
          enumValues(includeDeprecated: true) { name description isDeprecated deprecationReason }
          possibleTypes { ...TypeRef }
        }
        fragment InputValue on __InputValue { name description type { ...TypeRef } defaultValue }
        fragment TypeRef on __Type {
          kind name
          ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } }
        }
        """;

    [TestMethod]
    public void StandardIntrospectionQuery_Runs() {
        var (store, gql, _) = Open();
        try {
            var data = RequireData(gql.Execute(StandardIntrospectionQuery));
            Assert.AreEqual("Query", Get(data, "__schema", "queryType", "name"));
            Assert.IsNull(Get(data, "__schema", "mutationType"));
            var types = (List<object?>)Get(data, "__schema", "types")!;
            var names = types.Select(t => (string?)Get(t, "name")).ToList();
            CollectionAssert.Contains(names, "Article");
            CollectionAssert.Contains(names, "Node");
            CollectionAssert.Contains(names, "ArticleFilterInput");
            CollectionAssert.Contains(names, "__Type"); // meta types are included
            var directives = (List<object?>)Get(data, "__schema", "directives")!;
            var directiveNames = directives.Select(d => (string?)Get(d, "name")).ToList();
            CollectionAssert.Contains(directiveNames, "skip");
            CollectionAssert.Contains(directiveNames, "include");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void TypeIntrospection_ResolvesFieldsAndInterfaces() {
        var (store, gql, _) = Open();
        try {
            var data = RequireData(gql.Execute("""
                {
                  __type(name: "Article") {
                    kind name
                    fields { name type { kind name ofType { kind name } } }
                    interfaces { name }
                  }
                  missing: __type(name: "NoSuchType") { name }
                  __typename
                }
                """));
            Assert.AreEqual("OBJECT", Get(data, "__type", "kind"));
            Assert.AreEqual("Query", Get(data, "__typename"));
            Assert.IsNull(Get(data, "missing"));
            var fields = (List<object?>)Get(data, "__type", "fields")!;
            var idField = fields.First(f => (string?)Get(f, "name") == "id");
            Assert.AreEqual("NON_NULL", Get(idField, "type", "kind"));
            Assert.AreEqual("ID", Get(idField, "type", "ofType", "name"));
            var interfaceNames = ((List<object?>)Get(data, "__type", "interfaces")!).Select(i => (string?)Get(i, "name")).ToList();
            CollectionAssert.Contains(interfaceNames, "Node");
            CollectionAssert.Contains(interfaceNames, "ArticleInterface");
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void Introspection_CanBeDisabled() {
        var (store, gql, _) = Open(new GraphQLOptions { EnableIntrospection = false });
        try {
            var result = gql.Execute("{ __schema { queryType { name } } }");
            Assert.IsNull(result.Data);
            StringAssert.Contains(result.Errors![0].Message, "Introspection");
            // normal queries still work
            var ok = gql.Execute("{ articles { totalCount } }");
            Assert.IsNull(ok.Errors);
        } finally { store.Dispose(); }
    }
}
