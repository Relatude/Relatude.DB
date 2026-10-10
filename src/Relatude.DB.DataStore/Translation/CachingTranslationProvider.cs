using Relatude.DB.Common;

namespace Relatude.DB.Translation;

/// <summary>
/// A translation provider with the answers kept on this machine, text by text, the way the AI engine
/// keeps embeddings: each text of a call is looked up first and only the ones not kept are sent, in one
/// call, so a label or a product name translated once is never paid for again here. Finding languages
/// is kept the same way.
/// <para>A text is kept by what decides its translation: the text itself, the language it is said to
/// be in - or "found" when the service is to find it - the language it goes to, and the format. A text
/// whose languages were found is kept with what was found. <c>fresh</c> asks the service for every text
/// and keeps the new answers in place of the old.</para>
/// <para>An answer from here says <see cref="TranslatedText.Cached"/>, costs nothing and counts in
/// neither <see cref="TranslationResult.Characters"/> nor <see cref="TranslationResult.CachedCharacters"/>,
/// which are what the service was asked for. <see cref="TranslationResult.CreditsLeft"/> is what the last
/// answer from the service said, -1 before there was one.</para>
/// </summary>
public sealed class CachingTranslationProvider(ITranslationProvider inner, IServiceAnswerCache cache) : ITranslationProvider, ICachingServiceProvider {
    readonly ServiceAnswerCacheDefaults.CreditsSeen _credits = new();

    /// <summary>The provider that is asked when the cache has no answer.</summary>
    public ITranslationProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));
    readonly IServiceAnswerCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public string Name => Inner.Name;

    public async Task<TranslationResult> TranslateAsync(IReadOnlyList<TranslationText> texts, string? to = null, string? from = null,
        TranslationFormat format = TranslationFormat.Text, bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(texts);
        var keys = new string?[texts.Count];
        var answers = new TranslatedText?[texts.Count];
        for (var i = 0; i < texts.Count; i++) {
            var text = texts[i];
            var textTo = code(text?.To) ?? code(to);
            // a text the provider would refuse is not looked up: it goes, so the provider says why
            if (text?.Text == null || textTo == null) continue;
            keys[i] = "translate|v1|" + format + "|" + (code(text.From) ?? code(from) ?? "found") + "|" + textTo + "|" + text.Text;
            if (!fresh && ServiceAnswerCacheDefaults.Read<TranslatedText>(_cache, keys[i]!) is { } kept) answers[i] = kept with { Cached = true };
        }
        // every text not kept, a text asked for twice in the call only once
        var asked = new List<int>();
        var firstOfKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var sameAs = new int[texts.Count];
        for (var i = 0; i < texts.Count; i++) {
            if (answers[i] != null) continue;
            if (keys[i] is { } key && firstOfKey.TryGetValue(key, out var first)) {
                sameAs[i] = first;
                continue;
            }
            if (keys[i] is { } newKey) firstOfKey[newKey] = asked.Count;
            sameAs[i] = asked.Count;
            asked.Add(i);
        }
        if (asked.Count == 0) return new TranslationResult(Array.ConvertAll(answers, a => a!), 0, 0, 0, _credits.Left);

        var result = await Inner.TranslateAsync(asked.Select(i => texts[i]).ToArray(), to, from, format, fresh, cancellationToken);
        if (result.Translations.Length != asked.Count) throw new RelatudeServiceException(0, $"The {Name} answered {asked.Count} texts with {result.Translations.Length} translations. ");
        _credits.Saw(result.CreditsLeft);
        var keep = new List<KeyValuePair<string, string>>();
        for (var n = 0; n < asked.Count; n++) {
            if (keys[asked[n]] is { } key) keep.Add(new(key, ServiceAnswerCacheDefaults.Serialize(result.Translations[n] with { Cached = false })));
        }
        if (keep.Count > 0) _cache.SetMany(keep);
        for (var i = 0; i < texts.Count; i++) answers[i] ??= result.Translations[sameAs[i]];
        return new TranslationResult(Array.ConvertAll(answers, a => a!), result.Characters, result.CachedCharacters, result.Credits, result.CreditsLeft);
    }

    public async Task<LanguageDetectionResult> DetectLanguagesAsync(IReadOnlyList<string> texts, bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(texts);
        var keys = new string?[texts.Count];
        var answers = new DetectedLanguage?[texts.Count];
        for (var i = 0; i < texts.Count; i++) {
            if (texts[i] == null) continue;
            keys[i] = "detect|v1|" + texts[i];
            if (!fresh && ServiceAnswerCacheDefaults.Read<DetectedLanguage>(_cache, keys[i]!) is { } kept) answers[i] = kept with { Cached = true, Alternatives = kept.Alternatives ?? [] };
        }
        var asked = new List<int>();
        var firstOfKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var sameAs = new int[texts.Count];
        for (var i = 0; i < texts.Count; i++) {
            if (answers[i] != null) continue;
            if (keys[i] is { } key && firstOfKey.TryGetValue(key, out var first)) {
                sameAs[i] = first;
                continue;
            }
            if (keys[i] is { } newKey) firstOfKey[newKey] = asked.Count;
            sameAs[i] = asked.Count;
            asked.Add(i);
        }
        if (asked.Count == 0) return new LanguageDetectionResult(Array.ConvertAll(answers, a => a!), 0, 0, 0, _credits.Left);

        var result = await Inner.DetectLanguagesAsync(asked.Select(i => texts[i]).ToArray(), fresh, cancellationToken);
        if (result.Detections.Length != asked.Count) throw new RelatudeServiceException(0, $"The {Name} answered {asked.Count} texts with {result.Detections.Length} languages. ");
        _credits.Saw(result.CreditsLeft);
        var keep = new List<KeyValuePair<string, string>>();
        for (var n = 0; n < asked.Count; n++) {
            if (keys[asked[n]] is { } key) keep.Add(new(key, ServiceAnswerCacheDefaults.Serialize(result.Detections[n] with { Cached = false })));
        }
        if (keep.Count > 0) _cache.SetMany(keep);
        for (var i = 0; i < texts.Count; i++) answers[i] ??= result.Detections[sameAs[i]];
        return new LanguageDetectionResult(Array.ConvertAll(answers, a => a!), result.Characters, result.CachedCharacters, result.Credits, result.CreditsLeft);
    }

    /// <summary>Costs nothing, so it is not kept: the list is the service's as it is now.</summary>
    public Task<TranslationLanguages> GetLanguagesAsync(CancellationToken cancellationToken = default) => Inner.GetLanguagesAsync(cancellationToken);

    public void ClearCache() => _cache.ClearAll();

    public void Dispose() {
        Inner.Dispose();
        _cache.Dispose();
    }

    static string? code(string? language) => string.IsNullOrWhiteSpace(language) ? null : language.Trim();
}
