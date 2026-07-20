namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Builds the query cache key for a sorted value index. The <c>SetRegister</c> reuses a cached
/// result set whenever two queries produce the same key, so the key must be identical exactly when
/// the result set is. This class holds the (backend-independent) control flow; the ordering-sensitive
/// pieces live behind <see cref="IGapSource"/>, implemented by each backend in its own value ordering.
///
/// <para>The idea: for a query value that <em>is</em> indexed, the key includes the value (equality)
/// or the matching count (range). For a value that is <em>not</em> indexed, every query value in the
/// same open interval between two adjacent indexed values ("gap") yields the same result set, so the
/// key collapses to the gap's id counts. The last gap is cached, which is what makes repeated queries
/// with an ever-changing value (e.g. <c>DateTime.Now</c> on every request) resolve without touching
/// the backend.</para>
/// </summary>
public sealed class GapCacheKeyBuilder<T> where T : notnull {

    /// <summary>The backend operations this builder needs, expressed in the backend's own value ordering.</summary>
    public interface IGapSource {
        /// <summary>The index's current state id; the cached gap is invalidated when this changes.</summary>
        long StateId { get; }
        bool ContainsValue(T value);
        int CountGreaterThan(T value, bool inclusive);
        int CountLessThan(T value, bool inclusive);
        /// <summary>Build the gap around <paramref name="absentValue"/>, which is known not to be indexed.</summary>
        Gap BuildGap(T absentValue);
        /// <summary>Whether <paramref name="value"/> falls strictly inside <paramref name="gap"/>, using the backend's ordering.</summary>
        bool InGap(Gap gap, T value);
    }

    /// <summary>
    /// An open interval between two adjacent indexed values, with the id counts on either side.
    /// Only valid for the exact index state (<see cref="StateId"/>) it was built at. The bounds are
    /// stored as opaque objects so a backend can keep them in whatever representation its ordering
    /// uses (e.g. the raw stored string for a collated text column); null means "no bound on that side".
    /// </summary>
    public sealed class Gap {
        public required long StateId { get; init; }
        public required int CountBelow { get; init; } // ids with a value below the gap
        public required int CountAbove { get; init; } // ids with a value above the gap
        public object? Lower { get; init; }           // greatest indexed value below the gap, if any
        public object? Upper { get; init; }           // smallest indexed value above the gap, if any
    }

    readonly IGapSource _source;
    Gap? _gap;

    public GapCacheKeyBuilder(IGapSource source) => _source = source;

    /// <summary>Drops the cached gap (call from the index's <c>ClearCache</c>).</summary>
    public void Clear() => _gap = null;

    public object[] GetCacheKey(T queryValue, QueryType queryType) {
        var gap = _gap;
        if (gap == null || gap.StateId != _source.StateId || !_source.InGap(gap, queryValue)) {
            if (_source.ContainsValue(queryValue)) {
                return queryType switch {
                    QueryType.Equal or QueryType.NotEqual => [queryType, queryValue],
                    QueryType.Greater => [queryType, _source.CountGreaterThan(queryValue, false)],
                    QueryType.GreaterOrEqual => [queryType, _source.CountGreaterThan(queryValue, true)],
                    QueryType.Less => [queryType, _source.CountLessThan(queryValue, false)],
                    QueryType.LessOrEqual => [queryType, _source.CountLessThan(queryValue, true)],
                    _ => throw new Exception("Unknown query type: " + queryType + ". "),
                };
            }
            gap = _source.BuildGap(queryValue);
            _gap = gap;
        }
        // queryValue lies strictly inside the gap: no indexed value equals it, and the
        // inclusive/exclusive distinction cannot matter.
        return queryType switch {
            QueryType.Equal or QueryType.NotEqual => [queryType],
            QueryType.Greater or QueryType.GreaterOrEqual => [queryType, gap.CountAbove],
            QueryType.Less or QueryType.LessOrEqual => [queryType, gap.CountBelow],
            _ => throw new Exception("Unknown query type: " + queryType + ". "),
        };
    }
}
