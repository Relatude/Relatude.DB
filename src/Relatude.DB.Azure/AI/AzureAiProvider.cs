using Azure.AI.OpenAI;
using System.ClientModel;
using OpenAI.Chat;
using OpenAI.Embeddings;
using System.Diagnostics;
using Relatude.DB.Common;
namespace Relatude.DB.AI;
public class AzureAIProvider : IAIProvider {
    readonly ChatClient _chatClient;
    readonly EmbeddingClient _embeddingClient;
    public AzureAIProvider(AIProviderSettings settings) {
        if (string.IsNullOrEmpty(settings.ServiceUrl)) throw new ArgumentException("ServiceUrl is required in AIProviderSettings");
        if (string.IsNullOrEmpty(settings.ApiKey)) throw new ArgumentException("ApiKey is required in AIProviderSettings");
        var client = new AzureOpenAIClient(new Uri(settings.ServiceUrl), new ApiKeyCredential(settings.ApiKey));
        _chatClient = client.GetChatClient(settings.CompletionModel);
        _embeddingClient = client.GetEmbeddingClient(settings.EmbeddingModel);
    }
    public async Task<string> GetCompletionAsync(string prompt) {
        var r = await _chatClient.CompleteChatAsync(prompt);
        return r.Value.Content.FirstOrDefault()?.Text ?? "";
    }
    public async Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) {
        var rawEmbeddings = await _embeddingClient.GenerateEmbeddingsAsync(paragraphs);
        return rawEmbeddings.Value.Select(v => v.ToFloats().ToArray()).ToArray();
    }
    public void Dispose() {
    }
}
