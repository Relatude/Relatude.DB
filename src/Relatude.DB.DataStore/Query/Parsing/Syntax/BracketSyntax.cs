using Relatude.DB.Query.Parsing.Syntax;
namespace Relatude.DB.Query.Parsing;
public class BracketSyntax : SyntaxUnit {
    public BracketSyntax(SyntaxUnit content, string code, int pos1, int pos2) : base(code, pos1, pos2) {
        Content = content;
    }
    public SyntaxUnit Content { get; }
    static public BracketSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
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
        return new BracketSyntax(exp, code, startPos, newPos);
    }
    public override string ToString() {
        return base.ToString() + "(" + Content.ToString() + ")";
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.ExpressionBracket;
}

