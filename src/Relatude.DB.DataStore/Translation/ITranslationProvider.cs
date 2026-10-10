namespace Relatude.DB.Translation;

/// <summary>
/// Translation on behalf of the database: texts translated, and the languages they are in found, many
/// in one call, so application code does not hold a vendor account of its own. Reached through
/// <c>NodeStore.Services.Translation</c>.
///
/// <para>One implementation ships: <c>RelatudeServicesTranslationProvider</c>, which calls the hosted
/// Relatude Translation service and charges each call to the license. The interface is here so that an
/// account of your own can be plugged in later without any calling code changing, the way
/// <see cref="Relatude.DB.SMS.ISMSProvider"/> is.</para>
///
/// <para>Languages are BCP 47 codes - en, nb, de, zh-Hans, pt-PT - and an answer names them the way
/// <see cref="GetLanguagesAsync"/> lists them: <c>nb-NO</c> and <c>no</c> are answered as <c>nb</c>. A
/// text whose language is not given has it found, text by text, so one call may carry texts in many
/// languages. White space at the ends of a text is kept as it was sent.</para>
///
/// <para>A text translated before, to the same language, is answered from what the service kept, at
/// the price it sets for that. <c>fresh</c> asks for every text to be translated anew instead, which is
/// paid for again.</para>
///
/// <para>An implementation is long lived and shared: it is built once when the database is configured
/// and disposed with it, so it must be safe to call from several threads at once.</para>
/// </summary>
public interface ITranslationProvider : IDisposable {
    /// <summary>What this provider is, for the admin UI and the log.</summary>
    string Name { get; }

