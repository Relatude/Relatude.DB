using System.Net.Http.Headers;
using System.Text.Json;
using Relatude.DB.AI;
using Relatude.DB.Http;

namespace Relatude.DB.Email;

/// <summary>
/// Sends e-mail through the hosted Relatude Email service (Relatude.DB.Services.Email), over plain
/// HttpClient.
/// <para>Like <see cref="Relatude.DB.SMS.RelatudeServicesSMSProvider"/> it needs no account with a
/// vendor: the service holds the mail provider's credentials and charges every recipient to the
/// license behind the API key, and the license needs its "email" credit account with a balance, and no
/// feature. A refusal comes back as an exception repeating the service's own reason - out of credits,
/// no "email" account, an address that cannot be one, a message too large - so what the database logs
/// is what the person configuring it needs to read.</para>
/// <para>A message goes as the service's own sender, or as any address at a domain Relatude has
/// approved for the license, which the customer asks for on the license's page in Relatude Services:
/// the approval is for the domain, never for what is before the @. A license with the "emailanysender"
/// feature may name any sender; the license server decides. A sender - <see cref="EmailMessage.From"/>, or
/// <see cref="EmailProviderSettings.From"/> - is checked twice. On a server the provider is given
/// <c>senderCheck</c>, which asks the license server before anything is posted, so a sender that is not
/// approved is refused with the license server's reason and nothing is sent or charged; the service then
/// asks the license server again before it sends. Built without a check, outside a server, the provider
/// leaves the question to the service alone. Where replies go (<see cref="EmailMessage.ReplyTo"/>) needs
/// no approval and is not checked.</para>
/// <para>A message is paid for before the service hands it to the mail provider, and the credits are
/// never given back, so a send is repeated only on the two answers the service gives before any money
/// moves: 429, rate limited, and 503, the license server out of reach. Any other failure - the mail
/// provider failing, an error in the service, a timeout, a dropped connection - may already have cost
/// credits or reached the recipients, so it is thrown rather than retried, and whether to send again is
/// for the caller to decide. A quote costs nothing, so the usual retries apply to it.</para>
/// <para>The key is the one issued with the license. On a server it is the installation's own,
/// handed in as <c>licenseApiKey</c>, so a database's e-mail settings need none; elsewhere it is
/// <see cref="EmailProviderSettings.ApiKey"/>. A provider without either is still built, and says what
/// is missing the first time it is asked to send.</para>
/// <para><see cref="EmailProviderSettings.ServiceUrl"/> is the root of the service and defaults to
/// <c>https://email.relatude.com</c>; point it at your own deployment when the service is
/// self-hosted.</para>
/// </summary>
public class RelatudeServicesEmailProvider : IEmailProvider {
    const string _defaultServiceUrl = "https://email.relatude.com";

    /// <summary>The short name this provider is configured under, beside its own type name.
    /// <c>LateBindings.CreateEmailProvider</c> resolves both, and the settings page offers this one.</summary>
    public const string ShortName = "RelatudeServices";

    readonly HttpClient _http;
    readonly string _sendUrl;
    readonly string _quoteUrl;
    readonly EmailProviderSettings _settings;
    readonly Func<string?>? _licenseApiKey;
    readonly Func<string, CancellationToken, Task<string?>>? _senderCheck;

    public RelatudeServicesEmailProvider(EmailProviderSettings settings) : this(settings, null) { }

