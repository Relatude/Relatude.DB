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
/// The service's whole answer to an embeddings call: the vectors in input order, the model key they
/// came from, their length, what the call cost and what is left on the license's "ai_embeddings" account.
/// </summary>
public sealed record RelatudeServicesEmbeddings(string Model, int Dimensions, float[][] Embeddings, int Credits, int CreditsLeft);

/// <summary>The service's whole answer to a completion: the text, the model key, what it cost (prompt and answer together) and what is left.</summary>
public sealed record RelatudeServicesCompletion(string Model, string Text, int Credits, int CreditsLeft);

/// <summary>
/// AI provider for the hosted Relatude Services AI endpoint (Relatude.DB.Services.AI), over plain HttpClient.
/// <para>Unlike the other providers this one needs no account with an AI vendor: the service holds the vendor
/// credentials and meters every call against the caller's license: the installation's own, whose API key is
/// read at every call, or <see cref="AIProviderSettings.ApiKey"/> when the installation has none. The license
/// must carry the credit account the call is charged to, with a balance: "ai_embeddings" for embeddings, "ai_completion" for
/// completions. The account is what licenses the call; no feature is needed. A refusal comes back as an exception naming the reason
/// (no such account, no credits, rate limited).</para>
/// <para>A call is paid for before the service sends it upstream, and the credits are never given back,
/// so embeddings and completions are repeated only on the two answers the service gives before any
/// money moves: 429, rate limited, and 503, the license server out of reach. Any other failure - the
/// upstream model failing, a timeout, a dropped connection - may already have been charged, so it is
/// thrown rather than retried, and a failing model costs one charge per call rather than five. The
/// service retries the upstream model itself before it reports a failure. The model list costs
/// nothing, so the usual retries apply to it.</para>
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
    readonly AIProviderSettings _settings;
    readonly Func<string?>? _licenseApiKey;

    public RelatudeServicesAIProvider(AIProviderSettings settings) : this(settings, null) { }

    /// <param name="settings">Where the service is and which models to ask for.</param>
    /// <param name="licenseApiKey">The API key of the license the installation runs under, asked for
    /// at every call, so a new license takes effect without the database reopening. When it has one
    /// it is used before <see cref="AIProviderSettings.ApiKey"/>: that one is a copy the settings
    /// used to require, and it would go stale unnoticed when the license changes. The same rule as
    /// <c>RelatudeServicesSMSProvider</c>.</param>
    public RelatudeServicesAIProvider(AIProviderSettings settings, Func<string?>? licenseApiKey) {
        _settings = settings;
        _licenseApiKey = licenseApiKey;
        var baseUrl = rootUrl(settings.ServiceUrl);
        _embeddingsUrl = baseUrl + "/api/ai/embeddings";
        _completionsUrl = baseUrl + "/api/ai/completions";
        _http = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>The key a call is charged to, or null when neither the settings nor the installation has one.</summary>
    string? apiKeyOrNull() {
        var key = _licenseApiKey?.Invoke();
        if (string.IsNullOrWhiteSpace(key)) key = _settings.ApiKey;
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    /// <summary>The key a charged call is sent with, or an exception saying where one is set when there is none.</summary>
    string apiKey() => apiKeyOrNull() ?? throw new InvalidOperationException(
        "There is no API key to call the Relatude AI service with. The service charges every call to a license: "
        + "set this installation's license key and API key under License in the admin UI, or give the AI settings an API key of their own. ");

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
        => GetAvailableModelsAsync(_settings.ServiceUrl, apiKeyOrNull(), cancellationToken);

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
    public async Task<string> GetCompletionAsync(string prompt, string? modelKey = null)
        => (await CompleteAsync(prompt, resolveModel(modelKey), _settings.MaxOutputTokens)).Text;

    /// <summary>
    /// A completion with the service's whole answer, what it cost included. <paramref name="model"/> is
    /// a key the service publishes, sent as it is, or null for the service's own default; the settings'
    /// local model keys are <see cref="GetCompletionAsync"/>'s business.
    /// </summary>
    public async Task<RelatudeServicesCompletion> CompleteAsync(string prompt, string? model = null, int? maxOutputTokens = null) {
        string body;
        using (var ms = new MemoryStream()) {
            using (var w = new Utf8JsonWriter(ms)) {
                w.WriteStartObject();
                w.WriteString("prompt", prompt);
                if (!string.IsNullOrEmpty(model)) w.WriteString("model", model);
                if (maxOutputTokens.HasValue) w.WriteNumber("maxOutputTokens", maxOutputTokens.Value);
                w.WriteEndObject();
            }
            body = Encoding.UTF8.GetString(ms.ToArray());
        }
        var json = await postAsync(_completionsUrl, body);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) {
            throw new Exception("The Relatude AI service answered a completion without a text property. ");
        }
        return new RelatudeServicesCompletion(readString(root, "model") ?? model ?? "", text.GetString() ?? "", readInt(root, "credits"), readInt(root, "creditsLeft"));
    }

    public async Task<float[][]> GetEmbeddingsAsync(string[] paragraphs)
        => (await EmbedAsync(paragraphs, string.IsNullOrEmpty(_settings.EmbeddingModel) ? null : _settings.EmbeddingModel)).Embeddings;

    /// <summary>
    /// Embeddings with the service's whole answer, what they cost included. <paramref name="model"/> is
    /// a key the service publishes, or null for the service's own default.
    /// </summary>
    public async Task<RelatudeServicesEmbeddings> EmbedAsync(string[] paragraphs, string? model = null) {
        string body;
        using (var ms = new MemoryStream()) {
            using (var w = new Utf8JsonWriter(ms)) {
                w.WriteStartObject();
                if (!string.IsNullOrEmpty(model)) w.WriteString("model", model);
                w.WriteStartArray("input");
                foreach (var text in paragraphs) w.WriteStringValue(text);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            body = Encoding.UTF8.GetString(ms.ToArray());
        }
        var json = await postAsync(_embeddingsUrl, body);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var vectors = parseEmbeddings(root, paragraphs.Length);
        var dimensions = root.TryGetProperty("dimensions", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : vectors.Length > 0 ? vectors[0].Length : 0;
        return new RelatudeServicesEmbeddings(readString(root, "model") ?? model ?? "", dimensions, vectors, readInt(root, "credits"), readInt(root, "creditsLeft"));
    }
    /// <summary>The vectors in the order the input was sent, which is what the service guarantees. </summary>
    static float[][] parseEmbeddings(JsonElement root, int expectedCount) {
        if (!root.TryGetProperty("embeddings", out var embeddings) || embeddings.ValueKind != JsonValueKind.Array) {
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
    // an older service may leave the price out; it reads as nothing charged rather than as a failure
    static string? readString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int readInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    /// <summary>
    /// Posts and returns the body, turning a refusal into an exception that repeats the service's own reason.
    /// <para>Both routes that post, embeddings and completions, are charged: the service takes the credits
    /// before it calls the upstream model, and cannot give them back. So a call is repeated only on the
    /// answers the service gives before any money moves (<see cref="answeredBeforeCharging"/>). Any other
    /// status either comes once the credits may have been taken - 502 when the upstream model failed,
    /// 424 when the license server did not confirm the charge, 500 from an error after it, 504 from
    /// Azure's front end while the service is still at work on a long call - or is the service's final
    /// word on the call (400 to 403, 501). A 502 is not worth repeating in any case: the
    /// service has already retried the upstream model before giving it, or the model refused the input
    /// outright, so a repeat would only do all of that again, and pay again.</para>
    /// <para>A timeout is not retried, and neither is a connection error (<see cref="HttpRequestException"/>):
    /// both can come after the service had the request and charged it, and nothing in either says how far
    /// it got. Five attempts at the five-minute timeout would also hold the caller for close to half an
    /// hour. The caller is told, and decides.</para>
    /// </summary>
    async Task<string> postAsync(string url, string jsonBody) {
        // settled before the first attempt, so a missing key is said plainly rather than as the service's 401
        var key = apiKey();
        using var response = await HttpRetry.SendAsync(_http, () => {
            var request = new HttpRequestMessage(HttpMethod.Post, url) {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return request;
        }, retryOnTimeout: false, isTransient: answeredBeforeCharging, retryOnConnectionError: false);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) {
            throw new Exception($"The Relatude AI service at {url} returned {(int)response.StatusCode} {response.StatusCode}: {readError(body)}");
        }
        return body;
    }
    /// <summary>
    /// The failures a charged call may be repeated on: 429, rate limited with the Retry-After to wait,
    /// and 503, the license server out of reach. The service gives both before it takes any credits. None
    /// of the others <see cref="HttpRetry.IsTransient"/> would repeat is safe: a 500, 502 or 504 can come
    /// after the call was charged, and a 408 is not among the service's answers, so it says nothing about
    /// how far the request got.
    /// </summary>
    static bool answeredBeforeCharging(int statusCode) => statusCode is 429 or 503;
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
