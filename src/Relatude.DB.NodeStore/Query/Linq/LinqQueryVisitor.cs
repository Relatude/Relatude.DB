using System.Linq.Expressions;
using System.Reflection;
using Relatude.DB.Query;
namespace Relatude.DB.Query.Linq;
/// <summary>
/// Class looks for evaluatable subtrees and evaluates them into constant values and registers them as query parameters. 
/// </summary>
internal sealed class LinqQueryVisitor(List<Parameter> _queryParams) : ExpressionVisitor {
    readonly List<ParameterExpression> _lambdaParams = [];

    protected override Expression VisitMember(MemberExpression node) {
        if (!canBeEvaluated(node)) return base.VisitMember(node);
        return evaluateAndRegister(node);
    }
    protected override Expression VisitMethodCall(MethodCallExpression node) {
        if (!canBeEvaluated(node)) return base.VisitMethodCall(node);
        return evaluateAndRegister(node);
    }
    protected override Expression VisitInvocation(InvocationExpression node) {
        if (!canBeEvaluated(node)) return base.VisitInvocation(node);
        return evaluateAndRegister(node);
    }
    protected override Expression VisitLambda<T>(Expression<T> node) {
        // collect lambda parameters so we don't evaluate expressions depending on them
        foreach (var param in node.Parameters) _lambdaParams.Add(param);
        var body = Visit(node.Body);
        return Expression.Lambda(body, node.Parameters);
    }
    protected override Expression VisitConstant(ConstantExpression node) {
        return evaluateAndRegister(node);
    }

    private bool canBeEvaluated(Expression? expr) {
        return expr switch {
            // Anything that depends on parameters should *not* be evaluated
            null => true,
            ParameterExpression p when _lambdaParams.Contains(p) => false,
            ConstantExpression => true,

            // static member or member of evaluatable subtree
            MemberExpression me => (me.Expression == null || canBeEvaluated(me.Expression)) &&
                                   me.Member is FieldInfo or PropertyInfo,

            // static or pure method call
            MethodCallExpression mc => (mc.Object == null || canBeEvaluated(mc.Object)) &&
                                       mc.Arguments.All(canBeEvaluated),

            InvocationExpression ie => canBeEvaluated(ie.Expression) && ie.Arguments.All(canBeEvaluated),

            // unary / binary ops are evaluatable if all parts are
            UnaryExpression ue => canBeEvaluated(ue.Operand),
            BinaryExpression be => canBeEvaluated(be.Left) && canBeEvaluated(be.Right),

            _ => false
        };
    }

    private Expression evaluateAndRegister(Expression expr) {
        var value = evaluate(expr);
        var name = $"@P{_queryParams.Count}";
        _queryParams.Add(new(name, value));
        return Expression.Variable(expr.Type, name);
        //return Expression.Constant(value, expr.Type);
        //return Expression.Parameter(expr.Type, name);
    }
    private object? evaluate(Expression expr) {
        switch (expr) {
            case ConstantExpression c:
                return c.Value;
            case MemberExpression m:
                var target = m.Expression != null ? evaluate(m.Expression) : null;
                return m.Member switch {
                    FieldInfo fi => fi.GetValue(target),
                    PropertyInfo pi => pi.GetValue(target),
                    _ => throw new NotSupportedException($"Unsupported member: {m.Member.GetType().Name}")
                };
            case MethodCallExpression mc:
                var obj = mc.Object != null ? evaluate(mc.Object) : null;
                var args = mc.Arguments.Select(evaluate).ToArray();
                return mc.Method.Invoke(obj, args);
            case InvocationExpression ie:
                // Handle calling Func<T> or delegate references
                var lambda = evaluate(ie.Expression);
                var invokedArgs = ie.Arguments.Select(evaluate).ToArray();
                if (lambda is Delegate del)
                    return del.DynamicInvoke(invokedArgs);

                throw new NotSupportedException("Invocation target is not a delegate.");
            case UnaryExpression u:
                var operand = evaluate(u.Operand);
                return Expression.Lambda(u.Update(Expression.Constant(operand))).Compile().DynamicInvoke();
            case BinaryExpression b:
                var left = evaluate(b.Left);
                var right = evaluate(b.Right);
                return Expression
                    .Lambda(b.Update(Expression.Constant(left), b.Conversion, Expression.Constant(right))).Compile()
                    .DynamicInvoke();
            default: throw new NotSupportedException($"Cannot evaluate {expr.NodeType}");
        }
    }
}
