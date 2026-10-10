using System.Text;
using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using Relatude.DB.FileToText;
using Relatude.DB.Imaging;
using Relatude.DB.Translation;

namespace Relatude.Providers;

/// <summary>
/// The caches in front of the Translation, FileToText and Imaging providers: a question asked before is
/// answered on this machine, without a call and without a charge; only what is not kept goes to the
/// service; fresh asks again and keeps the new answer; and what is kept is told apart by everything that
/// decides the answer.
/// </summary>
[TestClass]
public class CachingServiceProviderTests {

    /// <summary>Translates by naming the language; counts every call and every text sent.</summary>
    public sealed class FakeTranslator : ITranslationProvider {
        public readonly List<string[]> Sent = [];
        public int Detects;
        public int Left = 1000;
        public string Name => "fake translator";
        public Task<TranslationResult> TranslateAsync(IReadOnlyList<TranslationText> texts, string? to = null, string? from = null,
            TranslationFormat format = TranslationFormat.Text, bool fresh = false, CancellationToken cancellationToken = default) {
            Sent.Add([.. texts.Select(t => t.Text)]);
            Left -= 1;
            var translations = texts.Select(t => new TranslatedText($"[{t.To ?? to}] {t.Text}", t.From ?? from ?? "en", (t.To ?? to)!, t.From == null && from == null, 0.9, false)).ToArray();
            return Task.FromResult(new TranslationResult(translations, texts.Sum(t => t.Text.Length), 0, 1, Left));
        }
        public Task<LanguageDetectionResult> DetectLanguagesAsync(IReadOnlyList<string> texts, bool fresh = false, CancellationToken cancellationToken = default) {
            Detects++;
            Left -= 1;
            return Task.FromResult(new LanguageDetectionResult([.. texts.Select(t => new DetectedLanguage(t.StartsWith("Hej") ? "sv" : "en", 0.8, true, false, [new("da", 0.1)]))], 10, 0, 1, Left));
        }
        public Task<TranslationLanguages> GetLanguagesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [TestMethod]
    public async Task OnlyTheTextsNotKeptAreSentAndTheAnswerKeepsTheOrder() {
        var inner = new FakeTranslator();
        ITranslationProvider translator = new CachingTranslationProvider(inner, new MemoryServiceAnswerCache());

        var first = await translator.TranslateAsync(["Hello", "Good bye"], "nb");
        Assert.AreEqual(1, first.Credits);
        Assert.IsFalse(first.Translations.Any(t => t.Cached));

        var second = await translator.TranslateAsync(["Good bye", "Welcome", "Hello"], "nb");
        CollectionAssert.AreEqual(new[] { "Welcome" }, inner.Sent[^1], "only the text not kept is sent");
        CollectionAssert.AreEqual(new[] { "[nb] Good bye", "[nb] Welcome", "[nb] Hello" }, second.Texts);
        CollectionAssert.AreEqual(new[] { true, false, true }, second.Translations.Select(t => t.Cached).ToArray());
        Assert.AreEqual("en", second.Translations[0].From, "what was found is kept with the text");
        Assert.IsTrue(second.Translations[0].Detected);

        var third = await translator.TranslateAsync(["Hello", "Welcome"], "nb");
        Assert.AreEqual(2, inner.Sent.Count, "everything kept: no call");
        Assert.AreEqual(0, third.Credits);
        Assert.AreEqual(0, third.Characters);
        Assert.AreEqual(inner.Left, third.CreditsLeft, "what the last answer from the service said");
        Assert.AreEqual("[nb] Welcome", await translator.TranslateAsync("Welcome", "nb"), "one text goes through the cache as well");
        Assert.AreEqual(2, inner.Sent.Count);
    }

    [TestMethod]
    public async Task ATextIsKeptByItsLanguagesAndFormat() {
        var inner = new FakeTranslator();
        ITranslationProvider translator = new CachingTranslationProvider(inner, new MemoryServiceAnswerCache());
        await translator.TranslateAsync(["Hello"], "nb");

        await translator.TranslateAsync(["Hello"], "de");
        await translator.TranslateAsync(["Hello"], "nb", from: "en");
        await translator.TranslateAsync(["Hello"], "nb", format: TranslationFormat.Html);
        await translator.TranslateAsync([new TranslationText("Hello", To: "sv")], "nb");
        Assert.AreEqual(5, inner.Sent.Count, "another language to, a language from, a format, a text's own language: each is a question of its own");

        await translator.TranslateAsync([new TranslationText("Hello", To: "de")], "nb");
        Assert.AreEqual(5, inner.Sent.Count, "a text's own language is the same question as the call's");
    }

