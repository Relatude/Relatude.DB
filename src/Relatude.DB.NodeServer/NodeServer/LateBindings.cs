using System.Reflection;
using Relatude.DB.AI;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;
using Relatude.DB.Tasks;

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
    public static IPersistentWordIndexFactory CreateLucenePersistentWordIndexFactory(string indexPath) {
        return create<IPersistentWordIndexFactory>("Relatude.DB.DataStores.Indexes.WordIndexLuceneFactory", "Relatude.DB.Lucene", "Relatude.DB.Plugins.Lucene", [indexPath]);
    }
    public static IPersistedIndexStore CreatePersistedIndexStore(string indexPath, IPersistentWordIndexFactory? wordIndexFactory) {
        return create<IPersistedIndexStore>("Relatude.DB.DataStores.Indexes.PersistedIndexStore", "Relatude.DB.Sqlite", "Relatude.DB.Plugins.Sqlite", [indexPath, wordIndexFactory]);
    }
    public static IQueueStore CreateSqliteQueueStore(string queuePath) {
        return create<IQueueStore>("Relatude.DB.Tasks.SqliteQueueStore", "Relatude.DB.Sqlite", "Relatude.DB.Plugins.Sqlite", [queuePath]);
    }
    public static IEmbeddingCache CreateSqlLiteEmbeddingCache(string? filePath) {
        return create<IEmbeddingCache>("Relatude.DB.AI.SqlLiteEmbeddingCache", "Relatude.DB.Sqlite", "Relatude.DB.Plugins.Sqlite", [filePath]);
    }
    internal static IAIProvider CreateAiProvider(AIProviderSettings aiSettings) {
        if (aiSettings.TypeName == "AzureAIProvider" || string.IsNullOrEmpty(aiSettings.TypeName)) {
            return create<IAIProvider>("Relatude.DB.AI.AzureAIProvider", "Relatude.DB.Azure", "Relatude.DB.Plugins.Azure", [aiSettings]);
        }
        if (aiSettings.TypeName == nameof(DummyAIProvider)) {
            return new DummyAIProvider();
        } else {
            return create<IAIProvider>(aiSettings.TypeName, null, null, [aiSettings]);
        }
    }
    internal static IIOProvider CreateAzureBlobIOProvider(IOSettings ioSettings) {
        return create<IIOProvider>("Relatude.DB.IO.AzureBlobIOProvider", "Relatude.DB.Azure", "Relatude.DB.Plugins.Azure", [ioSettings.BlobContainerName, ioSettings.BlobConnectionString, ioSettings.LockBlob]);
    }
}
