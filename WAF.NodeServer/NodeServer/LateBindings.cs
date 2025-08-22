using System.Reflection;
using WAF.AI;
using WAF.DataStores.Indexes;
using WAF.IO;
using WAF.Tasks;

namespace WAF.NodeServer;
public static class LateBindings {
    private static Type findType(string typeName, string moduleName, string nugetName) {
        var type = Type.GetType(typeName);
        if (type != null) return type;
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
    private static T create<T>(string typeName, string moduleName, string nugetName, object?[]? parameteres) {
        var type = findType(typeName, moduleName, nugetName);
        if (Activator.CreateInstance(type, parameteres) is T instance) return instance;
        throw new Exception($"The type {typeName} does not implement the interface {typeof(T).FullName} " +
            $"or the constructor parameters do not match. Make sure the nuget package {nugetName} is correctly referenced.");
    }
    public static IPersistentWordIndexFactory CreateLucenePersistentWordIndexFactory(string indexPath) {
        return create<IPersistentWordIndexFactory>("WAF.DataStores.Indexes.WordIndexLuceneFactory", "WAF.Lucene", "Relatude.DB.Plugins.Lucene", [indexPath]);
    }
    public static IPersistedIndexStore CreatePersistedIndexStore(string indexPath, IPersistentWordIndexFactory? wordIndexFactory) {
        return create<IPersistedIndexStore>("WAF.DataStores.Indexes.PersistedIndexStore", "WAF.Sqlite", "Relatude.DB.Plugins.Sqlite", [indexPath, wordIndexFactory]);
    }
    public static IQueueStore CreateSqliteQueueStore(string queuePath) {
        return create<IQueueStore>("WAF.Tasks.SqliteQueueStore", "WAF.Sqlite", "Relatude.DB.Plugins.Sqlite", [queuePath]);
    }
    public static IEmbeddingCache CreateSqlLiteEmbeddingCache(string? filePath, Action<string> log) {
        return create<IEmbeddingCache>("WAF.AI.SqlLiteEmbeddingCache", "WAF.Sqlite", "Relatude.DB.Plugins.Sqlite", [filePath, log]);
    }
    internal static IAIProvider CreateAzureAiProvider(AIProviderSettings aiSettings, IEmbeddingCache cache) {
        return create<IAIProvider>("WAF.AI.AzureAiProvider", "WAF.Azure", "Relatude.DB.Plugins.Azure", [aiSettings, cache]);
    }
    internal static IIOProvider CreateAzureBlobIOProvider(IOSettings ioSettings, Action<string> log) {
        return create<IIOProvider>("WAF.AI.AzureBlobIOProvider", "WAF.Azure", "Relatude.DB.Plugins.Azure", [ioSettings.BlobContainerName, ioSettings.BlobConnectionString, ioSettings.LockBlob, log]);
    }
}
