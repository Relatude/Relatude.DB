using Relatude.DB.Query.Parsing.Syntax;
namespace Relatude.DB.Query.Parsing;
public class LambdaSyntax : SyntaxUnit {
    public LambdaSyntax(string code, int pos1, int pos2) : base(code, pos1, pos2) {
    }
    public List<string>? Paramaters { get; set; }
    public SyntaxUnit? Body { get; set; }
    static public LambdaSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        pos = SkipWhiteSpace(code, pos);
        var expression = new LambdaSyntax(code, pos, pos);
        var usingBrackets = code[pos] == '(';
        if (usingBrackets) {
            pos++;
            pos = SkipWhiteSpace(code, pos);
        }
        var startOfArguments = pos;
        var endOfArguments = usingBrackets ? code.IndexOf(')', pos) : code.IndexOf('=', pos);
        if (endOfArguments - startOfArguments < 1) throw new SyntaxException("Expected lambda function. ", code, pos);
        var arguments = code[startOfArguments..endOfArguments].Split(',', StringSplitOptions.TrimEntries);

        for (int i = 0; i < arguments.Length; i++) arguments[i] = arguments[i].Split(' ').Last(); // removing type, ie: "Article a" to "a"
        expression.Paramaters = arguments.ToList();
        pos = code.IndexOf('>', endOfArguments);
        pos++;
        pos = SkipWhiteSpace(code, pos);
        var usingCurlyBrackets = code[pos] == '{';
        if (usingCurlyBrackets) throw new SyntaxException("Code sections is not supported (yet) in lambda expressions. ", code, pos); // could be added... but using
        if (usingCurlyBrackets) pos++;
        var body = ParseStatement(code, pos, out var endOfFunction, null, parameters);
        if (body == null) throw new NullReferenceException();
        expression.Body = body;
        pos = SkipWhiteSpace(code, endOfFunction);
        if (usingCurlyBrackets) {
            var lastChar = code[pos];
            if (lastChar != '}') throw new SyntaxException("Expected closing curly bracket. ", code, pos);
            pos++;
        }
        newPos = pos;
        expression.Pos2 = newPos;
        return expression;
    }
    public override string ToString() {
        if (Paramaters == null) return base.ToString() + "() => " + Body + "";
        return base.ToString() + "(" + string.Join(", ", Paramaters) + ") => " + Body + "";
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.LambdaDeclaration;
}
