using Relatude.DB.DataStores;
using Relatude.DB.FileToText;
using Relatude.DB.Imaging;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Translation;
using Relatude.Utils;

namespace Relatude.Store;

/// <summary>
/// NodeStore.Services: the AI, image AI, file-to-text, translation and SMS providers in one place, each
/// throwing when the database has none, owned by the store, and shared by a store made from Context.
/// </summary>
[TestClass]
public class NodeStoreServicesTests {

    sealed class FakeTranslator : ITranslationProvider {
        public bool Disposed;
        public string Name => "fake";
        public Task<TranslationResult> TranslateAsync(IReadOnlyList<TranslationText> texts, string? to = null, string? from = null,
            TranslationFormat format = TranslationFormat.Text, bool fresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranslationResult([.. texts.Select(t => new TranslatedText("[" + (t.To ?? to) + "] " + t.Text, from, (t.To ?? to)!, false, null, false))], 0, 0, 0, 0));
        public Task<LanguageDetectionResult> DetectLanguagesAsync(IReadOnlyList<string> texts, bool fresh = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<TranslationLanguages> GetLanguagesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    static IDataStore openData() => DataStoreLocal.Open(Helper.GetDatamodel(), null, new IOProviderMemory());

    [TestMethod]
    public void AServiceTheDatabaseDoesNotHaveThrowsAndSaysSo() {
        using var store = new NodeStore(openData());

        Assert.IsFalse(store.Services.HasAI);
        Assert.IsFalse(store.Services.HasSMS);
        Assert.IsFalse(store.Services.HasImaging);
        Assert.IsFalse(store.Services.HasFileToText);
        Assert.IsFalse(store.Services.HasTranslation);
        Assert.Throws<Exception>(() => store.Services.AI);
        StringAssert.Contains(Assert.Throws<Exception>(() => store.Services.SMS).Message, "SMS");
        StringAssert.Contains(Assert.Throws<Exception>(() => store.Services.Imaging).Message, "imaging");
        StringAssert.Contains(Assert.Throws<Exception>(() => store.Services.FileToText).Message, "file-to-text");
        StringAssert.Contains(Assert.Throws<Exception>(() => store.Services.Translation).Message, "translation");
    }

    [TestMethod]
    public async Task TheProvidersAreReachedThroughServiceAndSharedByAContextStore() {
        var translator = new FakeTranslator();
        var imaging = new RelatudeServicesImagingProvider(new ImagingProviderSettings());
        var fileToText = new RelatudeServicesFileToTextProvider(new FileToTextProviderSettings());
        var store = new NodeStore(openData(), imaging: imaging, fileToText: fileToText, translation: translator);
        try {
            Assert.IsTrue(store.Services.HasTranslation);
            Assert.AreSame(translator, store.Services.Translation);
            Assert.AreSame(imaging, store.Services.Imaging);
            Assert.AreSame(fileToText, store.Services.FileToText);
            Assert.AreEqual("[nb] Good morning", await store.Services.Translation.TranslateAsync("Good morning", "nb"));

            var norwegian = store.Context.Culture("nb-NO").Create();
            Assert.AreNotSame(store, norwegian);
            Assert.AreSame(store.Services, norwegian.Services, "a reading context is all that differs: the services are the database's");
        } finally {
            store.Dispose();
        }
        Assert.IsTrue(translator.Disposed, "the store owns the providers it was given, and disposes them with itself");
    }
}
