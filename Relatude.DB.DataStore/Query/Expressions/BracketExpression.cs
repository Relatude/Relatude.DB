namespace Relatude.DB.Query.Expressions;
public class BracketExpression : OperatorExpression {
    public BracketExpression(IExpression expression) : base(expression, false) { }
}
