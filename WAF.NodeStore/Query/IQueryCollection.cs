using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Xml.Linq;
using WAF.Nodes;

namespace WAF.Query;
public interface IQueryCollection<T> : IQueryExecutable<T> {
    T Execute(out int totalCount);
}