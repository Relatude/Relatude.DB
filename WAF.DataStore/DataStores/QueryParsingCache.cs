using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WAF.Common;
using WAF.Query.Expressions;

namespace WAF.DataStores {
    internal class QueryParsingCache:Cache<string,IExpression> {
        public QueryParsingCache(int capacity):base(capacity) {
        }
    }
}
