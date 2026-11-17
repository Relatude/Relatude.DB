using System.Runtime.CompilerServices;

namespace Relatude.DB.AI;

/// <summary>
/// An AI provider that answers from a script: the answers queued, in order, or what a function makes of each request.
/// For tests, and for building on tool calls with no model behind them. Every request it was given is kept.
/// <code>
/// var ai = new ScriptedAIProvider(
///     ScriptedAIProvider.Calls(new AIToolCall("1", "content_query", """{"type":"IArticle","count":true}""")),
///     ScriptedAIProvider.Says("There are 12 articles."));
/// </code>
/// </summary>
public sealed class ScriptedAIProvider : IAIProvider {
    readonly Queue<AIChatResult> _answers = new();
    readonly Func<AIChatRequest, AIChatResult>? _answer;
    readonly List<AIChatRequest> _requests = [];
    readonly DummyAIProvider _embeddings = new();

    public ScriptedAIProvider(params IEnumerable<AIChatResult> answers) => Enqueue(answers);
    public ScriptedAIProvider(Func<AIChatRequest, AIChatResult> answer) => _answer = answer;

    /// <summary>The requests answered so far, oldest first.</summary>
    public IReadOnlyList<AIChatRequest> Requests { get { lock (_requests) return [.. _requests]; } }
    /// <summary>A wait before each streamed piece, to see an answer come as a model's would.</summary>
    public TimeSpan Delay { get; set; }

    public void Enqueue(params IEnumerable<AIChatResult> answers) {
        lock (_requests) foreach (var a in answers) _answers.Enqueue(a);
    }

    /// <summary>An answer in words.</summary>
    public static AIChatResult Says(string text) => new(text) { FinishReason = AIFinishReason.Stop, Model = "scripted" };
    /// <summary>An answer asking for tools.</summary>
    public static AIChatResult Calls(params IEnumerable<AIToolCall> calls) => Calls("", calls);
    /// <summary>An answer saying something and asking for tools.</summary>
    public static AIChatResult Calls(string text, params IEnumerable<AIToolCall> calls) =>
        new(text) { FinishReason = AIFinishReason.ToolCalls, Model = "scripted", ToolCalls = [.. calls] };

    public bool SupportsImages => true;
    public bool SupportsTools => true;

    AIChatResult next(AIChatRequest request) {
        lock (_requests) {
            _requests.Add(request);
            if (_answer != null) return _answer(request);
            if (_answers.Count == 0) throw new InvalidOperationException("The script has no answer left for this request. ");
            return _answers.Dequeue();
        }
    }

    public Task<string> GetCompletionAsync(string prompt, string? modelKey = null) => Task.FromResult(next(new AIChatRequest(prompt) { ModelKey = modelKey }).Text);
    public Task<string> GetChatCompletionAsync(AIChatRequest request, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(next(request).Text);
    }
    public Task<AIChatResult> GetChatResultAsync(AIChatRequest request, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(next(request));
    }

    /// <summary>The text a word at a time, each tool call as its start and then its arguments, then the whole answer.</summary>
    public async IAsyncEnumerable<AIChatDelta> StreamChatAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        var result = next(request);
        foreach (var word in words(result.Text)) {
            await pause(cancellationToken);
            yield return new AITextDelta(word);
        }
        for (var i = 0; i < result.ToolCalls.Count; i++) {
            await pause(cancellationToken);
            yield return new AIToolCallDelta(i, result.ToolCalls[i].Id, result.ToolCalls[i].Name, null);
            yield return new AIToolCallDelta(i, null, null, result.ToolCalls[i].ArgumentsJson);
        }
        yield return new AIChatDone(result);
    }

    async Task pause(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
    }

    // each word with the white space after it, so the pieces put together are the text
    static IEnumerable<string> words(string text) {
        var start = 0;
        for (var i = 1; i < text.Length; i++) {
            if (!char.IsWhiteSpace(text[i]) && char.IsWhiteSpace(text[i - 1])) {
                yield return text[start..i];
                start = i;
            }
        }
        if (start < text.Length) yield return text[start..];
    }

    public Task<float[][]> GetEmbeddingsAsync(string[] paragraphs) => _embeddings.GetEmbeddingsAsync(paragraphs);
    public void Dispose() { }
}
