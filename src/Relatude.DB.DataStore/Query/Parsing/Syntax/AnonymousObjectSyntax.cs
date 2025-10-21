using Relatude.DB.Datamodels;
using Relatude.DB.Query.Parsing.Syntax;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Intrinsics.Arm;
using System.Text;
namespace Relatude.DB.Query.Parsing;
public class AnonymousObjectSyntax : SyntaxUnit {
    public AnonymousObjectSyntax(string code, int pos1, int pos2) : base(code, pos1, pos2) { }
    public List<string> Names { get; } = new();
    public List<SyntaxUnit> Values { get; } = new();
    static public AnonymousObjectSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        // new {a=122,b=3424}
        // new {a:122,b:3424}  // also supported....
        pos = SkipWhiteSpace(code, pos);
        var startPos = pos;
        if (pos + 3 > code.Length) throw new SyntaxException("Code ended unexpectedly. ", code, pos);
        var keyWord = code[pos..(pos + 3)];
        if (keyWord.Trim() != "new") throw new SyntaxException("Expected the new keyword. ", code, pos);
        pos += 3;
        pos = SkipWhiteSpace(code, pos);
        pos = code.IndexOf('{', pos);
        if (pos < -1) throw new SyntaxException("Expected opening curly bracket. ", code, pos);
        if (pos + 1 > code.Length) throw new SyntaxException("Code ended unexpectedly. ", code, pos);
        pos++;
        var expression = new AnonymousObjectSyntax(code, startPos, startPos);
        while (true) {
            pos = SkipWhiteSpace(code, pos);
            var startName = pos;
            var endOfName = code.IndexOfAnyOutsideStringLiterals(pos, '=', ':');
            var endOfProperty = code.IndexOfAnyOutsideStringLiterals(pos, ',', '}');
            if (endOfProperty == -1) throw new SyntaxException("Expected closing curly bracket. ", code, pos);
            string name;
            SyntaxUnit? valueExpression;
            if (endOfName == -1 || endOfProperty < endOfName) {
                // no name decleration, so use member access as name
                valueExpression = ParseStatement(code, pos, out newPos, null, parameters);
                if (valueExpression == null) throw new SyntaxException("Expected expression. ", code, pos);
                if (valueExpression is VariableReferenceSyntax rs) name = rs.MemberName;
                else throw new SyntaxException("Expected expression. ", code, pos);
            } else {
                name = code[startName..endOfName].Trim();
                valueExpression = ParseStatement(code, endOfName + 1, out newPos, null, parameters);
            }
            if (valueExpression == null) throw new SyntaxException("Expected expression. ", code, pos);
            expression.Names.Add(name);
            expression.Values.Add(valueExpression);
            pos = newPos;
            pos = SkipWhiteSpace(code, pos);
            if (pos + 1 > code.Length) throw new SyntaxException("Code ended unexpectedly. ", code, pos);
            var nextChar = code[pos];
            if (nextChar == '}') break; // end of contruction
            if (nextChar != '}' && code[pos] != ',') throw new SyntaxException("Expected closing curly bracket or comma. ", code, pos);
            // must be comma here, so continue..
            pos++;
        }
        newPos = pos + 1;
        expression.Pos2 = newPos;
        return expression;
    }
    public override string ToString() {
        var sb = new StringBuilder();
        sb.Append("new {");
        for (int n = 0; n < Names.Count; n++) {
            sb.Append(Names[n]);
            sb.Append("=");
            sb.Append(Values[n]);
            if (Names.Count - n > 1) sb.Append(", ");
        }
        sb.Append("}");
        return sb.ToString();
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.AnonymousObject;
}
