using Relatude.DB.Query.Parsing.Syntax;
using System.Text;
namespace Relatude.DB.Query.Parsing;
public class OperatorExpressionSyntax : SyntaxUnit {
    public OperatorExpressionSyntax(string operatorName, SyntaxUnit leftValue, SyntaxUnit rightValue, string code, int pos1)
        : base(code, pos1, pos1) {
        Values = new() { leftValue, rightValue };
        Operators = new() { operatorName };
    }
    public static bool IsOperatorChar(char c) {
        return c == '!' || c == '|' || c == '&' || c == '+' || c == '-' || c == '/' || c == '*' || c == '<' || c == '>' || c == '=';
    }
    public List<SyntaxUnit> Values { get; }
    public List<string> Operators { get; } // + - * /
    public bool HasEvaluatedOperatorOrder;
    void Add(SyntaxUnit unit, string operatorName) {
        Values.Add(unit);
        Operators.Add(operatorName);
    }
    static public OperatorExpressionSyntax Parse(string code, int pos, out int newPos, SyntaxUnit? prevUnit, IEnumerable<Parameter> parameters) {
        if (prevUnit == null) throw new SyntaxException("Error in operator expression. ", code, pos);
        var startPosOp = pos;
        while (pos < code.Length && IsOperatorChar(code[pos])) pos++;
        var operatorName = code[startPosOp..pos];
        pos = SkipWhiteSpace(code, pos);
        var nextUnit = ParseUnit(code, pos, out newPos, null, parameters);
        if (nextUnit == null) throw new SyntaxException("Missing right side of operator. ", code, pos);
        var opEx = new OperatorExpressionSyntax(operatorName, prevUnit, nextUnit, code, pos);
        pos = newPos;
        opEx.Pos2 = newPos;
        while (newPos < code.Length && whatIsNext(code, newPos, nextUnit) == SyntaxUnitTypes.OperatorExpression) {
            pos = SkipWhiteSpace(code, pos);
            startPosOp = pos;
            while (pos < code.Length && IsOperatorChar(code[pos])) pos++;
            operatorName = code[startPosOp..pos];
            nextUnit = ParseUnit(code, pos, out pos, null, parameters);
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
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.OperatorExpression;
}
