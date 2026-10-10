using Relatude.DB.AI;
using Relatude.DB.Common;

namespace Relatude.AI;

/// <summary>
/// The embedding cache is keyed by the model as well as the text: a vector from another model, or of
/// another size, is never handed out for this one. The entries kept before that, keyed by the text alone,
/// were paid for, so they are taken as the model's the cache is first opened with - and only that model's.
/// </summary>
[TestClass]
public class EmbeddingCacheKeyTests {

    sealed class CountingProvider : IAIProvider {
        public int Embedded;
        public Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) {
            Embedded += paragraphs.Length;
            return Task.FromResult(paragraphs.Select(p => new float[] { p.Length, 1, 2 }).ToArray());
        }
        public Task<string> GetCompletionAsync(string prompt, string? modelKey = null) => Task.FromResult("");
        public void Dispose() { }
    }

    static AIEngine engine(IEmbeddingCache cache, CountingProvider provider, string? model, int? dimensions = null)
        => new(provider, new AIProviderSettings { TypeName = "OpenAI", EmbeddingModel = model, ModelDimensions = dimensions }, cache);

    [TestMethod]
    public async Task AnotherModelIsAnotherKey() {
        var cache = new MemoryEmbeddingCache(1000);
        var provider = new CountingProvider();
        await engine(cache, provider, "small").GetEmbeddingsAsync(["Hello"]);
        Assert.AreEqual(1, provider.Embedded);

        await engine(cache, provider, "small").GetEmbeddingsAsync(["Hello"]);
        Assert.AreEqual(1, provider.Embedded, "the same model: kept");
        await engine(cache, provider, "large").GetEmbeddingsAsync(["Hello"]);
        Assert.AreEqual(2, provider.Embedded, "another model: embedded again");
        await engine(cache, provider, "small", dimensions: 256).GetEmbeddingsAsync(["Hello"]);
        Assert.AreEqual(3, provider.Embedded, "another size: embedded again");
        await engine(cache, provider, "small").GetEmbeddingsAsync(["Hello"]);
        Assert.AreEqual(3, provider.Embedded, "back to the first: still kept");
    }

    [TestMethod]
    public async Task EntriesKeyedByTheTextAloneAreTheFirstModelsOnly() {
        var cache = new MemoryEmbeddingCache(1000);
        float[] paidFor = [7, 7, 7];
        cache.Set("Hello".XXH64Hash(), paidFor); // as the cache was written before the model was part of the key
        var provider = new CountingProvider();

        var first = await engine(cache, provider, "small").GetEmbeddingsAsync(["Hello"]);
        Assert.AreEqual(0, provider.Embedded, "taken as the model's the cache is first opened with");
        CollectionAssert.AreEqual(paidFor, first[0]);

        await engine(cache, provider, "large").GetEmbeddingsAsync(["Hello"]);
        Assert.AreEqual(1, provider.Embedded, "not another model's");

        cache.Set("Bye".XXH64Hash(), paidFor);
        await engine(cache, provider, "small").GetEmbeddingsAsync(["Bye"]);
        Assert.AreEqual(1, provider.Embedded, "the first model keeps them after another has been used");
    }

    [TestMethod]
    public async Task ClearingTheCacheKeepsItWorking() {
        var cache = new MemoryEmbeddingCache(1000);
        var provider = new CountingProvider();
        var ai = engine(cache, provider, "small");
        await ai.GetEmbeddingsAsync(["Hello"]);
        ai.ClearCache();
        await ai.GetEmbeddingsAsync(["Hello"]);
        await ai.GetEmbeddingsAsync(["Hello"]);
        Assert.AreEqual(2, provider.Embedded);
    }
}
