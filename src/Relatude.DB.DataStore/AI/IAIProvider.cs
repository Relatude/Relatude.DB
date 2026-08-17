using Relatude.DB.Common;

namespace Relatude.DB.AI;
public interface IAIProvider : IDisposable {
    Task<float[][]> GetEmbeddingsAsync(string[] paragraphs);
    Task<string> GetCompletionAsync(string prompt, string? modelKey = null);
    //Task<string> GetChatCompletionAsync(ChatMessage[] conversation);
}
public class AIProviderSettings {
    public string? TypeName { get; set; }
    public string? Name { get; set; }
    public string? FilePath { get; set; }
    public string? ServiceUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? EmbeddingModel { get; set; }
    public string? CompletionModel { get; set; }
    public Dictionary<string, string>? CompletionModelsByKey { get; set; }
    public double? DefaultSemanticRatio { get; set; }
    public double? DefaultMinimumSimilarity { get; set; }

    public int? MaxCharsInBatch { get; set; }
    public int? MaxCountInBatch { get; set; }
    public int? MaxCharsOfEach { get; set; }
    public int? ModelDimensions { get; set; }

    public int GetMaxCharsInBatch() => MaxCharsInBatch ?? 50000;
    public int GetMaxCountInBatch() => MaxCountInBatch ?? 500;
    public int GetMaxCharsOfEach() => MaxCharsOfEach ?? 20000;

    public AIProviderCacheType? CacheType { get; set; } = AIProviderCacheType.Native;
    public AIIndexType IndexType { get; set; } = AIIndexType.Memory;
    public double? IndexCacheSizeInMb { get; set; }
}