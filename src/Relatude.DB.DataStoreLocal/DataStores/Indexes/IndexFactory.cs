using Relatude.DB.AI;
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
    internal static string GetIndexUniqueKey(Property property, string? cultureCode, string? subKey) {
        return property.Id
            + (string.IsNullOrEmpty(cultureCode) ? "" : "_" + cultureCode)
            + (string.IsNullOrEmpty(subKey) ? "" : "_" + subKey);
    }
    public static Dictionary<string, StringArrayIndex> CreateStringArrayIndexes(DataStoreLocal store, Property property, string? subKey) {
        Dictionary<string, StringArrayIndex> indexes = new();
        if (property.Model.CultureSensitive) {
            foreach (var culture in store._nativeModelStore.Cultures) {
                var index = createStringArrayIndex(store, culture.CultureCode, property, subKey);
                indexes[culture.CultureCode] = index;
            }
        } else {
            var index = createStringArrayIndex(store, null, property, subKey);
            indexes[string.Empty] = index;
        }
        return indexes;
    }
    static StringArrayIndex createStringArrayIndex(DataStoreLocal store, string? cultureCode, Property property, string? subKey) {
        var settings = store.Settings;
        var sets = store._definition.Sets;
        var uniqueKey = GetIndexUniqueKey(property, cultureCode, subKey);
        StringArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        var name = "Memory String Array Index " + classDef.CodeName + "." + property.CodeName;
        index = new StringArrayIndex(store._definition, uniqueKey, name, store.IOIndex, store.FileKeys, property.Id);
        return index;
    }

    public static Dictionary<string, IValueIndex<T>> CreateValueIndexes<T>(DataStoreLocal store, Property property, string? subKey, Action<T, IAppendStream> writeValue, Func<IReadStream, T> readValue) where T : notnull {
        Dictionary<string, IValueIndex<T>> indexes = new();
        var sets = store._definition.Sets;
        if (property.Model.CultureSensitive) {
            foreach (var culture in store._nativeModelStore.Cultures) {
                var index = createValueIndex<T>(store, culture.CultureCode, sets, property, subKey, writeValue, readValue);
                indexes[culture.CultureCode] = index;
            }
        } else {
            var index = createValueIndex<T>(store, null, sets, property, subKey, writeValue, readValue);
            indexes[string.Empty] = index;
        }
        return indexes;
    }
    static IValueIndex<T> createValueIndex<T>(DataStoreLocal store, string? cultureCode, SetRegister sets, Property property, string? subKey, Action<T, IAppendStream> writeValue, Func<IReadStream, T> readValue) where T : notnull {
        var settings = store.Settings;
        var uniqueKey = GetIndexUniqueKey(property, cultureCode, subKey);
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

    public static Dictionary<string, IWordIndex> CreateWordIndexes(DataStoreLocal store, StringProperty property, string? subKey) {
        Dictionary<string, IWordIndex> indexes = new();
        var sets = store._definition.Sets;
        if (property.Model.CultureSensitive) {
            foreach (var culture in store._nativeModelStore.Cultures) {
                var index = createWordIndex(store, culture.CultureCode, sets, property, subKey);
                indexes[culture.CultureCode] = index;
            }
        } else {
            var index = createWordIndex(store, null, sets, property, subKey);
            indexes[string.Empty] = index;
        }
        return indexes;

    }
    static IWordIndex createWordIndex(DataStoreLocal store, string? cultureCode, SetRegister sets, StringProperty p, string? subKey) {
        var settings = store.Settings;
        var uniqueKey = GetIndexUniqueKey(p, cultureCode, subKey);
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

    public static Dictionary<string, SemanticIndex> CreateSemanticIndexes(DataStoreLocal store, AIEngine ai, FloatArrayProperty property, string? subKey) {
        Dictionary<string, SemanticIndex> indexes = new();
        var sets = store._definition.Sets;
        if (property.Model.CultureSensitive) {
            foreach (var culture in store._nativeModelStore.Cultures) {
                var index = createSemanticIndex(store, ai, culture.CultureCode, sets, property, subKey);
                indexes[culture.CultureCode] = index;
            }
        } else {
            var index = createSemanticIndex(store, ai, null, sets, property, subKey);
            indexes[string.Empty] = index;
        }
        return indexes;

    }
    static SemanticIndex createSemanticIndex(DataStoreLocal store, AIEngine ai, string? cultureCode, SetRegister sets, FloatArrayProperty p, string? subKey) {
        var def = store._definition;
        var classDef = def.Datamodel.NodeTypes[p.Model.NodeType];
        var name = "Semantic " + classDef.CodeName + "." + p.Model.CodeName;
        var uniqueKey = GetIndexUniqueKey(p, cultureCode, subKey);
        return new SemanticIndex(def.Sets, uniqueKey, name, store.IOIndex, store.FileKeys, ai);
    }

}
