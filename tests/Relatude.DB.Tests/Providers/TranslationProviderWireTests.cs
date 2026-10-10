using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.Translation;

namespace Relatude.Providers;

/// <summary>
/// The Translation provider against the requests it puts on the wire. It talks to the hosted Relatude
/// Translation service, whose contract is its own (TranslationContracts.cs in Relatude.DB.Services): a
/// JSON call with the texts as strings, or as objects where a text has languages of its own, the license
/// API key as the bearer token, unknown fields refused - so nothing may be sent that the call did not ask
/// for - and the answer as JSON with what it cost. No files are sent.
/// </summary>
[TestClass]
public class TranslationProviderWireTests {
    const string _key = "cd4e092c-ac57-4661-86c8-9b3f4acf6438";

    // through the interface, as NodeStore.Services.Translation hands it out: the overloads for one text and
    // for plain strings are the interface's own
    static ITranslationProvider provider(RelatudeServiceStub stub, string? key = _key) =>
        new RelatudeServicesTranslationProvider(new TranslationProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = key });

    static string translated(params (string Text, string? From, string To, bool Detected, bool Cached)[] texts) => JsonSerializer.Serialize(new {
        translations = texts.Select(t => new { text = t.Text, from = t.From, to = t.To, detected = t.Detected, score = t.Detected ? 1.0 : (double?)null, cached = t.Cached }),
        characters = 29, cachedCharacters = 13, credits = 1, creditsLeft = 4211,
    });

    [TestMethod]
    public async Task TheTextsGoAsTheServiceDocumentsThemAndTheAnswerComesBackInOrder() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub);
        stub.EnqueueJson(200, translated(("God morgen", "en", "nb", true, false), ("Hei og velkommen", "sv", "nb", false, false), ("Kostenlose Lieferung", "en", "de", true, true)),
            ("X-Cache", "partial"));

        var answer = await translator.TranslateAsync(["Good morning", new TranslationText("Hej och välkommen", From: "sv"), new TranslationText("Free delivery", To: "de")], to: "nb");

        CollectionAssert.AreEqual(new[] { "God morgen", "Hei og velkommen", "Kostenlose Lieferung" }, answer.Texts);
        Assert.AreEqual("en", answer.Translations[0].From);
        Assert.IsTrue(answer.Translations[0].Detected);
        Assert.AreEqual(1.0, answer.Translations[0].Score);
        Assert.IsNull(answer.Translations[1].Score);
        Assert.AreEqual("de", answer.Translations[2].To);
        Assert.IsTrue(answer.Translations[2].Cached);
        Assert.AreEqual(29, answer.Characters);
        Assert.AreEqual(13, answer.CachedCharacters);
        Assert.AreEqual(1, answer.Credits);
        Assert.AreEqual(4211, answer.CreditsLeft);

        var call = stub.Single();
        Assert.AreEqual("POST", call.Method);
        Assert.AreEqual("/api/translation/translate", call.Path);
        Assert.AreEqual("Bearer " + _key, call.Header("Authorization"));
        Assert.IsNull(call.Header("Cache-Control"), "what was translated before is good enough unless asked otherwise");
        var json = call.Json;
        Assert.AreEqual("nb", json.GetProperty("to").GetString());
        // the service refuses fields it does not know, and an empty one would read as a language
        CollectionAssert.AreEquivalent(new[] { "to", "texts" }, json.EnumerateObject().Select(p => p.Name).ToArray(), "nothing is sent the call did not ask for");
        var texts = json.GetProperty("texts");
        Assert.AreEqual(JsonValueKind.String, texts[0].ValueKind, "a text without languages of its own goes as a plain string");
        Assert.AreEqual("Good morning", texts[0].GetString());
        Assert.AreEqual("Hej och välkommen", texts[1].GetProperty("text").GetString());
        Assert.AreEqual("sv", texts[1].GetProperty("from").GetString());
        Assert.IsFalse(texts[1].TryGetProperty("to", out _));
        Assert.AreEqual("de", texts[2].GetProperty("to").GetString());
        Assert.IsFalse(texts[2].TryGetProperty("from", out _));
    }

    [TestMethod]
    public async Task OneTextAndAListOfStringsTakeTheCallsLanguages() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub);

        stub.EnqueueJson(200, translated(("God morgen", "en", "nb", false, false)));
        Assert.AreEqual("God morgen", await translator.TranslateAsync("Good morning", "nb", from: "en"));
        var one = stub.Operations()[^1].Json;
        Assert.AreEqual("en", one.GetProperty("from").GetString());
        Assert.AreEqual("Good morning", one.GetProperty("texts")[0].GetString());

        stub.EnqueueJson(200, translated(("Hallo", "en", "de", true, false), ("Tschüss", "en", "de", true, false)));
        var many = await translator.TranslateAsync(["Hello", "Good bye"], "de");
        CollectionAssert.AreEqual(new[] { "Hallo", "Tschüss" }, many.Texts);
        Assert.AreEqual(2, stub.Operations()[^1].Json.GetProperty("texts").GetArrayLength());

        stub.EnqueueJson(200, translated(("Hallo", "en", "de", true, false)));
        await translator.TranslateAsync(new List<string> { "Hello" }, "de");
        Assert.AreEqual(3, stub.Operations().Length);
    }

    [TestMethod]
    public async Task HtmlAndFreshAreSaidOnlyWhenAskedFor() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub);
        stub.EnqueueJson(200, translated(("<p>Hei</p>", "en", "nb", true, false)));

        await translator.TranslateAsync("<p>Hi</p>", "nb", format: TranslationFormat.Html, fresh: true);

        var call = stub.Single();
        Assert.AreEqual("html", call.Json.GetProperty("format").GetString());
        Assert.AreEqual("no-cache", call.Header("Cache-Control"), "a fresh call has every text translated anew");
    }

    [TestMethod]
    public async Task TheLanguagesOfTextsAreFound() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub);
        stub.EnqueueJson(200, """
            {"detections":[
                {"language":"nb","score":0.92,"translatable":true,"cached":false,"alternatives":[{"language":"nn","score":0.31}]},
                {"language":null,"score":0,"translatable":false,"cached":false}],
             "characters":11,"cachedCharacters":0,"credits":1,"creditsLeft":50}
            """);

        var found = await translator.DetectLanguagesAsync(["Hei på deg", "1234"]);

        Assert.AreEqual("nb", found.Detections[0].Language);
        Assert.AreEqual(0.92, found.Detections[0].Score);
        Assert.AreEqual("nn", found.Detections[0].Alternatives.Single().Language);
        Assert.IsNull(found.Detections[1].Language, "a text with no letters to place");
        Assert.AreEqual(0, found.Detections[1].Alternatives.Length, "alternatives left out read as none");
        Assert.AreEqual(50, found.CreditsLeft);
        var call = stub.Single();
        Assert.AreEqual("/api/translation/detect", call.Path);
        CollectionAssert.AreEqual(new[] { "Hei på deg", "1234" }, call.Json.GetProperty("texts").EnumerateArray().Select(t => t.GetString()).ToArray());

        stub.EnqueueJson(200, """{"detections":[{"language":"en","score":1,"translatable":true,"cached":true,"alternatives":[]}],"characters":0,"cachedCharacters":5,"credits":1,"creditsLeft":49}""");
        var one = await translator.DetectLanguageAsync("Hello");
        Assert.AreEqual("en", one.Language);
        Assert.IsTrue(one.Cached);
    }

    [TestMethod]
    public async Task TheLanguagesAreReadWithoutALicense() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub, key: null);
        stub.EnqueueJson(200, """
            {"languages":[{"code":"nb","name":"Norwegian","nativeName":"Norsk Bokmål","direction":"ltr"},
                          {"code":"ar","name":"Arabic","nativeName":"العربية","direction":"rtl"}],
             "aliases":{"no":"nb"},"charsPerCredit":1000,"cachedCharsPerCredit":0,"maxTexts":1000,"maxTextChars":50000,"maxTotalChars":500000}
            """);

        var languages = await translator.GetLanguagesAsync();

        Assert.AreEqual(2, languages.Languages.Length);
        Assert.AreEqual("rtl", languages.Languages[1].Direction);
        Assert.AreEqual("nb", languages.Aliases["no"]);
        Assert.AreEqual(1000, languages.CharsPerCredit);
        Assert.AreEqual(0, languages.CachedCharsPerCredit);
        Assert.AreEqual(500000, languages.MaxTotalChars);
        var request = stub.Single();
        Assert.AreEqual("GET", request.Method);
        Assert.AreEqual("/api/translation/languages", request.Path);
        Assert.IsNull(request.Header("Authorization"));
    }

    /// <summary>
    /// A call is paid for before the service hands it to a provider, and the credits are never given
    /// back. So it is repeated only on 429 and 503, the two answers the service gives before any money
    /// moves; a 424 - the charge not confirmed - and a 502 are thrown at once, saying they may have been charged.
    /// </summary>
    [TestMethod]
    public async Task ACallIsRepeatedOnlyOnTheAnswersGivenBeforeAnythingIsCharged() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub);

        stub.EnqueueJson(503, """{"error":"The license server cannot be reached."}""", ("Retry-After", "0"));
        stub.EnqueueJson(429, """{"error":"Too fast."}""", ("Retry-After", "0"));
        stub.EnqueueJson(200, translated(("Hei", "en", "nb", true, false)));
        Assert.AreEqual("Hei", await translator.TranslateAsync("Hi", "nb"));
        Assert.AreEqual(3, stub.Operations().Length, "503 and 429 are given before the charge, so the call is made again");

        foreach (var status in new[] { 424, 500, 502, 504 }) {
            stub.Requests.Clear();
            stub.EnqueueJson(status, """{"error":"Not this time."}""");
            var error = await Assert.ThrowsExactlyAsync<RelatudeServiceException>(() => translator.TranslateAsync("Hi", "nb"));
            Assert.AreEqual(status, error.StatusCode);
            Assert.IsTrue(error.MayHaveBeenCharged, status + " can come after the charge");
            Assert.AreEqual(1, stub.Operations().Length, status + " must not be repeated: it may already have been paid for");
        }
    }

    [TestMethod]
    public async Task ARefusalRepeatsTheServicesReason() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub);
        stub.EnqueueJson(400, """{"error":"'texts[0].to' is xx, a language this service does not translate."}""");

        var error = await Assert.ThrowsExactlyAsync<RelatudeServiceException>(() => translator.TranslateAsync("Hi", "xx"));

        Assert.AreEqual(400, error.StatusCode);
        Assert.AreEqual("'texts[0].to' is xx, a language this service does not translate.", error.Reason);
        StringAssert.Contains(error.Message, "Relatude Translation service");
        Assert.IsFalse(error.MayHaveBeenCharged, "a 400 is given before anything is charged");
    }

    [TestMethod]
    public async Task ACallTheServiceWouldRefuseIsNotMade() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => translator.TranslateAsync(new TranslationText[] { "Hello", new("Hallo", To: "nb") }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => translator.TranslateAsync(Array.Empty<TranslationText>(), "nb"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => translator.DetectLanguagesAsync([]));
        Assert.AreEqual(0, stub.Requests.Count, "a call without a language to go to, or without texts, is the caller's mistake and costs nothing");

        // every text naming its own language is enough without the call's
        stub.EnqueueJson(200, translated(("Hallo", "en", "de", true, false)));
        await translator.TranslateAsync([new TranslationText("Hello", To: "de")]);
        Assert.IsFalse(stub.Single().Json.TryGetProperty("to", out _));
    }

    [TestMethod]
    public async Task AnAnswerWithTheWrongNumberOfTranslationsIsAFailure() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub);
        stub.EnqueueJson(200, translated(("Hei", "en", "nb", true, false)));

        var error = await Assert.ThrowsExactlyAsync<RelatudeServiceException>(() => translator.TranslateAsync(["Hi", "Bye"], "nb"));
        StringAssert.Contains(error.Message, "2 texts with 1 translations");
    }

    [TestMethod]
    public async Task WithoutAKeyTheCallSaysWhereToSetOne() {
        await using var stub = await RelatudeServiceStub.StartAsync("translation");
        using var translator = provider(stub, key: null);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => translator.TranslateAsync("Hi", "nb"));
        StringAssert.Contains(error.Message, "Services");
        Assert.AreEqual(0, stub.Requests.Count);
    }

    [TestMethod]
    public void TheHostedServiceIsTheDefault() {
        using var hosted = new RelatudeServicesTranslationProvider(new TranslationProviderSettings());
        Assert.AreEqual("https://translation.services.relatude.com", hosted.ServiceUrl);
        Assert.IsTrue(RelatudeServicesTranslationProvider.IsProviderName("RelatudeServices"));
        Assert.IsTrue(RelatudeServicesTranslationProvider.IsProviderName(nameof(RelatudeServicesTranslationProvider)));
        Assert.IsFalse(RelatudeServicesTranslationProvider.IsProviderName("My.Own.Translator"));
    }
}
