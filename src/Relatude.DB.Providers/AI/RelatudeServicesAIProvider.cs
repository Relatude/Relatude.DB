using System.Text;
using System.Text.Json;
using Relatude.DB.Http;

namespace Relatude.DB.AI;

/// <summary>
/// The model names the hosted Relatude AI service offers, from <c>GET /api/ai/models/available</c>.
/// <para>Names only. An embedding model's vector length is settled by the model itself: the service
/// reports it on every embeddings call, and a vector index takes its dimensions from the first
/// vector it is given, so picking the model is the whole of the choice.</para>
/// </summary>
public sealed record RelatudeServicesModels(string[] EmbeddingModels, string[] CompletionModels) {
    public static readonly RelatudeServicesModels Empty = new([], []);
}

/// <summary>
/// AI provider for the hosted Relatude Services AI endpoint (Relatude.DB.Services.AI), over plain HttpClient.
/// <para>Unlike the other providers this one needs no account with an AI vendor: the service holds the vendor
/// credentials and meters every call against the caller's license. <see cref="AIProviderSettings.ApiKey"/> is the
/// API key issued with that license, and the license must carry the AI feature and a credit account with a
/// balance. A refusal comes back as an exception naming the reason (no feature, no credits, rate limited).</para>
/// <para><see cref="AIProviderSettings.ServiceUrl"/> is the root of the service and defaults to
/// <c>https://ai.relatude.com</c>; point it at your own deployment when the service is self-hosted.
/// EmbeddingModel and CompletionModel are model keys the service publishes (not vendor deployment names) and
/// may be left empty, in which case the service picks its own default. CompletionModelsByKey maps a local key
/// onto one of those published keys, exactly as it does for the other providers.</para>
/// </summary>
public class RelatudeServicesAIProvider : IAIProvider {
    const string _defaultServiceUrl = "https://ai.relatude.com";

    /// <summary>The short name this provider is configured under, beside its own type name.
    /// <c>LateBindings.CreateAiProvider</c> resolves both, and the settings page offers this one.</summary>
    public const string ShortName = "RelatudeServices";

