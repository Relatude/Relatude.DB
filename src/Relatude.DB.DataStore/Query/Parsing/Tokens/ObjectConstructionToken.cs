namespace Relatude.DB.Query.Parsing.Tokens;
public class ObjectConstructionToken : TokenBase {
    public ObjectConstructionToken(string typeName, string code, int pos1, int pos2) : base(code, pos1, pos2) {
        TypeName = typeName;
    }
    public string TypeName { get; }
    public List<string>? GenericParams { get; set; }
    public List<TokenBase> Arguments { get; set; } = new();
    static public ObjectConstructionToken Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        var pos1 = pos;
        pos = SkipWhiteSpace(code, pos);
        var keyWord = code[pos..(pos + 4)];
        if (keyWord != "new ") throw new SyntaxException("Expected the new keyword. ", code, pos);
        pos += 4;
        pos = SkipWhiteSpace(code, pos);
        var startPos = pos;
        while (pos < code.Length && (char.IsLetterOrDigit(code[pos]) || code[pos] == '.' || code[pos] == '_')) pos++;
        var typeName = code[startPos..pos];
        var nextChar = code[pos];
        var expression = new ObjectConstructionToken(typeName, code, pos1, pos1);
        if (nextChar == '<') {
            var startGenerics = pos + 1;
            while (pos < code.Length && code[pos] == '>') pos++;
            var genericArgs = code[startGenerics..pos];
            expression.GenericParams = genericArgs.Split(',', StringSplitOptions.TrimEntries).ToList();
            nextChar = code[pos + 1];
        }
        if (nextChar != '(') throw new SyntaxException("Was expecting bracket after method name. ", code, pos);
        while (true) {
            pos++;
            var arg = ParseStatement(code, pos, out pos, null, parameters);
            if (arg == null) throw new SyntaxException("Invalid argument. ", code, pos);
            expression.Arguments.Add(arg);
            pos = SkipWhiteSpace(code, pos);
            if (pos > code.Length) throw new SyntaxException("Code ended unexpectedly. ", code, pos);
            nextChar = code[pos];
            if (nextChar == ')') break;
            if (nextChar != ',') throw new SyntaxException("Expected comma. ", code, pos);
        }
        pos++;
        newPos = pos;
        expression.Pos2 = newPos;
        return expression;
    }
    public override string ToString() {
        return base.ToString() + "new" + TypeName
            + (GenericParams != null && GenericParams.Count > 0 ? "<" + string.Join(',', GenericParams) + ">" : "")
            + "(" + string.Join(',', Arguments) + ")";
    }
    public override TokenTypes TokenType => TokenTypes.ObjectConstruction;
}
