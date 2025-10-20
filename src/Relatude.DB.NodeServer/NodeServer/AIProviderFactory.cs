using Relatude.DB.IO;
using Relatude.DB.AI;
namespace Relatude.DB.NodeServer;
public static class AIProviderFactory {
    public static AIEngine Create(AIProviderSettings settings, string? dataFolder, string? filePrefix) {
        string? filePath = null;
        if (!string.IsNullOrEmpty(dataFolder)) {
            filePath = dataFolder.SuperPathCombine(new FileKeyUtility(filePrefix).AiCacheFileKey);
        }
        IEmbeddingCache? cache = settings.CacheType switch {
            null => null,
            AIProviderCacheType.None => null,
            AIProviderCacheType.Memory => new MemoryEmbeddingCache(1000),
            AIProviderCacheType.Sqlite => LateBindings.CreateSqlLiteEmbeddingCache(filePath),
            _ => throw new NotImplementedException(),
        };
        var provider = LateBindings.CreateAiProvider(settings);
        return new AIEngine(provider, settings, cache);
    }
}
