using Relatude.DB.NodeServer.Json;
using Relatude.DB.NodeServer.Operations;
using System.Text.Json;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// The single command endpoint of the admin UI. Commands are operations of the domain <see cref="OperationDomains.Db"/>
/// in the server's <see cref="OperationRegistry"/>, dispatched on their name, so a new one is a new registration, not a
/// new endpoint. Results travel back on the POST response; anything server-initiated goes over the UIEventStream instead.
/// Thread-safe.
/// </summary>
public sealed class UICommands {
    readonly RelatudeDBServer _server;
    readonly OperationRegistry _operations;
    readonly OperationRuns _runs = new();
    const string runOwner = ""; // the admin UI's callers are the database's administrators alike
    internal UICommands(RelatudeDBServer server, OperationRegistry operations) {
        _server = server;
        _operations = operations;
        // stops a command sent with this run id (see OperationRuns); a command that does not watch its
        // cancellation token simply runs to its end
        Register("command-cancel", ctx => (object?)_runs.Cancel(runOwner, ctx.Payload<UICommandCancelPayload>().RunId ?? ""));
    }
    /// <param name="compress">A large answer goes compressed when the browser takes it (UICompressibleJson, <see cref="Operation.Compress"/>).</param>
    public void Register(string type, Func<UICommandContext, Task<object?>> handler, bool compress = false) {
        _operations.Register(new Operation {
            Domain = OperationDomains.Db,
            Name = type,
            Callers = [OperationCallers.AdminUI],
            Compress = compress,
            Handler = ctx => handler((UICommandContext)ctx),
        });
    }
    public void Register(string type, Func<UICommandContext, object?> handler, bool compress = false) {
        Register(type, ctx => Task.FromResult(handler(ctx)), compress);
    }
    bool tryGet(string type, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Operation operation) =>
        _operations.TryGet(OperationDomains.Db, type, out operation) && operation.Allows(OperationCallers.AdminUI);
    /// <summary>
    /// Runs a command outside the POST route - what a live feed does on the cadence a tab asked for
    /// (see <see cref="UILiveFeeds"/>). The same handler, the same context type; only the response
    /// differs, so a command never has to know which way it was called.
    /// </summary>
    /// <param name="cancellation">What stops the command: for a feed, the tab going away. A feed's http is the
    /// event stream's, not a request of its own, so its RequestAborted says nothing about the feed.</param>
    internal async Task<object?> Invoke(string type, RelatudeDBServer server, HttpContext http, JsonElement? payload, CancellationToken cancellation = default) {
        if (!tryGet(type, out var operation)) throw new Exception("Unknown command: " + type);
        return await operation.Handler(new UICommandContext(server, http, payload, cancellation, operation));
    }
    public async Task<IResult> Execute(HttpContext http) {
        UICommandRequest? request = null;
        try {
            request = await JsonSerializer.DeserializeAsync<UICommandRequest>(http.Request.Body, RelatudeDBJsonOptions.Default, http.RequestAborted);
        } catch (JsonException) { }
        if (request == null || string.IsNullOrWhiteSpace(request.Type)) {
            return Results.BadRequest(new { error = "Expected a JSON body: { type, payload }. " });
        }
        if (!tryGet(request.Type, out var operation)) return Results.BadRequest(new { error = "Unknown command: " + request.Type });
        using var run = string.IsNullOrEmpty(request.RunId) ? null : _runs.Begin(runOwner, request.RunId, http.RequestAborted);
        var cancellation = run?.Token ?? http.RequestAborted;
        try {
            var result = await operation.Handler(new UICommandContext(_server, http, request.Payload, cancellation, operation));
            return operation.Compress ? (IResult)new UICompressibleJson(result) : Results.Json(result, RelatudeDBJsonOptions.Default);
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            // stopped from the page, or the request was given up on: an answer, not a failure
            return Results.Json(new { error = "Stopped. ", cancelled = true }, RelatudeDBJsonOptions.Default, statusCode: StatusCodes.Status499ClientClosedRequest);
        } catch (Exception error) {
            return Results.Json(new { error = error.Message }, RelatudeDBJsonOptions.Default, statusCode: 500);
        }
    }
}
/// <summary>A command of the admin UI as it runs. Never cancelled for a command a live feed runs.</summary>
public sealed class UICommandContext : OperationContext {
    internal UICommandContext(RelatudeDBServer server, HttpContext http, JsonElement? payload, CancellationToken cancellation, Operation? operation = null)
        : base(payload, cancellation, operation: operation) {
        Server = server;
        Http = http;
    }
    public RelatudeDBServer Server { get; }
    public HttpContext Http { get; }
    public T Payload<T>() {
        if (PayloadElement is not JsonElement e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
            throw new Exception("Command requires a payload. ");
        }
        return e.Deserialize<T>(RelatudeDBJsonOptions.Default) ?? throw new Exception("Invalid command payload. ");
    }
}
sealed record UICommandRequest(string? Type, JsonElement? Payload, string? RunId = null);
sealed record UICommandCancelPayload(string? RunId);
