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

    // A culture's index is keyed by the culture's id, as its code can change. The index of no culture has the key of
    // the property's one index when it is not culture-sensitive: it holds the same values (Definition.IteratePropertyIndexes),
    // so making a property culture-sensitive keeps it and only adds the cultures' indexes.
    internal static string getUniqueKey(Property property, Guid? cultureId, string? subKey) {
        return property.Id
            + (cultureId is { } culture && culture != Guid.Empty ? "_" + culture.ToString("N") : "")
            + (string.IsNullOrEmpty(subKey) ? "" : "_" + subKey);
    }
    // what the index's name says of its culture, the code when the store knows it already (not while it opens)
    static string cultureOf(DataStoreLocal store, Guid? cultureId) {
        if (cultureId is not { } culture || culture == Guid.Empty) return "";
        return " (" + (store._nativeModelStore.TryGetCultureCode(culture, out var code) && !string.IsNullOrEmpty(code) ? code : culture.ToString("N")[..8]) + ")";
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

    public static IStringArrayIndex CreateStringArrayIndex(DataStoreLocal store, Property property, Guid? cultureId, string? subKey) {
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureId, subKey);
        var engine = valueEngine(store, property);
        IStringArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " String Array Index " + classDef.CodeName + "." + property.CodeName + cultureOf(store, cultureId);
            index = engine.OpenStringArrayIndex(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory String Array Index " + classDef.CodeName + "." + property.CodeName + cultureOf(store, cultureId);
            index = new StringArrayIndex(store._definition, uniqueKey, name, store.IOIndex, property.Id);
        }
        return index;
    }

    public static IGuidArrayIndex CreateGuidArrayIndex(DataStoreLocal store, Property property, Guid? cultureId, string? subKey) {
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureId, subKey);
        var engine = valueEngine(store, property);
        IGuidArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " Guid Array Index " + classDef.CodeName + "." + property.CodeName + cultureOf(store, cultureId);
            index = engine.OpenGuidArrayIndex(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Guid Array Index " + classDef.CodeName + "." + property.CodeName + cultureOf(store, cultureId);
            index = new GuidArrayIndex(store._definition, uniqueKey, name, store.IOIndex, property.Id);
        }
        return index;
    }

    public static IIntArrayIndex CreateIntArrayIndex(DataStoreLocal store, Property property, Guid? cultureId, string? subKey) {
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureId, subKey);
        var engine = valueEngine(store, property);
        IIntArrayIndex index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " Int Array Index " + classDef.CodeName + "." + property.CodeName + cultureOf(store, cultureId);
            index = engine.OpenIntArrayIndex(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Int Array Index " + classDef.CodeName + "." + property.CodeName + cultureOf(store, cultureId);
            index = new IntArrayIndex(store._definition, uniqueKey, name, store.IOIndex, property.Id);
        }
        return index;
    }

    public static IValueIndex<T> CreateValueIndex<T>(DataStoreLocal store, Property property, Guid? cultureId, string? subKey, Action<T, IAppendStream> writeValue, Func<IReadStream, T> readValue) where T : notnull {
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(property, cultureId, subKey);
        var engine = valueEngine(store, property);
        IValueIndex<T> index;
        var classDef = store.Datamodel.NodeTypes[property.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " Value Index " + classDef.CodeName + "." + property.CodeName + cultureOf(store, cultureId);
            // already wrapped in OptimizedValueIndex by the engine, which flushes the wrapper's
            // queued remove into the backend on every commit
            index = engine.OpenValueIndex<T>(sets, uniqueKey, name, property.PropertyType);
        } else {
            var name = "Memory Value Index " + classDef.CodeName + "." + property.CodeName + cultureOf(store, cultureId);
            index = new ValueIndex<T>(sets, uniqueKey, name, store.IOIndex, writeValue, readValue);
            if (useOptimizedIndexes) index = new OptimizedValueIndex<T>(index);
        }
        return index;
    }

    public static IWordIndex CreateWordIndex(DataStoreLocal store, StringProperty p, Guid? cultureId, string? subKey) {
        var sets = store._definition.Sets;
        var uniqueKey = getUniqueKey(p, cultureId, subKey);
        var engineId = resolveEngineId(((StringPropertyModel)p.Model).TextIndexType, store.Settings.DefaultTextIndex, "TextIndexType");
        var engine = store.Engines.TextEngine(engineId);
        IWordIndex index;
        var classDef = store.Datamodel.NodeTypes[p.Model.NodeType];
        if (engine != null) {
            var name = engine.Name + " Word Index " + classDef.CodeName + "." + p.CodeName + cultureOf(store, cultureId);
            // already wrapped in OptimizedWordIndex by the engine, which flushes the wrapper's
            // queued remove into the backend on every commit
            return engine.OpenWordIndex(sets, uniqueKey, name, new WordIndexOptions(p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch));
        } else {
            var name = "Memory Word Index " + classDef.CodeName + "." + p.CodeName + cultureOf(store, cultureId);
            index = new WordIndexTrie(sets, uniqueKey, name, store.IOIndex, p.MinWordLength, p.MaxWordLength, p.PrefixSearch, p.InfixSearch, (t, e) => store.LogError(t, e));
        }
        if (!useOptimizedIndexes) return index;
        return new OptimizedWordIndex(index);
    }

    public static ISemanticIndex CreateSemanticIndex(DataStoreLocal store, AIEngine ai, FloatArrayProperty p, Guid? cultureId, string? subKey) {
        var def = store._definition;
        var sets = def.Sets;
        var classDef = def.Datamodel.NodeTypes[p.Model.NodeType];
        var uniqueKey = getUniqueKey(p, cultureId, subKey);
        // semantic indexes have no per-property storage choice yet: every one follows the default
        var engine = store.Engines.VectorEngine(store.Settings.DefaultVectorIndex);
        if (engine != null) {
            var name = engine.Name + " Semantic Index " + classDef.CodeName + "." + p.Model.CodeName + cultureOf(store, cultureId);
            return engine.OpenSemanticIndex(sets, uniqueKey, name, ai, t => store.LogInfo(t));
        }
        return new MemorySemanticIndex(sets, uniqueKey, "Semantic " + classDef.CodeName + "." + p.Model.CodeName + cultureOf(store, cultureId),
            store.IOIndex, ai);
    }

}
