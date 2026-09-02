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
    /// <summary>Overrides the api-version query parameter for providers that use one (Azure OpenAI). </summary>
    public string? ApiVersion { get; set; }
    public string? EmbeddingModel { get; set; }
    /// <summary>Embeddings endpoint when it differs from ServiceUrl. Required for providers without
    /// an embeddings API (Anthropic), where it must point to an OpenAI-compatible endpoint. </summary>
    public string? EmbeddingServiceUrl { get; set; }
    /// <summary>Api key for EmbeddingServiceUrl, defaults to ApiKey. </summary>
    public string? EmbeddingApiKey { get; set; }
    public string? CompletionModel { get; set; }
    public Dictionary<string, string>? CompletionModelsByKey { get; set; }
    /// <summary>Max tokens for completions. Sent when set; providers that require the parameter (Anthropic) default to 4096. </summary>
    public int? MaxOutputTokens { get; set; }
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
}