    readonly HttpClient _http;
    readonly string _embeddingsUrl;
    readonly string _completionsUrl;
    readonly string _apiKey;
    readonly AIProviderSettings _settings;
    public RelatudeServicesAIProvider(AIProviderSettings settings) {
        _settings = settings;
        var baseUrl = rootUrl(settings.ServiceUrl);
        _embeddingsUrl = baseUrl + "/api/ai/embeddings";
        _completionsUrl = baseUrl + "/api/ai/completions";
        _apiKey = settings.ApiKey;
        _http = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>Whether a configured provider type name means this provider, by either of its names.</summary>
    public static bool IsProviderName(string? typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && (typeName.Trim().Equals(nameof(RelatudeServicesAIProvider), StringComparison.OrdinalIgnoreCase)
            || typeName.Trim().Equals(ShortName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The service root a settings value names, with the hosted service as the default.</summary>
    static string rootUrl(string? serviceUrl) => (string.IsNullOrWhiteSpace(serviceUrl) ? _defaultServiceUrl : serviceUrl).TrimEnd('/');

    // One client for the static model lookup, which is called from a settings page rather than from
    // a configured provider: there is no instance to own a client, and a new one per call would leak
    // sockets if the page asked often.
    static readonly HttpClient _lookupHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly JsonSerializerOptions _lookupJson = new(JsonSerializerDefaults.Web);

    /// <summary>The model names this provider's service offers. See the static overload.</summary>
    public Task<RelatudeServicesModels> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        => GetAvailableModelsAsync(_settings.ServiceUrl, _apiKey, cancellationToken);

    /// <summary>
    /// The embedding and completion model names the service publishes, for a settings page to offer.
    /// <para>Static because the caller is usually configuring a provider rather than using one: no
    /// license key is needed, and <paramref name="serviceUrl"/> may be empty for the hosted service.
    /// The key is sent when there is one, which costs nothing and lets a self-hosted deployment
    /// require it. Nothing here is cached; the caller decides how often to ask.</para>
    /// <para>Throws when the service cannot be reached or answers with anything but the list, so a
    /// settings page can say why the drop-down is empty rather than showing it as empty on purpose.</para>
    /// </summary>
    public static async Task<RelatudeServicesModels> GetAvailableModelsAsync(string? serviceUrl, string? apiKey = null, CancellationToken cancellationToken = default) {
        var url = rootUrl(serviceUrl) + "/api/ai/models/available";
        using var response = await HttpRetry.SendAsync(_lookupHttp, () => {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            return request;
        });
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) {
            throw new Exception($"The Relatude AI service at {url} returned {(int)response.StatusCode} {response.StatusCode}: {readError(body)}");
        }
        RelatudeServicesModels? models;
        try {
            models = JsonSerializer.Deserialize<RelatudeServicesModels>(body, _lookupJson);
        } catch (JsonException ex) {
            throw new Exception($"The Relatude AI service at {url} answered with something other than a model list: {OpenAIWire.Truncate(body, 200)}", ex);
        }
        if (models == null) throw new Exception($"The Relatude AI service at {url} answered with an empty model list. ");
        // an older service may know only one of the two lists; a missing one is empty, not null
        return new RelatudeServicesModels(models.EmbeddingModels ?? [], models.CompletionModels ?? []);
    }
    /// <summary>The model key sent to the service, or null to let the service choose. Unlike the vendor
    /// providers an unset completion model is not an error here: the service publishes a default. </summary>
    string? resolveModel(string? modelKey) {
        if (modelKey != null) {
            if (_settings.CompletionModelsByKey == null || !_settings.CompletionModelsByKey.TryGetValue(modelKey, out var model)) {
                throw new ArgumentException($"Model key '{modelKey}' not found in AIProviderSettings");
            }
            return model;
        }
        return string.IsNullOrEmpty(_settings.CompletionModel) ? null : _settings.CompletionModel;
    }
    public async Task<string> GetCompletionAsync(string prompt, string? modelKey = null) {
        string body;
        using (var ms = new MemoryStream()) {
            using (var w = new Utf8JsonWriter(ms)) {
                w.WriteStartObject();
                w.WriteString("prompt", prompt);
                if (resolveModel(modelKey) is { } model) w.WriteString("model", model);
                if (_settings.MaxOutputTokens.HasValue) w.WriteNumber("maxOutputTokens", _settings.MaxOutputTokens.Value);
                w.WriteEndObject();
            }
            body = Encoding.UTF8.GetString(ms.ToArray());
        }
        var json = await postAsync(_completionsUrl, body);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) {
            throw new Exception("The Relatude AI service answered a completion without a text property. ");
        }
        return text.GetString() ?? "";
    }
    public async Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) {
        string body;
        using (var ms = new MemoryStream()) {
            using (var w = new Utf8JsonWriter(ms)) {
                w.WriteStartObject();
                if (!string.IsNullOrEmpty(_settings.EmbeddingModel)) w.WriteString("model", _settings.EmbeddingModel);
                w.WriteStartArray("input");
                foreach (var text in paragraphs) w.WriteStringValue(text);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            body = Encoding.UTF8.GetString(ms.ToArray());
        }
        var json = await postAsync(_embeddingsUrl, body);
        return parseEmbeddings(json, paragraphs.Length);
    }
    /// <summary>The vectors in the order the input was sent, which is what the service guarantees. </summary>
    static float[][] parseEmbeddings(string json, int expectedCount) {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("embeddings", out var embeddings) || embeddings.ValueKind != JsonValueKind.Array) {
            throw new Exception("The Relatude AI service answered without an embeddings array. ");
        }
        if (embeddings.GetArrayLength() != expectedCount) {
            throw new Exception($"The Relatude AI service returned {embeddings.GetArrayLength()} vectors, expected {expectedCount}. ");
        }
        var result = new float[expectedCount][];
        var pos = 0;
        foreach (var vector in embeddings.EnumerateArray()) {
            var values = new float[vector.GetArrayLength()];
            var i = 0;
            foreach (var value in vector.EnumerateArray()) values[i++] = value.GetSingle();
            result[pos++] = values;
        }
        return result;
    }
    /// <summary>
    /// Posts and returns the body, turning a refusal into an exception that repeats the service's own reason.
    /// A licensing "no" (402, 403, 429) is a permanent answer for this call, so it is not retried: only the
    /// transient statuses <see cref="HttpRetry"/> knows about are.
    /// </summary>
    async Task<string> postAsync(string url, string jsonBody) {
        using var response = await HttpRetry.SendAsync(_http, () => {
            var request = new HttpRequestMessage(HttpMethod.Post, url) {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            return request;
        });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) {
            throw new Exception($"The Relatude AI service at {url} returned {(int)response.StatusCode} {response.StatusCode}: {readError(body)}");
        }
        return body;
    }
    /// <summary>The service's own explanation when it sent one, the raw body when it did not. </summary>
    static string readError(string body) {
        try {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String) {
                return error.GetString() ?? "";
            }
        } catch (JsonException) { // not JSON, fall through to the raw body
        }
        return OpenAIWire.Truncate(body, 500);
    }
    public void Dispose() {
        _http.Dispose();
    }
}
