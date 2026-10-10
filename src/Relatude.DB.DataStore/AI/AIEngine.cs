using Relatude.DB.Common;
using System.Diagnostics;
using System.Globalization;
namespace Relatude.DB.AI;

public class AIEngine {
    readonly IEmbeddingCache _cache;
    readonly IAIProvider _provider;
    // An embedding is the model's: the same text embedded by another model, or at another size, is another
    // vector, and one of those taken from the cache would be compared with the index's as if it were the
    // same kind. So the cache is keyed by the model as well as the text (EmbeddingModelKey). It used to be
    // keyed by the text alone, and those entries were paid for: they are taken as the model's that the
    // cache belongs to - the one it was first opened with since, recorded in an owner entry - and copied
    // to the new key as they are used. Once the model is changed they are left alone.
    readonly string _modelKey;
    readonly ulong _modelHash;
    bool _legacyIsOurs;
    static readonly ulong _ownerKey = "relatude.db/embedding-cache/owner-of-text-only-keys".XXH64Hash();
    public Action<string>? LogCallback { get; set; }
    public AIProviderSettings Settings { get; }
    public AIEngine(IAIProvider provider, AIProviderSettings settings, IEmbeddingCache? cache = null) {
        _provider = provider;
        Settings = settings;
        _cache = cache ?? new MemoryEmbeddingCache(10000);
        _modelKey = EmbeddingModelKey(settings);
        _modelHash = _modelKey.XXH64Hash();
        if (_cache.TryGet(_ownerKey, out var owner) && decodeOwner(owner) is { } ownerHash) {
            _legacyIsOurs = ownerHash == _modelHash;
        } else {
            _cache.Set(_ownerKey, encodeOwner(_modelHash));
            _legacyIsOurs = true;
        }
    }
    /// <summary>
    /// What decides an embedding beside its text: the model and the size asked for. Without a model
    /// named, the provider's default model decides, so the provider is part of it.
    /// </summary>
    public static string EmbeddingModelKey(AIProviderSettings settings) {
        var dimensions = settings.ModelDimensions?.ToString(CultureInfo.InvariantCulture) ?? "";
        var model = settings.EmbeddingModel?.Trim();
        return string.IsNullOrEmpty(model)
            ? "default:" + (settings.TypeName?.Trim().ToLowerInvariant() ?? "") + "|" + dimensions
            : "model:" + model + "|" + dimensions;
    }
    // the owner is a 64-bit hash kept as an embedding: four 16-bit parts, each exact in a float
    static float[] encodeOwner(ulong hash) => [hash & 0xFFFF, (hash >> 16) & 0xFFFF, (hash >> 32) & 0xFFFF, (hash >> 48) & 0xFFFF];
    static ulong? decodeOwner(float[] parts) {
        if (parts.Length != 4) return null;
        ulong hash = 0;
        for (var i = 0; i < 4; i++) {
            if (parts[i] < 0 || parts[i] > 0xFFFF || parts[i] != MathF.Floor(parts[i])) return null;
            hash |= (ulong)parts[i] << (16 * i);
        }
        return hash;
    }
    public Task<string> GetCompletionAsync(string prompt, string? modelKey = null) => _provider.GetCompletionAsync(prompt, modelKey);
    class resultSet(string text, string modelKey) {
        public readonly ulong Hash = (modelKey + (char)0 + text).XXH64Hash(); // the model and the text
        public readonly ulong TextOnlyHash = text.XXH64Hash(); // the key before the model was part of it
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

        var valueSet = paragraphs.Select(p => new resultSet(p, _modelKey)).ToArray(); // all values to process
        List<resultSet> missing = []; // values not in cache
        List<resultSet> adopted = []; // found under the text-only key, to be kept under the new one

        // check cache for existing embeddings and collect missing:
        foreach (var v in valueSet) {
            if (_cache.TryGet(v.Hash, out v.Embedding)) continue;
            if (_legacyIsOurs && _cache.TryGet(v.TextOnlyHash, out v.Embedding) && v.Embedding.Length > 0) {
                adopted.Add(v);
                continue;
            }
            v.Embedding = null;
            if (string.IsNullOrWhiteSpace(v.Text)) {
                v.Embedding = [];
                continue;
            }
            missing.Add(v);
        }

        totalCached += valueSet.Length - missing.Count;
        if (adopted.Count > 0) _cache.SetMany(adopted.Select(a => new Tuple<ulong, float[]>(a.Hash, a.Embedding!)));

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

        if (missing.Count > 0) {
            totalTimer.Stop();
            var cached = valueSet.Length - missing.Count;
            var ms = totalTimer.Elapsed.TotalMilliseconds.To1000C00N();
            LogCallback?.Invoke($"Embeddings: {missing.Count}({totalRequested}) requested, {cached}({totalCached}) cached, {ms}ms");
        }

        return result;
    }
    public void ClearCache() {
        _cache.ClearAll();
        // nothing is left under the text-only keys; the owner is written again for the next open
        _cache.Set(_ownerKey, encodeOwner(_modelHash));
        _legacyIsOurs = true;
    }
    public void Dispose() {
        _cache.Dispose();
        _provider.Dispose();
    }
    public static AIEngine CreateDummy() {
        var settings = new AIProviderSettings() {
            TypeName = nameof(DummyAIProvider),
        };
        var provider = new DummyAIProvider();
        return new AIEngine(provider, settings, new MemoryEmbeddingCache(1000));
    }
}