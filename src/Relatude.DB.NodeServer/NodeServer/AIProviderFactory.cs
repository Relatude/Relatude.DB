using Relatude.DB.IO;
using Relatude.DB.AI;
using Relatude.DB.Common;
namespace Relatude.DB.NodeServer;

public static class AIProviderFactory {
    /// <param name="settings"></param>

    /// <param name="dataFolder"></param>    /// <param name="licenseApiKey">The installation's license API key, read at every call, for the
    /// hosted Relatude service to charge, before any key in the settings.</param>
    /// <param name="owner">Names the database in the cache file name when <paramref name="dataFolder"/>
    /// is shared with other databases (see <see cref="ServiceCaches"/>); null in a database's own folder.</param>
    /// <param name="warnings">Where a cache that could not be opened as asked is reported; the engine is built all the same.</param>
    public static AIEngine Create(AIProviderSettings settings, string? dataFolder, Func<string?>? licenseApiKey = null, string? owner = null, List<string>? warnings = null) {
        string? filePath = null;
        if (!string.IsNullOrEmpty(dataFolder)) {
            var fileKey = FileKeyUtility.GetAiCacheFileKey(settings.CacheType, owner);
            if (fileKey != null) {
                filePath = Path.Combine([dataFolder, .. fileKey]);
                // the cache file lives in the indexes folder; the cache stores do not create it themselves
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // a legacy file in a shared folder is not this database's to take
                if (owner == null) {
                    try {
                        moveLegacyCacheFileIfAny(dataFolder, FileKeyUtility.GetLegacyRootAiCacheFileName(settings.CacheType), filePath);
                    } catch (Exception error) {
                        warnings?.Add("The AI embedding cache could not be moved from the root of " + dataFolder + " to " + filePath + ": " + error.Message);
                    }
                }
            }
        }
        IEmbeddingCache? cache = settings.CacheType switch {
            null => null,
            AIProviderCacheType.None => null,
            AIProviderCacheType.Memory => new MemoryEmbeddingCache(1000),
            AIProviderCacheType.Sqlite => LateBindings.CreateSqlLiteEmbeddingCache(filePath),
            AIProviderCacheType.Native => createNativeCache(filePath, warnings),
            _ => throw new NotImplementedException(),
        };
        IAIProvider? provider = null;
        try {
            provider = LateBindings.CreateAiProvider(settings, licenseApiKey);
            return new AIEngine(provider, settings, cache);
        } catch {
            // the engine never took ownership, so nothing else closes the cache file: left open, the
            // next open in this process finds it held, waits FileOpenRetry's 30 s and reports its own
            // handle as another process's
            try { provider?.Dispose(); } catch { }
            try { cache?.Dispose(); } catch { }
            throw;
        }
    }
    // only a cache: the database opens with one in memory rather than not at all
    static IEmbeddingCache createNativeCache(string? filePath, List<string>? warnings) {
        try {
            var cache = new NativeKvEmbeddingCache(filePath);
            if (cache.OpenNotice != null) warnings?.Add("The AI embedding cache at " + filePath + ": " + cache.OpenNotice);
            return cache;
        } catch (Exception error) {
            warnings?.Add("The AI embedding cache at " + filePath + " could not be opened, so it is kept in memory: " + error.Message);
            return new MemoryEmbeddingCache(1000);
        }
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
