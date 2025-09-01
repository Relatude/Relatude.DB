namespace Relatude.DB.Query.Expressions;
public class MinusPrefixExpression : IExpression {
    public readonly IExpression Subject;
    public MinusPrefixExpression(IExpression expression) {
        Subject = expression;
    }
    public object Evaluate(IVariables vars) {
        var v = Subject.Evaluate(vars);
        if (v is int i) return -i;
        if (v is double d) return -d;
        if (v is decimal dc) return -dc;
        if (v is float f) return -f;
        if (v is byte bt) return -bt;
        throw new NotSupportedException("Minus prefix operator is not supported for this datatype. ");
    }
}
public class NotPrefixExpression : IExpression {
    public readonly IExpression Subject;
    public NotPrefixExpression(IExpression expression) {
        Subject = expression;
    }
    public object Evaluate(IVariables vars) {
        var v = Subject.Evaluate(vars);
        if (v is bool b) return !b;
        throw new NotSupportedException("NOT prefix operator is not supported for this datatype. ");
    }
}
