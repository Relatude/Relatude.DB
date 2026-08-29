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
    internal UICommands(RelatudeDBServer server) {
        _server = server;
    }
    public void Register(string type, Func<UICommandContext, Task<object?>> handler) {
        lock (_handlers) _handlers[type] = handler;
    }
    public void Register(string type, Func<UICommandContext, object?> handler) {
        Register(type, ctx => Task.FromResult(handler(ctx)));
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
        try {
            var result = await handler(new UICommandContext(_server, http, request.Payload));
            return Results.Json(result, RelatudeDBJsonOptions.Default);
        } catch (Exception error) {
            return Results.Json(new { error = error.Message }, RelatudeDBJsonOptions.Default, statusCode: 500);
        }
    }
}
public sealed class UICommandContext {
    readonly JsonElement? _payload;
    internal UICommandContext(RelatudeDBServer server, HttpContext http, JsonElement? payload) {
        Server = server;
        Http = http;
        _payload = payload;
    }
    public RelatudeDBServer Server { get; }
    public HttpContext Http { get; }
    public T Payload<T>() {
        if (_payload is not JsonElement e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
            throw new Exception("Command requires a payload. ");
        }
        return e.Deserialize<T>(RelatudeDBJsonOptions.Default) ?? throw new Exception("Invalid command payload. ");
    }
}
sealed record UICommandRequest(string? Type, JsonElement? Payload);
