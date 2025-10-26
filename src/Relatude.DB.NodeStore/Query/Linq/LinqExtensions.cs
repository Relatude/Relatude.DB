using System.Linq.Expressions;

namespace Relatude.DB.Query.Linq;
internal static class LinqExtensions {
    /// <summary>
    /// Looks for evaluatable subtrees in the expression, evaluates them into constant values, and registers them as query parameters.
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="parameters"></param>
    /// <returns></returns>
    public static string ToQueryString(this Expression expression, List<Parameter> parameters) {
        var evaluator = new LinqQueryVisitor(parameters);
        var evaluatedExpression = evaluator.Visit(expression);
        return LinqToQueryString.Get(evaluatedExpression);
    }
    }