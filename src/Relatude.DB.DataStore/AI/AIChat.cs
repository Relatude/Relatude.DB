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

public enum AIChatRole { User, Assistant }

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
}

/// <summary>
/// What a completion used, in the provider's tokens. <see cref="InputTokens"/> includes those read from the
/// provider's prompt cache, which <see cref="CachedInputTokens"/> tells apart; <see cref="OutputTokens"/> includes
/// those a reasoning model spent thinking, which <see cref="ReasoningTokens"/> tells apart. <see cref="Estimated"/>:
/// the provider gave no count, so these are worked out from the characters.
/// </summary>
public sealed record AIUsage(int InputTokens, int OutputTokens, int CachedInputTokens = 0, int ReasoningTokens = 0, bool Estimated = false);

/// <summary>
/// A chat completion's whole answer (<see cref="IAIProvider.GetChatResultAsync"/>): the text, why the model stopped,
/// what it used, and what it cost where a service charges for it. Everything but the text is null where the provider
/// does not say.
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
}

/// <summary>One turn of a conversation. Only a user message can carry pictures.</summary>
public sealed record AIChatMessage {
    public AIChatMessage(AIChatRole role, string text, IEnumerable<AIImage>? images = null) {
        Role = role;
        Text = text ?? "";
        Images = images == null ? [] : [.. images];
        if (role != AIChatRole.User && Images.Count > 0) throw new ArgumentException("Only a user message can carry pictures. ", nameof(images));
        if (Images.Any(i => i == null || i.Bytes == null || i.Bytes.Length == 0)) throw new ArgumentException("A picture has no bytes. ", nameof(images));
    }
    public AIChatRole Role { get; }
    public string Text { get; }
    public IReadOnlyList<AIImage> Images { get; }
    public static AIChatMessage User(string text, params IEnumerable<AIImage> images) => new(AIChatRole.User, text, images);
    public static AIChatMessage Assistant(string text) => new(AIChatRole.Assistant, text);
}

/// <summary>
/// A chat completion: the conversation so far, with instructions kept apart from what the user wrote,
/// and pictures for the model to look at.
/// <code>
/// var answer = await db.Services.AI.GetChatCompletionAsync(new AIChatRequest("What does this screen show?", AIImage.FromBytes(png)) {
///     Instructions = "You help editors of a website. Answer in one paragraph.",
/// });
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
    public bool HasImages => Messages.Any(m => m.Images.Count > 0);
    public IEnumerable<AIImage> AllImages => Messages.SelectMany(m => m.Images);
    /// <summary>
    /// The request as one prompt, for a provider that takes nothing else: the instructions, then the
    /// messages, each named by its role unless there is just one from the user. Pictures are left out.
    /// </summary>
    public string ToPrompt() {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Instructions)) parts.Add(Instructions.Trim());
        if (Messages.Count == 1 && Messages[0].Role == AIChatRole.User) {
            parts.Add(Messages[0].Text);
        } else {
            foreach (var m in Messages) parts.Add((m.Role == AIChatRole.User ? "User: " : "Assistant: ") + m.Text);
        }
        return string.Join("\n\n", parts);
    }
}
