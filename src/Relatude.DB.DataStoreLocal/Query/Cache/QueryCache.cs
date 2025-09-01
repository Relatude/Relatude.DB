using Relatude.DB.Datamodels;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Query.Cache; 
internal class CacheEntry(QueryDependencies dependencies, object value) {
    public QueryDependencies Dependencies { get; } = dependencies;
    public object Value { get; } = value;
}
internal class QueryCache(Datamodel dm) {
    private Datamodel _dm = dm;
    readonly Dictionary<string, CacheEntry> _cache = [];
    object _lock = new();
    public void Add(string query, QueryDependencies dependencies, object value) {
        lock (_lock) {
            _cache[query] = new CacheEntry(dependencies, value);
        }
    }
    public void ClearEffectedEntires(TransactionChanges changes) {
        lock (_lock) {
            List<string> invalidated = [];
            foreach (var entry in _cache) {
                if (entry.Value.Dependencies.CouldBeAffectedByChanges(changes, _dm)) {
                    invalidated.Add(entry.Key);
                }
            }
            foreach (var key in invalidated) _cache.Remove(key);
        }
    }
    public bool TryGet(string query, [MaybeNullWhen(false)] out object value) {
        lock (_lock) {
            if (_cache.TryGetValue(query, out var entry)) {
                value = entry.Value;
                return true;
            }
            value = null;
            return false;
        }
    }
    public void Clear() {
        lock (_lock) {
            _cache.Clear();
        }
    }
}
