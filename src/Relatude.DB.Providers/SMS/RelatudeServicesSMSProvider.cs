using System.Text;
using System.Text.Json;
using Relatude.DB.AI;
using Relatude.DB.Http;

namespace Relatude.DB.SMS;

/// <summary>
/// Sends text messages through the hosted Relatude SMS service (Relatude.DB.Services.SMS), over
/// plain HttpClient.
/// <para>Like <see cref="RelatudeServicesAIProvider"/> it needs no account with a vendor: the
/// service holds the gateway credentials and charges every message to the license behind the API
/// key. <see cref="SMSProviderSettings.ApiKey"/> is the key issued with that license, and the
/// license must carry the SMS feature and a credit account with a balance. A refusal comes back as
/// an exception repeating the service's own reason - out of credits, not licensed, rate limited -
/// so what the database logs is what the person configuring it needs to read.</para>
/// <para><see cref="SMSProviderSettings.ServiceUrl"/> is the root of the service and defaults to
/// <c>https://sms.relatude.com</c>; point it at your own deployment when the service is
/// self-hosted.</para>
/// </summary>
public class RelatudeServicesSMSProvider : ISMSProvider {
    const string _defaultServiceUrl = "https://sms.relatude.com";

    /// <summary>The short name this provider is configured under, beside its own type name.
    /// <c>LateBindings.CreateSmsProvider</c> resolves both, and the settings page offers this one.</summary>
    public const string ShortName = "RelatudeServices";

    readonly HttpClient _http;
    readonly string _sendUrl;
    readonly string _quoteUrl;
    readonly string _apiKey;
    readonly SMSProviderSettings _settings;

    public RelatudeServicesSMSProvider(SMSProviderSettings settings) {
        if (string.IsNullOrEmpty(settings.ApiKey)) throw new ArgumentException("ApiKey is required in SMSProviderSettings. It is the API key issued with the Relatude license. ");
        _settings = settings;
        var baseUrl = (string.IsNullOrWhiteSpace(settings.ServiceUrl) ? _defaultServiceUrl : settings.ServiceUrl).TrimEnd('/');
        _sendUrl = baseUrl + "/api/sms/send";
        _quoteUrl = baseUrl + "/api/sms/quote";
        _apiKey = settings.ApiKey;
        // A message is one short call; a minute is already generous, and a hung gateway must not
        // hold a request thread for the five an embedding batch is allowed.
        _http = new HttpClient() { Timeout = TimeSpan.FromMinutes(1) };
    }

    /// <summary>Whether a configured provider type name means this provider, by either of its names.</summary>
    public static bool IsProviderName(string? typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && (typeName.Trim().Equals(nameof(RelatudeServicesSMSProvider), StringComparison.OrdinalIgnoreCase)
            || typeName.Trim().Equals(ShortName, StringComparison.OrdinalIgnoreCase));

    public string Name => string.IsNullOrWhiteSpace(_settings.Name) ? "Relatude SMS service" : _settings.Name!;

    public async Task<SmsReceipt> SendAsync(string to, string message, string? from = null, string? reference = null, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(to)) throw new ArgumentException("A recipient number is required. ", nameof(to));
        if (string.IsNullOrEmpty(message)) throw new ArgumentException("A message is required. ", nameof(message));
        var sender = string.IsNullOrWhiteSpace(from) ? _settings.From : from;
        var body = write(w => {
            w.WriteString("to", to);
            w.WriteString("message", message);
            if (!string.IsNullOrWhiteSpace(sender)) w.WriteString("from", sender);
            if (!string.IsNullOrWhiteSpace(reference)) w.WriteString("reference", reference);
        });
        var json = await postAsync(_sendUrl, body, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new SmsReceipt(
            text(root, "messageId") ?? "",
            text(root, "to") ?? to,
            number(root, "parts"),
            number(root, "credits"),
            number(root, "creditsLeft"),
            text(root, "reference"));
    }

    public async Task<SmsQuote> QuoteAsync(string to, string message, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(to)) throw new ArgumentException("A recipient number is required. ", nameof(to));
        var body = write(w => {
            w.WriteString("to", to);
            w.WriteString("message", message ?? string.Empty);
        });
        var json = await postAsync(_quoteUrl, body, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new SmsQuote(
            text(root, "to") ?? to,
            number(root, "parts"),
            number(root, "credits"),
            root.TryGetProperty("unicode", out var unicode) && unicode.ValueKind == JsonValueKind.True,
            number(root, "characters"));
    }

    static string write(Action<Utf8JsonWriter> properties) {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) {
            w.WriteStartObject();
            properties(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static string? text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static int number(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    /// <summary>
    /// Posts and returns the body, turning a refusal into an exception that repeats the service's own reason.
    /// A licensing "no" (402, 403) is a permanent answer for this message, so it is not retried; only the
    /// transient statuses <see cref="HttpRetry"/> knows about are. A send is not idempotent - a timed out
    /// attempt may already have reached a phone - so a timeout is not retried either, and the caller is told.
    /// </summary>
    async Task<string> postAsync(string url, string jsonBody, CancellationToken cancellationToken) {
        using var response = await HttpRetry.SendAsync(_http, () => {
            var request = new HttpRequestMessage(HttpMethod.Post, url) {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            return request;
        }, retryOnTimeout: false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) {
            throw new Exception($"The Relatude SMS service at {url} returned {(int)response.StatusCode} {response.StatusCode}: {readError(body)}");
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
        GC.SuppressFinalize(this);
    }
}
