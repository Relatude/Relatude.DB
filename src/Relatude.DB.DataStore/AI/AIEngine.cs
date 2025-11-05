using Relatude.DB.Common;
using System.Diagnostics;
using System.Globalization;
namespace Relatude.DB.AI;
public class AIEngine {
    readonly IEmbeddingCache _cache;
    readonly IAIProvider _provider;
    public Action<string>? LogCallback { get; set; }
    public AIProviderSettings Settings { get; }
    public AIEngine(IAIProvider provider, AIProviderSettings settings, IEmbeddingCache? cache = null) {
        _provider = provider;
        Settings = settings;
        _cache = cache ?? new MemoryEmbeddingCache(10000);
    }
    public Task<string> GetCompletionAsync(string prompt, string? modelKey = null) => _provider.GetCompletionAsync(prompt, modelKey);
    class resultSet(string text) {
        public readonly ulong Hash = text.XXH64Hash(); // hash of the text
        public readonly string Text = text;
        public float[]? Embedding; // null if not in cache
    }
    string ensureMaxLength(string value) => value.Length > Settings.GetMaxCharsOfEach() ? value[..Settings.GetMaxCharsOfEach()] : value;
    long totalCached = 0;
    long totalRequested = 0;
    public async Task<List<float[]>> GetEmbeddingsAsync(IEnumerable<string> paragraphs) {

        var totalTimer = Stopwatch.StartNew();
        var generatorTimer = new Stopwatch();

        paragraphs = paragraphs.Select(ensureMaxLength); // ensure max length of each

        var valueSet = paragraphs.Select(p => new resultSet(p)).ToArray(); // all values to process
        List<resultSet> missing = []; // values not in cache

        // check cache for existing embeddings and collect missing:
        foreach (var v in valueSet) {
            if (_cache.TryGet(v.Hash, out v.Embedding)) continue;
            if(string.IsNullOrWhiteSpace(v.Text)) {
                v.Embedding = [];
                continue;
            }
            missing.Add(v);
        }

        totalCached += valueSet.Length - missing.Count;

        if (missing.Count > 0) {

            // call external service to get missing embeddings:
            generatorTimer.Start();
            var embeddings = await _provider.GetEmbeddingsAsync([.. missing.Select(m => m.Text)]);
            generatorTimer.Stop();
            totalRequested += missing.Count;
            //LogCallback?.Invoke($"Embedding http request for {missing.Count} items. {generatorTimer.ElapsedMilliseconds.To1000N()}ms. ");

            // populate results back to missing list: ( this will also set the Embeddings in valueSet since they point to the same object )
            if (embeddings.Length != missing.Count) throw new Exception("Embedding count mismatch");
            for (var pos = 0; pos < embeddings.Length; pos++) missing[pos].Embedding = embeddings[pos];

            // validate that all embeddings are present and of correct length:
            foreach (var m in missing) {
                if (m.Embedding == null) throw new Exception("Embedding not found after generation");
                if (m.Embedding.Length != embeddings[0].Length) throw new Exception("Embedding length mismatch");
            }

            // store new embeddings in cache:
            _cache.SetMany(missing.Select(m => new Tuple<ulong, float[]>(m.Hash, m.Embedding!)));

        }
        var result = valueSet.Select(v => {
            if (v.Embedding == null) throw new Exception("Embedding not found");
            return v.Embedding;
        }).ToList();

        totalTimer.Stop();
        var cached = valueSet.Length - missing.Count;
        var ms = totalTimer.Elapsed.TotalMilliseconds.To1000C00N();
        LogCallback?.Invoke($"Embeddings: {missing.Count}({totalRequested}) requested, {cached}({totalCached}) cached, {ms}ms");

        return result;
    }
    public void ClearCache() {
        _cache.ClearAll();
    }
    public void Dispose() {
        _cache.Dispose();
        _provider.Dispose();
    }
    public static AIEngine CreateDummy() {
        var settings = new AIProviderSettings() {
            Id = Guid.NewGuid(),
            TypeName = nameof(DummyAIProvider),
        };
        var provider = new DummyAIProvider();
        return new AIEngine(provider, settings, new MemoryEmbeddingCache(1000));
    }
}