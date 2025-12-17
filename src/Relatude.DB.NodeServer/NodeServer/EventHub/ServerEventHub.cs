
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
    int _maximumUpdateIntervalMs = 3000; // maximum poll interval, to avoid too long delays
    RelatudeDBServer _server;
    public ServerEventHub(RelatudeDBServer server) {
        _pollPulseTimer = new Timer((o) => pollDuePollers(), null, 2000, Timeout.Infinite);
        _server = server;
    }
    void pollDuePollers() {
        var dueMs = 1000;
        try {
            foreach (var poller in _pollers.Where(t => t.DueTime <= DateTime.UtcNow)) {
                try {
                    if (!_directory.AnySubscribers(poller.Poller.EventName)) continue;
                    var filters = _directory.GetFiltersOfSubscribers(poller.Poller.EventName);
                    var events = poller.Poller.Poll(_server, filters, true, out int msNextCollect);
                    poller.DueTime = DateTime.UtcNow.AddMilliseconds(msNextCollect);
                    if (events == null) continue;
                    foreach (var b in events) _directory.EnqueueEvent(poller.Poller.EventName, b);
                } catch (Exception err1) {
                    RelatudeDBServer.Trace("Error polling events for " + poller.Poller.EventName + ": " + err1.Message);
                }
            }
            var nextDueTime = _pollers.MinDueTime();
            dueMs = (int)(nextDueTime - DateTime.UtcNow).TotalMilliseconds;
        } catch (Exception err2) {
            RelatudeDBServer.Trace("Error in pollDuePollers: " + err2.Message);
        } finally {
            if (dueMs < _minimumUpdateIntervalMs) dueMs = _minimumUpdateIntervalMs;
            if (dueMs > _maximumUpdateIntervalMs) dueMs = _maximumUpdateIntervalMs;
            _pollPulseTimer.Change(dueMs, Timeout.Infinite);
        }
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
            RelatudeDBServer.Trace("SSE client connected, connectionId: " + connectionId + ". Connections: " + cnnCount.ToString("N0"));
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
            RelatudeDBServer.Trace("SSE Error: " + error.Message + "\n" + error.StackTrace + "\n");
        } finally {
            cnnCount = _directory.Disconnect(connectionId);
            RelatudeDBServer.Trace("SSE client disconnected, connectionId: " + connectionId + ". Connections: " + (cnnCount).ToString("N0"));
        }
    }
    public void Disconnect(Guid connectionId) => _directory.Disconnect(connectionId);
    public Guid Subscribe(Guid connectionId, string name, string? filter = null) {
        var subId = _directory.Subscribe(connectionId, name, filter);
        PollNow(connectionId, name); // immediate poll for quick update, if any
        return subId;
    }
    public void Unsubscribe(Guid connectionId, Guid subId) => _directory.Unsubscribe(connectionId, subId);
    public void PollNow(Guid connectionId, string? eventName = null) {
        foreach (var poller in _pollers.All()) {
            if (eventName != null && poller.EventName != eventName) continue;
            var filters = _directory.GetFiltersOfSubscribers(connectionId, poller.EventName);
            try {
                var events = poller.Poll(_server, filters, false, out _);
                if (events == null) continue;
                foreach (var b in events) _directory.EnqueueEvent(poller.EventName, b);
            } catch (Exception ex) {
                RelatudeDBServer.Trace("Error polling events for " + poller.EventName + ": " + ex.Message);
                continue;
            }
        }
    }
    async Task writeEvent(HttpResponse response, CancellationToken cancellation, ServerEventData e) {
        var stringData = buildEvent(e);
        await response.WriteAsync(stringData, cancellation);
        await response.Body.FlushAsync(cancellation);
    }
    string buildEvent(object? dataObject, string? eventName = null, string? id = null, int? retryMs = null) {
        var json = JsonSerializer.Serialize(dataObject, RelatudeDBJsonOptions.SSE);
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