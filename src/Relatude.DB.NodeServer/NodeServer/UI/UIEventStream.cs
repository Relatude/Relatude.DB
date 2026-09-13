using Microsoft.AspNetCore.Http.Features;
using Relatude.DB.NodeServer.Json;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// The single SSE stream of the admin UI. All server-to-client push traffic flows through here:
/// one long-lived connection per browser tab, broadcasts fanned out to every connection, and
/// anything meant for one tab alone (see <see cref="UILiveFeeds"/>) sent to it by id. Thread-safe.
/// </summary>
public sealed class UIEventStream {
    const int maxQueuedEventsPerConnection = 1000; // a slow or gone client loses its oldest events instead of growing memory
    // sent as a real event (not an SSE comment) so the client can also use it as a liveness signal:
    // a proxy can keep the socket open after the server died, and silence is the only way to tell
    static readonly TimeSpan keepAliveInterval = TimeSpan.FromSeconds(10);
    readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    long _lastEventId;
    public int ConnectionCount => _connections.Count;
    /// <summary>Raised when a connection goes away, so anything keeping state per tab can drop it.</summary>
    public event Action<Guid>? Closed;
    public void Broadcast(string eventName, object? payload) {
        var e = new UIEvent(Interlocked.Increment(ref _lastEventId), eventName, payload);
        foreach (var connection in _connections.Values) connection.Channel.Writer.TryWrite(e);
    }
    /// <summary>Sends to one connection. False when it is no longer there, which is not an error.</summary>
    public bool Send(Guid connectionId, string eventName, object? payload) {
        if (!_connections.TryGetValue(connectionId, out var connection)) return false;
        return connection.Channel.Writer.TryWrite(new UIEvent(Interlocked.Increment(ref _lastEventId), eventName, payload));
    }
    /// <summary>
    /// The request the connection was opened with, which lives as long as the stream does. It carries
    /// the identity the tab authenticated with, so work done on the tab's behalf between requests -
    /// a live feed sampling a command - runs as the same user rather than as nobody.
    /// </summary>
    public bool TryGetHttpContext(Guid connectionId, out HttpContext context) {
        if (_connections.TryGetValue(connectionId, out var connection)) {
            context = connection.Http;
            return true;
        }
        context = null!;
        return false;
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
        _connections[connectionId] = new Connection(channel, context);
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
            try {
                Closed?.Invoke(connectionId);
            } catch (Exception error) {
                RelatudeDBServer.Trace("UI stream close handler error: " + error.Message);
            }
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
/// <summary>One open browser tab: what is queued for it, and the request it arrived on.</summary>
sealed record Connection(Channel<UIEvent> Channel, HttpContext Http);
