
using Microsoft.AspNetCore.Http.Features;
using Relatude.DB.NodeServer.Json;
using System.Text;
using System.Text.Json;
namespace Relatude.DB.NodeServer.EventHub;
/// <summary>
///  Server-Sent Events (SSE) hub for managing client connections, subscriptions, and event polling.
///  Thread-safe.
/// </summary>
internal class ServerEventHub {
    ServerSideConnectionDirectory _directory = new(); // thread-safe
    PollerDirectory _pollers = new(); // thread-safe
    Timer _pollPulseTimer;
    int _minimumUpdateIntervalMs = 100; // minimum poll interval, to avoid too busy looping
    RelatudeDBServer _server;
    public ServerEventHub(RelatudeDBServer server) {
        _pollPulseTimer = new Timer((o) => pollDuePollers(), null, 2000, 1000);
        _server = server;
    }
    void pollDuePollers() {
        foreach (var poller in _pollers.Where(t => t.DueTime <= DateTime.UtcNow)) {
            if (!_directory.AnySubscribers(poller.Poller.EventName)) continue;
            var filters = _directory.GetFiltersOfSubscribers(poller.Poller.EventName);
            var events = poller.Poller.Poll(_server, filters, out int msNextCollect);
            poller.DueTime = DateTime.UtcNow.AddMilliseconds(msNextCollect);
            if (events == null) continue;
            foreach (var b in events) _directory.EnqueueEvent(poller.Poller.EventName, b);
        }
        var nextDueTime = _pollers.MinDueTime();
        var dueMs = (int)(nextDueTime - DateTime.UtcNow).TotalMilliseconds;
        dueMs = Math.Max(dueMs, _minimumUpdateIntervalMs);
        _pollPulseTimer.Change(dueMs, 1000);
    }
    public void RegisterPoller(IEventPoller poller) {
        _pollers.Add(new PollerAndDueTime(poller, DateTime.UtcNow));
    }
    public async Task Connect(HttpContext context) {

        var response = context.Response;
        var headers = response.Headers;
        var cancellation = context.RequestAborted;

        headers.Append("Content-Type", "text/event-stream");
        headers.Append("Cache-Control", "no-cache");
        if (context.Request.Protocol == "HTTP/1.1") headers.Append("Connection", "keep-alive"); // not needed for HTTP/2 and later:
        headers.Append("X-Accel-Buffering", "no"); // Disable buffering for nginx

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var connectionId = _directory.Connect(new EventContext(), out int cnnCount); // Possible to add connection context info, like user identity
        try {
            await writeEvent(response, cancellation, new ServerEventData("connectionId", null, connectionId.ToString()));
            Console.WriteLine("SSE client connected, connectionId: " + connectionId + ". Connections: " + cnnCount.ToString("N0"));            
            while (!cancellation.IsCancellationRequested) {
                var eventData = _directory.Dequeue(connectionId);
                if (eventData != null) {
                    await writeEvent(response, cancellation, eventData);
                } else {
                    if (!_directory.IsConnected(connectionId)) break;
                    await Task.Delay(_minimumUpdateIntervalMs, cancellation);
                    _directory.ClearExpiredEvents();
                }
            }
        } catch (TaskCanceledException) {
        } catch (Exception error) {
            Console.WriteLine("SSE Error: " + error.Message + "\n" + error.StackTrace + "\n");
        } finally {
            cnnCount = _directory.Disconnect(connectionId);
            Console.WriteLine("SSE client disconnected, connectionId: " + connectionId + ". Connections: " + (cnnCount).ToString("N0"));
        }
    }
    public void Disconnect(Guid connectionId) => _directory.Disconnect(connectionId);
    public void SetSubscriptions(Guid connectionId, EventSubscription[] subscriptions) {
        _directory.SetSubscriptions(connectionId, subscriptions);
        PollNow(connectionId); // immediate poll for quick update, if any
    }
    public void Subscribe(Guid connectionId, string name, string? filter) {
        _directory.Subscribe(connectionId, name, filter);
        PollNow(connectionId, name); // immediate poll for quick update, if any
    }
    public void Unsubscribe(Guid connectionId, string? name, string? filter) => _directory.Unsubscribe(connectionId, name, filter);
    public void PollNow(Guid connectionId, string? eventName = null) {
        foreach (var poller in _pollers.All()) {
            if (eventName != null && poller.EventName != eventName) continue;
            var filters = _directory.GetFiltersOfSubscribers(connectionId, poller.EventName);
            var events = poller.Poll(_server, filters, out _);
            if (events == null) continue;
            foreach (var b in events) _directory.EnqueueEvent(poller.EventName, b);
        }
    }
    async Task writeEvent(HttpResponse response, CancellationToken cancellation, ServerEventData e) {
        var stringData = buildEvent(e);
        await response.WriteAsync(stringData, cancellation);
        await response.Body.FlushAsync(cancellation);
    }
    string buildEvent(object? dataObject, string? eventName = null, string? id = null, int? retryMs = null) {
        var json = JsonSerializer.Serialize(dataObject, RelatudeDBJsonOptions.Default);
        var sb = new StringBuilder(json.Length + 64);
        if (retryMs is int r) sb.Append("retry: ").Append(r).Append('\n');
        if (!string.IsNullOrEmpty(id)) sb.Append("id: ").Append(id).Append('\n');
        if (!string.IsNullOrEmpty(eventName)) sb.Append("event: ").Append(eventName).Append('\n');
        // supporting multi-line JSON data:
        using var reader = new StringReader(json);
        string? line;
        while ((line = reader.ReadLine()) != null) {
            sb.Append("data: ").Append(line).Append('\n');
        }
        sb.Append('\n');
        return sb.ToString();
    }
}
class PollerDirectory { // thread-safe
    List<PollerAndDueTime> _pollers = [];
    public void Add(PollerAndDueTime poller) {
        lock (_pollers) {
            _pollers.Add(poller);
        }
    }
    public PollerAndDueTime[] Where(Func<PollerAndDueTime, bool> predicate) {
        lock (_pollers) {
            return [.. _pollers.Where(predicate)];
        }
    }
    public IEventPoller[] All() {
        lock (_pollers) {
            return [.. _pollers.Select(p => p.Poller)];
        }
    }
    public DateTime MinDueTime() {
        lock (_pollers) {
            return _pollers.Min(p => p.DueTime);
        }
    }
}