    /// <summary>
    /// The texts translated, one translation for each text, in the order they were given. A text is a
    /// string, taking the call's <paramref name="to"/> and <paramref name="from"/>, or a
    /// <see cref="TranslationText"/> with languages of its own. <paramref name="to"/> is required, for
    /// the call or for every text. Without a <paramref name="from"/> the language of each text is found.
    /// <paramref name="format"/> <see cref="TranslationFormat.Html"/> keeps the markup and translates its text.
    /// </summary>
    Task<TranslationResult> TranslateAsync(IReadOnlyList<TranslationText> texts, string? to = null, string? from = null,
        TranslationFormat format = TranslationFormat.Text, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>The language of each text, in the order they were given. Priced as a translation is.</summary>
    Task<LanguageDetectionResult> DetectLanguagesAsync(IReadOnlyList<string> texts, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The languages there are to translate to and from, with what a call costs and how much it may
    /// carry. Costs nothing, and needs no license.
    /// </summary>
    Task<TranslationLanguages> GetLanguagesAsync(CancellationToken cancellationToken = default);

    /// <summary>The texts translated to <paramref name="to"/>, one translation for each, in order. See the main overload.</summary>
    Task<TranslationResult> TranslateAsync(IReadOnlyList<string> texts, string to, string? from = null,
        TranslationFormat format = TranslationFormat.Text, bool fresh = false, CancellationToken cancellationToken = default)
        => TranslateAsync(texts?.Select(t => new TranslationText(t)).ToArray() ?? throw new ArgumentNullException(nameof(texts)), to, from, format, fresh, cancellationToken);

    /// <summary>One text translated to <paramref name="to"/>. Many texts go cheaper and faster in one call.</summary>
    async Task<string> TranslateAsync(string text, string to, string? from = null,
        TranslationFormat format = TranslationFormat.Text, bool fresh = false, CancellationToken cancellationToken = default)
        => (await TranslateAsync([new TranslationText(text)], to, from, format, fresh, cancellationToken)).Translations[0].Text;

    /// <summary>The language of one text.</summary>
    async Task<DetectedLanguage> DetectLanguageAsync(string text, bool fresh = false, CancellationToken cancellationToken = default)
        => (await DetectLanguagesAsync([text], fresh, cancellationToken)).Detections[0];
}

/// <summary>
/// One text to translate, with the languages it goes from and to when they are not the call's. A string
/// is taken as a text without languages of its own, so a list may mix the two:
/// <code>["Good morning", new("Hej och välkommen", From: "sv"), new("Free delivery", To: "de")]</code>
/// </summary>
public sealed record TranslationText(string Text, string? From = null, string? To = null) {
    public static implicit operator TranslationText(string text) => new(text);
}

/// <summary>What a text is: plain text, or HTML whose markup is kept and only its text translated.</summary>
public enum TranslationFormat {
    Text,
    Html,
}

/// <summary>
/// The translations, one for each text, in the order the texts were given. <see cref="Characters"/> is
/// what was sent to be translated now and <see cref="CachedCharacters"/> what was given from what the
/// service kept, each different text counted once for each language it went to. <see cref="Credits"/>
/// is what the call cost for both, and <see cref="CreditsLeft"/> what is left on the license's account.
/// </summary>
public sealed record TranslationResult(TranslatedText[] Translations, int Characters, int CachedCharacters, int Credits, int CreditsLeft) {
    /// <summary>The translated texts alone, in order.</summary>
    public string[] Texts => Translations.Select(t => t.Text).ToArray();
}

/// <summary>
/// One text translated. <see cref="From"/> is the language it was taken to be in: as the caller named
/// it, or as it was found, which <see cref="Detected"/> says, with how sure that is in
/// <see cref="Score"/> (0 to 1). From is null for a text with nothing to translate, and for one whose
/// language was not said. <see cref="Cached"/> says the translation was made before.
/// </summary>
public sealed record TranslatedText(string Text, string? From, string To, bool Detected, double? Score, bool Cached);

/// <summary>
/// The languages found, one for each text, in the order the texts were given; priced as translations
/// are. <see cref="Characters"/>, <see cref="CachedCharacters"/>, <see cref="Credits"/> and
/// <see cref="CreditsLeft"/> are as for <see cref="TranslationResult"/>.
/// </summary>
public sealed record LanguageDetectionResult(DetectedLanguage[] Detections, int Characters, int CachedCharacters, int Credits, int CreditsLeft);

/// <summary>
/// The language of one text, how sure that is (0 to 1), whether it can be translated from, and the
/// other languages it might be in. <see cref="Language"/> is null for a text with nothing in it, or one
/// that could not be placed.
/// </summary>
public sealed record DetectedLanguage(string? Language, double Score, bool Translatable, bool Cached, LanguageScore[] Alternatives);

/// <summary>A language a text might be in, and how likely (0 to 1).</summary>
public sealed record LanguageScore(string Language, double Score);

/// <summary>
/// The languages there are, for a settings page to choose from, and <see cref="Aliases"/>, other codes
/// taken for some of them. Then the price - <see cref="CharsPerCredit"/> characters sent to be
/// translated for a credit, <see cref="CachedCharsPerCredit"/> given from what was kept (0: free) - and
/// the most one call may carry: <see cref="MaxTexts"/> texts, <see cref="MaxTextChars"/> characters in
/// one text and <see cref="MaxTotalChars"/> in all.
/// </summary>
public sealed record TranslationLanguages(TranslationLanguage[] Languages, IReadOnlyDictionary<string, string> Aliases,
    int CharsPerCredit, int CachedCharsPerCredit, int MaxTexts, int MaxTextChars, int MaxTotalChars);

/// <summary>One language: its code, its name in English and in itself, and which way it is written ("ltr" or "rtl").</summary>
public sealed record TranslationLanguage(string Code, string Name, string NativeName, string Direction);

/// <summary>
/// How the database reaches a translation service. The shape follows
/// <see cref="Relatude.DB.SMS.SMSProviderSettings"/>: a type name resolved when the database opens, an
/// endpoint, and the key that pays for the calls. The hosted Relatude service needs neither of the
/// last two on a server, which calls it with the installation's own license.
/// </summary>
public class TranslationProviderSettings {
    /// <summary>Which implementation is used. Empty or "RelatudeServices" is the hosted Relatude Translation service; anything else is taken as the full type name of a custom provider.</summary>
    public string? TypeName { get; set; }

    /// <summary>The root of the service. Empty uses the provider's own default; set it for a self-hosted or test deployment.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>The key a custom provider sends with. The Relatude service charges each call to the
    /// installation's license and uses the license's API key, falling back to this one only where the
    /// server has none - or where the provider is built from code, without a server.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Where the answers are kept on this machine, so the same call is not made, nor paid for, twice:
    /// Native (the default) in a file beside the AI embedding cache, Memory while the process runs, None never.
    /// Kept: every text translated, and every language found, text by text.</summary>
    public Relatude.DB.Common.ServiceCacheType CacheType { get; set; } = Relatude.DB.Common.ServiceCacheType.Native;
}
