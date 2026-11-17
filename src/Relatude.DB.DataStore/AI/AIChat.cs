using System.Text.Json;

namespace Relatude.DB.AI;

/// <summary>
/// A picture for a chat completion: PNG, JPEG, GIF or WebP. <see cref="Detail"/> is how closely the model
/// looks - low, high or auto - where the provider has such a setting; a low look costs less.
/// </summary>
public sealed record AIImage(byte[] Bytes, string ContentType, string Detail = "auto") {
    /// <summary>A picture whose type is read from its first bytes. Throws when it is none of PNG, JPEG, GIF and WebP.</summary>
    public static AIImage FromBytes(byte[] bytes, string detail = "auto")
        => new(bytes, ContentTypeOf(bytes) ?? throw new ArgumentException("The picture is not a PNG, JPEG, GIF or WebP file. ", nameof(bytes)), detail);
    /// <summary>The picture's type by its first bytes, or null when it is none of PNG, JPEG, GIF and WebP.</summary>
    public static string? ContentTypeOf(ReadOnlySpan<byte> b) {
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 4 && b[..4].SequenceEqual("GIF8"u8)) return "image/gif";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}

/// <summary><see cref="Tool"/> is the turn that carries what tools answered (<see cref="AIChatMessage.Tool"/>).</summary>
public enum AIChatRole { User, Assistant, Tool }

