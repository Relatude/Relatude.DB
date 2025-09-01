using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using static System.Linq.Expressions.Expression;
using static Relatude.DB.Query.ExpressionToString.ZSpitz.Language;
using Relatude.DB.Query.ExpressionToString.ZSpitz;

namespace Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions {
    public static class ExpressionExtensions {
        public static object? ExtractValue(this Expression expr) {
            if (expr is not LambdaExpression lambda) {
                lambda = Lambda(expr);
            }
            return lambda.Compile().DynamicInvoke();
        }

        public static bool TryExtractValue(this Expression expr, out object? value) {
            try {
                value = expr.ExtractValue();
                return true;
            } catch (Exception ex) {
                value = ex;
                return false;
            }
        }

        public static Expression SansConvert(this Expression expr) =>
            expr is UnaryExpression uexpr && expr.NodeType.In(ExpressionType.Convert, ExpressionType.ConvertChecked, ExpressionType.Unbox) ?
                uexpr.Operand :
                expr;

        public static bool IsEmpty(this Expression expr) =>
            expr is DefaultExpression && expr.Type == typeof(void);

        public static bool IsClosedVariable(this Expression expr) =>
            expr is MemberExpression mexpr && (mexpr.Expression?.Type.IsClosureClass() ?? false);

        public static string? Name(this Expression expr, Language language = CSharp) {
            string qualifiedName(Expression? instance, MemberInfo mi) =>
                (instance is null ? $"{mi.DeclaringType!.FriendlyName(language)}." : "") + mi.Name;

            return expr switch {
                ParameterExpression pexpr => pexpr.Name,
                MemberExpression mexpr => qualifiedName(mexpr.Expression, mexpr.Member),
                MethodCallExpression callExpr => qualifiedName(callExpr.Object, callExpr.Method),
                LambdaExpression lambdaExpr => lambdaExpr.Name,
                _ => "",
            };
        }
    }
}
