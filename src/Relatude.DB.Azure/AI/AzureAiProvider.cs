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
    public Action<string>? LogCallback { get; set; }
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
        public readonly ulong Hash = text.XXH64Hash(); // hash of the text
        public readonly string Text = text;
        public float[]? Embedding; // null if not in cache
    }
    public async Task<List<float[]>> GetEmbeddingsAsync(IEnumerable<string> paragraphs) {

        if (Settings.ApiKey == "DUMMY") return dummyData(paragraphs); // for testing without calling external service

        var valueSet = paragraphs.Select(p => new resultSet(p)).ToArray(); // all values to process
        List<resultSet> missing = []; // values not in cache

        // check cache for existing embeddings and collect missing:
        foreach (var v in valueSet) if (!_cache.TryGet(v.Hash, out v.Embedding)) missing.Add(v);

        if (missing.Count > 0) {

            // call external service to get missing embeddings:
            var sw = Stopwatch.StartNew();
            var rawEmbeddings = await _embeddingClient.GenerateEmbeddingsAsync(missing.Select(m => m.Text));
            sw.Stop();
            LogCallback?.Invoke($"Embedding http request for {missing.Count} items. {sw.ElapsedMilliseconds.To1000N()}ms. ");

            // convert to float[][]:
            var embeddings = rawEmbeddings.Value.Select(v => v.ToFloats().ToArray()).ToArray();

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
        return valueSet.Select(v => {
            if (v.Embedding == null) throw new Exception("Embedding not found");
            return v.Embedding;
        }).ToList();
    }
    List<float[]> dummyData(IEnumerable<string> paragraphs) {
        LogCallback?.Invoke($"Embedding http request for {paragraphs.Count()} items.");
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
    public void Dispose() {
        _cache.Dispose();
    }
    public void ClearCache() {
        _cache.ClearAll();
    }
}