    [TestMethod]
    public async Task ATextTwiceInOneCallIsSentOnce() {
        var inner = new FakeTranslator();
        ITranslationProvider translator = new CachingTranslationProvider(inner, new MemoryServiceAnswerCache());

        var result = await translator.TranslateAsync(["Hello", "Hello", "Bye"], "nb");

        CollectionAssert.AreEqual(new[] { "Hello", "Bye" }, inner.Sent.Single());
        CollectionAssert.AreEqual(new[] { "[nb] Hello", "[nb] Hello", "[nb] Bye" }, result.Texts);
    }

    [TestMethod]
    public async Task FreshAsksForEveryTextAndKeepsTheNewAnswers() {
        var inner = new FakeTranslator();
        var cache = new MemoryServiceAnswerCache();
        ITranslationProvider translator = new CachingTranslationProvider(inner, cache);
        await translator.TranslateAsync(["Hello"], "nb");

        await translator.TranslateAsync(["Hello"], "nb", fresh: true);
        Assert.AreEqual(2, inner.Sent.Count);

        cache.Set("translate|v1|Text|found|nb|Hello", """{"text":"corrected","from":"en","to":"nb","detected":true,"score":1,"cached":false}""");
        Assert.AreEqual("corrected", await translator.TranslateAsync("Hello", "nb"));
        await translator.TranslateAsync(["Hello"], "nb", fresh: true);
        Assert.AreEqual("[nb] Hello", await translator.TranslateAsync("Hello", "nb"), "the fresh answer is kept in place of the old");
    }

    [TestMethod]
    public async Task LanguagesFoundAreKeptTextByText() {
        var inner = new FakeTranslator();
        ITranslationProvider translator = new CachingTranslationProvider(inner, new MemoryServiceAnswerCache());

        await translator.DetectLanguagesAsync(["Hej", "Hello"]);
        var again = await translator.DetectLanguagesAsync(["Hello", "Hej"]);

        Assert.AreEqual(1, inner.Detects);
        CollectionAssert.AreEqual(new[] { "en", "sv" }, again.Detections.Select(d => d.Language).ToArray());
        Assert.IsTrue(again.Detections.All(d => d.Cached));
        Assert.AreEqual("da", again.Detections[0].Alternatives.Single().Language);
        Assert.AreEqual("sv", (await translator.DetectLanguageAsync("Hej")).Language);
        Assert.AreEqual(1, inner.Detects);
    }

