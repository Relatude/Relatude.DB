using Relatude.DB.GraphQL.Language;

namespace Relatude.GraphQL;

[TestClass]
public class GraphQLParserTests {

    [TestMethod]
    public void Parses_Operations_Fragments_Arguments_And_Values() {
        var doc = Parser.Parse("""
            # a comment
            query Articles($page: Int! = 0, $ids: [ID!], $filter: ArticleFilterInput) @cached(ttl: 10) {
              first: articles(page: $page, pageSize: 5, filter: { name: { in: ["a", "b"] }, and: [{ integerNum: { gt: -1.5e2 } }] }) {
                totalCount
                items { ...fields ... on Article2 { name2 } ... @include(if: true) { id } }
              }
              flag: article(id: "x") { size, enabled: __typename }
            }
            fragment fields on Article { id name }
            """);
        Assert.AreEqual(2, doc.Definitions.Count);
        var op = (OperationDefinition)doc.Definitions[0];
        Assert.AreEqual(OperationKind.Query, op.Operation);
        Assert.AreEqual("Articles", op.Name);
        Assert.AreEqual(3, op.Variables.Count);
        Assert.AreEqual("page", op.Variables[0].Name);
        Assert.IsInstanceOfType<NonNullTypeNode>(op.Variables[0].Type);
        Assert.IsInstanceOfType<IntValue>(op.Variables[0].DefaultValue);
        var idsType = (ListTypeNode)op.Variables[1].Type;
        Assert.IsInstanceOfType<NonNullTypeNode>(idsType.Type);
        Assert.AreEqual("cached", op.Directives[0].Name);
        Assert.AreEqual(2, op.SelectionSet.Selections.Count);

        var first = (FieldNode)op.SelectionSet.Selections[0];
        Assert.AreEqual("first", first.Alias);
        Assert.AreEqual("articles", first.Name);
        Assert.AreEqual("first", first.ResponseKey);
        Assert.AreEqual(3, first.Arguments.Count);
        Assert.IsInstanceOfType<VariableValue>(first.Arguments[0].Value);
        var filter = (ObjectValue)first.Arguments[2].Value;
        Assert.AreEqual("name", filter.Fields[0].Name);
        var inList = (ListValue)((ObjectValue)filter.Fields[0].Value).Fields[0].Value;
        Assert.AreEqual(2, inList.Values.Count);
        Assert.AreEqual("a", ((StringValue)inList.Values[0]).Value);
        var and = (ListValue)filter.Fields[1].Value;
        var gt = (FloatValue)((ObjectValue)((ObjectValue)and.Values[0]).Fields[0].Value).Fields[0].Value;
        Assert.AreEqual("-1.5e2", gt.Text);

        var items = (FieldNode)first.SelectionSet!.Selections[1];
        Assert.IsInstanceOfType<FragmentSpread>(items.SelectionSet!.Selections[0]);
        var inline = (InlineFragment)items.SelectionSet.Selections[1];
        Assert.AreEqual("Article2", inline.TypeCondition);
        var bare = (InlineFragment)items.SelectionSet.Selections[2];
        Assert.IsNull(bare.TypeCondition);
        Assert.AreEqual("include", bare.Directives[0].Name);
        Assert.IsTrue(((BooleanValue)bare.Directives[0].Arguments[0].Value).Value);

        var flag = (FieldNode)op.SelectionSet.Selections[1];
        Assert.AreEqual("enabled", ((FieldNode)flag.SelectionSet!.Selections[1]).Alias);
        Assert.AreEqual("__typename", ((FieldNode)flag.SelectionSet!.Selections[1]).Name);

        var frag = (FragmentDefinition)doc.Definitions[1];
        Assert.AreEqual("fields", frag.Name);
        Assert.AreEqual("Article", frag.TypeCondition);
    }

    [TestMethod]
    public void Anonymous_Query_And_Mutation_Keywords() {
        var anonymous = (OperationDefinition)Parser.Parse("{ a }").Definitions[0];
        Assert.AreEqual(OperationKind.Query, anonymous.Operation);
        Assert.IsNull(anonymous.Name);
        var mutation = (OperationDefinition)Parser.Parse("mutation { createArticle(input: { name: null, ok: true, size: Large }) { id } }").Definitions[0];
        Assert.AreEqual(OperationKind.Mutation, mutation.Operation);
        var input = (ObjectValue)((FieldNode)mutation.SelectionSet.Selections[0]).Arguments[0].Value;
        Assert.IsInstanceOfType<NullValue>(input.Fields[0].Value);
        Assert.IsInstanceOfType<BooleanValue>(input.Fields[1].Value);
        Assert.AreEqual("Large", ((EnumValue)input.Fields[2].Value).Name);
        var subscription = (OperationDefinition)Parser.Parse("subscription S { a }").Definitions[0];
        Assert.AreEqual(OperationKind.Subscription, subscription.Operation);
    }

