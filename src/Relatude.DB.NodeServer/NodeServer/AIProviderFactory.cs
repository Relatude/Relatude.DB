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
    public static AIEngine Create(AIProviderSettings settings, string? dataFolder, Func<string?>? licenseApiKey = null, string? owner = null) {
        string? filePath = null;
        if (!string.IsNullOrEmpty(dataFolder)) {
            var fileKey = FileKeyUtility.GetAiCacheFileKey(settings.CacheType, owner);
            if (fileKey != null) {
                filePath = Path.Combine([dataFolder, .. fileKey]);
                // the cache file lives in the indexes folder; the cache stores do not create it themselves
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // a legacy file in a shared folder is not this database's to take
                if (owner == null) moveLegacyCacheFileIfAny(dataFolder, FileKeyUtility.GetLegacyRootAiCacheFileName(settings.CacheType), filePath);
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
