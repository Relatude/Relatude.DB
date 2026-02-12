using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Indexes;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores.Definitions;

internal class IndexUtil<T> where T : IIndex {
    T? _index;
    Dictionary<string, T>? _indexByCulture;
    public void Initalize(Dictionary<string, T> indexes, bool cultureSensitive, List<IIndex> indexDirectory) {
        if (cultureSensitive) {
            _indexByCulture = indexes;
            foreach (var culture in indexes.Keys) {
                if (string.IsNullOrEmpty(culture)) throw new Exception("Culture sensitive index must have culture defined. ");
            }
        } else {
            if (indexes.Count != 1) throw new Exception("Non culture sensitive index must have only one index definition. ");
            _index = indexes.Values.First();
        }
        indexDirectory.AddRange(indexes.Values.Cast<IIndex>());
    }
    public bool TryGetIndex(QueryContext ctx, [MaybeNullWhen(false)]out T index) {
        if (_index != null) {
            index = _index;
            return true;
        }
        if (_indexByCulture != null && _indexByCulture.TryGetValue(ctx.CultureCode!, out var idx)) {
            index = idx;
            return true;
        }
        index = default;
        return false;
    }
    public T GetIndex(QueryContext ctx) {
        if (_index != null) return _index;
        if (_indexByCulture != null && _indexByCulture.TryGetValue(ctx.CultureCode!, out var index)) {
            return index;
        }
        throw new Exception("Index not found for culture: " + ctx.CultureCode);
    }
    public bool HasIndex(QueryContext ctx) {
        if (_index != null) return true;
        if (_indexByCulture != null && _indexByCulture.ContainsKey(ctx.CultureCode!)) {
            return true;
        }
        return false;
    }
}