    /// <param name="settings">Where the service is and who the messages are from when they name no sender.</param>
    /// <param name="licenseApiKey">The API key of the license the installation runs under, asked for
    /// at every call, so a new license takes effect without the database reopening. When it has one
    /// it is used before <see cref="EmailProviderSettings.ApiKey"/>: that one is for a provider built
    /// from code, and on a server it would go stale unnoticed when the license changes.</param>
    /// <param name="senderCheck">Asked before a message with a sender of its own is posted, with the
    /// sender's address alone: returns why the sender may not be used, or null when it may. On a server
    /// it asks the license server whether the address is approved for the license. Without it the
    /// service is the only check.</param>
    public RelatudeServicesEmailProvider(EmailProviderSettings settings, Func<string?>? licenseApiKey, Func<string, CancellationToken, Task<string?>>? senderCheck = null) {
        _settings = settings;
        _licenseApiKey = licenseApiKey;
        _senderCheck = senderCheck;
        var baseUrl = (string.IsNullOrWhiteSpace(settings.ServiceUrl) ? _defaultServiceUrl : settings.ServiceUrl).TrimEnd('/');
        _sendUrl = baseUrl + "/api/email/send";
        _quoteUrl = baseUrl + "/api/email/quote";
        // A message is one call, but it may carry some 20 MB of attachments, written out a third
        // larger as base64, to a service that hands it on to the mail provider before it answers.
        // Two minutes is room for that on a slow line, and still ends a call that has hung.
        _http = new HttpClient() { Timeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>The key this call is charged to, or an exception saying where one is set when there is none.</summary>
    string apiKey() {
        var key = _licenseApiKey?.Invoke();
        if (string.IsNullOrWhiteSpace(key)) key = _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) {
            throw new InvalidOperationException("There is no API key to send the e-mail with. The Relatude Email service charges every recipient to a license: "
                + "set this installation's license key and API key under License in the admin UI, or give the e-mail settings an API key of their own. ");
        }
        return key.Trim();
    }

    /// <summary>Whether a configured provider type name means this provider, by either of its names.</summary>
    public static bool IsProviderName(string? typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && (typeName.Trim().Equals(nameof(RelatudeServicesEmailProvider), StringComparison.OrdinalIgnoreCase)
            || typeName.Trim().Equals(ShortName, StringComparison.OrdinalIgnoreCase));

    public string Name => "Relatude Email service";

    public async Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default) {
        checkMessage(message);
        var sender = await senderOfAsync(message, cancellationToken);
        var body = write(w => writeMessage(w, message, sender));
        var json = await postAsync(_sendUrl, body, idempotent: false, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new EmailReceipt(
            text(root, "messageId") ?? "",
            text(root, "from") ?? sender?.ToString() ?? "",
            number(root, "recipients"),
            longNumber(root, "bytes"),
            number(root, "credits"),
            number(root, "creditsLeft"),
            text(root, "reference"));
    }

    public async Task<EmailQuote> QuoteAsync(EmailMessage message, CancellationToken cancellationToken = default) {
        checkMessage(message);
        // the same body as a send, sender and all, so the quote says whether the message could go
        var sender = await senderOfAsync(message, cancellationToken);
        var body = write(w => writeMessage(w, message, sender));
        var json = await postAsync(_quoteUrl, body, idempotent: true, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new EmailQuote(
            number(root, "recipients"),
            longNumber(root, "bytes"),
            number(root, "credits"));
    }

    /// <summary>
    /// What is checked here before anything is posted: that there is a message, someone to send it to,
    /// a subject and a body. The addresses themselves go as given: the service checks every one, and
    /// names the one it cannot send to.
    /// </summary>
    static void checkMessage(EmailMessage message) {
        ArgumentNullException.ThrowIfNull(message);
        if ((message.To?.Count ?? 0) + (message.Cc?.Count ?? 0) + (message.Bcc?.Count ?? 0) == 0) {
            throw new ArgumentException("At least one recipient is required, in To, Cc or Bcc. ", nameof(message));
        }
        if (string.IsNullOrWhiteSpace(message.Subject)) throw new ArgumentException("A subject is required. ", nameof(message));
        if (string.IsNullOrEmpty(message.Text) && string.IsNullOrEmpty(message.Html)) {
            throw new ArgumentException("A body is required: the message's Text, its Html, or both. ", nameof(message));
        }
    }

    /// <summary>
    /// The sender of a message - its own, or the one the settings give, with the settings' name beside
    /// it - checked before anything is posted when there is a check: a sender refused here is a message
    /// neither sent nor charged. Null is the service's own sender, which needs no approval.
    /// </summary>
    async Task<EmailAddress?> senderOfAsync(EmailMessage message, CancellationToken cancellationToken) {
        EmailAddress? sender = message.From is { } own && !string.IsNullOrWhiteSpace(own.Address) ? own
            : string.IsNullOrWhiteSpace(_settings.From) ? null
            : new EmailAddress(_settings.From.Trim(), string.IsNullOrWhiteSpace(_settings.FromName) ? null : _settings.FromName.Trim());
        if (sender != null && _senderCheck != null) {
            var refusal = await _senderCheck(bareAddress(sender.Address), cancellationToken);
            if (refusal != null) throw new InvalidOperationException(refusal);
        }
        return sender;
    }

    /// <summary>The address alone, out of "Name &lt;address&gt;" when it is written that way: what an approval names.</summary>
    static string bareAddress(string address) {
        var written = address.Trim();
        var open = written.LastIndexOf('<');
        var close = written.LastIndexOf('>');
        return open >= 0 && close > open ? written[(open + 1)..close].Trim() : written;
    }

    /// <summary>
    /// The body the service takes, in its own field names: it refuses a field it does not know, so
    /// nothing else is written, and a field that is empty is left out rather than sent empty.
    /// </summary>
    static void writeMessage(Utf8JsonWriter w, EmailMessage message, EmailAddress? sender) {
        if (sender != null) writeAddress(w, "from", sender);
        writeAddresses(w, "to", message.To);
        writeAddresses(w, "cc", message.Cc);
        writeAddresses(w, "bcc", message.Bcc);
        writeAddresses(w, "replyTo", message.ReplyTo);
        w.WriteString("subject", message.Subject);
        if (!string.IsNullOrEmpty(message.Text)) w.WriteString("text", message.Text);
        if (!string.IsNullOrEmpty(message.Html)) w.WriteString("html", message.Html);
        if (message.Attachments is { Count: > 0 } attachments) {
            w.WriteStartArray("attachments");
            for (var i = 0; i < attachments.Count; i++) {
                var attachment = attachments[i] ?? throw new ArgumentException($"Attachments[{i}] is null. ", nameof(message));
                if (attachment.Content == null) throw new ArgumentException($"Attachments[{i}], {attachment.FileName}, has no content. ", nameof(message));
                w.WriteStartObject();
                w.WriteString("fileName", attachment.FileName);
                w.WriteBase64String("content", attachment.Content);
                if (!string.IsNullOrWhiteSpace(attachment.ContentType)) w.WriteString("contentType", attachment.ContentType);
                if (!string.IsNullOrWhiteSpace(attachment.ContentId)) w.WriteString("contentId", attachment.ContentId);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (message.Headers is { Count: > 0 } headers) {
            w.WriteStartObject("headers");
            foreach (var (name, value) in headers) {
                if (string.IsNullOrWhiteSpace(name) || value == null) continue;
                w.WriteString(name, value);
            }
            w.WriteEndObject();
        }
        var tags = message.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
        if (tags.Length > 0) {
            w.WriteStartArray("tags");
            foreach (var tag in tags) w.WriteStringValue(tag);
            w.WriteEndArray();
        }
        if (!string.IsNullOrWhiteSpace(message.Reference)) w.WriteString("reference", message.Reference);
    }

    /// <summary>A list of addresses, left out when it is empty.</summary>
    static void writeAddresses(Utf8JsonWriter w, string name, List<EmailAddress>? addresses) {
        if (addresses == null || addresses.Count == 0) return;
        w.WriteStartArray(name);
        for (var i = 0; i < addresses.Count; i++) {
            var address = addresses[i] ?? throw new ArgumentException($"{name}[{i}] is null. ", "message");
            writeAddressValue(w, address);
        }
        w.WriteEndArray();
    }

    static void writeAddress(Utf8JsonWriter w, string name, EmailAddress address) {
        w.WritePropertyName(name);
        writeAddressValue(w, address);
    }

    /// <summary>An address as a plain string when it has no name - "a@b.no", or a whole "Name &lt;a@b.no&gt;" as
    /// written, which the service reads - and as <c>{ address, name }</c> when it has one.</summary>
    static void writeAddressValue(Utf8JsonWriter w, EmailAddress address) {
        if (string.IsNullOrWhiteSpace(address.Name)) {
            w.WriteStringValue(address.Address);
            return;
        }
        w.WriteStartObject();
        w.WriteString("address", address.Address);
        w.WriteString("name", address.Name);
        w.WriteEndObject();
    }

    /// <summary>The body as UTF-8 bytes, which is what goes on the wire: a message with its attachments
    /// can be large, and is not turned into a string and back on the way.</summary>
    static byte[] write(Action<Utf8JsonWriter> properties) {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) {
            w.WriteStartObject();
            properties(w);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    static string? text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static int number(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;

    static long longNumber(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// Posts and returns the body, turning a refusal into an exception that repeats the service's own reason.
    /// <para>A send is not idempotent: the service takes the credits before it hands the message to the
    /// mail provider, and cannot give them back. So it is repeated only on the answers the service gives
    /// before any money moves (<see cref="answeredBeforeCharging"/>). Any other status either comes once
    /// the credits may have been taken - 502 when the mail provider failed, 424 when the license server
    /// did not confirm the charge, 422 when the provider will not deliver the message, 500 from an error
    /// after it, 504 from Azure's front end while the service is still at work - or is the service's final
    /// word on the message (400 to 403, 413). A timeout is not retried, and neither is a connection error
    /// (<see cref="HttpRequestException"/>): both can come after the service had the request, when it may
    /// have charged and sent the message, and nothing in either says how far it got. The caller is told,
    /// and decides.</para>
    /// <para>A quote charges and sends nothing, so it is retried on everything <see cref="HttpRetry"/>
    /// counts as transient, a dropped connection included. Only a timeout is not retried on either
    /// route: two minutes is already long to wait for one answer.</para>
    /// </summary>
    async Task<string> postAsync(string url, byte[] jsonBody, bool idempotent, CancellationToken cancellationToken) {
        var key = apiKey(); // once, before anything is sent: every attempt goes with the same key
        using var response = await HttpRetry.SendAsync(_http, () => {
            var content = new ByteArrayContent(jsonBody);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return request;
        }, retryOnTimeout: false,
            isTransient: idempotent ? null : answeredBeforeCharging,
            retryOnConnectionError: idempotent,
            cancellationToken: cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) {
            throw new Exception($"The Relatude Email service at {url} returned {(int)response.StatusCode} {response.StatusCode}: {readError(body)}");
        }
        return body;
    }

    /// <summary>
    /// The failures a send may be repeated on: 429, rate limited with the Retry-After to wait, and 503,
    /// the license server out of reach. The service gives both before it takes any credits, and neither
    /// has reached the mail provider. None of the others <see cref="HttpRetry.IsTransient"/> would repeat
    /// is safe: a 500, 502 or 504 can come after the message was charged or sent, and a 408 is not among
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
