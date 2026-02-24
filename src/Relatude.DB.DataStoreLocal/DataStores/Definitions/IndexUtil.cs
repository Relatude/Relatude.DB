using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Indexes;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores.Definitions;

internal class IndexUtil<T> where T : IIndex {
    T? _index;
    Dictionary<string, T> _indexByCulture = new();
    public void Initalize(Dictionary<string, T> indexes, bool cultureSensitive, List<IIndex> indexDirectory) {
        if (cultureSensitive) {
            _indexByCulture = indexes;
            indexes.Keys.Where(string.IsNullOrEmpty).ForEach(c => throw new Exception("Culture sensitive index must have culture defined. "));
        } else {
            if (indexes.Count != 1) throw new Exception("Non culture sensitive index must have only one index definition. ");
            _index = indexes.Values.First();
        }
        indexDirectory.AddRange(indexes.Values.Cast<IIndex>());
    }
    public bool TryGetIndex(QueryContext ctx, [MaybeNullWhen(false)] out T index) {
        index = _index;
        if (index != null) return true;
        if (ctx.CultureCode == null) return false;
        return _indexByCulture.TryGetValue(ctx.CultureCode!, out index);
    }
    public T GetIndex(QueryContext ctx) {
        if (_index != null) return _index;
        if (ctx.CultureCode == null) throw new Exception("Culture code must be provided for culture sensitive index. ");
        if (_indexByCulture.TryGetValue(ctx.CultureCode!, out var index)) return index;
        throw new Exception("Index " + typeof(T).Name + "not found for culture: " + ctx.CultureCode);
    }
}
