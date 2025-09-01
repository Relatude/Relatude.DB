using Relatude.DB.Datamodels;

namespace Relatude.DB.Query {
    public class ResultSetSearch<T> : ResultSet<SearchResultHit<T>> {
        public ResultSetSearch(IEnumerable<SearchResultHit<T>> values, int count, int totalCount, int pageIndex, int? pageSize, double durationMs)
            : base(values, count, totalCount, pageIndex, pageSize, durationMs) {
        }
    }
}
