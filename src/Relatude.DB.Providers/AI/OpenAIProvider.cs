namespace Relatude.DB.AI;
/// <summary>
/// AI provider for OpenAI and any OpenAI-compatible endpoint (Mistral, Groq, Ollama, OpenRouter,
/// Gemini's compatibility endpoint, LM Studio, etc.), implemented over plain HttpClient.
/// ServiceUrl is the API base, defaulting to https://api.openai.com/v1.
/// Set EmbeddingServiceUrl/EmbeddingApiKey to source embeddings from a different endpoint than completions.
/// </summary>
public class OpenAIProvider : IAIProvider {
    readonly HttpClient _http;
    readonly string _chatUrl;
    readonly string _embeddingsUrl;
    readonly string? _apiKey;
    readonly string? _embeddingApiKey;
    readonly AIProviderSettings _settings;
    public OpenAIProvider(AIProviderSettings settings) {
        _settings = settings;
        var baseUrl = string.IsNullOrEmpty(settings.ServiceUrl) ? "https://api.openai.com/v1" : settings.ServiceUrl.TrimEnd('/');
        var embeddingBaseUrl = string.IsNullOrEmpty(settings.EmbeddingServiceUrl) ? baseUrl : settings.EmbeddingServiceUrl.TrimEnd('/');
        _chatUrl = baseUrl + "/chat/completions";
        _embeddingsUrl = embeddingBaseUrl + "/embeddings";
        _apiKey = settings.ApiKey; // optional: local endpoints like Ollama need no key
        _embeddingApiKey = string.IsNullOrEmpty(settings.EmbeddingApiKey) ? settings.ApiKey : settings.EmbeddingApiKey;
        _http = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };
    }
    string resolveModel(string? modelKey) {
        if (modelKey != null) {
            if (_settings.CompletionModelsByKey == null || !_settings.CompletionModelsByKey.TryGetValue(modelKey, out var model)) {
                throw new ArgumentException($"Model key '{modelKey}' not found in AIProviderSettings");
            }
            return model;
        }
        if (string.IsNullOrEmpty(_settings.CompletionModel)) throw new Exception("CompletionModel is required in AIProviderSettings");
        return _settings.CompletionModel;
    }
    public async Task<string> GetCompletionAsync(string prompt, string? modelKey = null) {
        var body = OpenAIWire.BuildChatRequest(resolveModel(modelKey), prompt, _settings.MaxOutputTokens);
        var json = await OpenAIWire.PostAsync(_http, _chatUrl, body, r => setAuth(r, _apiKey));
        return OpenAIWire.ParseChatResponse(json);
    }
    public async Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) {
        if (string.IsNullOrEmpty(_settings.EmbeddingModel)) throw new Exception("EmbeddingModel is required in AIProviderSettings");
        var body = OpenAIWire.BuildEmbeddingsRequest(_settings.EmbeddingModel, paragraphs);
        var json = await OpenAIWire.PostAsync(_http, _embeddingsUrl, body, r => setAuth(r, _embeddingApiKey));
        return OpenAIWire.ParseEmbeddingsResponse(json, paragraphs.Length);
    }
    static void setAuth(HttpRequestMessage request, string? apiKey) {
        if (!string.IsNullOrEmpty(apiKey)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
    }
    public void Dispose() {
        _http.Dispose();
    }
}
