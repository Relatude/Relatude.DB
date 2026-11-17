using System.Runtime.CompilerServices;
using Relatude.DB.Common;

namespace Relatude.DB.AI;
public interface IAIProvider : IDisposable {
    Task<float[][]> GetEmbeddingsAsync(string[] paragraphs);
    Task<string> GetCompletionAsync(string prompt, string? modelKey = null);
    /// <summary>Whether <see cref="GetChatCompletionAsync"/> can send pictures.</summary>
    bool SupportsImages => false;
    /// <summary>
    /// Whether the model can ask for tools (<see cref="AIChatRequest.Tools"/>) and take their results
    /// (<see cref="AIChatMessage.Tool"/>), through <see cref="GetChatResultAsync"/> and <see cref="StreamChatAsync"/>.
    /// </summary>
    bool SupportsTools => false;
    /// <summary>
    /// A completion of a conversation, with the instructions apart from the messages, and pictures where
    /// <see cref="SupportsImages"/> says so. A provider that does not implement it sends the request as one
    /// prompt (<see cref="AIChatRequest.ToPrompt"/>) through <see cref="GetCompletionAsync"/>, without
    /// <see cref="AIChatRequest.MaxOutputTokens"/>, and throws <see cref="NotSupportedException"/> for pictures and tools.
    /// </summary>
    Task<string> GetChatCompletionAsync(AIChatRequest request, CancellationToken cancellationToken = default) {
        if (request.HasImages) throw new NotSupportedException($"The AI provider {GetType().Name} cannot send pictures. ");
        request.RequireNoTools(GetType().Name);
        return GetCompletionAsync(request.ToPrompt(), request.ModelKey);
    }
    /// <summary>
    /// <see cref="GetChatCompletionAsync"/> with the whole answer: the tools the model asks for, why it stopped, what
    /// it used and, from a service that charges, what it cost. A provider that does not implement it answers the text alone.
    /// </summary>
    async Task<AIChatResult> GetChatResultAsync(AIChatRequest request, CancellationToken cancellationToken = default)
        => new(await GetChatCompletionAsync(request, cancellationToken));
    /// <summary>
    /// The answer as it is made, ending in an <see cref="AIChatDone"/> with all of it. A provider that does not
    /// stream gives the text in one piece once it has it.
    /// </summary>
    async IAsyncEnumerable<AIChatDelta> StreamChatAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        var result = await GetChatResultAsync(request, cancellationToken);
        if (result.Text.Length > 0) yield return new AITextDelta(result.Text);
        yield return new AIChatDone(result);
    }
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