/// <summary>The effort levels of <see cref="AIChatRequest.Effort"/>, as the providers spell them.</summary>
public static class AIEffort {
    public const string Minimal = "minimal";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

/// <summary>Why a model stopped writing, as <see cref="AIChatResult.FinishReason"/> says it, whatever the provider's own word.</summary>
public static class AIFinishReason {
    /// <summary>The model was done.</summary>
    public const string Stop = "stop";
    /// <summary>The model reached <see cref="AIChatRequest.MaxOutputTokens"/>, so the answer is cut short, or empty when a reasoning model spent it all thinking.</summary>
    public const string Length = "length";
    /// <summary>The provider withheld the answer, or part of it.</summary>
    public const string ContentFilter = "content_filter";
    /// <summary>The model asks for tools to be called (<see cref="AIChatResult.ToolCalls"/>) before it goes on.</summary>
    public const string ToolCalls = "tool_calls";
}

/// <summary>
/// What a completion used, in the provider's tokens. <see cref="InputTokens"/> includes those read from the
/// provider's prompt cache, which <see cref="CachedInputTokens"/> tells apart, and those written to it, where the
/// provider says (<see cref="CacheWriteTokens"/>); <see cref="OutputTokens"/> includes those a reasoning model spent
/// thinking, which <see cref="ReasoningTokens"/> tells apart. <see cref="Estimated"/>: the provider gave no count, so
/// these are worked out from the characters.
/// </summary>
public sealed record AIUsage(int InputTokens, int OutputTokens, int CachedInputTokens = 0, int ReasoningTokens = 0, bool Estimated = false, int CacheWriteTokens = 0);

/// <summary>
/// A tool the model may call: its name, what it does, written for the model, and the JSON Schema of its arguments.
/// A schema taken from a <see cref="JsonDocument"/> must be a clone, or it goes when the document does.
/// </summary>
public sealed record AITool(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// A call the model asks for. <see cref="ArgumentsJson"/> is the arguments as the provider gave them, and is sent back
/// as it is: written out again, even with other white space, a provider's prompt cache misses from there on.
/// </summary>
public sealed record AIToolCall(string Id, string Name, string ArgumentsJson) {
    /// <summary>The arguments parsed, an empty object when the model gave none. Throws <see cref="JsonException"/> when they are no JSON.</summary>
    public JsonElement Arguments() {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(ArgumentsJson) ? "{}" : ArgumentsJson);
        return doc.RootElement.Clone();
    }
}

/// <summary>What a tool answered to the call of that id. <see cref="IsError"/>: the tool failed, and the content says how.</summary>
public sealed record AIToolResult(string CallId, string Content, bool IsError = false);

/// <summary>
/// Something a provider gave with its answer and needs back as it was, such as a reasoning block. It is sent only to
/// the provider it came from, and means nothing to another.
/// </summary>
public sealed record AIOpaque(string Provider, string Json);

/// <summary>A JSON Schema the answer has to fit (<see cref="AIChatRequest.ResponseSchema"/>).</summary>
public sealed record AIResponseSchema(string Name, JsonElement Schema, bool Strict = true);

/// <summary>The words of <see cref="AIChatRequest.ToolChoice"/>; any other value is the name of the one tool to call.</summary>
public static class AIToolChoice {
    public const string Auto = "auto";
    public const string None = "none";
    public const string Required = "required";
}

/// <summary>
/// A chat completion's whole answer (<see cref="IAIProvider.GetChatResultAsync"/>): the text, the tools the model asks
/// for, why it stopped, what it used, and what it cost where a service charges for it. Everything but the text is
/// null or empty where the provider does not say.
/// </summary>
public sealed record AIChatResult(string Text) {
    /// <summary>One of <see cref="AIFinishReason"/>'s, or the provider's own word for another reason.</summary>
    public string? FinishReason { get; init; }
    public AIUsage? Usage { get; init; }
    /// <summary>The model that answered, as the provider names it.</summary>
    public string? Model { get; init; }
    /// <summary>The effort the model ran with, where the provider says (the Relatude AI service does).</summary>
    public string? Effort { get; init; }
    /// <summary>What the call cost, from a service that charges in credits (the Relatude AI service).</summary>
    public int? Credits { get; init; }
    /// <summary>What is left on the account the call was charged to, from a service that charges in credits.</summary>
    public int? CreditsLeft { get; init; }
    /// <summary>Whether the model stopped at <see cref="AIChatRequest.MaxOutputTokens"/> rather than when it was done.</summary>
    public bool WasCut => FinishReason == AIFinishReason.Length;
    /// <summary>
    /// The tools the model asks to have called. Call each, then ask again with <see cref="ToMessage"/> and an
    /// <see cref="AIChatMessage.Tool"/> message of their results after the messages there were.
    /// </summary>
    public IReadOnlyList<AIToolCall> ToolCalls { get; init; } = [];
    /// <summary>What the provider needs back with this answer (<see cref="AIOpaque"/>).</summary>
    public IReadOnlyList<AIOpaque> Opaque { get; init; } = [];
    /// <summary>The answer as the assistant's turn of the conversation, tool calls and all.</summary>
    public AIChatMessage ToMessage() => AIChatMessage.Assistant(Text, ToolCalls, Opaque);
}

/// <summary>
/// One turn of a conversation. Only a user message can carry pictures, only an assistant message tool calls, and a
/// tool message carries the results of the calls in the assistant message before it, one for each.
/// </summary>
public sealed record AIChatMessage {
    public AIChatMessage(AIChatRole role, string text, IEnumerable<AIImage>? images = null) : this(role, text, images, null, null, null) { }
    AIChatMessage(AIChatRole role, string? text, IEnumerable<AIImage>? images, IEnumerable<AIToolCall>? toolCalls, IEnumerable<AIToolResult>? toolResults, IEnumerable<AIOpaque>? opaque) {
        Role = role;
        Text = text ?? "";
        Images = images == null ? [] : [.. images];
        ToolCalls = toolCalls == null ? [] : [.. toolCalls];
        ToolResults = toolResults == null ? [] : [.. toolResults];
        Opaque = opaque == null ? [] : [.. opaque];
        if (role != AIChatRole.User && Images.Count > 0) throw new ArgumentException("Only a user message can carry pictures. ", nameof(images));
        if (Images.Any(i => i == null || i.Bytes == null || i.Bytes.Length == 0)) throw new ArgumentException("A picture has no bytes. ", nameof(images));
        if (role != AIChatRole.Assistant && (ToolCalls.Count > 0 || Opaque.Count > 0)) throw new ArgumentException("Only an assistant message can carry tool calls. ", nameof(toolCalls));
        if (ToolCalls.Any(c => c == null || string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.Name))) throw new ArgumentException("A tool call needs an id and a name. ", nameof(toolCalls));
        if ((role == AIChatRole.Tool) != (ToolResults.Count > 0)) throw new ArgumentException("A tool message carries the tools' results, and no other message does: AIChatMessage.Tool(...). ", nameof(toolResults));
        if (ToolResults.Any(r => r == null || string.IsNullOrEmpty(r.CallId))) throw new ArgumentException("A tool result needs the id of its call. ", nameof(toolResults));
    }
    public AIChatRole Role { get; }
    public string Text { get; }
    public IReadOnlyList<AIImage> Images { get; }
    public IReadOnlyList<AIToolCall> ToolCalls { get; }
    public IReadOnlyList<AIToolResult> ToolResults { get; }
    public IReadOnlyList<AIOpaque> Opaque { get; }
    public static AIChatMessage User(string text, params IEnumerable<AIImage> images) => new(AIChatRole.User, text, images);
    public static AIChatMessage Assistant(string text) => new(AIChatRole.Assistant, text);
    /// <summary>The model's turn when it asked for tools: what it said, the calls, and what its provider needs back.</summary>
    public static AIChatMessage Assistant(string? text, IEnumerable<AIToolCall> toolCalls, IEnumerable<AIOpaque>? opaque = null) => new(AIChatRole.Assistant, text, null, toolCalls, null, opaque);
    /// <summary>What the tools answered.</summary>
    public static AIChatMessage Tool(params IEnumerable<AIToolResult> results) => new(AIChatRole.Tool, null, null, null, results, null);
}

