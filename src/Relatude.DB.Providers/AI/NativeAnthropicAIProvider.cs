using System.Text;
using System.Text.Json;
using Relatude.DB.Http;

namespace Relatude.DB.AI;
/// <summary>
/// AI provider for the Anthropic API (Claude) over plain HttpClient.
/// ServiceUrl defaults to https://api.anthropic.com, CompletionModel defaults to claude-opus-5.
/// Anthropic has no embeddings endpoint, so embeddings require EmbeddingServiceUrl pointing at an
/// OpenAI-compatible endpoint (OpenAI, Voyage AI, a local model, ...) with EmbeddingApiKey/EmbeddingModel.
/// </summary>
public class NativeAnthropicAIProvider : IAIProvider {
    const string _defaultModel = "claude-opus-5";
    const string _apiVersion = "2023-06-01";
    const int _defaultMaxOutputTokens = 4096;
    readonly HttpClient _http;
    readonly string _messagesUrl;
    readonly string? _embeddingsUrl;
    readonly string _apiKey;
    readonly string? _embeddingApiKey;
    readonly AIProviderSettings _settings;
    public NativeAnthropicAIProvider(AIProviderSettings settings) {
        if (string.IsNullOrEmpty(settings.ApiKey)) throw new ArgumentException("ApiKey is required in AIProviderSettings");
        _settings = settings;
        var baseUrl = string.IsNullOrEmpty(settings.ServiceUrl) ? "https://api.anthropic.com" : settings.ServiceUrl.TrimEnd('/');
        _messagesUrl = baseUrl + "/v1/messages";
        _embeddingsUrl = string.IsNullOrEmpty(settings.EmbeddingServiceUrl) ? null : settings.EmbeddingServiceUrl.TrimEnd('/') + "/embeddings";
        _apiKey = settings.ApiKey;
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
        return string.IsNullOrEmpty(_settings.CompletionModel) ? _defaultModel : _settings.CompletionModel;
    }
    public async Task<string> GetCompletionAsync(string prompt, string? modelKey = null) {
        string body;
        using (var ms = new MemoryStream()) {
            using (var w = new Utf8JsonWriter(ms)) {
                w.WriteStartObject();
                w.WriteString("model", resolveModel(modelKey));
                w.WriteNumber("max_tokens", _settings.MaxOutputTokens ?? _defaultMaxOutputTokens);
                w.WriteStartArray("messages");
                w.WriteStartObject();
                w.WriteString("role", "user");
                w.WriteString("content", prompt);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            body = Encoding.UTF8.GetString(ms.ToArray());
        }
        var json = await OpenAIWire.PostAsync(_http, _messagesUrl, body, setAuth);
        return parseMessagesResponse(json);
    }
    public bool SupportsImages => true;
    public async Task<string> GetChatCompletionAsync(AIChatRequest request, CancellationToken cancellationToken = default)
        => (await GetChatResultAsync(request, cancellationToken)).Text;
    /// <summary>
    /// The answer with stop_reason in <see cref="AIFinishReason"/>'s words and usage (input_tokens counting those read
    /// from and written to the cache too). <see cref="AIChatRequest.Effort"/> is left out.
    /// </summary>
    public async Task<AIChatResult> GetChatResultAsync(AIChatRequest request, CancellationToken cancellationToken = default) {
        var body = buildMessagesRequest(resolveModel(request.ModelKey), request, request.MaxOutputTokens ?? _settings.MaxOutputTokens ?? _defaultMaxOutputTokens);
        var json = await OpenAIWire.PostAsync(_http, _messagesUrl, body, setAuth, cancellationToken);
        var text = parseMessagesResponse(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var stop = root.TryGetProperty("stop_reason", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        AIUsage? usage = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object) {
            int n(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
            var cached = n("cache_read_input_tokens");
            usage = new AIUsage(n("input_tokens") + cached + n("cache_creation_input_tokens"), n("output_tokens"), cached);
        }
        return new AIChatResult(text) {
            FinishReason = stop switch {
                "end_turn" or "stop_sequence" => AIFinishReason.Stop,
                "max_tokens" => AIFinishReason.Length,
                _ => stop,
            },
            Usage = usage,
            Model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
        };
    }
    /// <summary>The instructions as the top-level system, pictures as base64 image blocks before the text, which Anthropic advises.</summary>
    static string buildMessagesRequest(string model, AIChatRequest request, int maxTokens) {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", maxTokens);
            if (!string.IsNullOrWhiteSpace(request.Instructions)) w.WriteString("system", request.Instructions);
            w.WriteStartArray("messages");
            foreach (var m in request.Messages) {
                w.WriteStartObject();
                w.WriteString("role", m.Role == AIChatRole.User ? "user" : "assistant");
                if (m.Images.Count == 0) {
                    w.WriteString("content", m.Text);
                } else {
                    w.WriteStartArray("content");
                    foreach (var image in m.Images) {
                        w.WriteStartObject();
                        w.WriteString("type", "image");
                        w.WriteStartObject("source");
                        w.WriteString("type", "base64");
                        w.WriteString("media_type", OpenAIWire.ImageContentType(image));
                        w.WriteBase64String("data", image.Bytes);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    // Anthropic refuses an empty text block
                    if (m.Text.Length > 0) {
                        w.WriteStartObject();
                        w.WriteString("type", "text");
                        w.WriteString("text", m.Text);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
    static string parseMessagesResponse(string json) {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("stop_reason", out var stopReason) && stopReason.ValueKind == JsonValueKind.String && stopReason.GetString() == "refusal") {
            var detail = "";
            if (root.TryGetProperty("stop_details", out var stopDetails) && stopDetails.ValueKind == JsonValueKind.Object) {
                if (stopDetails.TryGetProperty("explanation", out var explanation) && explanation.ValueKind == JsonValueKind.String) detail = " " + explanation.GetString();
            }
            throw new Exception("The Anthropic API declined to answer the prompt (stop_reason: refusal)." + detail);
        }
        var sb = new StringBuilder();
        foreach (var block in root.GetProperty("content").EnumerateArray()) {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text") {
                sb.Append(block.GetProperty("text").GetString());
            }
        }
        return sb.ToString();
    }
    public async Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) {
        if (_embeddingsUrl == null) {
            throw new NotSupportedException("Anthropic has no embeddings API. Set EmbeddingServiceUrl in AIProviderSettings to an " +
                "OpenAI-compatible endpoint (e.g. https://api.openai.com/v1 or https://api.voyageai.com/v1) together with EmbeddingModel and EmbeddingApiKey. ");
        }
        if (string.IsNullOrEmpty(_settings.EmbeddingModel)) throw new Exception("EmbeddingModel is required in AIProviderSettings");
        var body = OpenAIWire.BuildEmbeddingsRequest(_settings.EmbeddingModel, paragraphs);
        var json = await OpenAIWire.PostAsync(_http, _embeddingsUrl, body, r => {
            if (!string.IsNullOrEmpty(_embeddingApiKey)) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _embeddingApiKey);
        });
        return OpenAIWire.ParseEmbeddingsResponse(json, paragraphs.Length);
    }
    void setAuth(HttpRequestMessage request) {
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", _apiVersion);
    }
    public void Dispose() {
        _http.Dispose();
    }
}
