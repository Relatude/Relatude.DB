namespace Relatude.DB.GraphQL.Language;

/// <summary>Recursive-descent parser for GraphQL executable documents (operations and fragments).</summary>
public sealed class Parser {
    readonly string _src;
    readonly Lexer _lexer;
    Token _tok;

    Parser(string source) {
        _src = source;
        _lexer = new Lexer(source);
        _tok = _lexer.Next();
    }

    /// <summary>Parses a document; throws <see cref="GraphQLSyntaxException"/> on the first error.</summary>
    public static Document Parse(string source) => new Parser(source).parseDocument();

    GraphQLSyntaxException error(string message) {
        var (line, column) = Lexer.LineColumn(_src, _tok.Start);
        return new GraphQLSyntaxException($"Syntax error: {message} (line {line}, column {column}).", line, column);
    }

    string describe(Token t) => t.Kind switch {
        TokenKind.EndOfFile => "end of document",
        TokenKind.Name => $"name \"{t.Value}\"",
        TokenKind.Int or TokenKind.Float => $"number \"{t.Value}\"",
        TokenKind.String or TokenKind.BlockString => "string",
        _ => $"\"{t.Value}\"",
    };

    void advance() => _tok = _lexer.Next();

    bool peek(TokenKind kind) => _tok.Kind == kind;

    bool skip(TokenKind kind) {
        if (_tok.Kind != kind) return false;
        advance();
        return true;
    }

    Token expect(TokenKind kind, string what) {
        if (_tok.Kind != kind) throw error($"expected {what} but found {describe(_tok)}");
        var t = _tok;
        advance();
        return t;
    }

    string expectName() => expect(TokenKind.Name, "a name").Value;

    bool peekKeyword(string keyword) => _tok.Kind == TokenKind.Name && _tok.Value == keyword;

    void expectKeyword(string keyword) {
        if (!peekKeyword(keyword)) throw error($"expected \"{keyword}\" but found {describe(_tok)}");
        advance();
    }

    Document parseDocument() {
        var doc = new Document { Source = _src, Position = 0 };
        if (peek(TokenKind.EndOfFile)) throw error("the document is empty");
        while (!peek(TokenKind.EndOfFile)) doc.Definitions.Add(parseDefinition());
        return doc;
    }

    Definition parseDefinition() {
        if (peek(TokenKind.BraceL)) return parseOperation(OperationKind.Query, anonymous: true);
        if (peek(TokenKind.Name)) {
            switch (_tok.Value) {
                case "query": return parseOperation(OperationKind.Query, anonymous: false);
                case "mutation": return parseOperation(OperationKind.Mutation, anonymous: false);
                case "subscription": return parseOperation(OperationKind.Subscription, anonymous: false);
                case "fragment": return parseFragmentDefinition();
                case "schema" or "scalar" or "type" or "interface" or "union" or "enum" or "input" or "directive" or "extend":
                    throw error($"type system definitions (\"{_tok.Value}\") are not accepted here");
            }
        }
        throw error($"unexpected {describe(_tok)}");
    }

    OperationDefinition parseOperation(OperationKind kind, bool anonymous) {
        var start = _tok.Start;
        string? name = null;
        var variables = new List<VariableDefinition>();
        var directives = new List<Directive>();
        if (!anonymous) {
            advance(); // the operation keyword
            if (peek(TokenKind.Name)) name = expectName();
            if (peek(TokenKind.ParenL)) variables = parseVariableDefinitions();
            directives = parseDirectives();
        }
        var op = new OperationDefinition { Position = start, Operation = kind, Name = name, SelectionSet = parseSelectionSet() };
        op.Variables.AddRange(variables);
        op.Directives.AddRange(directives);
        return op;
    }

    List<VariableDefinition> parseVariableDefinitions() {
        var list = new List<VariableDefinition>();
        expect(TokenKind.ParenL, "\"(\"");
        if (peek(TokenKind.ParenR)) throw error("expected a variable definition");
        while (!skip(TokenKind.ParenR)) {
            var start = _tok.Start;
            expect(TokenKind.Dollar, "\"$\"");
            var name = expectName();
            expect(TokenKind.Colon, "\":\"");
            var type = parseType();
            ValueNode? defaultValue = null;
            if (skip(TokenKind.Equals)) defaultValue = parseValue(constant: true);
            var def = new VariableDefinition { Position = start, Name = name, Type = type, DefaultValue = defaultValue };
            def.Directives.AddRange(parseDirectives());
            list.Add(def);
        }
        return list;
    }

    TypeNode parseType() {
        var start = _tok.Start;
        TypeNode type;
        if (skip(TokenKind.BracketL)) {
            var inner = parseType();
            expect(TokenKind.BracketR, "\"]\"");
            type = new ListTypeNode { Position = start, Type = inner };
        } else {
            type = new NamedTypeNode { Position = start, Name = expectName() };
        }
        if (skip(TokenKind.Bang)) type = new NonNullTypeNode { Position = start, Type = type };
        return type;
    }

