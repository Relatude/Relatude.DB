using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Indexes.Trie;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;

internal static class IndexFactory {
    static bool useOptimizedIndexes = true;

    internal static string getUniqueKey(Property property, string? cultureCode, string? subKey) {
        return property.Id
            + (string.IsNullOrEmpty(cultureCode) ? "" : "_" + cultureCode)
            + (string.IsNullOrEmpty(subKey) ? "" : "_" + subKey);
    }

    public static Dictionary<string, IStringArrayIndex> CreateStringArrayIndexes(DataStoreLocal store, Property property, string? subKey) {
        Dictionary<string, IStringArrayIndex> indexes = new();
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
    static IStringArrayIndex createStringArrayIndex(DataStoreLocal store, string? cultureCode, Property property, string? subKey) {
        var settings = store.Settings;
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureCode, subKey);
        var useProvider = property.Model.IndexType switch {
            IndexStorageType.Default => settings.UsePersistedValueIndexesByDefault,
            IndexStorageType.Memory => false,
            IndexStorageType.Persisted => true,
            _ => throw new NotSupportedException("IndexType not supported. "),
        };
        IStringArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (useProvider && store.Engines.Value != null) {
            var name = store.Engines.Value.Name + " String Array Index " + classDef.CodeName + "." + property.CodeName;
            index = store.Engines.Value.OpenStringArrayIndex(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory String Array Index " + classDef.CodeName + "." + property.CodeName;
            index = new StringArrayIndex(store._definition, uniqueKey, name, store.IOIndex, store.FileKeys, property.Id);
        }
        return index;
    }

    public static Dictionary<string, IGuidArrayIndex> CreateGuidArrayIndexes(DataStoreLocal store, Property property, string? subKey) {
        Dictionary<string, IGuidArrayIndex> indexes = new();
        if (property.Model.CultureSensitive) {
            foreach (var culture in store._nativeModelStore.Cultures) {
                var index = createGuidArrayIndex(store, culture.CultureCode, property, subKey);
                indexes[culture.CultureCode] = index;
            }
        } else {
            var index = createGuidArrayIndex(store, null, property, subKey);
            indexes[string.Empty] = index;
        }
        return indexes;
    }
    static IGuidArrayIndex createGuidArrayIndex(DataStoreLocal store, string? cultureCode, Property property, string? subKey) {
        var settings = store.Settings;
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureCode, subKey);
        var useProvider = property.Model.IndexType switch {
            IndexStorageType.Default => settings.UsePersistedValueIndexesByDefault,
            IndexStorageType.Memory => false,
            IndexStorageType.Persisted => true,
            _ => throw new NotSupportedException("IndexType not supported. "),
        };
        IGuidArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (useProvider && store.Engines.Value != null) {
            var name = store.Engines.Value.Name + " Guid Array Index " + classDef.CodeName + "." + property.CodeName;
            index = store.Engines.Value.OpenGuidArrayIndex(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Guid Array Index " + classDef.CodeName + "." + property.CodeName;
            index = new GuidArrayIndex(store._definition, uniqueKey, name, store.IOIndex, store.FileKeys, property.Id);
        }
        return index;
    }

    public static Dictionary<string, IIntArrayIndex> CreateIntArrayIndexes(DataStoreLocal store, Property property, string? subKey) {
        Dictionary<string, IIntArrayIndex> indexes = new();
        if (property.Model.CultureSensitive) {
            foreach (var culture in store._nativeModelStore.Cultures) {
                var index = createIntArrayIndex(store, culture.CultureCode, property, subKey);
                indexes[culture.CultureCode] = index;
            }
        } else {
            var index = createIntArrayIndex(store, null, property, subKey);
            indexes[string.Empty] = index;
        }
        return indexes;
    }
    static IIntArrayIndex createIntArrayIndex(DataStoreLocal store, string? cultureCode, Property property, string? subKey) {
        var settings = store.Settings;
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureCode, subKey);
        var useProvider = property.Model.IndexType switch {
            IndexStorageType.Default => settings.UsePersistedValueIndexesByDefault,
            IndexStorageType.Memory => false,
            IndexStorageType.Persisted => true,
            _ => throw new NotSupportedException("IndexType not supported. "),
        };
        IIntArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (useProvider && store.Engines.Value != null) {
            var name = store.Engines.Value.Name + " Int Array Index " + classDef.CodeName + "." + property.CodeName;
            index = store.Engines.Value.OpenIntArrayIndex(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Int Array Index " + classDef.CodeName + "." + property.CodeName;
            index = new IntArrayIndex(store._definition, uniqueKey, name, store.IOIndex, store.FileKeys, property.Id);
        }
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
        var uniqueKey = getUniqueKey(property, cultureCode, subKey);
        var useProvider = property.Model.IndexType switch {
            IndexStorageType.Default => settings.UsePersistedValueIndexesByDefault,
            IndexStorageType.Memory => false,
            IndexStorageType.Persisted => true,
            _ => throw new NotSupportedException("IndexType not supported. "),
        };
        IValueIndex<T> index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (useProvider && store.Engines.Value != null) {
            var name = store.Engines.Value.Name + " Value Index " + classDef.CodeName + "." + property.CodeName;
            // already wrapped in OptimizedValueIndex by the engine, which flushes the wrapper's
            // queued remove into the backend on every commit
            index = store.Engines.Value.OpenValueIndex<T>(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Value Index " + classDef.CodeName + "." + property.CodeName;
            index = new ValueIndex<T>(sets, uniqueKey, name, store.IOIndex, store.FileKeys, writeValue, readValue);
            if (useOptimizedIndexes) index = new OptimizedValueIndex<T>(index);
        }
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
        var uniqueKey = getUniqueKey(p, cultureCode, subKey);
        var useProvider = ((StringPropertyModel)p.Model).TextIndexType switch {
            IndexStorageType.Default => settings.UsePersistedTextIndexesByDefault,
            IndexStorageType.Memory => false,
            IndexStorageType.Persisted => true,
            _ => throw new NotSupportedException("TextIndexType not supported. "),
        };
        IWordIndex index;
        var classDef = store.Datamodel.NodeTypes[p.Model.NodeType];
        if (useProvider && store.Engines.Text != null) {
            var name = store.Engines.Text.Name + " Word Index " + classDef.CodeName + "." + p.CodeName;
            // already wrapped in OptimizedWordIndex by the engine, which flushes the wrapper's
            // queued remove into the backend on every commit
            return store.Engines.Text.OpenWordIndex(sets, uniqueKey, name, new WordIndexOptions(p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch));
        } else {
            var name = "Memory Word Index " + classDef.CodeName + "." + p.CodeName;
            index = new WordIndexTrie(sets, uniqueKey, name, store.IOIndex, store.FileKeys, p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch, (t, e) => store.LogError(t, e));
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
        var uniqueKey = getUniqueKey(p, cultureCode, subKey);
        return new SemanticIndex(def.Sets, uniqueKey, name, store.IOIndex, store.FileKeys, ai, t => store.LogInfo(t));
    }

}
