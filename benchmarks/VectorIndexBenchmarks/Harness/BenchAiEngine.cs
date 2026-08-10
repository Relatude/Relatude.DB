using Relatude.DB.AI;

namespace VectorIndexBenchmarks.Harness;

/// <summary>
/// The AI engine both indexes embed their search text with. A semantic index takes a query as
/// text and embeds it itself (<c>ISemanticIndex.SearchForHitData</c>), which is the only search
/// surface the in-memory implementation has — so to compare the two on generated vectors the
/// benchmark supplies an engine that maps each query text straight back to its generated vector
/// instead of calling a model.
///
/// <para>Every query is embedded once and then served from the engine's own cache, so what the
/// search phases measure is the index and not an embedding call; both engines pay the identical
/// cache lookup.</para>
/// </summary>
public static class BenchAiEngine {
    public static AIEngine Create(Corpus corpus) {
        var settings = new AIProviderSettings {
            Id = Guid.Empty,
            TypeName = nameof(BenchAiProvider),
            ModelDimensions = corpus.Dimensions,
        };
        var provider = new BenchAiProvider(corpus);
        return new AIEngine(provider, settings, new MemoryEmbeddingCache(Corpus.QueryCount * 2));
    }

    sealed class BenchAiProvider(Corpus corpus) : IAIProvider {
        readonly Dictionary<string, float[]> _vectorByText =
            corpus.QueryTexts.Zip(corpus.QueryVectors).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);

        public Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) {
            var result = new float[paragraphs.Length][];
            for (var i = 0; i < paragraphs.Length; i++) {
                if (!_vectorByText.TryGetValue(paragraphs[i], out var vector))
                    throw new ArgumentException($"The benchmark AI engine only embeds its own generated queries, got '{paragraphs[i]}'. ");
                result[i] = vector;
            }
            return Task.FromResult(result);
        }
        public Task<string> GetCompletionAsync(string prompt, string? modelKey = null)
            => throw new NotSupportedException("The benchmark never asks for a completion. ");
        public void Dispose() { }
    }
}
