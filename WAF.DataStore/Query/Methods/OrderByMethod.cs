using WAF.Query.Data;
using WAF.Query.Expressions;
namespace WAF.Query.Methods;
public class OrderByMethod(IExpression input, LambdaExpression lambda, bool descending) : IExpression {
    public object Evaluate(IVariables vars) {
        var evaluatedInput = input.Evaluate(vars);
        if (evaluatedInput is not ICollectionData coll) throw new("OrderBy statemens only excepts a enumerable node collection as input. ");
        if (evaluatedInput is IStoreNodeDataCollection sd
            && lambda.Body is PropertyReferenceExpression propRef
            && sd.TryOrderByIndexes(propRef.PropertyName, descending)) {
            return evaluatedInput;
        }
        var values = Helper.EvaluateLambdaOnCollection(vars, coll, lambda);
        var v = values.Values.FirstOrDefault();
        if(v == null) return coll;
        IEnumerable<int> order;
        if (v is int) order = reorder<int>(values.Values, descending);
        else if (v is float) order = reorder<float>(values.Values, descending);
        else if (v is string) order = reorder<string>(values.Values, descending);
        else if (v is double) order = reorder<double>(values.Values, descending);
        else if (v is decimal) order = reorder<decimal>(values.Values, descending);
        else if (v is long) order = reorder<long>(values.Values, descending);
        else if (v is DateTime) order = reorder<DateTime>(values.Values, descending);
        else if (v is TimeSpan) order = reorder<TimeSpan>(values.Values, descending);
        else throw new Exception("OrderBy does not support the type of the values in the collection. ");
        return coll.ReOrder(order);
    }
    struct Rec<T>(int index, object rec) {
        public int Index = index;
        public T Value = (T)rec;
    }
    IEnumerable<int> reorder<T>(IEnumerable<object> values, bool descending) {
        var casted = values.Select((v, i) => new Rec<T>(i, v));
        var sorted = descending ? casted.OrderByDescending(v => v.Value) : casted.OrderBy(v => v.Value);
        return sorted.Select(x => x.Index);
    }
    public override string ToString() => input + ".OrderBy(" + lambda + ")";
}
