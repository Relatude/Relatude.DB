using System.Text;
namespace Relatude.DB.Query.Parsing.Tokens;
public class OperatorExpressionToken : TokenBase {
    public OperatorExpressionToken(string operatorName, TokenBase leftValue, TokenBase rightValue, string code, int pos1)
        : base(code, pos1, pos1) {
        Values = [leftValue, rightValue];
        Operators = [operatorName];
    }
    public static bool IsOperatorChar(char c) {
        return c == '!' || c == '|' || c == '&' || c == '+' || c == '-' || c == '/' || c == '*' || c == '<' || c == '>' || c == '=';
    }
    public List<TokenBase> Values { get; }
    public List<string> Operators { get; } // + - * /
    public bool HasEvaluatedOperatorOrder;
    void Add(TokenBase unit, string operatorName) {
        Values.Add(unit);
        Operators.Add(operatorName);
    }
    static public OperatorExpressionToken Parse(string code, int pos, out int newPos, TokenBase? prevUnit, IEnumerable<Parameter> parameters) {
        if (prevUnit == null) throw new SyntaxException("Error in operator expression. ", code, pos);
        var startPosOp = pos;
        while (pos < code.Length && IsOperatorChar(code[pos])) pos++;
        var operatorName = code[startPosOp..pos];
        pos = SkipWhiteSpace(code, pos);
        var nextUnit = ParseToken(code, pos, out newPos, null, parameters);
        if (nextUnit == null) throw new SyntaxException("Missing right side of operator. ", code, pos);
        var opEx = new OperatorExpressionToken(operatorName, prevUnit, nextUnit, code, pos);
        pos = newPos;
        opEx.Pos2 = newPos;
        while (newPos < code.Length && whatIsNext(code, newPos, nextUnit) == TokenTypes.OperatorExpression) {
            pos = SkipWhiteSpace(code, pos);
            startPosOp = pos;
            while (pos < code.Length && IsOperatorChar(code[pos])) pos++;
            operatorName = code[startPosOp..pos];
            nextUnit = ParseToken(code, pos, out pos, null, parameters);
            if (nextUnit == null) throw new NullReferenceException();
            opEx.Add(nextUnit, operatorName);
            newPos = pos;
            opEx.Pos2 = newPos;
        }
        return opEx;
    }
    public override string ToString() {
        var sb = new StringBuilder();
        sb.Append(base.ToString());
        sb.Append('(');
        for (var n = 0; n < Values.Count - 1; n++) {
            sb.Append(Values[n].ToString());
            sb.Append(Operators[n]);
        }
        sb.Append(Values.Last().ToString());
        sb.Append(')');
        return sb.ToString();
    }
    public override TokenTypes TokenType => TokenTypes.OperatorExpression;
}
