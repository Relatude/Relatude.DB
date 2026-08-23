using Relatude.DB.IO;
using Relatude.DB.AI;
using Relatude.DB.Common;
namespace Relatude.DB.NodeServer;

public static class AIProviderFactory {
    public static AIEngine Create(AIProviderSettings settings, string? dataFolder, string? filePrefix) {
        string? filePath = null;
        if (!string.IsNullOrEmpty(dataFolder)) {
            var fileKey = new FileKeyUtility(filePrefix).GetAiCacheFileKey(settings.CacheType);
            if (fileKey != null) {
                filePath = dataFolder.SuperPathCombine(fileKey);
                // the cache file lives in the indexes folder; the cache stores do not create it themselves
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
        }
        IEmbeddingCache? cache = settings.CacheType switch {
            null => null,
            AIProviderCacheType.None => null,
            AIProviderCacheType.Memory => new MemoryEmbeddingCache(1000),
            AIProviderCacheType.Sqlite => LateBindings.CreateSqlLiteEmbeddingCache(filePath),
            AIProviderCacheType.Native => new NativeKvEmbeddingCache(filePath),
            _ => throw new NotImplementedException(),
        };
        var provider = LateBindings.CreateAiProvider(settings);
        return new AIEngine(provider, settings, cache);
    }
}