/// <summary>
/// A chat completion: the conversation so far, with instructions kept apart from what the user wrote,
/// pictures for the model to look at, and tools it may ask to have called.
/// <code>
/// var answer = await db.Services.AI.GetChatCompletionAsync(new AIChatRequest("What does this screen show?", AIImage.FromBytes(png)) {
///     Instructions = "You help editors of a website. Answer in one paragraph.",
/// });
/// </code>
/// With tools, the model answers with the calls it wants (<see cref="AIChatResult.ToolCalls"/>) until it is done:
/// <code>
/// var messages = new List&lt;AIChatMessage&gt; { AIChatMessage.User("How many articles are there?") };
/// while (true) {
///     var result = await db.Services.AI.GetChatResultAsync(new AIChatRequest(messages) { Tools = tools });
///     if (result.ToolCalls.Count == 0) return result.Text;
///     messages.Add(result.ToMessage());
///     messages.Add(AIChatMessage.Tool(result.ToolCalls.Select(c => new AIToolResult(c.Id, call(c)))));
/// }
/// </code>
/// </summary>
public sealed record AIChatRequest {
    public AIChatRequest(params IEnumerable<AIChatMessage> messages) {
        Messages = [.. messages];
        if (Messages.Count == 0) throw new ArgumentException("A chat request needs at least one message. ", nameof(messages));
    }
    public AIChatRequest(string userText, params IEnumerable<AIImage> images) : this(AIChatMessage.User(userText, images)) { }
    /// <summary>The system prompt: how the model is to behave, sent apart from the messages where the provider can.</summary>
    public string? Instructions { get; init; }
    public IReadOnlyList<AIChatMessage> Messages { get; }
    /// <summary>A key of <see cref="AIProviderSettings.CompletionModelsByKey"/>, or null for the configured model.</summary>
    public string? ModelKey { get; init; }
    /// <summary>
    /// Overrides <see cref="AIProviderSettings.MaxOutputTokens"/> for this call. For a reasoning model it bounds the
    /// thinking as well as the answer, so a low ceiling can leave the answer cut short or empty
    /// (<see cref="AIChatResult.WasCut"/>).
    /// </summary>
    public int? MaxOutputTokens { get; init; }
    /// <summary>
    /// How hard a reasoning model thinks before it answers: <see cref="AIEffort.Low"/>, <see cref="AIEffort.Medium"/>
    /// or <see cref="AIEffort.High"/> (<see cref="AIEffort.Minimal"/> where the model has it); null for the model's own
    /// default. Less effort answers sooner and uses fewer tokens; more reasons further. Sent as <c>reasoning_effort</c>
    /// by the OpenAI and Azure OpenAI providers (Azure takes it from api-version 2024-12-01-preview, so set
    /// <see cref="AIProviderSettings.ApiVersion"/>), and as <c>effort</c> to the Relatude AI service, which leaves it out
    /// for a model that takes none. The Anthropic provider leaves it out.
    /// </summary>
    public string? Effort { get; init; }
    /// <summary>
    /// The tools the model may ask for, where the provider has them (<see cref="IAIProvider.SupportsTools"/>). Keep
    /// their order and wording the same from call to call: they come first in what a provider caches.
    /// </summary>
    public IReadOnlyList<AITool> Tools { get; init; } = [];
    /// <summary>One of <see cref="AIToolChoice"/>'s, or the name of the one tool to call; null is <see cref="AIToolChoice.Auto"/>.</summary>
    public string? ToolChoice { get; init; }
    /// <summary>Whether the model may ask for several tools in one answer; null for the provider's default.</summary>
    public bool? ParallelToolCalls { get; init; }
    /// <summary>
    /// A JSON Schema the answer has to fit. A provider without structured answers leaves it out, so the answer is
    /// still to be checked.
    /// </summary>
    public AIResponseSchema? ResponseSchema { get; init; }
    /// <summary>
    /// A name for what the calls of one conversation have in common, which a provider with a prompt cache routes by
    /// (OpenAI's <c>prompt_cache_key</c>). Sent only when set, as an older endpoint refuses what it does not know.
    /// </summary>
    public string? CacheKey { get; init; }
    public bool HasImages => Messages.Any(m => m.Images.Count > 0);
    public IEnumerable<AIImage> AllImages => Messages.SelectMany(m => m.Images);
    /// <summary>Whether the request has tools, or a conversation in which tools were called.</summary>
    public bool HasTools => Tools.Count > 0 || Messages.Any(m => m.ToolCalls.Count > 0 || m.ToolResults.Count > 0);
    /// <summary>Throws for a request with tools, on behalf of a provider that has none.</summary>
    public void RequireNoTools(string provider) {
        if (HasTools) throw new NotSupportedException($"The AI provider {provider} has no tool calling. ");
    }
    /// <summary>
    /// The request as one prompt, for a provider that takes nothing else: the instructions, then the
    /// messages, each named by its role unless there is just one from the user. Pictures and tools are left out.
    /// </summary>
    public string ToPrompt() {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Instructions)) parts.Add(Instructions.Trim());
        if (Messages.Count == 1 && Messages[0].Role == AIChatRole.User) {
            parts.Add(Messages[0].Text);
        } else {
            foreach (var m in Messages) {
                parts.Add(m.Role switch {
                    AIChatRole.User => "User: " + m.Text,
                    AIChatRole.Tool => "Tool: " + string.Join("\n", m.ToolResults.Select(r => r.Content)),
                    _ => "Assistant: " + m.Text,
                });
            }
        }
        return string.Join("\n\n", parts);
    }
}

/// <summary>
/// A piece of an answer as it is made (<see cref="IAIProvider.StreamChatAsync"/>). The last piece is always an
/// <see cref="AIChatDone"/> with the whole answer; the pieces before it are for showing it as it comes.
/// </summary>
public abstract record AIChatDelta;

/// <summary>More of the answer's text.</summary>
public sealed record AITextDelta(string Text) : AIChatDelta;

/// <summary>
/// A tool call taking shape: the first piece of a call has its <see cref="Id"/> and <see cref="Name"/>, the ones after
/// it more of its arguments' JSON. <see cref="Index"/> tells the calls of one answer apart.
/// </summary>
public sealed record AIToolCallDelta(int Index, string? Id, string? Name, string? ArgumentsJson) : AIChatDelta;

/// <summary>The answer is whole.</summary>
public sealed record AIChatDone(AIChatResult Result) : AIChatDelta;
