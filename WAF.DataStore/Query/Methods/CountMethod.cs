using WAF.Datamodels.Properties;
using WAF.Query.Data;
using WAF.Query.Expressions;

namespace WAF.Query.Methods {
    public class CountMethod : IExpression {
        readonly IExpression _subject;
        public CountMethod(IExpression subject) {
            _subject = subject;
        }
        public object Evaluate(IVariables vars) {
            var values = _subject.Evaluate(vars);
            if (values is not ICollectionData coll) throw new Exception("Count is only supported on collections. ");
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
        override public string ToString() => _subject + ".Count()";
    }
}
