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

    /// <summary>
    /// The engine id a property's index storage choice resolves to. Default follows the store's
    /// default engine; Memory is always the memory index. Persisted also follows the default: when
    /// that is the memory index there is no engine to persist to, and the index stays in memory -
    /// the host logs a note about that combination at open, since it is legal but easy to miss.
    /// </summary>
    static Guid resolveEngineId(IndexStorageType storage, Guid defaultEngine, string settingName) {
        return storage switch {
            IndexStorageType.Default => defaultEngine,
            IndexStorageType.Memory => Guid.Empty,
            IndexStorageType.Persisted => defaultEngine,
            _ => throw new NotSupportedException(settingName + " not supported. "),
        };
    }
    /// <summary>The value engine a property's indexes go to, or null for the memory index.</summary>
    static IValueIndexEngine? valueEngine(DataStoreLocal store, Property property) {
        var id = resolveEngineId(property.Model.IndexType, store.Settings.DefaultValueIndex, "IndexType");
        return store.Engines.ValueEngine(id);
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
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureCode, subKey);
        var engine = valueEngine(store, property);
        IStringArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " String Array Index " + classDef.CodeName + "." + property.CodeName;
            index = engine.OpenStringArrayIndex(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory String Array Index " + classDef.CodeName + "." + property.CodeName;
            index = new StringArrayIndex(store._definition, uniqueKey, name, store.IOIndex, property.Id);
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
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureCode, subKey);
        var engine = valueEngine(store, property);
        IGuidArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " Guid Array Index " + classDef.CodeName + "." + property.CodeName;
            index = engine.OpenGuidArrayIndex(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Guid Array Index " + classDef.CodeName + "." + property.CodeName;
            index = new GuidArrayIndex(store._definition, uniqueKey, name, store.IOIndex, property.Id);
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
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureCode, subKey);
        var engine = valueEngine(store, property);
        IIntArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " Int Array Index " + classDef.CodeName + "." + property.CodeName;
            index = engine.OpenIntArrayIndex(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Int Array Index " + classDef.CodeName + "." + property.CodeName;
            index = new IntArrayIndex(store._definition, uniqueKey, name, store.IOIndex, property.Id);
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
        var uniqueKey = getUniqueKey(property, cultureCode, subKey);
        var engine = valueEngine(store, property);
        IValueIndex<T> index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " Value Index " + classDef.CodeName + "." + property.CodeName;
            // already wrapped in OptimizedValueIndex by the engine, which flushes the wrapper's
            // queued remove into the backend on every commit
            index = engine.OpenValueIndex<T>(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Value Index " + classDef.CodeName + "." + property.CodeName;
            index = new ValueIndex<T>(sets, uniqueKey, name, store.IOIndex, writeValue, readValue);
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
        var uniqueKey = getUniqueKey(p, cultureCode, subKey);
        var engineId = resolveEngineId(((StringPropertyModel)p.Model).TextIndexType, store.Settings.DefaultTextIndex, "TextIndexType");
        var engine = store.Engines.TextEngine(engineId);
        IWordIndex index;
        var classDef = store.Datamodel.NodeTypes[p.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " Word Index " + classDef.CodeName + "." + p.CodeName;
            // already wrapped in OptimizedWordIndex by the engine, which flushes the wrapper's
            // queued remove into the backend on every commit
            return engine.OpenWordIndex(sets, uniqueKey, name, new WordIndexOptions(p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch));
        } else {
            var name = "Memory Word Index " + classDef.CodeName + "." + p.CodeName;
            index = new WordIndexTrie(sets, uniqueKey, name, store.IOIndex, p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch, (t, e) => store.LogError(t, e));
        }
        if (!useOptimizedIndexes) return index;
        return new OptimizedWordIndex(index);
    }

    public static Dictionary<string, ISemanticIndex> CreateSemanticIndexes(DataStoreLocal store, AIEngine ai, FloatArrayProperty property, string? subKey) {
        Dictionary<string, ISemanticIndex> indexes = new();
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
    static ISemanticIndex createSemanticIndex(DataStoreLocal store, AIEngine ai, string? cultureCode, SetRegister sets, FloatArrayProperty p, string? subKey) {
        var def = store._definition;
        var classDef = def.Datamodel.NodeTypes[p.Model.NodeType];
        var uniqueKey = getUniqueKey(p, cultureCode, subKey);
        // semantic indexes have no per-property storage choice yet: every one follows the default
        var engine = store.Engines.VectorEngine(store.Settings.DefaultVectorIndex);
        if (engine != null) {
            var name = engine.Name + " Semantic Index " + classDef.CodeName + "." + p.Model.CodeName;
            return engine.OpenSemanticIndex(sets, uniqueKey, name, ai, t => store.LogInfo(t));
        }
        return new MemorySemanticIndex(def.Sets, uniqueKey, "Semantic " + classDef.CodeName + "." + p.Model.CodeName,
            store.IOIndex, ai);
    }

}
