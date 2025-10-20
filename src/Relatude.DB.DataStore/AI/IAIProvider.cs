namespace Relatude.DB.AI;
public interface IAIProvider : IDisposable {
    Task<float[][]> GetEmbeddingsAsync(string[] paragraphs);
    Task<string> GetCompletionAsync(string prompt);
}

public enum AIProviderCacheType {
    None = 0,
    Memory = 1,
    Sqlite = 2,
}
public class AIProviderSettings {
    public Guid Id { get; set; }
    public string? TypeName { get; set; }
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

    public int GetMaxCharsInBatch ()=> MaxCharsInBatch ?? 50000;
    public int GetMaxCountInBatch ()=> MaxCountInBatch ?? 500;
    public int GetMaxCharsOfEach ()=> MaxCharsOfEach ?? 20000;

    public AIProviderCacheType? CacheType { get; set; }
}