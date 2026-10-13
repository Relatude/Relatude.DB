using Relatude.DB.NodeServer.Json;
using System.Text.Json;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// The single command endpoint of the admin UI. Commands are dispatched on their type name,
/// so a new operation is a new registration, not a new endpoint. Results travel back on the
/// POST response; anything server-initiated goes over the UIEventStream instead.
/// Thread-safe.
/// </summary>
public sealed class UICommands {
    readonly RelatudeDBServer _server;
    readonly Dictionary<string, Func<UICommandContext, Task<object?>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    readonly UICommandRuns _runs = new();
    internal UICommands(RelatudeDBServer server) {
        _server = server;
        // stops a command sent with this run id (see UICommandRuns); a command that does not watch its
        // cancellation token simply runs to its end
        Register("command-cancel", ctx => (object?)_runs.Cancel(ctx.Payload<UICommandCancelPayload>().RunId ?? ""));
    }
    public void Register(string type, Func<UICommandContext, Task<object?>> handler) {
        lock (_handlers) _handlers[type] = handler;
    }
    public void Register(string type, Func<UICommandContext, object?> handler) {
        Register(type, ctx => Task.FromResult(handler(ctx)));
    }
    /// <summary>
    /// Runs a command outside the POST route - what a live feed does on the cadence a tab asked for
    /// (see <see cref="UILiveFeeds"/>). The same handler, the same context type; only the response
    /// differs, so a command never has to know which way it was called.
    /// </summary>
    /// <param name="cancellation">What stops the command: for a feed, the tab going away. A feed's http is the
    /// event stream's, not a request of its own, so its RequestAborted says nothing about the feed.</param>
    internal async Task<object?> Invoke(string type, RelatudeDBServer server, HttpContext http, JsonElement? payload, CancellationToken cancellation = default) {
        Func<UICommandContext, Task<object?>>? handler;
        lock (_handlers) _handlers.TryGetValue(type, out handler);
        if (handler == null) throw new Exception("Unknown command: " + type);
        return await handler(new UICommandContext(server, http, payload, cancellation));
    }
    public async Task<IResult> Execute(HttpContext http) {
        UICommandRequest? request = null;
        try {
            request = await JsonSerializer.DeserializeAsync<UICommandRequest>(http.Request.Body, RelatudeDBJsonOptions.Default, http.RequestAborted);
        } catch (JsonException) { }
        if (request == null || string.IsNullOrWhiteSpace(request.Type)) {
            return Results.BadRequest(new { error = "Expected a JSON body: { type, payload }. " });
        }
        Func<UICommandContext, Task<object?>>? handler;
        lock (_handlers) _handlers.TryGetValue(request.Type, out handler);
        if (handler == null) return Results.BadRequest(new { error = "Unknown command: " + request.Type });
        using var run = string.IsNullOrEmpty(request.RunId) ? null : _runs.Begin(request.RunId, http.RequestAborted);
        var cancellation = run?.Token ?? http.RequestAborted;
        try {
            var result = await handler(new UICommandContext(_server, http, request.Payload, cancellation));
            return Results.Json(result, RelatudeDBJsonOptions.Default);
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            // stopped from the page, or the request was given up on: an answer, not a failure
            return Results.Json(new { error = "Stopped. ", cancelled = true }, RelatudeDBJsonOptions.Default, statusCode: StatusCodes.Status499ClientClosedRequest);
        } catch (Exception error) {
            return Results.Json(new { error = error.Message }, RelatudeDBJsonOptions.Default, statusCode: 500);
        }
    }
}
public sealed class UICommandContext {
    readonly JsonElement? _payload;
    internal UICommandContext(RelatudeDBServer server, HttpContext http, JsonElement? payload, CancellationToken cancellation) {
        Server = server;
        Http = http;
        _payload = payload;
        Cancellation = cancellation;
    }
    public RelatudeDBServer Server { get; }
    public HttpContext Http { get; }
    /// <summary>
    /// Cancelled when the page stops the command (it was sent with a run id, see <see cref="UICommandRuns"/>)
    /// or the request is aborted. Never cancelled for a command a live feed runs.
    /// </summary>
    public CancellationToken Cancellation { get; }
    public T Payload<T>() {
        if (_payload is not JsonElement e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
            throw new Exception("Command requires a payload. ");
        }
        return e.Deserialize<T>(RelatudeDBJsonOptions.Default) ?? throw new Exception("Invalid command payload. ");
    }
}
sealed record UICommandRequest(string? Type, JsonElement? Payload, string? RunId = null);
sealed record UICommandCancelPayload(string? RunId);
