using System.Text;
namespace Relatude.DB.Query.Parsing.Tokens;
public class AnonymousObjectToken : TokenBase {
    public AnonymousObjectToken(string code, int pos1, int pos2) : base(code, pos1, pos2) { }
    public List<string> Names { get; } = new();
    public List<TokenBase> Values { get; } = new();
    static public AnonymousObjectToken Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
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
        var expression = new AnonymousObjectToken(code, startPos, startPos);
        while (true) {
            pos = SkipWhiteSpace(code, pos);
            var startName = pos;
            var endOfName = indexOfAnyOutsideStringLiterals(code, pos, '=', ':');
            var endOfProperty = indexOfAnyOutsideStringLiterals(code, pos, ',', '}');
            if (endOfProperty == -1) throw new SyntaxException("Expected closing curly bracket. ", code, pos);
            string name;
            TokenBase? valueExpression;
            if (endOfName == -1 || endOfProperty < endOfName) {
                // no name decleration, so use member access as name
                valueExpression = ParseStatement(code, pos, out newPos, null, parameters);
                if (valueExpression == null) throw new SyntaxException("Expected expression. ", code, pos);
                if (valueExpression is VariableReferenceToken rs) name = rs.MemberName;
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
    public override TokenTypes TokenType => TokenTypes.AnonymousObject;
    static int indexOfAnyOutsideStringLiterals(string s, int startIndex, params char[] anyOf) {
        return indexOfAnyOutsideStringLiterals(s, startIndex, s.Length - 1, anyOf);
    }
    static int indexOfAnyOutsideStringLiterals(string s, int startIndex, int endIndex, params char[] anyOf) {
        if (s == null) throw new ArgumentNullException(nameof(s));
        if (anyOf == null || anyOf.Length == 0) return -1;
        if (startIndex < 0 || startIndex >= s.Length) throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (endIndex < startIndex || endIndex >= s.Length) throw new ArgumentOutOfRangeException(nameof(endIndex));

        bool insideStringLiteral = false;
        char prevChar = '\0';

        for (int i = startIndex; i <= endIndex; i++) {
            char c = s[i];

            // Handle escape sequences within string literals, like \" or \\
            if (insideStringLiteral && prevChar == '\\') {
                prevChar = c;
                continue;
            }

            // Toggle string literal state when encountering an unescaped "
            if (c == '"') {
                insideStringLiteral = !insideStringLiteral;
            }

            // If outside string literal and character matches any of the target characters, return the index
            if (!insideStringLiteral && anyOf.Contains(c)) {
                return i;
            }

            prevChar = c;
        }

        return -1;
    }
}
