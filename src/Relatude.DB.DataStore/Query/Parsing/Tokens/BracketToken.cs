namespace Relatude.DB.Query.Parsing.Tokens;
public class BracketToken : TokenBase {
    public BracketToken(TokenBase content, string code, int pos1, int pos2) : base(code, pos1, pos2) {
        Content = content;
    }
    public TokenBase Content { get; }
    static public BracketToken Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        pos = SkipWhiteSpace(code, pos);
        var startPos = pos;
        if (code[pos] != '(') throw new SyntaxException(code, pos);
        pos++; // skip bracket
        var exp = ParseStatement(code, pos, out pos, null, parameters);
        if (exp == null) throw new SyntaxException("Empty bracket. ", code, pos);
        pos = SkipWhiteSpace(code, pos);
        if (code[pos] != ')') throw new SyntaxException("Expected closing bracket. ", code, pos);
        pos++; // skip bracket
        newPos = pos;
        return new BracketToken(exp, code, startPos, newPos);
    }
    public override string ToString() {
        return base.ToString() + "(" + Content.ToString() + ")";
    }
    public override TokenTypes TokenType => TokenTypes.ExpressionBracket;
}

