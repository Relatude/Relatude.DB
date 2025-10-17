using Relatude.DB.Query.ExpressionToString.ExpressionTreeToString;
using System.Linq.Expressions;
using System.Reflection;

namespace Relatude.DB.Query;

internal static class ExpressionExtension
{
    public static string ToQueryString(this Expression expression, int parametersCount, out IReadOnlyCollection<Parameter> parameters)
    {
        var visited = new Visitor().Visit(expression);
        parameters = visited.ExtractParameters();
        return visited.ToString("C#");
    }

    private class Visitor : ExpressionVisitor
    {
        // making sure all local variables references are converted to actual values where possible
        protected override Expression VisitMember(MemberExpression node)
        {
            // Try to evaluate member expressions that are based on constants or nested members of constants
            if (!CanBeEvaluated(node))
                return base.VisitMember(node);

            var value = GetValue(node);
            return Expression.Constant(value, node.Type);
        }

        private static bool CanBeEvaluated(Expression expr)
        {
            return expr switch
            {
                // Constants and member access of constants can be evaluated
                ConstantExpression => true,
                MemberExpression { Expression: not null } me => CanBeEvaluated(me.Expression),
                _ => false
            };
        }

        private static object? GetValue(Expression expr)
        {
            switch (expr)
            {
                case ConstantExpression c:
                    return c.Value;

                case MemberExpression { Expression: not null } m:
                    var target = GetValue(m.Expression);
                    return m.Member switch
                    {
                        FieldInfo fi => fi.GetValue(target),
                        PropertyInfo pi => pi.GetValue(target),
                        _ => throw new NotSupportedException($"Unsupported member: {m.Member.GetType().Name}")
                    };

                default:
                    throw new NotSupportedException($"Cannot evaluate expression of type {expr.GetType().Name}");
            }
        }
    }
}