    [TestMethod]
    public void Strings_Escapes_And_BlockStrings() {
        var doc = Parser.Parse("{ a(s: \"tab\\there \\\"quoted\\\" \\u00e9 \\uD83D\\uDE00 \\u{1F600}\", b: \"\"\"\n      Hello,\n        World!\n\n      Yours,\n        GraphQL.\n      \"\"\") }");
        var field = (FieldNode)((OperationDefinition)doc.Definitions[0]).SelectionSet.Selections[0];
        var s = ((StringValue)field.Arguments[0].Value).Value;
        Assert.AreEqual("tab\there \"quoted\" " + (char)0xE9 + " " + char.ConvertFromUtf32(0x1F600) + " " + char.ConvertFromUtf32(0x1F600), s);
        var block = ((StringValue)field.Arguments[1].Value).Value;
        Assert.AreEqual("Hello,\n  World!\n\nYours,\n  GraphQL.", block);
        var escaped = Parser.Parse("{ a(s: \"\"\"a \\\"\"\" b\"\"\") }");
        Assert.AreEqual("a \"\"\" b", ((StringValue)((FieldNode)((OperationDefinition)escaped.Definitions[0]).SelectionSet.Selections[0]).Arguments[0].Value).Value);
    }

    [TestMethod]
    public void Numbers_Are_Classified() {
        var doc = Parser.Parse("{ a(i: 0, j: -12, f: 1.5, g: 2e3, h: -0.5E-2) }");
        var args = ((FieldNode)((OperationDefinition)doc.Definitions[0]).SelectionSet.Selections[0]).Arguments;
        Assert.IsInstanceOfType<IntValue>(args[0].Value);
        Assert.AreEqual("-12", ((IntValue)args[1].Value).Text);
        Assert.IsInstanceOfType<FloatValue>(args[2].Value);
        Assert.IsInstanceOfType<FloatValue>(args[3].Value);
        Assert.AreEqual("-0.5E-2", ((FloatValue)args[4].Value).Text);
    }

    [DataTestMethod]
    [DataRow("", "empty")]
    [DataRow("{ a(s: \"unterminated) }", "unterminated string")]
    [DataRow("{ a(i: 01) }", "invalid number")]
    [DataRow("{ a(i: 1.) }", "invalid number")]
    [DataRow("{ a(i: 1a) }", "invalid number")]
    [DataRow("{ a ", "end of document")]
    [DataRow("{ a(b: ) }", "expected a value")]
    [DataRow("{ a } }", "unexpected")]
    [DataRow("query { }", "at least one field")]
    [DataRow("type Foo { a: Int }", "type system")]
    [DataRow("{ a(s: \"bad \\q escape\") }", "invalid escape")]
    [DataRow("{ a(x: $v) } fragment on on Article { id }", "may not be named")]
    [DataRow("{ a @ }", "expected a name")]
    [DataRow("{ ~ }", "unexpected character")]
    [DataRow("{ a(f: { { eq: 1 } }) }", "each value inside { } needs a name")]
    [DataRow("{ a(f: { b { eq: 1 } }) }", "expected \":\" after \"b\"")]
    [DataRow("{ a(f { eq: 1 }) }", "expected \":\" after \"f\"")]
    public void Syntax_Errors_Are_Reported_With_Position(string text, string expected) {
        var ex = Assert.ThrowsExactly<GraphQLSyntaxException>(() => Parser.Parse(text));
        StringAssert.Contains(ex.Message.ToLowerInvariant(), expected);
        Assert.IsTrue(ex.Line >= 1 && ex.Column >= 1, ex.Message);
    }

    [TestMethod]
    public void Positions_Map_To_Lines_And_Columns() {
        var doc = Parser.Parse("{\n  a\n  b(c: 1)\n}");
        var op = (OperationDefinition)doc.Definitions[0];
        var b = (FieldNode)op.SelectionSet.Selections[1];
        Assert.AreEqual((3, 3), doc.LineColumn(b.Position));
        Assert.AreEqual((3, 5), doc.LineColumn(b.Arguments[0].Position));
        var ex = Assert.ThrowsExactly<GraphQLSyntaxException>(() => Parser.Parse("{\r\n  a\r\n  b(c: ) }"));
        Assert.AreEqual(3, ex.Line);
        Assert.AreEqual(8, ex.Column);
    }

    [TestMethod]
    public void Commas_And_Bom_Are_Ignored() {
        var doc = Parser.Parse((char)0xFEFF + "{ a, b,, c }");
        Assert.AreEqual(3, ((OperationDefinition)doc.Definitions[0]).SelectionSet.Selections.Count);
    }
}
