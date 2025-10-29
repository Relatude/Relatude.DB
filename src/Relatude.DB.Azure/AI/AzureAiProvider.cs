using Azure.AI.OpenAI;
using System.ClientModel;
using OpenAI.Chat;
using OpenAI.Embeddings;
using System.Diagnostics;
using Relatude.DB.Common;
namespace Relatude.DB.AI;
public class AzureAIProvider : IAIProvider {
    readonly ChatClient _chatClient;
    readonly Dictionary<string, ChatClient> _chatClientByKey = [];
    readonly EmbeddingClient _embeddingClient;
    public AzureAIProvider(AIProviderSettings settings) {
        if (string.IsNullOrEmpty(settings.ServiceUrl)) throw new ArgumentException("ServiceUrl is required in AIProviderSettings");
        if (string.IsNullOrEmpty(settings.ApiKey)) throw new ArgumentException("ApiKey is required in AIProviderSettings");
        var client = new AzureOpenAIClient(new Uri(settings.ServiceUrl), new ApiKeyCredential(settings.ApiKey));
        _chatClient = client.GetChatClient(settings.CompletionModel);
        if (settings.CompletionModelsByKey != null) {
            foreach (var kv in settings.CompletionModelsByKey) {
                _chatClientByKey[kv.Key] = client.GetChatClient(kv.Value);
            }
        }
        _embeddingClient = client.GetEmbeddingClient(settings.EmbeddingModel);
    }
    public async Task<string> GetCompletionAsync(string prompt, string? modelKey = null) {
        ChatClient? chatClient;
        if (modelKey != null) {
            if (!_chatClientByKey.TryGetValue(modelKey, out chatClient)) {
                throw new ArgumentException($"Model key '{modelKey}' not found in AIProviderSettings");
            }
        } else {
            chatClient = _chatClient;
        }
        var r = await chatClient.CompleteChatAsync(prompt);
        return r.Value.Content.FirstOrDefault()?.Text ?? "";
    }
    public async Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) {
        var rawEmbeddings = await _embeddingClient.GenerateEmbeddingsAsync(paragraphs);
        return rawEmbeddings.Value.Select(v => v.ToFloats().ToArray()).ToArray();
    }
    public void Dispose() {
    }
}
