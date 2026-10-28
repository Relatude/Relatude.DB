using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Indexes;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores.Definitions;

/// <summary>
/// A property's indexes of one kind. A property that is not culture-sensitive has one. One that is has an index for
/// no culture (<see cref="Guid.Empty"/>) and one for each of the store's cultures, each holding what a reader in its
/// culture reads (<see cref="Definition.IteratePropertyIndexes"/>), and a query uses the one of its culture
/// (<see cref="Definition.IndexCultureOf"/>). The cultures' indexes come and go with the cultures
/// (<see cref="Definition.SyncCultureIndexes"/>).
/// </summary>
internal class IndexUtil<T> : ICultureIndexes where T : class, IIndex {
    T? _index;
    // replaced whole when a culture comes or goes, which is under the store's write lock
    Dictionary<Guid, T> _byCulture = [];
    Func<Guid?, T> _create = null!;
    Property _owner = null!;
    /// <param name="owner">The property the indexes are of.</param>
    /// <param name="create">Makes the index of a culture, or of no culture: null when the property is not culture-sensitive.</param>
    public void Initalize(Property owner, Func<Guid?, T> create) {
        _owner = owner;
        _create = create;
        if (owner.CultureSensitive) {
            var byCulture = new Dictionary<Guid, T>();
            foreach (var culture in owner.Definition.IndexedCultures.Prepend(Guid.Empty)) {
                var index = create(culture);
                byCulture[culture] = index;
                owner.AddIndex(index, culture);
            }
            _byCulture = byCulture;
            owner.CultureIndexes.Add(this);
        } else {
            _index = create(null);
            owner.AddIndex(_index, Guid.Empty);
        }
    }
    public bool TryGetIndex(QueryContext ctx, [MaybeNullWhen(false)] out T index) {
        index = _index ?? forCulture(ctx);
        return index != null;
    }
    public T GetIndex(QueryContext ctx) => _index ?? forCulture(ctx) ?? throw new Exception("The property " + _owner.CodeName + " has no " + typeof(T).Name + ". ");
    // a culture with no index yet reads the one of no culture, which holds what its readers get from every node
    // without a revision of its own in it: every node, until its index is made
    T? forCulture(QueryContext ctx) {
        var byCulture = _byCulture;
        if (byCulture.Count == 0) return null;
        return byCulture.TryGetValue(_owner.Definition.IndexCultureOf(ctx), out var index) ? index : byCulture[Guid.Empty];
    }
    public IIndex? AddCulture(Guid cultureId) {
        if (_byCulture.ContainsKey(cultureId)) return null;
        var index = _create(cultureId);
        _byCulture = new(_byCulture) { [cultureId] = index };
        _owner.AddIndex(index, cultureId);
        return index;
    }
    public IIndex? RemoveCulture(Guid cultureId) {
        if (cultureId == Guid.Empty || !_byCulture.TryGetValue(cultureId, out var index)) return null;
        var byCulture = new Dictionary<Guid, T>(_byCulture);
        byCulture.Remove(cultureId);
        _byCulture = byCulture;
        _owner.RemoveIndex(index);
        return index;
    }
}

/// <summary>The indexes of one kind a culture-sensitive property has per culture (<see cref="IndexUtil{T}"/>).</summary>
internal interface ICultureIndexes {
    /// <summary>Makes the culture's index; null when it has one.</summary>
    IIndex? AddCulture(Guid cultureId);
    /// <summary>Takes the culture's index away; null when it has none.</summary>
    IIndex? RemoveCulture(Guid cultureId);
}
