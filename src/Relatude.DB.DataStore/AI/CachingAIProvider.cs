using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Relatude.DB.Common;

namespace Relatude.DB.AI;

/// <summary>
/// An AI provider that keeps the answers to requests marked <see cref="AIChatRequest.CacheAnswer"/> on this machine,
/// the way <see cref="Translation.CachingTranslationProvider"/> keeps translations, so the same request is answered
/// again at no cost. Every other request, embeddings and <see cref="GetCompletionAsync"/> go to the provider as they are.
/// <para>A request is kept by everything that decides its answer: the provider and its models (<c>identity</c>,
/// <see cref="IdentityOf"/>), the model key, the effort, the output ceiling, the instructions, the messages (pictures by
/// their SHA-256), the tools, the tool choice and the response schema. <see cref="AIChatRequest.CacheKey"/> is left
/// out, as it only routes.</para>
/// <para>The database's AI engine has one on a server, and <c>NodeStore.Services.WithAnswerCache</c> wraps a provider
/// the application calls itself in one over the same cache.</para>
/// </summary>
/// <param name="inner">The provider asked when nothing is kept.</param>
/// <param name="cache">Where the answers are kept.</param>
/// <param name="identity">What answers, beside the request: the provider and its models (<see cref="IdentityOf"/>).</param>
/// <param name="ownsCache">Whether <see cref="Dispose"/> disposes the cache too, false for a cache that outlives this provider.</param>
public sealed class CachingAIProvider(IAIProvider inner, IServiceAnswerCache cache, string identity, bool ownsCache = true) : IAIProvider, ICachingServiceProvider {
    readonly IServiceAnswerCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    readonly ServiceAnswerCacheDefaults.CreditsSeen _credits = new();

