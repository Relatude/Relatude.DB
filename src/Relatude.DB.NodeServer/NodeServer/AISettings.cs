using Relatude.DB.IO;
using Relatude.DB.AI;
using Relatude.DB.DataStores.Indexes;
namespace Relatude.DB.NodeServer {
    public enum AITypes {
        Azure = 0,
    }
    public class AISettings {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? FilePath { get; set; }
        public string? ServiceUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? EmbeddingModel { get; set; }
        public string? CompletionModel { get; set; }

        public double? DefaultSemanticRatio { get; set; }
        public double? DefaultMinimumSimilarity { get; set; }

        public int? MaxCharsInBatch { get; set; }
        public int? MaxCountInBatch { get; set; }
        public int? MaxCharsOfEach { get; set; }

        public AITypes AIType { get; set; }
        public AIProviderCacheType? CacheType { get; set; }

        public static IAIProvider Create(AISettings settings, string? dataFolder, string? filePrefix) {
            switch (settings.AIType) {
                case AITypes.Azure: {

                        var s = new AIProviderSettings();

                        if (settings.ServiceUrl != null) s.ServiceUrl = settings.ServiceUrl;
                        if (settings.ApiKey != null) s.ApiKey = settings.ApiKey;
                        if (settings.EmbeddingModel != null) s.EmbeddingModel = settings.EmbeddingModel;
                        if (settings.CompletionModel != null) s.CompletionModel = settings.CompletionModel;
                        if (settings.DefaultSemanticRatio.HasValue) s.DefaultSemanticRatio = settings.DefaultSemanticRatio.Value;
                        if (settings.DefaultMinimumSimilarity.HasValue) s.DefaultMinimumSimilarity = settings.DefaultMinimumSimilarity.Value;
                        if (settings.MaxCharsOfEach.HasValue) s.MaxCharsOfEach = settings.MaxCharsOfEach.Value;
                        if (settings.MaxCharsInBatch.HasValue) s.MaxCharsInBatch = settings.MaxCharsInBatch.Value;
                        if (settings.MaxCountInBatch.HasValue) s.MaxCountInBatch = settings.MaxCountInBatch.Value;
                        if (settings.CacheType.HasValue) s.CacheType = settings.CacheType.Value;

                        string? filePath = null;
                        if (!string.IsNullOrEmpty(dataFolder)) {
                            filePath = dataFolder.SuperPathCombine(new FileKeyUtility(filePrefix).AiCacheFileKey);
                        }
                        IEmbeddingCache cache = s.CacheType switch {
                            AIProviderCacheType.Memory => new MemoryEmbeddingCache(1000),
                            AIProviderCacheType.SqlLite => LateBindings.CreateSqlLiteEmbeddingCache(filePath),
                            _ => throw new NotImplementedException(),
                        };
                        return LateBindings.CreateAzureAiProvider(s, cache);
                    }
                default:
                    throw new NotImplementedException();
            }
        }
    }
}
