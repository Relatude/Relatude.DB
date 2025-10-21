using Relatude.DB.Datamodels;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Intrinsics.Arm;
using System.Text;
namespace Relatude.DB.Query.Parsing.Syntax;
/// <summary>
/// This is the first step in parsing a query
/// It will parse the string and return a tree of primitive syntax units like:
/// Values, Methods, Lamdas Exp, Anonymous constructions etc.
/// It is focused on parsing the query. It is comparable to a tokenizer in a normal database query parser.
/// </summary>
public abstract class SyntaxTree {
    static public SyntaxUnit Parse(string code, IEnumerable<Parameter> parameters) {
        if (code == null) throw new ArgumentNullException("Query code is null. ");
        code = code.Trim();
        if (code.Length == 0) throw new Exception("Query code is empty. ");
        var unit = SyntaxUnit.ParseStatement(code, 0, out _, null, parameters);
        if (unit == null) throw new Exception("The result of the query parsing is empty. ");
        return unit;
    }
}
public enum SyntaxUnitTypes {
    Empty,
    Variable,
    ValueConstant,
    ExpressionBracket,
    MethodCall,
    LambdaDeclaration,
    AnonymousObject,
    ObjectConstruction,
    OperatorExpression,
    PreFixOperatorExpression,
}
public class SyntaxException : Exception {
    string _code;
    int _pos;
    string? _message;
    public SyntaxException(string code, int pos) {
        _pos = pos;
        _code = code;
    }
    public SyntaxException(string message, string code, int pos) {
        _pos = pos;
        _code = code;
        _message = message;
    }
    string extract(int from, int to, string text) {
        if (from < 0) from = 0;
        if (to > text.Length) to = text.Length;
        return text[from..to];
    }
    public override string Message {
        get {
            var padding = 30;
            if (_pos <= 0) _pos = 0;
            else if (_pos >= _code.Length) _pos = _code.Length - 1;
            return (_message == null ? "" : _message)
                + "Unexpected syntax at postion " + _pos + ": "
                + (_pos - padding > 0 ? "..." : "")
                + extract(_pos - padding, _pos, _code)
                //+ _code[_pos] + "\u0333"
                + " ==> " + _code[_pos] + " <== "
                + extract(_pos + 1, _pos + padding, _code)
                + (_pos + padding < _code.Length ? "..." : "");
        }
    }
}
public abstract class SyntaxUnit {
    public string Code { get; }
    public int Pos1 { get; }
    public int Pos2 { get; set; }
    protected SyntaxUnit(string code, int pos1, int pos2) {
        Pos1 = pos1;
        Pos1 = pos2;
        Code = code;
    }
    public static SyntaxUnit? ParseStatement(string code, int pos, out int newPos, SyntaxUnit? prevUnit, IEnumerable<Parameter> parameters) {
        do {
            pos = SkipWhiteSpace(code, pos);
            var nextUnit = ParseUnit(code, pos, out pos, prevUnit, parameters);
            newPos = pos;
            if (nextUnit == null) return prevUnit;
            prevUnit = nextUnit;
        } while (pos < code.Length);
        return prevUnit;
    }
    public static SyntaxUnit? ParseUnit(string code, int pos, out int newPos, SyntaxUnit? prevUnit, IEnumerable<Parameter> parameters) {
        pos = SkipWhiteSpace(code, pos);
        var unitType = whatIsNext(code, pos, prevUnit);
        newPos = pos;
        return unitType switch {
            SyntaxUnitTypes.Variable => VariableReferenceSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.MethodCall => MethodCallSyntax.Parse(code, pos, out newPos, prevUnit, parameters),
            SyntaxUnitTypes.ExpressionBracket => BracketSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.OperatorExpression => OperatorExpressionSyntax.Parse(code, pos, out newPos, prevUnit, parameters),
            SyntaxUnitTypes.PreFixOperatorExpression => PreFixSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.LambdaDeclaration => LambdaSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.ObjectConstruction => ObjectConstructionSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.AnonymousObject => AnonymousObjectSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.ValueConstant => ValueConstantSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.Empty => null,
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
    protected static SyntaxUnitTypes whatIsNext(string code, int pos, SyntaxUnit? prevUnit) {
        pos = SkipWhiteSpace(code, pos);
        var firstNoneWhiteSpaceChar = code[pos];
        if (char.IsDigit(firstNoneWhiteSpaceChar) || firstNoneWhiteSpaceChar == '\"') return SyntaxUnitTypes.ValueConstant;
        if (isNextElement(firstNoneWhiteSpaceChar, code, pos, "false")) return SyntaxUnitTypes.ValueConstant;
        if (isNextElement(firstNoneWhiteSpaceChar, code, pos, "true")) return SyntaxUnitTypes.ValueConstant;
        if (isNextElement(firstNoneWhiteSpaceChar, code, pos, "null")) return SyntaxUnitTypes.ValueConstant;
        if (firstNoneWhiteSpaceChar == '[') return SyntaxUnitTypes.ValueConstant;
        if (firstNoneWhiteSpaceChar == '(') {
            var pos2 = code.IndexOf(')', pos); // posFirstEndBracket 
            if (pos2 > -1) {
                pos2 = SkipWhiteSpace(code, pos2 + 1); // in case: (...) =>
                if (code.Length - pos2 > 2 && code[pos2..(pos2 + 2)] == "=>") return SyntaxUnitTypes.LambdaDeclaration;
            }
            return SyntaxUnitTypes.ExpressionBracket;
        }
        if (firstNoneWhiteSpaceChar == ')') return SyntaxUnitTypes.Empty;
        if (OperatorExpressionSyntax.IsOperatorChar(firstNoneWhiteSpaceChar)) {
            if (prevUnit == null) {
                if (firstNoneWhiteSpaceChar == '-' && whatIsNext(code, pos + 1, prevUnit) == SyntaxUnitTypes.ValueConstant) {
                    // minus infront of number is a constant
                    return SyntaxUnitTypes.ValueConstant;
                }
                return SyntaxUnitTypes.PreFixOperatorExpression;
            } else {
                return SyntaxUnitTypes.OperatorExpression;
            }
        }
        var startFirstWord = pos;
        pos = SkipUntilAfterReferenceIncludingGenerics(code, pos);
        if (pos == startFirstWord)
            return SyntaxUnitTypes.Empty;
        if (pos + 1 < code.Length && code[startFirstWord..(pos + 1)] == "new ") {
            pos = SkipWhiteSpace(code, pos);
            if (code[pos] == '{') return SyntaxUnitTypes.AnonymousObject;
            else return SyntaxUnitTypes.ObjectConstruction;
        }
        pos = SkipWhiteSpace(code, pos);
        if (code.Length - pos > 2 && code[pos..(pos + 2)] == "=>") return SyntaxUnitTypes.LambdaDeclaration;
        if (pos < code.Length && code[pos] == '(') return SyntaxUnitTypes.MethodCall;
        return SyntaxUnitTypes.Variable;
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
    public abstract SyntaxUnitTypes SyntaxType { get; }
}
