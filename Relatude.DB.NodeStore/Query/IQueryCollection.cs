using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Xml.Linq;
using Relatude.DB.Nodes;

namespace Relatude.DB.Query;
public interface IQueryCollection<T> : IQueryExecutable<T> {
    T Execute(out int totalCount);
}