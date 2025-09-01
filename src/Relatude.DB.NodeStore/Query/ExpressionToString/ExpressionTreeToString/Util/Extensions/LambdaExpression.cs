using System.Linq.Expressions;

namespace Relatude.DB.Query.ExpressionToString.ExpressionTreeToString.Util.Extensions {
    public static class LambdaExpressionExtensions {
        public static object? GetTarget(this LambdaExpression expr) => expr.Compile().Target;
    }
}