    FragmentDefinition parseFragmentDefinition() {
        var start = _tok.Start;
        expectKeyword("fragment");
        if (peekKeyword("on")) throw error("a fragment may not be named \"on\"");
        var name = expectName();
        expectKeyword("on");
        var typeCondition = expectName();
        var directives = parseDirectives();
        var frag = new FragmentDefinition { Position = start, Name = name, TypeCondition = typeCondition, SelectionSet = parseSelectionSet() };
        frag.Directives.AddRange(directives);
        return frag;
    }

    SelectionSet parseSelectionSet() {
        var set = new SelectionSet { Position = _tok.Start };
        expect(TokenKind.BraceL, "\"{\"");
        if (peek(TokenKind.BraceR)) throw error("a selection set must select at least one field");
        while (!skip(TokenKind.BraceR)) {
            if (peek(TokenKind.EndOfFile)) throw error("expected \"}\" but found end of document");
            set.Selections.Add(parseSelection());
        }
        return set;
    }

    Selection parseSelection() {
        var start = _tok.Start;
        if (skip(TokenKind.Spread)) {
            if (peek(TokenKind.Name) && _tok.Value != "on") {
                var spread = new FragmentSpread { Position = start, Name = expectName() };
                spread.Directives.AddRange(parseDirectives());
                return spread;
            }
            string? typeCondition = null;
            if (peekKeyword("on")) {
                advance();
                typeCondition = expectName();
            }
            var directives = parseDirectives();
            var inline = new InlineFragment { Position = start, TypeCondition = typeCondition, SelectionSet = parseSelectionSet() };
            inline.Directives.AddRange(directives);
            return inline;
        }
        var first = expectName();
        string? alias = null;
        var name = first;
        if (skip(TokenKind.Colon)) {
            alias = first;
            name = expectName();
        }
        var field = new FieldNode { Position = start, Alias = alias, Name = name };
        if (peek(TokenKind.ParenL)) field.Arguments.AddRange(parseArguments(constant: false));
        field.Directives.AddRange(parseDirectives());
        if (peek(TokenKind.BraceL)) field.SelectionSet = parseSelectionSet();
        return field;
    }

    List<Argument> parseArguments(bool constant) {
        var list = new List<Argument>();
        expect(TokenKind.ParenL, "\"(\"");
        if (peek(TokenKind.ParenR)) throw error("expected an argument");
        while (!skip(TokenKind.ParenR)) {
            var start = _tok.Start;
            var name = expectName();
            expect(TokenKind.Colon, $"\":\" after \"{name}\"");
            list.Add(new Argument { Position = start, Name = name, Value = parseValue(constant) });
        }
        return list;
    }

    List<Directive> parseDirectives() {
        var list = new List<Directive>();
        while (peek(TokenKind.At)) {
            var start = _tok.Start;
            advance();
            var d = new Directive { Position = start, Name = expectName() };
            if (peek(TokenKind.ParenL)) d.Arguments.AddRange(parseArguments(constant: false));
            list.Add(d);
        }
        return list;
    }

    ValueNode parseValue(bool constant) {
        var start = _tok.Start;
        switch (_tok.Kind) {
            case TokenKind.Dollar: {
                    if (constant) throw error("a variable is not allowed in a constant value");
                    advance();
                    return new VariableValue { Position = start, Name = expectName() };
                }
            case TokenKind.BracketL: {
                    advance();
                    var list = new ListValue { Position = start };
                    while (!skip(TokenKind.BracketR)) {
                        if (peek(TokenKind.EndOfFile)) throw error("expected \"]\" but found end of document");
                        list.Values.Add(parseValue(constant));
                    }
                    return list;
                }
            case TokenKind.BraceL: {
                    advance();
                    var obj = new ObjectValue { Position = start };
                    while (!skip(TokenKind.BraceR)) {
                        if (peek(TokenKind.EndOfFile)) throw error("expected \"}\" but found end of document");
                        var fieldStart = _tok.Start;
                        // "{ { eq: 1 } }" is a common slip: say what an input object holds
                        if (!peek(TokenKind.Name)) throw error($"expected an input field name but found {describe(_tok)}: each value inside {{ }} needs a name in front, as in {{ eq: \"...\" }}");
                        var name = expectName();
                        expect(TokenKind.Colon, $"\":\" after \"{name}\"");
                        obj.Fields.Add(new ObjectField { Position = fieldStart, Name = name, Value = parseValue(constant) });
                    }
                    return obj;
                }
            case TokenKind.Int: { var t = _tok; advance(); return new IntValue { Position = start, Text = t.Value }; }
            case TokenKind.Float: { var t = _tok; advance(); return new FloatValue { Position = start, Text = t.Value }; }
            case TokenKind.String:
            case TokenKind.BlockString: { var t = _tok; advance(); return new StringValue { Position = start, Value = t.Value }; }
            case TokenKind.Name: {
                    var t = _tok;
                    advance();
                    return t.Value switch {
                        "true" => new BooleanValue { Position = start, Value = true },
                        "false" => new BooleanValue { Position = start, Value = false },
                        "null" => new NullValue { Position = start },
                        _ => new EnumValue { Position = start, Name = t.Value },
                    };
                }
            default:
                throw error($"expected a value but found {describe(_tok)}");
        }
    }
}
