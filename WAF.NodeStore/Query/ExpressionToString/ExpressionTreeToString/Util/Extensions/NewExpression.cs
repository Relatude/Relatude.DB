using WAF.Query.ExpressionToString.ZSpitz.Extensions;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace WAF.Query.ExpressionToString.ExpressionTreeToString.Util.Extensions {
    internal static class NewExpressionExtensions {
        internal static IEnumerable<(string?, Expression, int)> NamesArguments(this NewExpression expr) =>
            expr.Constructor!.GetParameters().Zip(expr.Arguments, (x, y) => (x.Name, y)).WithIndex()!;
    }
}
