using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Indexes.Trie;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using System;
using System.Text.RegularExpressions;

namespace Relatude.DB.DataStores.Indexes;

internal static class IndexFactory {
    static bool useOptimizedIndexes = true;
    public static Dictionary<string, IValueIndex<T>> CreateValueIndexes<T>(DataStoreLocal store, SetRegister sets, Property property, string? subKey, Action<T, IAppendStream> writeValue, Func<IReadStream, T> readValue) where T : notnull {
        Dictionary<string, IValueIndex<T>> indexes = new();
        if (property.Model.CultureSensitive) {
            foreach (var culture in store._nativeModelStore.Cultures) {
                var index = CreateValueIndex<T>(store, culture.CultureCode, sets, property, subKey, writeValue, readValue);
                indexes[culture.CultureCode] = index;
            }
        } else {
            var index = CreateValueIndex<T>(store, null, sets, property, subKey, writeValue, readValue);
            indexes[string.Empty] = index;
        }
        return indexes;
    }
    static IValueIndex<T> CreateValueIndex<T>(DataStoreLocal store, string? cultureCode, SetRegister sets, Property property, string? subKey, Action<T, IAppendStream> writeValue, Func<IReadStream, T> readValue) where T : notnull {
        var settings = store.Settings;
        var uniqueKey = property.Id
            + (string.IsNullOrEmpty(cultureCode) ? "" : "_" + cultureCode)
            + (string.IsNullOrEmpty(subKey) ? "" : "_" + subKey);
        var useProvider = property.Model.IndexType switch {
            IndexStorageType.Default => settings.UsePersistedValueIndexesByDefault,
            IndexStorageType.Memory => false,
            IndexStorageType.Persisted => true,
            _ => throw new NotSupportedException("IndexType not supported. "),
        };
        IValueIndex<T> index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (useProvider && store.PersistedIndexStore != null) {
            var name = (store.PersistedIndexStore.GetType()!.Name).Decamelize() + " Value Index " + classDef.CodeName + "." + property.CodeName;
            index = store.PersistedIndexStore.OpenValueIndex<T>(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Value Index " + classDef.CodeName + "." + property.CodeName;
            index = new ValueIndex<T>(sets, uniqueKey, name, store.IOIndex, store.FileKeys, writeValue, readValue);
        }
        if (useOptimizedIndexes) index = new OptimizedValueIndex<T>(index);
        return index;
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
        if (useProvider && store.PersistedIndexStore != null) {
            var name = (store.PersistedIndexStore.GetType()!.Name).Decamelize() + " Word Index " + classDef.CodeName + "." + p.CodeName;
            index = store.PersistedIndexStore.OpenWordIndex(sets, uniqueKey, name, p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch);
        } else {
            var name = "Memory Word Index " + classDef.CodeName + "." + p.CodeName;
            index = new WordIndexTrie(sets, uniqueKey, name, store.IOIndex, store.FileKeys, p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch);
        }
        if (!useOptimizedIndexes) return index;
        return new OptimizedWordIndex(index);
    }
}
