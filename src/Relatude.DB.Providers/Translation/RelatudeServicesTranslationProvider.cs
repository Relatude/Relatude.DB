using System.Text;
using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.Http;

namespace Relatude.DB.Translation;

/// <summary>
/// Translation through the hosted Relatude Translation service (Relatude.DB.Services.Translation), over plain HttpClient.
/// <para>Like <c>RelatudeServicesImagingProvider</c> it needs no account with a vendor: the service holds
/// the vendor credentials and charges every call to the license behind the API key, which needs its
/// "translation" credit account with a balance. A call is paid for by the characters it sends, and a
/// text translated before, for the same license and to the same language, is given from what the
/// service kept, at the price it sets for that. A refusal comes back as a
/// <see cref="RelatudeServiceException"/> repeating the service's own reason - out of credits, no
/// "translation" account, a language it does not translate - so what the database logs is what the
/// person configuring it needs to read.</para>
/// <para>A call is paid for before the service hands it to a provider, and the credits are never given
/// back, so it is repeated only on the two answers the service gives before any money moves (429 and
/// 503); see <c>RelatudeServiceClient</c>. A 424 says the license server did not confirm the charge and
/// nothing was translated, but it may have been charged all the same: the caller decides whether to ask again.</para>
/// <para>The key is the one issued with the license. On a server it is the installation's own, handed
/// in as <c>licenseApiKey</c>, so a database's translation settings need none; elsewhere it is
/// <see cref="TranslationProviderSettings.ApiKey"/>. A provider without either is still built, and says
/// what is missing the first time it is called.</para>
/// <para><see cref="TranslationProviderSettings.ServiceUrl"/> is the root of the service and defaults to
/// <c>https://translation.services.relatude.com</c>; point it at your own deployment when the service is
/// self-hosted.</para>
/// </summary>
public class RelatudeServicesTranslationProvider : ITranslationProvider {
    /// <summary>The hosted service, used when the settings name no other.</summary>
    public const string DefaultServiceUrl = "https://translation.services.relatude.com";

    /// <summary>The short name this provider is configured under, beside its own type name.
    /// <c>LateBindings.CreateTranslationProvider</c> resolves both, and the settings page offers this one.</summary>
    public const string ShortName = "RelatudeServices";

    readonly RelatudeServiceClient _client;

    public RelatudeServicesTranslationProvider(TranslationProviderSettings settings) : this(settings, null) { }

    /// <param name="settings">Where the service is, and the key to use when there is no license key.</param>
    /// <param name="licenseApiKey">The API key of the license the installation runs under, asked for at
    /// every call, so a new license takes effect without the database reopening. When it has one it is
    /// used before <see cref="TranslationProviderSettings.ApiKey"/>, the same rule as <c>RelatudeServicesSMSProvider</c>.</param>
    public RelatudeServicesTranslationProvider(TranslationProviderSettings settings, Func<string?>? licenseApiKey) {
        ArgumentNullException.ThrowIfNull(settings);
        ServiceUrl = RootUrl(settings.ServiceUrl);
        _client = new RelatudeServiceClient(Name, ServiceUrl + "/api/translation", () => {
            var key = licenseApiKey?.Invoke();
            return string.IsNullOrWhiteSpace(key) ? settings.ApiKey : key;
        }, "There is no API key to call the Relatude Translation service with. The service charges every call to a license: "
            + "set this installation's license key and API key under Services in the admin UI, or give the translation settings an API key of their own. ",
            // the service gives each provider two minutes, and a call may go on to a second one
            TimeSpan.FromMinutes(5));
    }

    /// <summary>The root of the service this provider calls, without a trailing slash.</summary>
    public string ServiceUrl { get; }

    /// <summary>The service root a settings value names, with the hosted service as the default.</summary>
    public static string RootUrl(string? serviceUrl) => (string.IsNullOrWhiteSpace(serviceUrl) ? DefaultServiceUrl : serviceUrl.Trim()).TrimEnd('/');

