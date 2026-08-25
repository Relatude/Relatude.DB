using Relatude.DB.IO;
using Relatude.DB.AI;
using Relatude.DB.Common;
namespace Relatude.DB.NodeServer;

public static class AIProviderFactory {
    public static AIEngine Create(AIProviderSettings settings, string? dataFolder) {
        string? filePath = null;
        if (!string.IsNullOrEmpty(dataFolder)) {
            var fileKey = FileKeyUtility.GetAiCacheFileKey(settings.CacheType);
            if (fileKey != null) {
                filePath = Path.Combine([dataFolder, .. fileKey]);
                // the cache file lives in the indexes folder; the cache stores do not create it themselves
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                moveLegacyCacheFileIfAny(dataFolder, FileKeyUtility.GetLegacyRootAiCacheFileName(settings.CacheType), filePath);
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
    /// <summary>
    /// Before the folder layout the cache file lived in the root of its local disk folder; it is
    /// moved to its new path once, sqlite sidecar files (-wal/-shm) included. It is only a cache,
    /// so an inconsistent move at worst rebuilds it, but moving preserves the paid-for embeddings.
    /// </summary>
    static void moveLegacyCacheFileIfAny(string dataFolder, string? legacyFileName, string newFilePath) {
        if (legacyFileName == null) return;
        var legacyPath = Path.Combine(dataFolder, legacyFileName);
        if (!File.Exists(legacyPath) || File.Exists(newFilePath)) return;
        File.Move(legacyPath, newFilePath);
        foreach (var suffix in new[] { "-wal", "-shm" }) {
            if (File.Exists(legacyPath + suffix) && !File.Exists(newFilePath + suffix)) File.Move(legacyPath + suffix, newFilePath + suffix);
        }
    }
}
