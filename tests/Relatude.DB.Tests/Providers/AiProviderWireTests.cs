using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Relatude.DB.AI;
using System.Text.Json;

namespace Relatude.Providers;

/// <summary>
/// A tiny scripted HTTP server so the AI providers can be verified against the exact requests
/// they put on the wire, without any real AI service involved.
/// </summary>
sealed class AiServiceStub : IAsyncDisposable {
    readonly WebApplication _app;
    public record RecordedRequest(string Method, string Path, string Query, Dictionary<string, string> Headers, string Body) {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }
    public readonly List<RecordedRequest> Requests = [];
    readonly Queue<(int Status, string Body, string? RetryAfter)> _responses = new();
    public string BaseUrl { get; private set; } = "";
    AiServiceStub(WebApplication app) { _app = app; }
    public static async Task<AiServiceStub> StartAsync() {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new AiServiceStub(app);
        app.Run(async context => {
            string body;
            using (var reader = new StreamReader(context.Request.Body)) body = await reader.ReadToEndAsync();
            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => (string)h.Value!, StringComparer.OrdinalIgnoreCase);
            lock (stub.Requests) stub.Requests.Add(new(context.Request.Method, context.Request.Path.Value ?? "", context.Request.QueryString.Value ?? "", headers, body));
            (int Status, string Body, string? RetryAfter) response;
            lock (stub._responses) response = stub._responses.Count > 0 ? stub._responses.Dequeue() : (500, "{\"error\":\"no scripted response left\"}", null);
            context.Response.StatusCode = response.Status;
            if (response.RetryAfter != null) context.Response.Headers.RetryAfter = response.RetryAfter;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response.Body);
        });
        await app.StartAsync();
        stub.BaseUrl = app.Urls.First().TrimEnd('/');
        return stub;
    }
    public void Enqueue(int status, string body, string? retryAfter = null) {
        lock (_responses) _responses.Enqueue((status, body, retryAfter));
    }
    public RecordedRequest Single() {
        lock (Requests) {
            Assert.AreEqual(1, Requests.Count, "expected exactly one request");
            return Requests[0];
        }
    }
    public async ValueTask DisposeAsync() {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

[TestClass]
public class AiProviderWireTests {
    const string _chatResponse = """{"choices":[{"message":{"role":"assistant","content":"hello from stub"}}]}""";

    [TestMethod]
    public async Task OpenAICompletionSendsBearerAuthAndModelAndParsesContent() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new OpenAIProvider(new AIProviderSettings {
            ServiceUrl = stub.BaseUrl + "/v1",
            ApiKey = "sk-test",
            CompletionModel = "gpt-4o",
        });
        stub.Enqueue(200, _chatResponse);
        var result = await provider.GetCompletionAsync("Say hello");
        Assert.AreEqual("hello from stub", result);
        var request = stub.Single();
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("/v1/chat/completions", request.Path);
        Assert.AreEqual("Bearer sk-test", request.Headers["Authorization"]);
        Assert.AreEqual("gpt-4o", request.Json.GetProperty("model").GetString());
        Assert.IsFalse(request.Json.TryGetProperty("max_tokens", out _), "max_tokens should not be sent unless configured");
        var message = request.Json.GetProperty("messages")[0];
        Assert.AreEqual("user", message.GetProperty("role").GetString());
        Assert.AreEqual("Say hello", message.GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task OpenAICompletionResolvesModelKeysAndSendsMaxTokensWhenConfigured() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new OpenAIProvider(new AIProviderSettings {
            ServiceUrl = stub.BaseUrl + "/v1",
            ApiKey = "sk-test",
            CompletionModel = "gpt-4o",
            CompletionModelsByKey = new() { ["fast"] = "gpt-4o-mini" },
            MaxOutputTokens = 123,
        });
        stub.Enqueue(200, _chatResponse);
        await provider.GetCompletionAsync("hi", "fast");
        var request = stub.Single();
        Assert.AreEqual("gpt-4o-mini", request.Json.GetProperty("model").GetString());
        Assert.AreEqual(123, request.Json.GetProperty("max_tokens").GetInt32());
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => provider.GetCompletionAsync("hi", "no-such-key"));
    }

    [TestMethod]
    public async Task OpenAIEmbeddingsAreOrderedByIndexNotByResponseOrder() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new OpenAIProvider(new AIProviderSettings {
            ServiceUrl = stub.BaseUrl + "/v1",
            ApiKey = "sk-test",
            EmbeddingModel = "text-embedding-3-small",
        });
        stub.Enqueue(200, """{"data":[{"index":1,"embedding":[0.5,0.6]},{"index":0,"embedding":[0.1,0.2]}]}""");
        var vectors = await provider.GetEmbeddingsAsync(["first", "second"]);
        Assert.AreEqual(2, vectors.Length);
        CollectionAssert.AreEqual(new float[] { 0.1f, 0.2f }, vectors[0]);
        CollectionAssert.AreEqual(new float[] { 0.5f, 0.6f }, vectors[1]);
        var request = stub.Single();
        Assert.AreEqual("/v1/embeddings", request.Path);
        Assert.AreEqual("text-embedding-3-small", request.Json.GetProperty("model").GetString());
        var input = request.Json.GetProperty("input");
        Assert.AreEqual("first", input[0].GetString());
        Assert.AreEqual("second", input[1].GetString());
    }

    [TestMethod]
    public async Task TransientErrorsAreRetriedAndRealErrorsSurfaceStatusAndBody() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new OpenAIProvider(new AIProviderSettings {
            ServiceUrl = stub.BaseUrl + "/v1",
            ApiKey = "sk-test",
            CompletionModel = "gpt-4o",
        });
        stub.Enqueue(429, """{"error":"slow down"}""", retryAfter: "0");
        stub.Enqueue(200, _chatResponse);
        var result = await provider.GetCompletionAsync("hi");
        Assert.AreEqual("hello from stub", result);
        Assert.AreEqual(2, stub.Requests.Count, "the 429 should have been retried once");

        stub.Enqueue(400, """{"error":{"message":"bad model"}}""");
        var error = await Assert.ThrowsExceptionAsync<Exception>(() => provider.GetCompletionAsync("hi"));
        StringAssert.Contains(error.Message, "400");
        StringAssert.Contains(error.Message, "bad model");
    }

    [TestMethod]
    public async Task AzureOpenAIUsesDeploymentPathApiVersionAndApiKeyHeader() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new AzureAIProvider(new AIProviderSettings {
            ServiceUrl = stub.BaseUrl,
            ApiKey = "azure-key",
            CompletionModel = "my-gpt4o-deployment",
            EmbeddingModel = "my-embedding-deployment",
        });
        stub.Enqueue(200, _chatResponse);
        var result = await provider.GetCompletionAsync("hi");
        Assert.AreEqual("hello from stub", result);
        var request = stub.Single();
        Assert.AreEqual("/openai/deployments/my-gpt4o-deployment/chat/completions", request.Path);
        Assert.AreEqual("?api-version=2024-10-21", request.Query);
        Assert.AreEqual("azure-key", request.Headers["api-key"]);
        Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
        Assert.IsFalse(request.Json.TryGetProperty("model", out _), "the deployment is addressed by the url, not the body");

        stub.Requests.Clear();
        stub.Enqueue(200, """{"data":[{"index":0,"embedding":[1.0]}]}""");
        await provider.GetEmbeddingsAsync(["text"]);
        Assert.AreEqual("/openai/deployments/my-embedding-deployment/embeddings", stub.Single().Path);
    }

    [TestMethod]
    public async Task AnthropicCompletionSendsMessagesRequestAndConcatenatesTextBlocks() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new AnthropicAIProvider(new AIProviderSettings {
            ServiceUrl = stub.BaseUrl,
            ApiKey = "sk-ant-test",
            CompletionModel = "claude-opus-5",
        });
        stub.Enqueue(200, """{"content":[{"type":"text","text":"hello "},{"type":"thinking","thinking":""},{"type":"text","text":"world"}],"stop_reason":"end_turn"}""");
        var result = await provider.GetCompletionAsync("Say hello");
        Assert.AreEqual("hello world", result);
        var request = stub.Single();
        Assert.AreEqual("/v1/messages", request.Path);
        Assert.AreEqual("sk-ant-test", request.Headers["x-api-key"]);
        Assert.AreEqual("2023-06-01", request.Headers["anthropic-version"]);
        Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
        Assert.AreEqual("claude-opus-5", request.Json.GetProperty("model").GetString());
        Assert.AreEqual(4096, request.Json.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual("Say hello", request.Json.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task AnthropicRefusalStopReasonThrowsInsteadOfReturningEmptyText() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new AnthropicAIProvider(new AIProviderSettings {
            ServiceUrl = stub.BaseUrl,
            ApiKey = "sk-ant-test",
        });
        stub.Enqueue(200, """{"content":[],"stop_reason":"refusal","stop_details":{"type":"refusal","explanation":"declined"}}""");
        var error = await Assert.ThrowsExceptionAsync<Exception>(() => provider.GetCompletionAsync("hi"));
        StringAssert.Contains(error.Message, "refusal");
        StringAssert.Contains(error.Message, "declined");
    }

    [TestMethod]
    public async Task AnthropicEmbeddingsRequireAndUseTheSeparateEmbeddingEndpoint() {
        await using var stub = await AiServiceStub.StartAsync();
        using var withoutEndpoint = new AnthropicAIProvider(new AIProviderSettings { ApiKey = "sk-ant-test" });
        await Assert.ThrowsExceptionAsync<NotSupportedException>(() => withoutEndpoint.GetEmbeddingsAsync(["text"]));

        using var provider = new AnthropicAIProvider(new AIProviderSettings {
            ServiceUrl = stub.BaseUrl,
            ApiKey = "sk-ant-test",
            EmbeddingServiceUrl = stub.BaseUrl + "/v1",
            EmbeddingApiKey = "sk-embeddings",
            EmbeddingModel = "voyage-3",
        });
        stub.Enqueue(200, """{"data":[{"index":0,"embedding":[0.1]}]}""");
        var vectors = await provider.GetEmbeddingsAsync(["text"]);
        Assert.AreEqual(1, vectors.Length);
        var request = stub.Single();
        Assert.AreEqual("/v1/embeddings", request.Path);
        Assert.AreEqual("Bearer sk-embeddings", request.Headers["Authorization"]);
        Assert.AreEqual("voyage-3", request.Json.GetProperty("model").GetString());
    }
}
