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
/// key, and the license needs its "sms" credit account with a balance, and no feature. A refusal
/// comes back as an exception repeating the service's own reason - out of credits, no "sms" account,
/// rate limited - so what the database logs is what the person configuring it needs to read.</para>
/// <para>A message goes as the service's own sender, or as a sender Relatude has approved for the
/// license: a name of up to eleven letters and digits, or a phone number with its country code,
/// which the customer asks for on the license's page in Relatude Services. A license with the
/// "smsanysender" feature may name any such sender; the license server decides. A sender - <c>from</c>,
/// or <see cref="SMSProviderSettings.From"/> - is checked twice. On a server the provider is given
/// <c>senderCheck</c>, which asks the license server before anything is posted, so a sender that is
/// not approved is refused with the license server's reason and nothing is sent or charged; the
/// service then asks the license server again before it sends. Built without a check, outside a
/// server, the provider leaves the question to the service alone.</para>
/// <para>A message is paid for before the service hands it to the gateway, and the credits are never
/// given back, so a send is repeated only on the two answers the service gives before any money moves:
/// 429, rate limited, and 503, the license server out of reach. Any other failure - the gateway
/// failing, an error in the service, a timeout, a dropped connection - may already have cost credits
/// or reached the phone, so it is thrown rather than retried, and whether to send again is for the
/// caller to decide. A quote costs nothing, so the usual retries apply to it.</para>
/// <para>The key is the one issued with the license. On a server it is the installation's own,
/// handed in as <c>licenseApiKey</c>, so a database's SMS settings need none; elsewhere it is
/// <see cref="SMSProviderSettings.ApiKey"/>. A provider without either is still built, and says what
/// is missing the first time it is asked to send.</para>
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
    readonly SMSProviderSettings _settings;
    readonly Func<string?>? _licenseApiKey;
    readonly Func<string, CancellationToken, Task<string?>>? _senderCheck;

    public RelatudeServicesSMSProvider(SMSProviderSettings settings) : this(settings, null) { }

    /// <param name="settings">Where the service is and who the messages are from.</param>
    /// <param name="licenseApiKey">The API key of the license the installation runs under, asked for
    /// at every call, so a new license takes effect without the database reopening. When it has one
    /// it is used before <see cref="SMSProviderSettings.ApiKey"/>: that one is a copy the settings
    /// used to require, and it would go stale unnoticed when the license changes.</param>
    /// <param name="senderCheck">Asked before a message with a sender of its own is posted: returns
    /// why the sender may not be used, or null when it may. On a server it asks the license server
    /// whether the sender is approved for the license. Without it the service is the only check.</param>
    public RelatudeServicesSMSProvider(SMSProviderSettings settings, Func<string?>? licenseApiKey, Func<string, CancellationToken, Task<string?>>? senderCheck = null) {
        _settings = settings;
        _licenseApiKey = licenseApiKey;
        _senderCheck = senderCheck;
        var baseUrl = (string.IsNullOrWhiteSpace(settings.ServiceUrl) ? _defaultServiceUrl : settings.ServiceUrl).TrimEnd('/');
        _sendUrl = baseUrl + "/api/sms/send";
        _quoteUrl = baseUrl + "/api/sms/quote";
        // A message is one short call; a minute is already generous, and a hung gateway must not
        // hold a request thread for the five an embedding batch is allowed.
        _http = new HttpClient() { Timeout = TimeSpan.FromMinutes(1) };
    }

    /// <summary>The key this call is charged to, or an exception saying where one is set when there is none.</summary>
    string apiKey() {
        var key = _licenseApiKey?.Invoke();
        if (string.IsNullOrWhiteSpace(key)) key = _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) {
            throw new InvalidOperationException("There is no API key to send the message with. The Relatude SMS service charges every message to a license: "
                + "set this installation's license key and API key under License in the admin UI, or give the SMS settings an API key of their own. ");
        }
        return key.Trim();
    }

    /// <summary>Whether a configured provider type name means this provider, by either of its names.</summary>
    public static bool IsProviderName(string? typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && (typeName.Trim().Equals(nameof(RelatudeServicesSMSProvider), StringComparison.OrdinalIgnoreCase)
            || typeName.Trim().Equals(ShortName, StringComparison.OrdinalIgnoreCase));

    public string Name => "Relatude SMS service";

    public async Task<SmsReceipt> SendAsync(string to, string message, string? from = null, string? reference = null, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(to)) throw new ArgumentException("A recipient number is required. ", nameof(to));
        if (string.IsNullOrEmpty(message)) throw new ArgumentException("A message is required. ", nameof(message));
        var sender = string.IsNullOrWhiteSpace(from) ? _settings.From : from;
        if (!string.IsNullOrWhiteSpace(sender) && _senderCheck != null) {
            // before anything is posted: a sender refused here is a message neither sent nor charged
            var refusal = await _senderCheck(sender, cancellationToken);
            if (refusal != null) throw new InvalidOperationException(refusal);
        }
        var body = write(w => {
            w.WriteString("to", to);
            w.WriteString("message", message);
            if (!string.IsNullOrWhiteSpace(sender)) w.WriteString("from", sender);
            if (!string.IsNullOrWhiteSpace(reference)) w.WriteString("reference", reference);
        });
        var json = await postAsync(_sendUrl, body, idempotent: false, cancellationToken);
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
        var json = await postAsync(_quoteUrl, body, idempotent: true, cancellationToken);
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
    /// <para>A send is not idempotent: the service takes the credits before it hands the message to the
    /// gateway, and cannot give them back. So it is repeated only on the answers the service gives before
    /// any money moves (<see cref="answeredBeforeCharging"/>). Any other status either comes once the
    /// credits may have been taken - 502 when the gateway failed, 424 when the license server did not
    /// confirm the charge, 500 from an error after it, 504 from Azure's front end while the service is
    /// still at work - or is the service's final word on the message (400 to 403, 422).
    /// A timeout is not retried, and neither is a connection error (<see cref="HttpRequestException"/>):
    /// both can come after the service had the request, when it may have charged and sent the message,
    /// and nothing in either says how far it got. The caller is told, and decides.</para>
    /// <para>A quote charges and sends nothing, so it is retried on everything <see cref="HttpRetry"/>
    /// counts as transient, a dropped connection included. Only a timeout is not retried on either
    /// route: a minute is already long to wait for one answer.</para>
    /// </summary>
    async Task<string> postAsync(string url, string jsonBody, bool idempotent, CancellationToken cancellationToken) {
        var key = apiKey(); // once, before anything is sent: every attempt goes with the same key
        using var response = await HttpRetry.SendAsync(_http, () => {
            var request = new HttpRequestMessage(HttpMethod.Post, url) {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return request;
        }, retryOnTimeout: false,
            isTransient: idempotent ? null : answeredBeforeCharging,
            retryOnConnectionError: idempotent);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) {
            throw new Exception($"The Relatude SMS service at {url} returned {(int)response.StatusCode} {response.StatusCode}: {readError(body)}");
        }
        return body;
    }

    /// <summary>
    /// The failures a send may be repeated on: 429, rate limited with the Retry-After to wait, and 503,
    /// the license server out of reach. The service gives both before it takes any credits, and neither
    /// has reached the gateway. None of the others <see cref="HttpRetry.IsTransient"/> would repeat is
    /// safe: a 500, 502 or 504 can come after the message was charged or sent, and a 408 is not among
    /// the service's answers, so it says nothing about how far the request got.
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
        GC.SuppressFinalize(this);
    }
}
