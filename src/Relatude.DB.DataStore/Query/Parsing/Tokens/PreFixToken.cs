using System.Text;
namespace Relatude.DB.Query.Parsing.Tokens;
public class PreFixSyntax : TokenBase {
    public PreFixSyntax(string prefix, TokenBase value, string code, int pos1)
        : base(code, pos1, pos1) {
        Value = value;
        Prefix = prefix;
    }
    public static bool IsOperatorChar(char c) {
        return c == '!' || c == '-';
    }
    public TokenBase Value { get; }
    public string Prefix { get; } // + - * /
    public bool HasEvaluatedOperatorOrder;
    static public PreFixSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        var startPosOp = pos;
        while (pos < code.Length && IsOperatorChar(code[pos])) pos++;
        var prefix = code[startPosOp..pos];
        pos = SkipWhiteSpace(code, pos);
        var nextUnit = ParseToken(code, pos, out newPos, null, parameters);
        if (nextUnit == null) throw new SyntaxException("No value after prefix operator. ", code, pos);
        var opEx = new PreFixSyntax(prefix, nextUnit, code, pos);
        opEx.Pos2 = newPos;
        return opEx;
    }
    public override string ToString() {
        var sb = new StringBuilder();
        sb.Append(Prefix);
        sb.Append(Value);
        return sb.ToString();
    }
    public override TokenTypes TokenType => TokenTypes.PreFixOperatorExpression;
}
