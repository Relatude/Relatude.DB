using Relatude.DB.Query.ExpressionToString.ExpressionTreeToString;
using System.Linq.Expressions;
using System.Reflection;
namespace Relatude.DB.Query;
internal static class ExpressionExtension {
    public static string ToQueryString(this Expression expression) {
        return new Visitor().Visit(expression).ToString("C#");
    }
    private class Visitor : ExpressionVisitor {
        // making sure all local variables references are converted to actual values where possible
        protected override Expression VisitMember(MemberExpression node) {
            if (node.Member is FieldInfo fieldInfo && node.Expression is ConstantExpression constEx) {
                var value = fieldInfo.GetValue(constEx.Value);
                //if(value is Guid g) 
                //    return Expression.Constant(g.ToString());
                return Expression.Constant(value);
            }
            return base.VisitMember(node);
        }
    }
}
