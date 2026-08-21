namespace Relatude.DB.AI;
/// <summary>
/// AI provider for Azure OpenAI over plain HttpClient, replacing the Azure SDK based provider
/// with identical settings: ServiceUrl is the resource endpoint (https://myresource.openai.azure.com/),
/// CompletionModel/EmbeddingModel and the CompletionModelsByKey values are deployment names.
/// ApiVersion overrides the api-version query parameter (defaults to a stable GA version).
/// </summary>
public class AzureAIProvider : IAIProvider {
    const string _defaultApiVersion = "2024-10-21";
    readonly HttpClient _http;
    readonly string _baseUrl;
    readonly string _apiVersion;
    readonly string _apiKey;
    readonly AIProviderSettings _settings;
    public AzureAIProvider(AIProviderSettings settings) {
        if (string.IsNullOrEmpty(settings.ServiceUrl)) throw new ArgumentException("ServiceUrl is required in AIProviderSettings");
        if (string.IsNullOrEmpty(settings.ApiKey)) throw new ArgumentException("ApiKey is required in AIProviderSettings");
        _settings = settings;
        _baseUrl = settings.ServiceUrl.TrimEnd('/');
        _apiVersion = string.IsNullOrEmpty(settings.ApiVersion) ? _defaultApiVersion : settings.ApiVersion;
        _apiKey = settings.ApiKey;
        _http = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };
    }
    string url(string deployment, string operation) => $"{_baseUrl}/openai/deployments/{Uri.EscapeDataString(deployment)}/{operation}?api-version={Uri.EscapeDataString(_apiVersion)}";
    string resolveDeployment(string? modelKey) {
        if (modelKey != null) {
            if (_settings.CompletionModelsByKey == null || !_settings.CompletionModelsByKey.TryGetValue(modelKey, out var deployment)) {
                throw new ArgumentException($"Model key '{modelKey}' not found in AIProviderSettings");
            }
            return deployment;
        }
        if (string.IsNullOrEmpty(_settings.CompletionModel)) throw new Exception("CompletionModel is required in AIProviderSettings");
        return _settings.CompletionModel;
    }
    public async Task<string> GetCompletionAsync(string prompt, string? modelKey = null) {
        // the deployment is addressed by the url, so no model property in the body
        var body = OpenAIWire.BuildChatRequest(null, prompt, _settings.MaxOutputTokens);
        var json = await OpenAIWire.PostAsync(_http, url(resolveDeployment(modelKey), "chat/completions"), body, setAuth);
        return OpenAIWire.ParseChatResponse(json);
    }
    public async Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) {
        if (string.IsNullOrEmpty(_settings.EmbeddingModel)) throw new Exception("EmbeddingModel is required in AIProviderSettings");
        var body = OpenAIWire.BuildEmbeddingsRequest(null, paragraphs);
        var json = await OpenAIWire.PostAsync(_http, url(_settings.EmbeddingModel, "embeddings"), body, setAuth);
        return OpenAIWire.ParseEmbeddingsResponse(json, paragraphs.Length);
    }
    void setAuth(HttpRequestMessage request) {
        request.Headers.TryAddWithoutValidation("api-key", _apiKey);
    }
    public void Dispose() {
        _http.Dispose();
    }
}
