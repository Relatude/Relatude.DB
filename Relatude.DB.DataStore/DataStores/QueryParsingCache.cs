using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Relatude.DB.Common;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.DataStores {
    internal class QueryParsingCache:Cache<string,IExpression> {
        public QueryParsingCache(int capacity):base(capacity) {
        }
    }
}
