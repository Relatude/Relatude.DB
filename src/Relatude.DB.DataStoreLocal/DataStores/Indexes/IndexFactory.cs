using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.Trie;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using System.Xml.Linq;

namespace Relatude.DB.DataStores.Indexes;
internal static class IndexFactory {
    static bool useOptimizedIndexes = true;
    public static IValueIndex<T> CreateValueIndex<T>(DataStoreLocal store, SetRegister sets, Property property, string? subKey, Action<T, IAppendStream> writeValue, Func<IReadStream, T> readValue) where T : notnull {
        var settings = store.Settings;
        var uniqueKey = property.Id + (string.IsNullOrEmpty(subKey) ? "" : "_" + subKey);
        var useProvider = property.Model.IndexType switch {
            IndexStorageType.Default => settings.UsePersistedValueIndexesByDefault,
            IndexStorageType.Memory => false,
            IndexStorageType.Persisted => true,
            _ => throw new NotSupportedException("IndexType not supported. "),
        };
        IValueIndex<T> index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        var name = "Semantic " + classDef.CodeName + "." + property.CodeName;
        if (useProvider && store.PersistedIndexStore != null) {
            index = store.PersistedIndexStore.OpenValueIndex<T>(sets, uniqueKey, name, property.PropertyType);
        } else {
            index = new ValueIndex<T>(sets, uniqueKey, name, store.IOIndex, store.FileKeys, writeValue, readValue);
        }
        if (!useOptimizedIndexes) return index;
        return new OptimizedValueIndex<T>(index);
    }
    internal static IWordIndex CreateWordIndex(DataStoreLocal store, SetRegister sets, StringProperty p) {
        var settings = store.Settings;
        var uniqueKey = p.Id + nameof(IWordIndex);
        var useProvider = ((StringPropertyModel)p.Model).TextIndexType switch {
            IndexStorageType.Default => settings.UsePersistedTextIndexesByDefault,
            IndexStorageType.Memory => false,
            IndexStorageType.Persisted => true,
            _ => throw new NotSupportedException("TextIndexType not supported. "),
        };
        IWordIndex index;
        var classDef = store.Datamodel.NodeTypes[p.Model.NodeType];
        var name = "Semantic " + classDef.CodeName + "." + p.CodeName;
        if (useProvider && store.PersistedIndexStore != null) {
            index = store.PersistedIndexStore.OpenWordIndex(sets, uniqueKey, name, p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch);
        } else {
            index = new WordIndexTrie(sets, uniqueKey, name, store.IOIndex, store.FileKeys, p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch);
        }
        if (!useOptimizedIndexes) return index;
        return new OptimizedWordIndex(index);
    }
    //public static IWordIndex CreateWordIndex(DataStoreLocal store, SetRegister sets, Property property, string? subKey, Action<T, IAppendStream> writeValue, Func<IReadStream, T> readValue) where T : notnull {
    //    var settings = store.Settings;
    //    var uniqueKey = property.Id + (string.IsNullOrEmpty(subKey) ? "" : "_" + subKey);
    //    var useProvider = property.Model.IndexType switch {
    //        IndexStorageType.Default => settings.UsePersistedIndexStoreByDefault,
    //        IndexStorageType.Memory => false,
    //        IndexStorageType.Persisted => true,
    //        _ => throw new NotSupportedException("IndexType not supported. "),
    //    };
    //    if (useProvider && settings.EnablePersistedIndexStore && store.PersistedIndexStore != null) {
    //        return store.PersistedIndexStore.OpenValueIndex<T>(sets, uniqueKey, property.PropertyType);
    //    } else {
    //        return new ValueIndex<T>(sets, uniqueKey, writeValue, readValue);
    //    }
    //}
}
