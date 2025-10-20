using Relatude.DB.Query.ExpressionToString.ExpressionTreeToString;
using System.Linq.Expressions;
using System.Reflection;

namespace Relatude.DB.Query;

internal static class ExpressionExtensions
{
    public static string ToQueryString(this Expression expression)
    {
        var visited = new SubtreeEvaluator().Visit(expression);
        return visited.ToString("C#");
    }

    private class SubtreeEvaluator : ExpressionVisitor
    {
        private readonly HashSet<ParameterExpression> _parameters = new();

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            // collect lambda parameters so we don't evaluate expressions depending on them
            foreach (var param in node.Parameters)
                _parameters.Add(param);

            var body = Visit(node.Body);
            return Expression.Lambda(body, node.Parameters);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (!CanBeEvaluated(node))
                return base.VisitMember(node);

            var value = GetValue(node);
            return Expression.Constant(value, node.Type);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!CanBeEvaluated(node))
                return base.VisitMethodCall(node);

            var value = GetValue(node);
            return Expression.Constant(value, node.Type);
        }

        private bool CanBeEvaluated(Expression? expr) =>
            expr switch
            {
                // Anything that depends on parameters should *not* be evaluated
                null => true,
                ParameterExpression p when _parameters.Contains(p) => false,
                ConstantExpression => true,

                // static member or member of evaluatable subtree
                MemberExpression me => (me.Expression == null || CanBeEvaluated(me.Expression)) &&
                                       me.Member is FieldInfo or PropertyInfo,

                // static or pure method call
                MethodCallExpression mc => (mc.Object == null || CanBeEvaluated(mc.Object)) &&
                                           mc.Arguments.All(CanBeEvaluated),

                InvocationExpression ie => CanBeEvaluated(ie.Expression) && ie.Arguments.All(CanBeEvaluated),

                // unary / binary ops are evaluatable if all parts are
                UnaryExpression ue => CanBeEvaluated(ue.Operand),
                BinaryExpression be => CanBeEvaluated(be.Left) && CanBeEvaluated(be.Right),

                _ => false
            };

        private static object? GetValue(Expression expr)
        {
            switch (expr)
            {
                case ConstantExpression c:
                    return c.Value;
                
                case MemberExpression m:
                    var target = m.Expression != null ? GetValue(m.Expression) : null;
                    return m.Member switch
                    {
                        FieldInfo fi => fi.GetValue(target),
                        PropertyInfo pi => pi.GetValue(target),
                        _ => throw new NotSupportedException($"Unsupported member: {m.Member.GetType().Name}")
                    };

                case MethodCallExpression mc:
                    var obj = mc.Object != null ? GetValue(mc.Object) : null;
                    var args = mc.Arguments.Select(GetValue).ToArray();
                    return mc.Method.Invoke(obj, args);

                case InvocationExpression ie:
                    // Handle calling Func<T> or delegate references
                    var lambda = GetValue(ie.Expression);
                    var delegateArgs = ie.Arguments.Select(GetValue).ToArray();
                    if (lambda is Delegate del)
                        return del.DynamicInvoke(delegateArgs);

                    throw new NotSupportedException($"Cannot invoke expression of type {lambda?.GetType().Name}");

                case UnaryExpression u:
                    var operand = GetValue(u.Operand);
                    return Expression.Lambda(u.Update(Expression.Constant(operand))).Compile().DynamicInvoke();

                case BinaryExpression b:
                    var left = GetValue(b.Left);
                    var right = GetValue(b.Right);
                    return Expression
                        .Lambda(b.Update(Expression.Constant(left), b.Conversion, Expression.Constant(right))).Compile()
                        .DynamicInvoke();

                default: throw new NotSupportedException($"Cannot evaluate {expr.NodeType}");
            }
        }
    }
}