using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.IO;
using Relatude.DB.Tasks;
using System.Reflection;

namespace Relatude.DB.NodeServer;
/// <summary>
/// Utility class to create instances of types from optional dependencies using late binding.
/// With focus on providing better error messages when the dependency is missing.
/// </summary>
public static class LateBindings {
    private static Type findType(string typeName, string? moduleName, string? nugetName) {
        var type = Type.GetType(typeName);
        if (type != null) return type;
        if (moduleName == null) throw new Exception($"The type \"{typeName}\" was not found. You may need to reference a nuget. {nugetName}");
        Assembly ass;
        try {
            ass = Assembly.Load(new AssemblyName(moduleName));
        } catch (Exception ex) {
            throw new Exception($"Unable to load the assembly \"{moduleName}\". Verify you are referencing the correct nuget: \"{nugetName}\". " + ex.Message, ex);
        }
        type = ass.GetType(typeName);
        if (type == null) throw new Exception($"The type \"{typeName}\" was not found in the assembly \"{moduleName}\". Verify you are referencing the correct nuget: \"{nugetName}\"");
        return type;
    }
    private static T create<T>(string typeName, string? moduleName, string? nugetName, object?[]? parameteres) {
        var type = findType(typeName, moduleName, nugetName);
        if (Activator.CreateInstance(type, parameteres) is T instance) return instance;
        throw new Exception($"The type {typeName} does not implement the interface {typeof(T).FullName} " +
            $"or the constructor parameters do not match. Make sure the nuget package {nugetName} is correctly referenced.");
    }

    // ---- index engines, resolved by IndexEngineSettings.TypeName ------------------------------
    // The built-in engines are known by the short names in IndexEngineTypes; anything else is taken
    // as the full type name of a custom engine, constructed with the engine folder as its only
    // argument (so a custom engine gets no memory budget). LateBindingsTests checks that every
    // name the settings page suggests is recognised here.

    /// <summary>The engine behind a value index engine entry, writing below <paramref name="engineFolder"/>.</summary>
    public static IValueIndexEngine CreateValueIndexEngine(IndexEngineSettings engine, string engineFolder) {
        var bytes = engine.MaxMemoryUsageInBytes;
        if (IndexEngineTypes.Is(engine.TypeName, IndexEngineTypes.Native)) return new NativeKvIndexStore(engineFolder, null, bytes);
        if (IndexEngineTypes.Is(engine.TypeName, IndexEngineTypes.Sqlite)) return CreateSqliteIndexStore(engineFolder, bytes);
        return createCustom<IValueIndexEngine>(engine, engineFolder, "value", IndexEngineTypes.ValueEngines);
    }
    /// <summary>The engine behind a text index engine entry, writing below <paramref name="engineFolder"/>.
    /// For SQLite, prefer <see cref="CreateSqliteIndexStore"/> shared with the value engine when both are
    /// SQLite, so all index data commits in one SQLite transaction (see <see cref="NodeStoreContainer"/>).</summary>
    public static ITextIndexEngine CreateTextIndexEngine(IndexEngineSettings engine, string engineFolder) {
        var bytes = engine.MaxMemoryUsageInBytes;
        if (IndexEngineTypes.Is(engine.TypeName, IndexEngineTypes.Native)) return new TextIndexEngine(engineFolder, new TextIndexOptions { MaxCacheBytes = bytes });
        if (IndexEngineTypes.Is(engine.TypeName, IndexEngineTypes.Sqlite)) return (ITextIndexEngine)CreateSqliteIndexStore(engineFolder, bytes);
        if (IndexEngineTypes.Is(engine.TypeName, IndexEngineTypes.Lucene)) {
            return create<ITextIndexEngine>("Relatude.DB.DataStores.Indexes.LuceneTextIndexEngine", "Relatude.DB.Lucene", "Relatude.DB.Plugins.Lucene", [engineFolder, bytes]);
        }
        return createCustom<ITextIndexEngine>(engine, engineFolder, "text", IndexEngineTypes.TextEngines);
    }
    /// <summary>The engine behind a vector index engine entry, writing below <paramref name="engineFolder"/>.</summary>
    public static ISemanticIndexEngine CreateVectorIndexEngine(IndexEngineSettings engine, string engineFolder) {
        var bytes = engine.MaxMemoryUsageInBytes;
        if (IndexEngineTypes.Is(engine.TypeName, IndexEngineTypes.IVS)) {
            return new ISVEngine(engineFolder, new Relatude.DB.AI.ISV.VectorIndexOptions { MaxCacheBytes = bytes });
        }
        if (IndexEngineTypes.Is(engine.TypeName, IndexEngineTypes.HNSW)) {
            return new HnswEngine(engineFolder, new Relatude.DB.AI.HNSW.VectorIndexOptions { MaxMemoryBytes = bytes });
        }
        return createCustom<ISemanticIndexEngine>(engine, engineFolder, "vector", IndexEngineTypes.VectorEngines);
    }
    /// <summary>The SQLite engine, which serves value indexes and FTS5 word indexes from one database.</summary>
    public static IValueIndexEngine CreateSqliteIndexStore(string engineFolder, long maxMemoryBytes) {
        return create<IValueIndexEngine>("Relatude.DB.DataStores.Indexes.SqliteIndexStore", "Relatude.DB.Sqlite", "Relatude.DB.Plugins.Sqlite", [engineFolder, maxMemoryBytes]);
    }
    static T createCustom<T>(IndexEngineSettings engine, string engineFolder, string kind, string[] known) {
        if (string.IsNullOrWhiteSpace(engine.TypeName)) throw new Exception("The " + kind + " index engine " + engine.Id + " has no TypeName. Use one of " + string.Join(", ", known) + " or the full type name of a custom engine. ");
        try {
            return create<T>(engine.TypeName, null, null, [engineFolder]);
        } catch (Exception err) {
            throw new Exception("The " + kind + " index engine " + engine.Id + " names the type \"" + engine.TypeName + "\", which is neither a built-in engine ("
                + string.Join(", ", known) + ") nor a custom engine type that could be created: " + err.Message, err);
        }
    }

