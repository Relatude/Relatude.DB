using Relatude.DB.Common;
using System.Diagnostics;

namespace Relatude.DB.AI;
public class DummyAIProvider : IAIProvider {
    public Task<string> GetCompletionAsync(string prompt, string? modelKey = null) {
        return Task.FromResult($"Dummy completion for prompt: {prompt}");
    }
    public async Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) {
        var rnd = new Random();
        var result = new float[paragraphs.Length][];
        for (int i = 0; i < paragraphs.Length; i++) {
            result[i] = createRandomVector(paragraphs[i]);
            await Task.Delay(rnd.Next(2)); // simulate some async delay
        }
        return result;
    }
    float[] createRandomVector(string v) {
        var hash = v.XXH64Hash();
        var rnd = new Random((int)(hash % int.MaxValue)); // seed with hash to always get the same result for the same input
        var createDummyVector = new float[1536];
        for (int i = 0; i < createDummyVector.Length; i++) {
            createDummyVector[i] = (float)(rnd.NextDouble() * 2 - 1);
        }
        // normalize:
        var len = Math.Sqrt(createDummyVector.Select(x => x * x).Sum());
        if (len > 0) for (int j = 0; j < createDummyVector.Length; j++) createDummyVector[j] /= (float)len;
        return createDummyVector;
    }
    public void Dispose() {
    }

}