    public IAIProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));
    public string Identity { get; } = identity ?? "";

    /// <summary>What decides the answers of a provider of these settings beside the request: its type, address and models.</summary>
    public static string IdentityOf(AIProviderSettings settings) {
        var models = settings.CompletionModelsByKey == null ? "" : string.Join(",", settings.CompletionModelsByKey.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + "=" + m.Value));
        return "type:" + settings.TypeName + "|url:" + settings.ServiceUrl + "|model:" + settings.CompletionModel + "|models:" + models;
    }

    public bool SupportsImages => Inner.SupportsImages;
    public bool SupportsTools => Inner.SupportsTools;
    public Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) => Inner.GetEmbeddingsAsync(paragraphs);
    public Task<string> GetCompletionAsync(string prompt, string? modelKey = null) => Inner.GetCompletionAsync(prompt, modelKey);

    public async Task<string> GetChatCompletionAsync(AIChatRequest request, CancellationToken cancellationToken = default)
        => request.CacheAnswer ? (await GetChatResultAsync(request, cancellationToken)).Text : await Inner.GetChatCompletionAsync(request, cancellationToken);

    public async Task<AIChatResult> GetChatResultAsync(AIChatRequest request, CancellationToken cancellationToken = default) {
        if (!request.CacheAnswer) return await Inner.GetChatResultAsync(request, cancellationToken);
        var key = KeyOf(request, Identity);
        if (read(key) is { } kept) return kept;
        var result = await Inner.GetChatResultAsync(request, cancellationToken);
        keep(key, request, result);
        return result;
    }

    public IAsyncEnumerable<AIChatDelta> StreamChatAsync(AIChatRequest request, CancellationToken cancellationToken = default)
        => request.CacheAnswer ? streamKeeping(request, cancellationToken) : Inner.StreamChatAsync(request, cancellationToken);

    async IAsyncEnumerable<AIChatDelta> streamKeeping(AIChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken) {
        var key = KeyOf(request, Identity);
        if (read(key) is { } kept) {
            if (kept.Text.Length > 0) yield return new AITextDelta(kept.Text);
            yield return new AIChatDone(kept);
            yield break;
        }
        await foreach (var piece in Inner.StreamChatAsync(request, cancellationToken).WithCancellation(cancellationToken)) {
            if (piece is AIChatDone done) keep(key, request, done.Result);
            yield return piece;
        }
    }

    /// <summary>The answer kept for the request, whether or not it asks to be kept; null when there is none. Nothing is sent.</summary>
    public AIChatResult? TryGetCached(AIChatRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return read(KeyOf(request, Identity));
    }

    sealed record Kept(string Text, string? FinishReason, string? Model, string? Effort, AIToolCall[]? ToolCalls, AIOpaque[]? Opaque);

    AIChatResult? read(string key) {
        if (ServiceAnswerCacheDefaults.Read<Kept>(_cache, key) is not { } k) return null;
        var left = _credits.Left;
        return new AIChatResult(k.Text ?? "") {
            FinishReason = k.FinishReason,
            Model = k.Model,
            Effort = k.Effort,
            ToolCalls = k.ToolCalls ?? [],
            Opaque = k.Opaque ?? [],
            Credits = 0,
            CreditsLeft = left < 0 ? null : left,
            Cached = true,
        };
    }

    void keep(string key, AIChatRequest request, AIChatResult result) {
        if (result.CreditsLeft is int left) _credits.Saw(left);
        var whole = result.FinishReason is null or AIFinishReason.Stop or AIFinishReason.ToolCalls;
        if (!whole || (string.IsNullOrWhiteSpace(result.Text) && result.ToolCalls.Count == 0)) return;
        if (request.CacheAnswerIf?.Invoke(result) == false) return;
        _cache.Set(key, ServiceAnswerCacheDefaults.Serialize(new Kept(result.Text, result.FinishReason, result.Model, result.Effort, [.. result.ToolCalls], [.. result.Opaque])));
    }

    /// <summary>The key a request's answer is kept by: everything that decides it, as JSON, hashed.</summary>
    internal static string KeyOf(AIChatRequest r, string identity) {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) {
            w.WriteStartObject();
            w.WriteString("provider", identity);
            w.WriteString("model", r.ModelKey);
            w.WriteString("effort", r.Effort);
            if (r.MaxOutputTokens is int max) w.WriteNumber("max", max); else w.WriteNull("max");
            w.WriteString("instructions", r.Instructions);
            w.WriteStartArray("messages");
            foreach (var m in r.Messages) {
                w.WriteStartObject();
                w.WriteString("role", m.Role.ToString());
                w.WriteString("text", m.Text);
                w.WriteStartArray("images");
                foreach (var i in m.Images) {
                    w.WriteStartObject();
                    w.WriteString("sha256", ServiceAnswerCacheDefaults.Sha256(i.Bytes));
                    w.WriteString("type", i.ContentType);
                    w.WriteString("detail", i.Detail);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("calls");
                foreach (var c in m.ToolCalls) {
                    w.WriteStartObject();
                    w.WriteString("id", c.Id);
                    w.WriteString("name", c.Name);
                    w.WriteString("arguments", c.ArgumentsJson);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("results");
                foreach (var t in m.ToolResults) {
                    w.WriteStartObject();
                    w.WriteString("id", t.CallId);
                    w.WriteString("content", t.Content);
                    w.WriteBoolean("error", t.IsError);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("opaque");
                foreach (var o in m.Opaque) {
                    w.WriteStartObject();
                    w.WriteString("provider", o.Provider);
                    w.WriteString("json", o.Json);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("tools");
            foreach (var t in r.Tools) {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                writeJson(w, "schema", t.InputSchema);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("toolChoice", r.ToolChoice);
            if (r.ParallelToolCalls is bool parallel) w.WriteBoolean("parallel", parallel); else w.WriteNull("parallel");
            if (r.ResponseSchema is { } s) {
                w.WriteStartObject("responseSchema");
                w.WriteString("name", s.Name);
                w.WriteBoolean("strict", s.Strict);
                writeJson(w, "schema", s.Schema);
                w.WriteEndObject();
            } else {
                w.WriteNull("responseSchema");
            }
            w.WriteEndObject();
        }
        return "chat|v1|" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    static void writeJson(Utf8JsonWriter w, string name, JsonElement value) {
        w.WritePropertyName(name);
        if (value.ValueKind == JsonValueKind.Undefined) w.WriteNullValue(); else value.WriteTo(w);
    }

    public void ClearCache() => _cache.ClearAll();

    public void Dispose() {
        try { Inner.Dispose(); } finally { if (ownsCache) _cache.Dispose(); }
    }
}
