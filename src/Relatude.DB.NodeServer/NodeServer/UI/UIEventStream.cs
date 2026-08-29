using Microsoft.AspNetCore.Http.Features;
using Relatude.DB.NodeServer.Json;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// The single SSE stream of the admin UI. All server-to-client push traffic flows through here:
/// one long-lived connection per browser tab, broadcasts fanned out to every connection.
/// Thread-safe.
/// </summary>
public sealed class UIEventStream {
    const int maxQueuedEventsPerConnection = 1000; // a slow or gone client loses its oldest events instead of growing memory
    // sent as a real event (not an SSE comment) so the client can also use it as a liveness signal:
    // a proxy can keep the socket open after the server died, and silence is the only way to tell
    static readonly TimeSpan keepAliveInterval = TimeSpan.FromSeconds(10);
    readonly ConcurrentDictionary<Guid, Channel<UIEvent>> _connections = new();
    long _lastEventId;
    public int ConnectionCount => _connections.Count;
    public void Broadcast(string eventName, object? payload) {
        var e = new UIEvent(Interlocked.Increment(ref _lastEventId), eventName, payload);
        foreach (var connection in _connections.Values) connection.Writer.TryWrite(e);
    }
    public async Task Connect(HttpContext context) {
        var response = context.Response;
        response.Headers.Append("Content-Type", "text/event-stream");
        response.Headers.Append("Cache-Control", "no-cache");
        if (context.Request.Protocol == "HTTP/1.1") response.Headers.Append("Connection", "keep-alive"); // not needed for HTTP/2 and later
        response.Headers.Append("X-Accel-Buffering", "no"); // disable buffering for nginx
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var connectionId = Guid.NewGuid();
        var channel = Channel.CreateBounded<UIEvent>(new BoundedChannelOptions(maxQueuedEventsPerConnection) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _connections[connectionId] = channel;
        var cancellation = context.RequestAborted;
        RelatudeDBServer.Trace("UI stream connected: " + connectionId + ". Connections: " + ConnectionCount.ToString("N0"));
        try {
            await response.WriteAsync("retry: 2000\n\n", cancellation); // reconnect sooner than the browser default
            await writeEvent(response, new UIEvent(0, "connected", new { ConnectionId = connectionId }), cancellation);
            while (!cancellation.IsCancellationRequested) {
                var read = channel.Reader.WaitToReadAsync(cancellation).AsTask();
                if (await Task.WhenAny(read, Task.Delay(keepAliveInterval, cancellation)) == read) {
                    if (!await read) break;
                    while (channel.Reader.TryRead(out var e)) await writeEvent(response, e, cancellation);
                } else { // nothing to send, keep the connection alive and let the client see it is alive
                    await writeEvent(response, new UIEvent(0, "ping", null), cancellation);
                }
            }
        } catch (OperationCanceledException) { // client disconnected
        } catch (Exception error) {
            RelatudeDBServer.Trace("UI stream error: " + error.Message);
        } finally {
            _connections.TryRemove(connectionId, out _);
            RelatudeDBServer.Trace("UI stream disconnected: " + connectionId + ". Connections: " + ConnectionCount.ToString("N0"));
        }
    }
    static async Task writeEvent(HttpResponse response, UIEvent e, CancellationToken cancellation) {
        var json = JsonSerializer.Serialize(e.Payload, RelatudeDBJsonOptions.SSE);
        var sb = new StringBuilder(json.Length + 64);
        if (e.Id > 0) sb.Append("id: ").Append(e.Id).Append('\n');
        sb.Append("event: ").Append(e.Name).Append('\n');
        // supporting multi-line JSON data:
        using var reader = new StringReader(json);
        string? line;
        while ((line = reader.ReadLine()) != null) sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        await response.WriteAsync(sb.ToString(), cancellation);
        await response.Body.FlushAsync(cancellation);
    }
}
public sealed record UIEvent(long Id, string Name, object? Payload);
