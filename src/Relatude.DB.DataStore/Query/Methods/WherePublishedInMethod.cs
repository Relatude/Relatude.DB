using Relatude.DB.Common;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;

/// <summary>
/// The nodes that have (<paramref name="has"/>), or have not, a version published in the culture, released and not
/// expired at the time the query is read at: what readers of the culture read as its own, without falling back to
/// another. The culture is a code or an id; null or "" is no culture, content in none. A set operation on the
/// context's cached id sets (IStoreNodeDataCollection.WherePublishedIn): no node is read.
/// </summary>
public class WherePublishedInMethod(IExpression input, string? culture, bool has) : IExpression {
    public object Evaluate(IVariables vars) {
        if (input.Evaluate(vars) is not IStoreNodeDataCollection nodes) throw new Exception("WherePublishedIn follows a query of nodes. ");
        return nodes.WherePublishedIn(culture, has);
    }
    public override string ToString() => input + ".WherePublishedIn(" + (culture == null ? "null" : culture.ToStringLiteral()) + ", " + (has ? "true" : "false") + ")";
}
