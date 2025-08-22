namespace WAF.AI;
public interface IAIProvider : IDisposable {
    Task<List<float[]>> GetEmbeddingsAsync(IEnumerable<string> paragraphs);
    Task<string> GetCompletionAsync(string prompt);
    void ClearCache();
    AIProviderSettings Settings { get; }
    Action<string, bool>? LogCallback { get; set; }
}
public enum AIProviderCacheType {
    Memory = 0,
    SqlLite = 1,
}
public class AIProviderSettings {
    public string ServiceUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public string CompletionModel { get; set; } = ""; 
    public double DefaultSemanticRatio { get; set; } = 0.5;
    public double DefaultMinimumSimilarity { get; set; } = 0.17;
    public AIProviderCacheType CacheType { get; set; } = AIProviderCacheType.SqlLite; // defaults to SqlLite!
    public int MaxCharsInBatch{ get; set; } = 50000;
    public int MaxCountInBatch { get; set; } = 500;
    public int MaxCharsOfEach { get; set; } = 5000;
}