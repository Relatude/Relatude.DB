using Azure.AI.OpenAI;
using System.ClientModel;
using OpenAI.Chat;
using OpenAI.Embeddings;
using System.Diagnostics;
using Relatude.DB.Common;
namespace Relatude.DB.AI;
public class Chat {
    public Guid Id { get; set; }
    public string Instructions { get; set; } = "";
    public List<Message> Messages { get; set; } = [];
    public string[] ContextSources { get; set; } = [];
}
public class Message {
    public Guid Id { get; set; }
    public bool FromUser { get; set; }
    public string Content { get; set; } = "";
}
public class AzureAiProvider : IAIProvider {
    readonly IEmbeddingCache _cache;
    readonly AzureOpenAIClient _client;
    readonly ChatClient _chatClient;
    readonly EmbeddingClient _embeddingClient;
    public Action<string, bool>? LogCallback { get; set; }
    public AIProviderSettings Settings { get; }
    public double DefaultSemanticRatio => Settings.DefaultSemanticRatio;
    public AzureAiProvider(AIProviderSettings settings, IEmbeddingCache? cache = null) {
        Settings = settings;
        _cache = cache ?? new MemoryEmbeddingCache(10000);
        _client = new(new Uri(settings.ServiceUrl), new ApiKeyCredential(settings.ApiKey));
        _chatClient = _client.GetChatClient(settings.CompletionModel);
        _embeddingClient = _client.GetEmbeddingClient(settings.EmbeddingModel);
    }
    public async Task<string> GetChatCompletion(Chat conversation) {
        List<ChatMessage> messages = new();
        messages.Add(ChatMessage.CreateSystemMessage(conversation.Instructions));
        foreach (var message in conversation.Messages) {
            messages.Add(message.FromUser ? ChatMessage.CreateUserMessage(message.Content) : ChatMessage.CreateAssistantMessage(message.Content));
        }
        var r = await _chatClient.CompleteChatAsync(messages);
        return r.Value.Content.FirstOrDefault()?.Text ?? "";
    }
    public string ParseInstructionTemplate(string instructionTemplate, string context) {
        return instructionTemplate.Replace("[CONTEXT]", context);
    }
    public async Task<string> GetCompletionAsync(string prompt) {
        var r = await _chatClient.CompleteChatAsync(Settings.CompletionModel, prompt);
        return r.Value.Content.FirstOrDefault()?.Text ?? "";
    }
    class resultSet(string text) {
        public ulong Hash = text.XXH64Hash();
        public string Text = text;
        public float[]? Embedding;
    }
    public async Task<List<float[]>> GetEmbeddingsAsync(IEnumerable<string> paragraphs) {

        if (Settings.ApiKey == "DUMMY") {
            LogCallback?.Invoke($"Embedding http request for {paragraphs.Count()} items.", false);
            var createDummy = (string v) => {
                var hash = v.XXH64Hash();
                if (_cache.TryGet(hash, out var createDummyVector)) return createDummyVector;
                createDummyVector = new float[1536];
                for (int i = 0; i < createDummyVector.Length; i++) {
                    createDummyVector[i] = (float)(new Random().NextDouble() * 2 - 1);
                }
                _cache.Set(hash, createDummyVector);
                Thread.Sleep(40);
                return createDummyVector;
            };
            return paragraphs.Select(p => createDummy(p)).ToList();
        }

        var valueSet = paragraphs.Select(p => new resultSet(p)).ToList();
        foreach (var v in valueSet) _cache.TryGet(v.Hash, out v.Embedding);
        var missing = valueSet.Where(r => r.Embedding == null).Select(v => v.Text).ToList();
        if (missing.Count > 0) {
            var totalTextLength = missing.Sum(m => m.Length);
            var sw = Stopwatch.StartNew();
            var clientResult = await _embeddingClient.GenerateEmbeddingsAsync(missing);
            sw.Stop();
            LogCallback?.Invoke($"Embedding http request for {missing.Count} items. {sw.ElapsedMilliseconds.To1000N()}ms. ", false);
            var result = clientResult.Value.Select(v => v.ToFloats().ToArray()).ToList();
            var pos = 0;
            foreach (var v in valueSet) {
                if (v.Embedding == null) v.Embedding = result[pos++];
            }
            _cache.SetMany(result.Select((r, i) => new Tuple<ulong, float[]>(valueSet[i].Hash, r)));
        }
        return valueSet.Select(v => {
            if (v.Embedding == null) throw new Exception("Embedding not found");
            return v.Embedding;
        }).ToList();
    }
    public void Dispose() {
        _cache.Dispose();
    }
    public void ClearCache() {
        _cache.ClearAll();
    }
}
