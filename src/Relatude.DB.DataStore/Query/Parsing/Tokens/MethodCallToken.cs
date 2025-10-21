namespace Relatude.DB.Query.Parsing.Tokens;
public class MethodCallToken : TokenBase {
    public MethodCallToken(string code, int pos1, int pos2) : base(code, pos1, pos2) { }
    public TokenBase? Subject { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> GenericParams { get; set; } = new();
    public List<TokenBase> Arguments { get; set; } = new();
    static public MethodCallToken Parse(string code, int pos, out int newPos, TokenBase? subject, IEnumerable<Parameter> parameters) {
        //if (subject == null) throw new SyntaxException("Unable to determine method object reference. ", code, pos);
        pos = SkipWhiteSpace(code, pos);
        if (code[pos] == '.') pos++;
        var startPos = pos;
        while (pos < code.Length && (char.IsLetterOrDigit(code[pos]) || code[pos] == '_' || code[pos] == '.')) pos++;
        var methodName = code[startPos..pos];
        var lastDotInName = methodName.LastIndexOf('.');
        if (lastDotInName != -1) {
            var refName = methodName.Substring(0, lastDotInName);
            if (subject == null) {
                subject = new VariableReferenceToken(refName, code, startPos, startPos + refName.Length);
                methodName = methodName.Substring(lastDotInName + 1);
            } else {
                throw new Exception("Unknown error");
            }
        }
        var expression = new MethodCallToken(code, startPos, startPos) { Name = methodName, Subject = subject };
        var nextChar = code[pos];
        if (nextChar == '<') {
            var startGenerics = pos + 1;
            var posClosingGenerics = code.IndexOf('>', pos);
            if (posClosingGenerics == -1) throw new SyntaxException("Generic parameter not closed. ", code, pos);
            pos = posClosingGenerics;
            var genericArgs = code[startGenerics..pos];
            expression.GenericParams = genericArgs.Split(',', StringSplitOptions.TrimEntries).ToList();
            pos++; // skip >
            pos = SkipWhiteSpace(code, pos);
            nextChar = code[pos];
        }
        if (nextChar != '(') throw new SyntaxException("Was expecting bracket after method name. ", code, pos);
        pos++; // skip bracket
        pos = SkipWhiteSpace(code, pos);
        while (true) {
            pos = SkipWhiteSpace(code, pos);
            nextChar = code[pos];
            if (nextChar == ',') pos = SkipWhiteSpace(code, pos + 1);
            else if (nextChar == ')') break;
            var pa = ParseStatement(code, pos, out pos, null, parameters);
            if (pa == null) break;
            expression.Arguments.Add(pa);
        }
        pos = SkipWhiteSpace(code, pos);
        pos++; // skip bracket
        newPos = pos;
        expression.Pos2 = newPos;
        return expression;
    }
    public override string ToString() {
        return base.ToString() + (Subject == null ? "" : Subject + ".") + Name + (GenericParams.Count > 0 ? "<" + string.Join(", ", GenericParams) + ">" : "") + "(" + string.Join(", ", Arguments) + ")";
    }
    public override TokenTypes TokenType => TokenTypes.MethodCall;
}