    /// <summary>Reads a file as its UTF-8 text; counts the calls and remembers the stream it was given.</summary>
    sealed class FakeReader : IFileToTextProvider {
        public int Calls;
        public byte[]? LastStreamBytes;
        public string Name => "fake reader";
        public Task<FileToTextResult> ExtractTextAsync(byte[] file, string? fileName = null, IReadOnlyList<string>? languages = null, bool fresh = false, CancellationToken cancellationToken = default) {
            Calls++;
            var text = Encoding.UTF8.GetString(file) + " (" + string.Join(",", languages ?? []) + ")";
            return Task.FromResult(new FileToTextResult(text, "txt", fileName, text.Length, 1, false, false, null, null, "en", 1, 500 - Calls, false) { Timed = fileName?.EndsWith(".mp3") == true, Duration = 3.5 });
        }
        public async Task<FileToTextResult> ExtractTextAsync(Stream file, string? fileName = null, IReadOnlyList<string>? languages = null, bool fresh = false, CancellationToken cancellationToken = default) {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);
            LastStreamBytes = buffer.ToArray();
            return await ExtractTextAsync(LastStreamBytes, fileName, languages, fresh, cancellationToken);
        }
        public Task<FileToTextFormats> GetFormatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>A stream that can only be read forwards, as a request body is.</summary>
    sealed class ForwardOnly(byte[] bytes) : MemoryStream(bytes) {
        public override bool CanSeek => false;
        public override long Position { get => base.Position; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task AFileIsKeptByWhatItHoldsAndTheLanguages() {
        var inner = new FakeReader();
        IFileToTextProvider reader = new CachingFileToTextProvider(inner, new MemoryServiceAnswerCache());
        var file = Encoding.UTF8.GetBytes("Contract text");

        var first = await reader.ExtractTextAsync(file, "contract.pdf", ["nb", "en"]);
        var again = await reader.ExtractTextAsync(file, @"C:\other\name.pdf", ["NB", " en "]);

        Assert.AreEqual(1, inner.Calls, "the same bytes and languages: read once, whatever the file is called");
        Assert.AreEqual(first.Text, again.Text);
        Assert.AreEqual("name.pdf", again.FileName, "the name of this call, without its folder");
        Assert.IsTrue(again.Cached);
        Assert.AreEqual(0, again.Credits);
        Assert.AreEqual(first.CreditsLeft, again.CreditsLeft);
        Assert.AreEqual(3.5, again.Duration, "everything the service said is kept");

        await reader.ExtractTextAsync(file, "contract.pdf", ["en", "nb"]);
        Assert.AreEqual(2, inner.Calls, "the languages in another order are another question: the first is the most likely");
        await reader.ExtractTextAsync(file, "contract.pdf", ["nb", "en"], fresh: true);
        Assert.AreEqual(3, inner.Calls);
    }

    [TestMethod]
    public async Task AStreamIsKeptByWhatItHoldsAndStillReachesTheProviderWhole() {
        var inner = new FakeReader();
        IFileToTextProvider reader = new CachingFileToTextProvider(inner, new MemoryServiceAnswerCache());
        var bytes = Encoding.UTF8.GetBytes(new string('x', 200_000));

        await reader.ExtractTextAsync(new ForwardOnly(bytes), "big.txt");
        CollectionAssert.AreEqual(bytes, inner.LastStreamBytes, "a stream that cannot seek is copied aside while it is hashed, and handed on whole");

        var seekable = new MemoryStream(bytes);
        await reader.ExtractTextAsync(seekable, "big.txt");
        Assert.AreEqual(1, inner.Calls, "the same bytes as a seekable stream: kept");
        await reader.ExtractTextAsync(bytes, "big.txt");
        Assert.AreEqual(1, inner.Calls, "and as bytes");

        var other = Encoding.UTF8.GetBytes("different");
        await reader.ExtractTextAsync(new MemoryStream(other), "small.txt");
        CollectionAssert.AreEqual(other, inner.LastStreamBytes, "a seekable stream is rewound after hashing");
    }

    /// <summary>Answers every imaging call; counts them.</summary>
    sealed class FakeImaging : IImagingProvider {
        public int Calls;
        public int Rotation;
        public string Name => "fake imaging";
        ImagingImage image() { Calls++; return new ImagingImage([1, 2, 3], 1, 1, "sha", 5, 90, false); }
        public Task<ImagingImage> CreateImageAsync(string description, IReadOnlyList<byte[]>? inspiration = null, int? width = null, int? height = null, bool transparent = false, bool fresh = false, CancellationToken cancellationToken = default) => Task.FromResult(image());
        public Task<ImagingImage> ManipulateImageAsync(byte[] image, string instruction, IReadOnlyList<byte[]>? references = null, byte[]? mask = null, bool fresh = false, CancellationToken cancellationToken = default) => Task.FromResult(this.image());
        public Task<ImagingImage> RemoveBackgroundAsync(byte[] image, bool fresh = false, CancellationToken cancellationToken = default) => Task.FromResult(this.image());
        public Task<ImagingImage> UpscaleAsync(byte[] image, int factor = 2, bool fresh = false, CancellationToken cancellationToken = default) => Task.FromResult(this.image());
        public Task<ImagingImage> RemoveObjectAsync(byte[] image, byte[] mask, bool fresh = false, CancellationToken cancellationToken = default) => Task.FromResult(this.image());
        public Task<ImagingImage> ExpandImageAsync(byte[] image, ImageMargins margins, string? hint = null, bool fresh = false, CancellationToken cancellationToken = default) => Task.FromResult(this.image());
        public Task<ImagingImage> ShrinkImageAsync(byte[] image, ImageMargins margins, bool fresh = false, CancellationToken cancellationToken = default) => Task.FromResult(this.image());
        public Task<ImagingRotation> RotateIfNeededAsync(byte[] image, bool fresh = false, CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(new ImagingRotation(Rotation, Rotation == 0 ? null : new ImagingImage([4], 1, 1, "turned", 5, 80, false), 5, 80, false));
        }
        public Task<ImagingMeta> ImageToMetaAsync(byte[] image, string? language = null, bool fresh = false, CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(new ImagingMeta("Title " + language, "A description", ["a", "b"], language, new ImagingFocusPoint(3, 4), [new ImagingObject("face", "a face", 0.9, 1, 2, 3, 4)], 5, 70, false));
        }
        public Task<ImagingAnswer> AskAboutImageAsync(byte[] image, string question, bool fresh = false, CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(new ImagingAnswer("Answer to " + question, 5, 60, false));
        }
        public Task<ImagingBoolAnswer> AskAboutImageBoolAsync(byte[] image, string question, bool fresh = false, CancellationToken cancellationToken = default) {
            Calls++;
            return Task.FromResult(new ImagingBoolAnswer(true, 87, 5, 50, false));
        }
        public Task<ImagingOperations> GetOperationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [TestMethod]
    public async Task TheAnswersInWordsAreKeptAndTheImagesAreNot() {
        var inner = new FakeImaging();
        IImagingProvider imaging = new CachingImagingProvider(inner, new MemoryServiceAnswerCache());
        byte[] photo = [9, 8, 7, 6];

        var meta = await imaging.ImageToMetaAsync(photo, "nb");
        var metaAgain = await imaging.ImageToMetaAsync(photo, "nb");
        Assert.AreEqual(1, inner.Calls);
        Assert.AreEqual(meta.Title, metaAgain.Title);
        Assert.AreEqual(ImageObjectType.Face, metaAgain.Objects.Single().ObjectType);
        Assert.AreEqual(3, metaAgain.Focus!.X);
        Assert.IsTrue(metaAgain.Cached);
        Assert.AreEqual(0, metaAgain.Credits);
        await imaging.ImageToMetaAsync(photo, "en");
        await imaging.ImageToMetaAsync([1, 1, 1], "nb");
        Assert.AreEqual(3, inner.Calls, "another language, another image: each a question of its own");

        await imaging.AskAboutImageAsync(photo, "What is it?");
        Assert.AreEqual("Answer to What is it?", (await imaging.AskAboutImageAsync(photo, "What is it?")).Answer);
        await imaging.AskAboutImageAsync(photo, "What colour is it?");
        await imaging.AskAboutImageBoolAsync(photo, "Is it red?");
        var yes = await imaging.AskAboutImageBoolAsync(photo, "Is it red?");
        Assert.AreEqual(6, inner.Calls);
        Assert.AreEqual(87, yes.Certainty);
        Assert.AreEqual(50, yes.CreditsLeft, "what the last answer from the service said");

        await imaging.RemoveBackgroundAsync(photo);
        await imaging.RemoveBackgroundAsync(photo);
        Assert.AreEqual(8, inner.Calls, "an image the service makes is asked for every time");
    }

    [TestMethod]
    public async Task OnlyUprightIsKeptOfTheTurn() {
        var inner = new FakeImaging();
        IImagingProvider imaging = new CachingImagingProvider(inner, new MemoryServiceAnswerCache());
        byte[] photo = [9, 8, 7, 6];

        await imaging.RotateIfNeededAsync(photo);
        var again = await imaging.RotateIfNeededAsync(photo);
        Assert.AreEqual(1, inner.Calls);
        Assert.AreEqual(0, again.Rotation);
        Assert.IsNull(again.Image);
        Assert.IsTrue(again.Cached);

        inner.Rotation = 90;
        var turned = await imaging.RotateIfNeededAsync(photo, fresh: true);
        Assert.AreEqual(90, turned.Rotation);
        await imaging.RotateIfNeededAsync(photo);
        Assert.AreEqual(3, inner.Calls, "a turn is an image, so it is not kept, and the old upright is gone");
    }
}
