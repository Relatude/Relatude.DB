namespace Relatude.DB.Query.Parsing.Tokens;
public abstract class TokenBase {
    public string Code { get; }
    public int Pos1 { get; }
    public int Pos2 { get; set; }
    protected TokenBase(string code, int pos1, int pos2) {
        Pos1 = pos1;
        Pos1 = pos2;
        Code = code;
    }
    public static TokenBase? ParseStatement(string code, int pos, out int newPos, TokenBase? previous, IEnumerable<Parameter> parameters) {
        do {
            pos = SkipWhiteSpace(code, pos);
            var token = ParseToken(code, pos, out pos, previous, parameters);
            newPos = pos;
            if (token == null) return previous;
            previous = token;
        } while (pos < code.Length);
        return previous;
    }
    public static TokenBase? ParseToken(string code, int pos, out int newPos, TokenBase? previous, IEnumerable<Parameter> parameters) {
        pos = SkipWhiteSpace(code, pos);
        var unitType = whatIsNext(code, pos, previous);
        newPos = pos;
        return unitType switch {
            TokenTypes.Variable => VariableReferenceToken.Parse(code, pos, out newPos, parameters),
            TokenTypes.MethodCall => MethodCallToken.Parse(code, pos, out newPos, previous, parameters),
            TokenTypes.ExpressionBracket => BracketToken.Parse(code, pos, out newPos, parameters),
            TokenTypes.OperatorExpression => OperatorExpressionToken.Parse(code, pos, out newPos, previous, parameters),
            TokenTypes.PreFixOperatorExpression => PreFixSyntax.Parse(code, pos, out newPos, parameters),
            TokenTypes.LambdaDeclaration => LambdaToken.Parse(code, pos, out newPos, parameters),
            TokenTypes.ObjectConstruction => ObjectConstructionToken.Parse(code, pos, out newPos, parameters),
            TokenTypes.AnonymousObject => AnonymousObjectToken.Parse(code, pos, out newPos, parameters),
            TokenTypes.ValueConstant => ValueConstantToken.Parse(code, pos, out newPos, parameters),
            TokenTypes.Empty => null,
            _ => throw new SyntaxException(code, pos),
        };
    }
    internal static bool isNextElement(char firstNoneWhiteSpaceChar, string code, int pos, string word) {
        if (firstNoneWhiteSpaceChar != word[0]) return false; // first char does not match
        if (pos + word.Length > code.Length) return false; // not enough code left for word
        if (code[pos..(pos + word.Length)] == word) return true; // match
        if (pos + word.Length == code.Length) return true; // last expression, so ok
        return false;
        //var c = code[pos + word.Length]; // next char must be whitespace, operator or end bracket
        //return char.IsWhiteSpace(c) || c == ')' || OperatorExpressionSyntax.IsOperatorChar(c);
    }
    protected static TokenTypes whatIsNext(string code, int pos, TokenBase? previous) {
        pos = SkipWhiteSpace(code, pos);
        var firstNoneWhiteSpaceChar = code[pos];
        if (char.IsDigit(firstNoneWhiteSpaceChar) || firstNoneWhiteSpaceChar == '\"') return TokenTypes.ValueConstant;
        if (isNextElement(firstNoneWhiteSpaceChar, code, pos, "false")) return TokenTypes.ValueConstant;
        if (isNextElement(firstNoneWhiteSpaceChar, code, pos, "true")) return TokenTypes.ValueConstant;
        if (isNextElement(firstNoneWhiteSpaceChar, code, pos, "null")) return TokenTypes.ValueConstant;
        if (firstNoneWhiteSpaceChar == '[') return TokenTypes.ValueConstant;
        if (firstNoneWhiteSpaceChar == '(') {
            var pos2 = code.IndexOf(')', pos); // posFirstEndBracket 
            if (pos2 > -1) {
                pos2 = SkipWhiteSpace(code, pos2 + 1); // in case: (...) =>
                if (code.Length - pos2 > 2 && code[pos2..(pos2 + 2)] == "=>") return TokenTypes.LambdaDeclaration;
            }
            return TokenTypes.ExpressionBracket;
        }
        if (firstNoneWhiteSpaceChar == ')') return TokenTypes.Empty;
        if (OperatorExpressionToken.IsOperatorChar(firstNoneWhiteSpaceChar)) {
            if (previous == null) {
                if (firstNoneWhiteSpaceChar == '-' && whatIsNext(code, pos + 1, previous) == TokenTypes.ValueConstant) {
                    // minus infront of number is a constant
                    return TokenTypes.ValueConstant;
                }
                return TokenTypes.PreFixOperatorExpression;
            } else {
                return TokenTypes.OperatorExpression;
            }
        }
        var startFirstWord = pos;
        pos = SkipUntilAfterReferenceIncludingGenerics(code, pos);
        if (pos == startFirstWord) return TokenTypes.Empty;
        if (pos + 1 < code.Length && code[startFirstWord..(pos + 1)] == "new ") {
            pos = SkipWhiteSpace(code, pos);
            if (code[pos] == '{') return TokenTypes.AnonymousObject;
            else return TokenTypes.ObjectConstruction;
        }
        pos = SkipWhiteSpace(code, pos);
        if (code.Length - pos > 2 && code[pos..(pos + 2)] == "=>") return TokenTypes.LambdaDeclaration;
        if (pos < code.Length && code[pos] == '(') return TokenTypes.MethodCall;
        return TokenTypes.Variable;
    }
    protected static int SkipWhiteSpace(string code, int pos) {
        while (pos < code.Length && char.IsWhiteSpace(code[pos])) pos++;
        return pos;
    }
    protected static int SkipUntilAfterReferenceIncludingGenerics(string code, int pos) {
        while (pos < code.Length) {
            var c = code[pos];
            if (char.IsLetterOrDigit(c) || c == '_') {
                pos++;
            } else if (c == '.') {
                pos++;
            } else if (c == '<') {
                var endGenerics = code.IndexOf('>', pos);
                if (endGenerics != -1 && isValidAsGenericArgument(code[pos..endGenerics])) {
                    pos = endGenerics + 1;
                } else {
                    // in this case it will assume < is an operator and not start of generic argument
                }
                return pos;
            } else {
                return pos;
            }
        }
        return pos;
    }
    protected static bool isValidAsGenericArgument(string v) {
        if (v.Length == 0) return false; // must have length
        if (!(char.IsLetter(v[0]) || v[0] == '_')) return false; // must start with _ or letter
        foreach (var c in v) if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != ',' && c != ' ') return false; // can only contain letter or digit or _ or . or , or space
        return true;
    }
    public override string ToString() {
        return string.Empty;// "[" + GetType().Name.Substring(0, 2) + "]";
    }
    public abstract TokenTypes TokenType { get; }
}
