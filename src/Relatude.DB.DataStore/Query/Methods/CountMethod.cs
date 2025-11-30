using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods; 
public class CountMethod (IExpression subject) : IExpression {
    public object Evaluate(IVariables vars) {
        var values = subject.Evaluate(vars);
        if (values is not ICollectionBase coll) throw new Exception("Count is only supported on collections. ");
        var pageSize = coll.PageSizeUsed.HasValue && coll.PageSizeUsed.Value > 0 ? coll.PageSizeUsed.Value : coll.TotalCount;
        int count;
        if (coll.TotalCount > 0) { // counts on page
            var skip = pageSize * coll.PageIndexUsed;
            if (skip > coll.TotalCount) {
                count = 0;
            } else {
                count = skip + pageSize > coll.TotalCount ? coll.TotalCount - skip : pageSize;
            }
        } else {
            count = coll.TotalCount;
        }
        return count;
    }
    override public string ToString() => subject + ".Count()";
}
