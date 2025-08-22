using System.Linq.Expressions;

namespace WAF.Query.ExpressionToString.ExpressionTreeToString.Util.Extensions {
    public static class LambdaExpressionExtensions {
        public static object? GetTarget(this LambdaExpression expr) => expr.Compile().Target;
    }
}