    public static IQueueStore CreateSqliteQueueStore(string queuePath) {
        return create<IQueueStore>("Relatude.DB.Tasks.SqliteQueueStore", "Relatude.DB.Sqlite", "Relatude.DB.Plugins.Sqlite", [queuePath]);
    }
    public static IEmbeddingCache CreateSqlLiteEmbeddingCache(string? filePath) {
        return create<IEmbeddingCache>("Relatude.DB.AI.SqlLiteEmbeddingCache", "Relatude.DB.Sqlite", "Relatude.DB.Plugins.Sqlite", [filePath]);
    }
    internal static IAIProvider CreateAiProvider(AIProviderSettings aiSettings) {
        switch (aiSettings.TypeName) {
            case null or "" or nameof(NativeAzureAIProvider) or "AzureAI":
                return new NativeAzureAIProvider(aiSettings);
            case nameof(NativeOpenAIProvider) or "OpenAI":
                return new NativeOpenAIProvider(aiSettings);
            case nameof(NativeAnthropicAIProvider) or "Anthropic":
                return new NativeAnthropicAIProvider(aiSettings);
            case nameof(DummyAIProvider) or "Dummy":
                return new DummyAIProvider();
            default:
                return create<IAIProvider>(aiSettings.TypeName, null, null, [aiSettings]);
        }
    }
    internal static IIOProvider CreateAzureBlobIOProvider(IOSettings ioSettings) {
        if (ioSettings.BlobContainerName == null) throw new Exception("BlobContainerName is required for AzureBlobIOProvider.");
        if (ioSettings.BlobConnectionString == null) throw new Exception("BlobConnectionString is required for AzureBlobIOProvider.");
        return new AzureBlobIOProvider(ioSettings.BlobContainerName, ioSettings.BlobConnectionString, ioSettings.LockBlob);
        //return create<IIOProvider>("Relatude.DB.IO.AzureBlobIOProvider", "Relatude.DB.Providers", "Relatude.DB.Plugins.Providers", [ioSettings.BlobContainerName, ioSettings.BlobConnectionString, ioSettings.LockBlob]);
    }
}