    /// <summary>Whether a configured provider type name means this provider, by either of its names.</summary>
    public static bool IsProviderName(string? typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && (typeName.Trim().Equals(nameof(RelatudeServicesTranslationProvider), StringComparison.OrdinalIgnoreCase)
            || typeName.Trim().Equals(ShortName, StringComparison.OrdinalIgnoreCase));

    public string Name => "Relatude Translation service";

    sealed record TranslateAnswer(TranslatedText[]? Translations, int Characters, int CachedCharacters, int Credits, int CreditsLeft);

    public async Task<TranslationResult> TranslateAsync(IReadOnlyList<TranslationText> texts, string? to = null, string? from = null,
        TranslationFormat format = TranslationFormat.Text, bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required. ", nameof(texts));
        var callTo = code(to);
        var callFrom = code(from);
        for (var i = 0; i < texts.Count; i++) {
            var text = texts[i] ?? throw new ArgumentException($"texts[{i}] is null. ", nameof(texts));
            if (text.Text == null) throw new ArgumentException($"texts[{i}] has no text. ", nameof(texts));
            if (callTo == null && code(text.To) == null) throw new ArgumentException($"There is no language to translate texts[{i}] to: give the call a language, or every text one of its own. ", nameof(to));
        }
        var body = write(w => {
            if (callTo != null) w.WriteString("to", callTo);
            if (callFrom != null) w.WriteString("from", callFrom);
            if (format == TranslationFormat.Html) w.WriteString("format", "html");
            w.WriteStartArray("texts");
            foreach (var text in texts) {
                var (textFrom, textTo) = (code(text.From), code(text.To));
                // a text without languages of its own goes as a plain string, the way the service documents it
                if (textFrom == null && textTo == null) {
                    w.WriteStringValue(text.Text);
                    continue;
                }
                w.WriteStartObject();
                w.WriteString("text", text.Text);
                if (textFrom != null) w.WriteString("from", textFrom);
                if (textTo != null) w.WriteString("to", textTo);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        var answer = await postAsync<TranslateAnswer>("translate", body, fresh, "translations", cancellationToken);
        var translations = answer.Translations ?? [];
        if (translations.Length != texts.Count) throw new RelatudeServiceException(0, $"The {Name} answered {texts.Count} texts with {translations.Length} translations. ");
        return new TranslationResult(translations, answer.Characters, answer.CachedCharacters, answer.Credits, answer.CreditsLeft);
    }

    sealed record DetectAnswer(DetectedLanguage[]? Detections, int Characters, int CachedCharacters, int Credits, int CreditsLeft);

    public async Task<LanguageDetectionResult> DetectLanguagesAsync(IReadOnlyList<string> texts, bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required. ", nameof(texts));
        for (var i = 0; i < texts.Count; i++) {
            if (texts[i] == null) throw new ArgumentException($"texts[{i}] is null. ", nameof(texts));
        }
        var body = write(w => {
            w.WriteStartArray("texts");
            foreach (var text in texts) w.WriteStringValue(text);
            w.WriteEndArray();
        });
        var answer = await postAsync<DetectAnswer>("detect", body, fresh, "the languages of texts", cancellationToken);
        var detections = answer.Detections ?? [];
        if (detections.Length != texts.Count) throw new RelatudeServiceException(0, $"The {Name} answered {texts.Count} texts with {detections.Length} languages. ");
        // an older service, or a text that could not be placed, may leave the alternatives out
        for (var i = 0; i < detections.Length; i++) {
            if (detections[i].Alternatives == null) detections[i] = detections[i] with { Alternatives = [] };
        }
        return new LanguageDetectionResult(detections, answer.Characters, answer.CachedCharacters, answer.Credits, answer.CreditsLeft);
    }

    public async Task<TranslationLanguages> GetLanguagesAsync(CancellationToken cancellationToken = default) {
        var json = await _client.GetAsync("languages", cancellationToken);
        var languages = _client.Parse<TranslationLanguages>(json, "languages", "a list of languages");
        // a list that could not be had is answered 503 by the service; one that is empty reads as nothing offered
        return languages with {
            Languages = languages.Languages ?? [],
            Aliases = languages.Aliases ?? new Dictionary<string, string>(),
        };
    }

    async Task<T> postAsync<T>(string operation, string body, bool fresh, string what, CancellationToken cancellationToken) where T : class {
        var (response, _) = await _client.PostAsync(operation, body, fresh, [], cancellationToken);
        using (response) {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return _client.Parse<T>(json, operation, what);
        }
    }

    static string? code(string? language) => string.IsNullOrWhiteSpace(language) ? null : language.Trim();

    static string write(Action<Utf8JsonWriter> properties) {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) {
            w.WriteStartObject();
            properties(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose() {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
